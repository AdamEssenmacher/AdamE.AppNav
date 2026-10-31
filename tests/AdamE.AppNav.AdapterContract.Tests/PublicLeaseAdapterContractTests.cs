using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;

namespace AdamE.AppNav.AdapterContract.Tests;

// A lease-aware adapter and host written only against AppNav's public API: the reference for an adapter author.
public sealed class PublicLeaseAdapterContractTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DroppedEntryLeavesTheNativeTreeBeforeWhatItBorrowsIsDisposed()
    {
        var log = new List<string>();
        var host = new ContractLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var presenter = new LeaseAwarePresenter(leases, log);
        IRouterNavigator navigator = CreateNavigator(presenter, leases);

        await navigator.NavigateAsync(Request("first"));
        Assert.True(leases.TryGet("first", out RouteEntryLease? first));
        await navigator.NavigateAsync(Request("second"));
        await first.Released.WaitAsync(Timeout);

        Assert.Equal(RouteEntryLeaseRetirement.Dropped, first.Retirement);
        Assert.True(
            Lines(log).IndexOf("removed first") < Lines(log).IndexOf("disposed first"),
            string.Join(", ", Lines(log)));
        Assert.Empty(host.Defects);
        await ShutDownAsync(navigator, presenter);
    }

    [Fact]
    public async Task RetiredLeaseThatIsNotCommittedFailsThePresentationBeforeAnythingIsShown()
    {
        var log = new List<string>();
        var leases = new RouteEntryLeaseRegistry(new ContractLeaseHost());
        var presenter = new LeaseAwarePresenter(leases, log);
        RouteEntryLease lease = leases.Open("detail");
        // The application retires the lease (an account change, say) while its request is in flight.
        IRouterNavigator navigator = CreateNavigator(presenter, leases, onPlanning: lease.Retire);
        var viewModel = new LoggedResource("detail view model", log);
        try
        {
            lease.Own(viewModel);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => navigator.NavigateAsync(Request("detail"), lease).AsTask());
        }
        finally
        {
            lease.Close();
        }

        await lease.Released.WaitAsync(Timeout);
        Assert.False(lease.IsCommitted);
        Assert.Same(NavigationState.Empty, navigator.CurrentState);
        Assert.DoesNotContain("shown detail", Lines(log));
        Assert.True(viewModel.Disposed);
        await ShutDownAsync(navigator, presenter);
    }

    [Fact]
    public async Task RetiredLeaseThatIsStillCommittedIsPresentedAsAPlaceholder()
    {
        var log = new List<string>();
        var leases = new RouteEntryLeaseRegistry(new ContractLeaseHost());
        var presenter = new LeaseAwarePresenter(leases, log);
        IRouterNavigator navigator = CreateNavigator(presenter, leases);
        await navigator.NavigateAsync(Request("first"));
        Assert.True(leases.TryGet("first", out RouteEntryLease? first));

        first.Retire();
        // Host recreation rebuilds the native tree while the entry is still committed.
        presenter.Recreate();
        await navigator.RefreshPresentationAsync();

        Assert.Contains("placeholder first", Lines(log));
        await first.Released.WaitAsync(Timeout);
        Assert.True(first.IsCommitted);
        await ShutDownAsync(navigator, presenter);
    }

    [Fact]
    public async Task ShutdownReleasesEveryLeaseOnceTheAdapterTearsDownAlongsideIt()
    {
        var log = new List<string>();
        var leases = new RouteEntryLeaseRegistry(new ContractLeaseHost());
        var presenter = new LeaseAwarePresenter(leases, log);
        IRouterNavigator navigator = CreateNavigator(presenter, leases);
        await navigator.NavigateAsync(Request("first"));
        Assert.True(leases.TryGet("first", out RouteEntryLease? first));

        await ShutDownAsync(navigator, presenter);

        Assert.True(first.Released.IsCompleted);
        Assert.Equal(RouteEntryLeaseRetirement.Shutdown, first.Retirement);
        Assert.True(Lines(log).IndexOf("removed first") < Lines(log).IndexOf("disposed first"), string.Join(", ", Lines(log)));
    }

    // The host starts the adapter's teardown alongside the router's disposal, never after awaiting it.
    private static async Task ShutDownAsync(IRouterNavigator navigator, LeaseAwarePresenter presenter)
    {
        Task disposal = navigator.DisposeAsync().AsTask();
        presenter.TearDown();
        await disposal.WaitAsync(Timeout);
    }

    // Release runs on the host's dispatcher, which may still be writing the log.
    private static List<string> Lines(List<string> log)
    {
        lock (log)
        {
            return [.. log];
        }
    }

    private static IRouterNavigator CreateNavigator(
        LeaseAwarePresenter presenter,
        RouteEntryLeaseRegistry leases,
        Action? onPlanning = null)
    {
        return RouterNavigatorFactory.Create(
            new RouteTableBuilder().Build(),
            new OneScreenPlanner(onPlanning),
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });
    }

    private static RouterNavigationRequest Request(string name)
    {
        return RouterNavigationRequest.FromRoute(new ScreenRoute(name), NavigationRequestSource.Test);
    }

    private sealed record ScreenRoute(string Name) : AppRoute;

    // Each request shows one screen whose entry ID is its name, so the next request drops the previous screen.
    private sealed class OneScreenPlanner(Action? onPlanning) : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            onPlanning?.Invoke();
            var route = (ScreenRoute)context.Route;
            var state = new NavigationState(
                [new WindowNode("main", new StackNode("stack", [new RouteEntry(route.Name, route)]))],
                "main");
            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    // Runs lease work on one serialized "UI dispatcher" and records what it is told.
    private sealed class ContractLeaseHost : IRouteEntryLeaseHost
    {
        private readonly SemaphoreSlim _dispatcher = new(1, 1);
        private readonly List<RouteEntryLeaseReleaseException> _defects = [];

        public IReadOnlyList<RouteEntryLeaseReleaseException> Defects
        {
            get
            {
                lock (_defects)
                {
                    return _defects.ToArray();
                }
            }
        }

        public async Task InvokeAsync(Func<ValueTask> work)
        {
            await _dispatcher.WaitAsync().ConfigureAwait(false);
            try
            {
                await work().ConfigureAwait(false);
            }
            finally
            {
                _dispatcher.Release();
            }
        }

        // Neither report may block: record, and leave crashing the app to the host's own fatal boundary.
        public void ReportDefect(RouteEntryLeaseReleaseException defect)
        {
            lock (_defects)
            {
                _defects.Add(defect);
            }
        }

        public void ReportFatal(RouteEntryLeaseReleaseException failure)
        {
            ReportDefect(failure);
        }
    }

    private sealed class LoggedResource(string name, List<string> log) : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            lock (log)
            {
                log.Add($"disposed {name.Split(' ')[0]}");
            }

            return ValueTask.CompletedTask;
        }
    }

    // A fake native tree: one view per shown entry, each attached to its entry's lease while it is in the tree.
    private sealed class LeaseAwarePresenter(RouteEntryLeaseRegistry leases, List<string> log) : INavigationPresenter
    {
        private readonly Dictionary<string, RouteEntryLeaseAttachment?> _views = new(StringComparer.Ordinal);

        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add { }
            remove { }
        }

        public ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            RouteEntry[] entries = plan.TargetState.Windows
                .Select(static window => window.Root)
                .OfType<StackNode>()
                .SelectMany(static stack => stack.Entries)
                .ToArray();

            // Attach first, then change the native tree. TryAttach is the check: the application may retire a lease at
            // any time, so a separate IsRetired check before attaching could be stale by the time it attaches.
            var staged = new Dictionary<string, RouteEntryLeaseAttachment?>(StringComparer.Ordinal);
            try
            {
                foreach (RouteEntry entry in entries)
                {
                    if (_views.ContainsKey(entry.Id))
                        continue;

                    RouteEntryLease lease = leases.GetOrOpen(entry.Id, out bool opened);
                    // The adapter that opened the lease attaches what the entry borrows during this operation.
                    if (opened)
                        lease.Own(new LoggedResource($"{entry.Id} scope", log));

                    if (lease.TryAttach(out RouteEntryLeaseAttachment? attachment))
                        staged[entry.Id] = attachment;
                    else if (lease.IsCommitted)
                        staged[entry.Id] = null; // Retired but still committed: a placeholder that borrows nothing.
                    else
                        throw new OperationCanceledException($"The lease of route entry '{entry.Id}' has retired.");
                }
            }
            catch
            {
                // Nothing was shown yet, so undoing the attachments is the whole rollback.
                foreach (RouteEntryLeaseAttachment? attachment in staged.Values)
                    attachment?.Detach();
                throw;
            }

            var target = new HashSet<string>(entries.Select(static entry => entry.Id), StringComparer.Ordinal);
            foreach (string shown in _views.Keys.Where(id => !target.Contains(id)).ToArray())
                Remove(shown);

            foreach ((string entryId, RouteEntryLeaseAttachment? attachment) in staged)
            {
                Log(attachment is null ? $"placeholder {entryId}" : $"shown {entryId}");
                _views[entryId] = attachment;
            }

            return ValueTask.CompletedTask;
        }

        // The native tree was destroyed and will be rebuilt by the next refresh.
        public void Recreate()
        {
            foreach (string shown in _views.Keys.ToArray())
                Remove(shown);
        }

        public void TearDown() => Recreate();

        // Remove the view from the native tree first, then detach: release runs only after the last detach.
        private void Remove(string entryId)
        {
            RouteEntryLeaseAttachment? attachment = _views[entryId];
            _views.Remove(entryId);
            Log($"removed {entryId}");
            attachment?.Detach();
        }

        private void Log(string line)
        {
            lock (log)
            {
                log.Add(line);
            }
        }
    }
}
