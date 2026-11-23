using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Oahu;

/// <summary>Applies AppNav plans to a replaceable SwiftUI or Navigation 3 host.</summary>
public sealed class OahuNavigationPresenter : INavigationPresenter, INavigationPresentationSettlement, IDisposable
{
    private sealed record Attachment(long Epoch, IOahuNavigationSurface Surface)
    {
        public CancellationTokenSource Detached { get; } = new();
        public Exception? Failure { get; set; }
    }

    private sealed record Presentation(Attachment Host, NavigationState State, OahuNavigationTarget Target)
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Interaction(Presentation origin, RouterNavigationRequest request, string key)
    {
        private readonly WeakReference<Presentation> source = new(origin);
        private readonly DateTimeOffset requestedAt = request.Timestamp;
        private readonly AppRoute? route = request.Route;
        private readonly Uri? uri = request.Uri;
        private int available = 1;
        public string Consume(Presentation? current, RouterNavigationRequest resolved)
        {
            if (resolved.Timestamp != requestedAt || !Equals(resolved.Route, route) || !Equals(resolved.Uri, uri))
                return "";
            if (!source.TryGetTarget(out var captured) || !ReferenceEquals(captured, current))
                return "";
            return Interlocked.Exchange(ref available, 0) == 1 ? key : "";
        }

        public void End() => Interlocked.Exchange(ref available, 0);
    }

    private readonly object gate = new();
    private readonly TimeSpan timeout;
    private readonly Func<RouteEntry, string>? screenName;
    private readonly AsyncLocal<Interaction?> interaction = new();
    private IRouterNavigator? router;
    private Attachment? host;
    private TaskCompletionSource attached = NewSignal();
    // Waiting for the first exact native observation of a staged plan.
    private Presentation? pending;
    // Latest witnessed native state. Consecutive gestures may move this ahead of router history.
    private Presentation? observed;
    private readonly Dictionary<string, Presentation> awaitingSettlement = new(StringComparer.Ordinal);
    private readonly Dictionary<NavigationState, Presentation> witnessedReconciliations = new(ReferenceEqualityComparer.Instance);
    private long nextEpoch;
    private long nextRevision;
    private bool disposed;

