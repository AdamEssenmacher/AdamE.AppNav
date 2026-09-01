using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal sealed class MauiNativeTreeEpoch
{
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

    public HashSet<TabbedPage> TrackedTabbedPages { get; } = new(ReferenceEqualityComparer.Instance);

    public HashSet<MauiBranchFlyoutPage> TrackedFlyoutPages { get; } = new(ReferenceEqualityComparer.Instance);

    public HashSet<Page> TrackedModalPages { get; } = new(ReferenceEqualityComparer.Instance);

    public Dictionary<IMauiBranchHost, string> PendingBranchHostSelections { get; } =
        new(ReferenceEqualityComparer.Instance);

    public bool SuppressedNavigationPopDrainQueued { get; set; }

    public bool BranchHostSelectionDrainQueued { get; set; }

    public bool HostBackReconciliationPending { get; set; }

    public AppRoute? PendingHostBackRoute { get; set; }

    public CancellationToken CancellationToken => _cancellation.Token;

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

    public async Task CompleteAsync()
    {
        try
        {
            await Cancellation.ConfigureAwait(false);
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
