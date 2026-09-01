using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal sealed class MauiNativeTreeEpoch
{
    private static readonly CancellationToken AlreadyCancelled = new(canceled: true);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<Page> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Window> _windows = new(ReferenceEqualityComparer.Instance);
    private bool _open = true;

    public Dictionary<NavigationPage, string> NavigationPageStackIds { get; } =
        new(ReferenceEqualityComparer.Instance);

    public Dictionary<NavigationPage, IReadOnlyList<Page>> NavigationPageKnownPages { get; } =
        new(ReferenceEqualityComparer.Instance);

    public Dictionary<NavigationPage, SuppressedNavigationPop> SuppressedNavigationPops { get; } =
        new(ReferenceEqualityComparer.Instance);

    public HashSet<Page> TrackedModalPages { get; } = new(ReferenceEqualityComparer.Instance);

    public Dictionary<IMauiBranchHost, string> PendingBranchHostSelections { get; } =
        new(ReferenceEqualityComparer.Instance);

    public bool SuppressedNavigationPopDrainQueued { get; set; }

    public bool BranchHostSelectionDrainQueued { get; set; }

    public bool HostBackReconciliationPending { get; set; }

    public AppRoute? PendingHostBackRoute { get; set; }

    /// <summary>
    /// A token that cancels when this epoch closes.
    /// </summary>
    /// <remarks>
    /// Once the epoch is closed its <see cref="CancellationTokenSource"/> may already have been disposed by
    /// <see cref="MauiNativeTreeEpochClosure.CompleteAsync"/>, so a closed epoch reports a pre-cancelled token
    /// instead of touching the disposed source.
    /// </remarks>
    public CancellationToken CancellationToken
    {
        get
        {
            lock (_gate)
            {
                if (!_open)
                    return AlreadyCancelled;
            }

            try
            {
                return _cancellation.Token;
            }
            catch (ObjectDisposedException)
            {
                return AlreadyCancelled;
            }
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _open;
        }
    }

    public void Register(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (_gate)
        {
            if (!_open)
                throw new MauiNativeTreeInvalidatedException();

            _pages.Add(page);
        }
    }

    public void Register(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
        {
            if (!_open)
                throw new MauiNativeTreeInvalidatedException();

            _windows.Add(window);
        }
    }

    public void Forget(Page page)
    {
        lock (_gate)
            _pages.Remove(page);
    }

    public void Forget(Window window)
    {
        lock (_gate)
            _windows.Remove(window);
    }

    public bool Owns(Page page)
    {
        lock (_gate)
            return _open && _pages.Contains(page);
    }

    public bool Owns(Window window)
    {
        lock (_gate)
            return _open && _windows.Contains(window);
    }

    public MauiNativeTreeEpochClosure Close()
    {
        Page[] pages;
        Window[] windows;
        lock (_gate)
        {
            if (!_open)
                return MauiNativeTreeEpochClosure.Empty;

            _open = false;
            pages = _pages.ToArray();
            windows = _windows.ToArray();
            _pages.Clear();
            _windows.Clear();
        }

        Task cancellation;
        try
        {
            cancellation = _cancellation.CancelAsync();
        }
        catch
        {
            cancellation = Task.CompletedTask;
        }

        return new MauiNativeTreeEpochClosure(pages, windows, cancellation, _cancellation);
    }
}

internal sealed record MauiNativeTreeEpochClosure(
    IReadOnlyList<Page> Pages,
    IReadOnlyList<Window> Windows,
    Task Cancellation,
    CancellationTokenSource? CancellationSource)
{
    public static MauiNativeTreeEpochClosure Empty { get; } = new([], [], Task.CompletedTask, null);

    /// <summary>
    /// Waits for epoch cancellation to finish propagating and disposes the source.
    /// </summary>
    /// <remarks>
    /// Application code may register cancellation callbacks against the epoch token. A callback that throws
    /// faults <see cref="CancellationTokenSource.CancelAsync"/>, and this is awaited from shutdown finalization
    /// and from destruction cleanup -- neither of which can afford to abort. The fault is surfaced through
    /// <paramref name="onFault"/> instead of propagating.
    /// </remarks>
    public async Task CompleteAsync(Action<Exception>? onFault = null)
    {
        try
        {
            await Cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onFault?.Invoke(ex);
        }
        finally
        {
            CancellationSource?.Dispose();
        }
    }
}

internal sealed record SuppressedNavigationPop(
    NavigationPage NavigationPage,
    string? WindowId,
    string StackId,
    string? OwnerModalId,
    bool IsNavigationTarget,
    IReadOnlyList<Page> KnownPages,
    IReadOnlyList<Page> RemainingPages);

internal sealed class MauiNativeTreeInvalidatedException : OperationCanceledException
{
    public MauiNativeTreeInvalidatedException()
        : base("The MAUI native page tree is no longer current.")
    {
    }
}