    public OahuNavigationPresenter(TimeSpan? acknowledgementTimeout = null, Func<RouteEntry, string>? screenName = null)
    {
        this.screenName = screenName;
        timeout = acknowledgementTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(acknowledgementTimeout));
    }

    // Reconciliation is submitted with an awaitable result through the bound navigator.
    event EventHandler<NavigationReconciliationRequestedEventArgs>? INavigationPresenter.ReconciliationRequested
    { add { } remove { } }

    /// <summary>Raised after a native reconciliation commits. UI subscribers must dispatch to their UI thread.</summary>
    public event Action? Reconciled;

    /// <summary>Binds the navigator that native reconciliations are submitted to.</summary>
    /// <remarks>
    /// The presenter calls <see cref="IRouterNavigator.ReconcileAsync"/> while holding its own lock, so the navigator
    /// must queue the reconciliation and never start it inline, as the router does.
    /// </remarks>
    public void Bind(IRouterNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (router != null && !ReferenceEquals(router, navigator))
                throw new InvalidOperationException("A presenter belongs to one navigator.");
            router = navigator;
        }
    }

    /// <summary>Navigate from a visible element. Its optional visual hint exists only for this call and attachment.</summary>
    public async ValueTask<NavigationResult> NavigateFromAsync(RouterNavigationRequest request,
        string sourceEntryId, string sourceKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        IRouterNavigator navigator;
        Interaction? hint;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            navigator = router ?? throw new InvalidOperationException("Bind the presenter before navigating.");
            hint = pending is null && observed is { } last && last.Target.ShowsOnTop(sourceEntryId)
                ? new Interaction(last, request, sourceKey) : null;
        }
        var previous = interaction.Value;
        interaction.Value = hint;
        try { return await navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false); }
        finally
        {
            hint?.End();
            interaction.Value = previous;
        }
    }

    /// <summary>Capture the returned epoch in callbacks from this host, including delayed callbacks.</summary>
    public long Attach(IOahuNavigationSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Attachment? previous;
        Attachment current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previous = host;
            host = current = new(++nextEpoch, surface);
            observed = pending = null;
            awaitingSettlement.Clear();
            attached.TrySetResult();
        }
        previous?.Detached.Cancel();
        return current.Epoch;
    }

    public void Detach(long epoch)
    {
        Attachment previous;
        lock (gate)
        {
            if (host is null || host.Epoch != epoch) return;
            previous = host;
            host = null;
            observed = pending = null;
            awaitingSettlement.Clear();
            attached = NewSignal();
        }
        previous.Detached.Cancel();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<Attachment> WaitForHostAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (host is not null) return host;
                wait = attached.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async ValueTask ApplyAsync(NavigationPlan plan, NavigationPresentationContext context,
        CancellationToken cancellationToken = default)
    {
        if (plan.Kind == NavigationPlanKind.Reconcile)
        {
            await AcceptReconciliationAsync(plan.TargetState, context, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Refuse every shape no surface presents before waiting for one. Only whether the attached surface presents
        // splits waits for the host.
        _ = new OahuNavigationTarget(plan.TargetState, 0, presentsSplits: true);
        var revision = Interlocked.Increment(ref nextRevision);
        var replay = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attachment current = await WaitForHostAsync(cancellationToken).ConfigureAwait(false);
            // The attached surface decides which shapes it presents, so each host builds its own target.
            var target = new OahuNavigationTarget(plan.TargetState, revision, current.Surface.PresentsSplits);
            var transition = DescribeTransition(current, plan, context, target, replay);
            try
            {
                var accepted = await PresentAsync(current, plan.TargetState, target with { Transition = transition },
                    cancellationToken).ConfigureAwait(false);
                RememberSettlement(context.OperationId, accepted);
                return;
            }
            catch (Exception) when (current.Detached.IsCancellationRequested || !IsCurrent(current))
            {
                replay = true;
                // A different native host must witness this same pending plan before a logical commit.
                continue;
            }
            catch (Exception failure)
            {
                await RollbackAsync(current, context, failure).ConfigureAwait(false);
                throw;
            }
        }
    }

    private OahuPresentationTransition DescribeTransition(Attachment current, NavigationPlan plan,
        NavigationPresentationContext context, OahuNavigationTarget target, bool replay)
    {
        Presentation? before;
        lock (gate)
        {
            if (current.Failure is { } failure)
                throw new InvalidOperationException("Replace the native host after failed rollback.", failure);
            before = observed;
        }

        string? operation = null;
        if (replay || (before is null && context.CurrentState.Windows.Count > 0))
            operation = "reattach";
        else if (plan.Kind == NavigationPlanKind.Refresh)
            operation = "refresh";

        var sourceKey = "";
        if (operation is null && plan.Kind == NavigationPlanKind.Navigate && !plan.ContextualFallback &&
            ReferenceEquals(before?.State, context.CurrentState) && interaction.Value is { } hint)
            sourceKey = hint.Consume(before, context.Request);

        return OahuPresentationTransition.Describe(before?.Target, target, operation, sourceKey, screenName);
    }

    private async Task RollbackAsync(Attachment current, NavigationPresentationContext context, Exception failure)
    {
        try
        {
            var state = context.CurrentState;
            var rollback = new OahuNavigationTarget(state, Interlocked.Increment(ref nextRevision),
                current.Surface.PresentsSplits);
            OahuNavigationTarget? previous;
            lock (gate) previous = observed?.Target;
            // Recovery must be able to restore the committed stack even when an app callback fails.
            rollback = rollback with { Transition = OahuPresentationTransition.Describe(previous, rollback, "rollback", "", screenName: null) };
            var restored = await PresentAsync(current, state, rollback, CancellationToken.None).ConfigureAwait(false);
            RememberSettlement(context.OperationId, restored);
        }
        catch (Exception recovery) when (IsCurrent(current))
        {
            lock (gate) current.Failure = new AggregateException(failure, recovery);
            throw new InvalidOperationException("The native host could not restore its committed surface.", current.Failure);
        }
        catch (Exception) when (!IsCurrent(current)) { /* Detached hosts have no visible surface. */ }
    }

    private bool IsCurrent(Attachment attachment)
    {
        lock (gate) return ReferenceEquals(host, attachment);
    }

    private async Task<Presentation> PresentAsync(Attachment current, NavigationState state, OahuNavigationTarget target,
        CancellationToken cancellationToken)
    {
        var stage = new Presentation(current, state, target);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Detached.Token);
        lifetime.CancelAfter(timeout);
        lock (gate)
        {
            if (host != current) throw new OperationCanceledException("Host detached.");
            pending = stage;
        }
        try
        {
            await current.Surface.ShowAsync(target, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
            await stage.Observed.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            await AcceptIfCurrentAsync(stage, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            lock (gate)
                if (host != current) throw new OperationCanceledException("Host detached before acceptance.");
            return stage;
        }
        catch (OperationCanceledException error) when (lifetime.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested && !current.Detached.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out presenting native revision {target.Revision}.", error);
        }
        finally
        {
            lock (gate)
                if (ReferenceEquals(pending, stage)) pending = null;
        }
    }

    /// <summary>Report settled native state; a canceled gesture reports its original state.</summary>
    /// <param name="epoch">The epoch <see cref="Attach"/> returned for the reporting host.</param>
    /// <param name="revision">The staged revision the reading belongs to.</param>
    /// <param name="branch">The selected branch, or a split's primary column.</param>
    /// <param name="entries">The entry IDs of that stack, bottom first.</param>
    /// <param name="modalId">The presented modal, or null or empty for none.</param>
    /// <param name="modalEntries">The modal stack's entry IDs.</param>
    /// <param name="secondaryEntries">A split's secondary column entry IDs; empty when the split has none.</param>
    /// <param name="compactColumn">For a split, the column a collapsed presentation shows on top: the column on top
    /// when collapsed, and the staged column otherwise. Null for a window without a split.</param>
    public Task ObserveAsync(long epoch, long revision, string branch, IReadOnlyList<string> entries,
        string? modalId = null, IReadOnlyList<string>? modalEntries = null,
        IReadOnlyList<string>? secondaryEntries = null, SplitColumn? compactColumn = null)
    {
        var actual = new SurfaceObservation(branch, entries, string.IsNullOrEmpty(modalId) ? null : modalId,
            modalEntries ?? [], secondaryEntries ?? [], compactColumn);
        lock (gate)
        {
            if (host is null || host.Epoch != epoch || host.Failure != null) return Task.CompletedTask;
            if (pending is { } stage)
            {
                if (stage.Target.Revision == revision && actual.Matches(stage.Target))
                {
                    observed = stage;
                    pending = null;
                    stage.Observed.TrySetResult();
                }
                return Task.CompletedTask;
            }
            if (observed is not { } last || last.Target.Revision != revision ||
                actual.Reconcile(last.State, last.Target) is not { } change)
                return Task.CompletedTask;
            return EnqueueReconciliation(host, last.Target, change);
        }
    }

    // Called under gate. Advance the observed state immediately so a second native pop is
    // interpreted against the first, even if the router has not accepted the first one yet.
    private Task EnqueueReconciliation(Attachment current, OahuNavigationTarget previousTarget,
        NavigationReconciliation change)
    {
        var navigator = router ?? throw new InvalidOperationException("Bind the presenter before reporting native navigation.");
        var reconciled = new OahuNavigationTarget(change.TargetState, previousTarget.Revision,
            current.Surface.PresentsSplits);
        // The native host performed this gesture itself. Describe what it did rather than republishing the
        // transition that moved the app into the previous target, whose source key was consumed there.
        var target = reconciled with
        {
            Transition = OahuPresentationTransition.Describe(
                previousTarget, reconciled, operation: null, sourceKey: "", screenName)
        };
        var next = new Presentation(current, change.TargetState, target);
        observed = next;
        witnessedReconciliations.Add(next.State, next);

        // Ask the router now, under the gate and so in report order: it counts the reconciliation before the
        // operation that staged the observed revision releases its lock, so a navigation queued behind that operation
        // can't plan against the state this report replaces. A native callback can arrive in the router's execution
        // context, so the call starts from a clean one. The router queues it and never runs it inline.
        Task reconciliation = null!;
        ExecutionContext.Run(CleanContext, _ => reconciliation = ReconcileAsync(navigator, change), null);
        return reconciliation;
    }

    private static readonly ExecutionContext CleanContext = CaptureCleanContext();

    // A thread started without the caller's context has the empty one. Waiting on a task instead could run it inline
    // in the caller's context.
    private static ExecutionContext CaptureCleanContext()
    {
        ExecutionContext? clean = null;
        var thread = new Thread(() => clean = ExecutionContext.Capture()) { IsBackground = true };
        thread.UnsafeStart();
        thread.Join();
        return clean ?? throw new InvalidOperationException("Could not capture an empty execution context.");
    }

    private sealed class SupersededObservationException : Exception;

    private async Task AcceptReconciliationAsync(NavigationState state, NavigationPresentationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Presentation stage;
        lock (gate)
        {
            if (!witnessedReconciliations.TryGetValue(state, out var completion))
                throw new InvalidOperationException("The host did not witness this reconciliation.");
            stage = completion;
            if (host != stage.Host || observed?.Target.Revision != stage.Target.Revision)
                throw new SupersededObservationException();
        }
        // Commit every witnessed gesture in order, but only the latest can synchronize the UI.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, stage.Host.Detached.Token);
        lifetime.CancelAfter(timeout);
        try
        {
            await AcceptIfCurrentAsync(stage, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(stage.Host)) throw new SupersededObservationException();
            RememberSettlement(context.OperationId, stage);
        }
        catch (Exception) when (!IsCurrent(stage.Host)) { throw new SupersededObservationException(); }
        catch (Exception failure)
        {
            await RollbackAsync(stage.Host, context, failure).ConfigureAwait(false);
            throw;
        }
    }

    private Task AcceptIfCurrentAsync(Presentation stage, CancellationToken token)
    {
        return stage.Host.Surface.AcceptAsync(stage.Target, ApplyIfCurrent, token);

        bool ApplyIfCurrent(Action apply)
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(host, stage.Host) || !ReferenceEquals(observed, stage))
                    return false;
                apply();
                return true;
            }
        }
    }

    private void RememberSettlement(string operationId, Presentation stage)
    {
        lock (gate)
            if (ReferenceEquals(host, stage.Host))
                awaitingSettlement[operationId] = stage;
    }

    async ValueTask INavigationPresentationSettlement.OnPresentationSettledAsync(NavigationState state,
        string operationId)
    {
        Presentation? settled;
        lock (gate) awaitingSettlement.Remove(operationId, out settled);
        if (settled is not null && ReferenceEquals(settled.State, state))
            await SettleAsync(settled).ConfigureAwait(false);
    }

    private async Task SettleAsync(Presentation stage)
    {
        var detached = stage.Host.Detached.Token;
        try
        {
            if (!IsCurrent(stage.Host)) return;
            await stage.Host.Surface.SettleAsync(stage.Target, detached).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (detached.IsCancellationRequested) { }
    }

    private async Task ReconcileAsync(IRouterNavigator navigator, NavigationReconciliation change)
    {
        try
        {
            await navigator.ReconcileAsync(change).ConfigureAwait(false);
            Reconciled?.Invoke();
        }
        catch (SupersededObservationException) { }
        catch (StaleNavigationReconciliationException) { }
        finally { lock (gate) witnessedReconciliations.Remove(change.TargetState); }
    }

    public void Dispose()
    {
        Attachment? previous;
        lock (gate)
        {
            disposed = true;
            previous = host;
            host = null;
            pending = observed = null;
            awaitingSettlement.Clear();
            attached.TrySetResult();
        }
        previous?.Detached.Cancel();
    }
}
