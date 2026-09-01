using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Describes the locations at which a MAUI branch host may be presented.
/// </summary>
[Flags]
public enum MauiBranchHostPlacement
{
    /// <summary>No placement is supported.</summary>
    None = 0,
    /// <summary>The host is the direct page of the attached MAUI window.</summary>
    WindowRoot = 1,
    /// <summary>The host is nested below another branch host.</summary>
    Nested = 2,
    /// <summary>The host is the content of a logical modal.</summary>
    ModalContent = 4,
    /// <summary>All supported placement flags.</summary>
    All = 7
}

/// <summary>
/// Supplies the route-owned pages and display metadata for one branch.
/// </summary>
public sealed record MauiBranchHostBranch(string Id, string Title, Page Page);

/// <summary>
/// Supplies context used when creating a branch-host instance.
/// </summary>
public sealed class MauiBranchHostCreationContext
{
    /// <summary>
    /// Initializes context for creating a branch host.
    /// </summary>
    public MauiBranchHostCreationContext(
        BranchHostNode branchHost,
        MauiBranchHostPlacement placement,
        NavigationPresentationContext presentationContext,
        IServiceProvider services)
    {
        BranchHost = branchHost ?? throw new ArgumentNullException(nameof(branchHost));
        PresentationContext = presentationContext ?? throw new ArgumentNullException(nameof(presentationContext));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Placement = placement;
    }

    /// <summary>Gets the logical branch host being presented.</summary>
    public BranchHostNode BranchHost { get; }

    /// <summary>Gets the location at which the host will be presented.</summary>
    public MauiBranchHostPlacement Placement { get; }

    /// <summary>Gets the active navigation presentation context.</summary>
    public NavigationPresentationContext PresentationContext { get; }

    /// <summary>Gets the application service provider.</summary>
    public IServiceProvider Services { get; }
}

/// <summary>
/// Supplies the desired branch-host state to a branch-host instance.
/// </summary>
public sealed class MauiBranchHostUpdateContext
{
    /// <summary>
    /// Initializes context for applying a branch-host update.
    /// </summary>
    public MauiBranchHostUpdateContext(
        BranchHostNode branchHost,
        MauiBranchHostPlacement placement,
        IReadOnlyList<MauiBranchHostBranch> branches,
        string selectedBranchId,
        NavigationPresentationContext presentationContext)
    {
        BranchHost = branchHost ?? throw new ArgumentNullException(nameof(branchHost));
        Branches = branches ?? throw new ArgumentNullException(nameof(branches));
        if (string.IsNullOrWhiteSpace(selectedBranchId))
            throw new ArgumentException("A selected branch id is required.", nameof(selectedBranchId));
        SelectedBranchId = selectedBranchId;
        PresentationContext = presentationContext ?? throw new ArgumentNullException(nameof(presentationContext));
        Placement = placement;
    }

    /// <summary>Gets the logical branch host being presented.</summary>
    public BranchHostNode BranchHost { get; }

    /// <summary>Gets the location at which the host is presented.</summary>
    public MauiBranchHostPlacement Placement { get; }

    /// <summary>Gets the target branch presentations in logical order.</summary>
    public IReadOnlyList<MauiBranchHostBranch> Branches { get; }

    /// <summary>Gets the branch that must be selected after the update.</summary>
    public string SelectedBranchId { get; }

    /// <summary>Gets the active navigation presentation context.</summary>
    public NavigationPresentationContext PresentationContext { get; }
}

/// <summary>
/// Provides data for a branch selection raised by a host-owned MAUI control.
/// </summary>
public sealed class MauiBranchHostSelectionChangedEventArgs(string branchId) : EventArgs
{
    /// <summary>
    /// Gets the selected branch identifier.
    /// </summary>
    public string BranchId { get; } =
        string.IsNullOrWhiteSpace(branchId)
            ? throw new ArgumentException("A branch id is required.", nameof(branchId))
            : branchId;
}

/// <summary>
/// Creates presentation instances for logical branch hosts.
/// </summary>
/// <remarks>
/// Factory instances are configuration objects. AppNav does not dispose them. Calls occur on the MAUI main
/// thread under AppNav's serialized presentation lock and must not re-enter the router.
/// </remarks>
public interface IMauiBranchHostFactory
{
    /// <summary>
    /// Gets the locations supported by instances created by this factory.
    /// </summary>
    MauiBranchHostPlacement SupportedPlacements { get; }

    /// <summary>
    /// Creates one branch-host instance. The presenter owns the returned instance until release.
    /// </summary>
    /// <remarks>
    /// Creation runs on the MAUI presentation path. The returned host must expose all branch pages needed for
    /// verification and release. A host may be reused for compatible updates; it must not retain the supplied
    /// creation context after this call unless it owns an appropriate lifetime.
    /// </remarks>
    ValueTask<IMauiBranchHost> CreateAsync(
        MauiBranchHostCreationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Presents the branches of one logical branch host through a MAUI page.
/// </summary>
/// <remarks>
/// AppNav owns and disposes each returned host exactly once. Disposal releases only host-owned UI and resources;
/// branch pages remain owned by AppNav.
/// </remarks>
public interface IMauiBranchHost : IAsyncDisposable
{
    /// <summary>
    /// Gets the MAUI page that represents this branch host.
    /// </summary>
    Page Page { get; }

    /// <summary>
    /// Gets the branch pages currently represented by the host, in logical order.
    /// </summary>
    IReadOnlyList<MauiBranchHostBranch> Branches { get; }

    /// <summary>
    /// Gets the branch selected by the host, if one is currently selected.
    /// </summary>
    string? SelectedBranchId { get; }

    /// <summary>
    /// Gets the page currently displayed for the selected branch.
    /// </summary>
    Page? SelectedBranchPage { get; }

    /// <summary>
    /// Occurs when the user selects a branch through host-owned UI.
    /// </summary>
    event EventHandler<MauiBranchHostSelectionChangedEventArgs>? SelectionChanged;

    /// <summary>
    /// Applies the target branch list and selection to the host.
    /// </summary>
    /// <remarks>
    /// This method must either return with the target state visible or restore the prior state before throwing.
    /// The returned update is provisional until <see cref="IMauiBranchHostUpdate.CommitAsync"/> is called and must
    /// retain the exact preceding implementation-specific state. The presenter calls
    /// <see cref="IMauiBranchHostUpdate.RollbackAsync"/> when presentation fails or is cancelled. Presenter-driven
    /// changes must not raise <see cref="SelectionChanged"/>; raise it only for user-originated host actions after
    /// updating the observable selection properties.
    /// </remarks>
    ValueTask<IMauiBranchHostUpdate> ApplyAsync(
        MauiBranchHostUpdateContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a reversible branch-host presentation update.
/// </summary>
public interface IMauiBranchHostUpdate : IAsyncDisposable
{
    /// <summary>Commits the provisional update without changing visible topology.</summary>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores the state that preceded the provisional update.</summary>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

internal interface IMauiBranchHostNativeOperations
{
    void SetNativeOperations(IMauiNativeNavigationOperations operations);
}

/// <summary>
/// Creates the default MAUI tab branch host.
/// </summary>
public sealed class MauiTabbedBranchHostFactory : IMauiBranchHostFactory
{
    private readonly Func<MauiBranchHostCreationContext, TabbedPage>? _pageFactory;

    public MauiTabbedBranchHostFactory(
        Func<MauiBranchHostCreationContext, TabbedPage>? pageFactory = null)
    {
        _pageFactory = pageFactory;
    }

    /// <inheritdoc />
    public MauiBranchHostPlacement SupportedPlacements => MauiBranchHostPlacement.All;

    /// <inheritdoc />
    public ValueTask<IMauiBranchHost> CreateAsync(
        MauiBranchHostCreationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IMauiBranchHost>(
            new MauiTabbedBranchHost(
                _pageFactory?.Invoke(context)));
    }
}

/// <summary>
/// Creates a MAUI flyout branch host with a built-in branch menu.
/// </summary>
public sealed class MauiFlyoutBranchHostFactory : IMauiBranchHostFactory
{
    public MauiFlyoutBranchHostFactory(
        string menuTitle,
        FlyoutLayoutBehavior layoutBehavior = FlyoutLayoutBehavior.Default,
        bool isGestureEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuTitle);
        if (!Enum.IsDefined(layoutBehavior))
            throw new ArgumentOutOfRangeException(nameof(layoutBehavior));

        MenuTitle = menuTitle;
        LayoutBehavior = layoutBehavior;
        IsGestureEnabled = isGestureEnabled;
    }

    internal string MenuTitle { get; }

    internal FlyoutLayoutBehavior LayoutBehavior { get; }

    internal bool IsGestureEnabled { get; }

    /// <inheritdoc />
    public MauiBranchHostPlacement SupportedPlacements => MauiBranchHostPlacement.WindowRoot;

    /// <inheritdoc />
    public ValueTask<IMauiBranchHost> CreateAsync(
        MauiBranchHostCreationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IMauiBranchHost>(
            new MauiFlyoutBranchHost(MenuTitle, LayoutBehavior, IsGestureEnabled));
    }
}

/// <summary>
/// A branch host backed by a native <see cref="TabbedPage"/>.
/// </summary>
internal sealed class MauiTabbedBranchHost : IMauiBranchHost, IMauiBranchHostNativeOperations
{
    private readonly TabbedPage _page;
    private IMauiNativeNavigationOperations _nativeOperations = MauiNativeNavigationOperations.Instance;
    private IReadOnlyList<MauiBranchHostBranch> _branches = [];
    private bool _disposed;
    private bool _suppressSelectionChanged;

    public MauiTabbedBranchHost(TabbedPage? page = null)
    {
        _page = page ?? new TabbedPage();
        _page.CurrentPageChanged += HandleCurrentPageChanged;
    }

    public void SetNativeOperations(IMauiNativeNavigationOperations operations) =>
        _nativeOperations = operations ?? throw new ArgumentNullException(nameof(operations));

    public Page Page => _page;

    public IReadOnlyList<MauiBranchHostBranch> Branches => _page.Children
        .Select(DescribeBranch)
        .ToArray();

    public string? SelectedBranchId { get; private set; }

    public Page? SelectedBranchPage => _page.CurrentPage;

    public event EventHandler<MauiBranchHostSelectionChangedEventArgs>? SelectionChanged;

    private MauiBranchHostBranch DescribeBranch(Page child)
    {
        return _branches.FirstOrDefault(branch => ReferenceEquals(branch.Page, child)) ??
               new MauiBranchHostBranch(
                   MauiPresentationMetadata.GetBranchId(child) ?? string.Empty,
                   child.Title ?? string.Empty,
                   child);
    }

    public ValueTask<IMauiBranchHostUpdate> ApplyAsync(
        MauiBranchHostUpdateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        MauiBranchHostBranch[] branches = context.Branches.ToArray();
        Page[] previousChildren = _page.Children.ToArray();
        MauiBranchHostBranch[] previousBranches = _branches.ToArray();
        Page? previousCurrentPage = _page.CurrentPage;
        string? previousSelected = SelectedBranchId;
        var snapshotPages = previousChildren.ToList();
        foreach (MauiBranchHostBranch branch in branches)
        {
            if (!snapshotPages.Any(page => ReferenceEquals(page, branch.Page)))
                snapshotPages.Add(branch.Page);
        }

        MauiTabbedBranchPageSnapshot[] pageSnapshots = snapshotPages
            .Select(MauiTabbedBranchPageSnapshot.Capture)
            .ToArray();
        _branches = branches;
        var update = new MauiTabbedBranchHostUpdate(this, _nativeOperations, previousBranches,
            previousChildren, previousCurrentPage, previousSelected, pageSnapshots);
        _suppressSelectionChanged = true;
        try
        {
            foreach (Page child in _page.Children.ToArray())
            {
                if (!branches.Any(branch => ReferenceEquals(branch.Page, child)))
                    _nativeOperations.RemoveTab(_page, child);
            }

            for (var index = 0; index < branches.Length; index++)
            {
                MauiBranchHostBranch branch = branches[index];
                branch.Page.Title = branch.Title;
                MauiPresentationMetadata.SetBranchId(branch.Page, branch.Id);

                if (index < _page.Children.Count && ReferenceEquals(_page.Children[index], branch.Page))
                    continue;

                if (_page.Children.Any(child => ReferenceEquals(child, branch.Page)))
                    _nativeOperations.RemoveTab(_page, branch.Page);

                _nativeOperations.InsertTab(_page, Math.Min(index, _page.Children.Count), branch.Page);
            }

            SelectedBranchId = context.SelectedBranchId;
            Page? selectedPage = _branches.FirstOrDefault(branch =>
                StringComparer.Ordinal.Equals(branch.Id, context.SelectedBranchId))?.Page;
            _nativeOperations.SetCurrentTab(_page, selectedPage ?? _page.Children.FirstOrDefault());
            return ValueTask.FromResult<IMauiBranchHostUpdate>(update);
        }
        catch (Exception applyException)
        {
            try
            {
                update.RollbackAsync().GetAwaiter().GetResult();
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("Branch-host update failed and could not be restored.",
                    applyException, rollbackException);
            }

            throw;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _page.CurrentPageChanged -= HandleCurrentPageChanged;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleCurrentPageChanged(object? sender, EventArgs e)
    {
        if (_disposed || _suppressSelectionChanged || _page.CurrentPage is null)
            return;

        string? branchId = MauiPresentationMetadata.GetBranchId(_page.CurrentPage);
        if (!string.IsNullOrWhiteSpace(branchId))
        {
            SelectedBranchId = branchId;
            SelectionChanged?.Invoke(this, new MauiBranchHostSelectionChangedEventArgs(branchId));
        }
    }

    private sealed class MauiTabbedBranchHostUpdate(
        MauiTabbedBranchHost host,
        IMauiNativeNavigationOperations nativeOperations,
        IReadOnlyList<MauiBranchHostBranch> branches,
        IReadOnlyList<Page> children,
        Page? currentPage,
        string? selectedBranchId,
        IReadOnlyList<MauiTabbedBranchPageSnapshot> pageSnapshots) : IMauiBranchHostUpdate
    {
        private bool _completed;

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed)
                return ValueTask.CompletedTask;
            bool previousSuppression = host._suppressSelectionChanged;
            host._suppressSelectionChanged = true;
            try
            {
                foreach (Page child in host._page.Children.ToArray())
                    nativeOperations.RemoveTab(host._page, child);
                for (var index = 0; index < children.Count; index++)
                    nativeOperations.InsertTab(host._page, Math.Min(index, host._page.Children.Count), children[index]);
                host._branches = branches.ToArray();
                host.SelectedBranchId = selectedBranchId;
                nativeOperations.SetCurrentTab(host._page, currentPage);
                foreach (MauiTabbedBranchPageSnapshot snapshot in pageSnapshots)
                    snapshot.Restore();
                _completed = true;
            }
            finally
            {
                host._suppressSelectionChanged = previousSuppression;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record MauiTabbedBranchPageSnapshot(
        Page Page,
        string? Title,
        string? BranchId)
    {
        public static MauiTabbedBranchPageSnapshot Capture(Page page) =>
            new(page, page.Title, MauiPresentationMetadata.GetBranchId(page));

        public void Restore()
        {
            Page.Title = Title;
            MauiPresentationMetadata.SetBranchId(Page, BranchId);
        }
    }
}

/// <summary>
/// A branch host backed by a native <see cref="FlyoutPage"/> and a built-in menu.
/// </summary>
internal sealed class MauiFlyoutBranchHost : IMauiBranchHost, IMauiBranchHostNativeOperations
{
    private readonly MauiBranchFlyoutPage _page;
    private IMauiNativeNavigationOperations _nativeOperations = MauiNativeNavigationOperations.Instance;
    private IReadOnlyList<MauiBranchHostBranch> _branches = [];
    private bool _disposed;
    private bool _suppressSelectionChanged;

    public MauiFlyoutBranchHost(
        string menuTitle,
        FlyoutLayoutBehavior layoutBehavior = FlyoutLayoutBehavior.Default,
        bool isGestureEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuTitle);
        if (!Enum.IsDefined(layoutBehavior))
            throw new ArgumentOutOfRangeException(nameof(layoutBehavior));

        _page = new MauiBranchFlyoutPage(new MauiFlyoutBranchHostOptions(menuTitle, layoutBehavior, isGestureEnabled));
        _page.Detail = new ContentPage { IsVisible = false };
        _page.BranchSelected += HandleBranchSelected;
    }

    public void SetNativeOperations(IMauiNativeNavigationOperations operations) =>
        _nativeOperations = operations ?? throw new ArgumentNullException(nameof(operations));

    public Page Page => _page;

    public IReadOnlyList<MauiBranchHostBranch> Branches => _page.Branches
        .Select(branch => new MauiBranchHostBranch(branch.Id, branch.Title, branch.Page))
        .ToArray();

    public string? SelectedBranchId => _page.SelectedBranchId;

    public Page? SelectedBranchPage => _page.Branches.Count == 0 ? null : _page.Detail;

    public event EventHandler<MauiBranchHostSelectionChangedEventArgs>? SelectionChanged;

    public ValueTask<IMauiBranchHostUpdate> ApplyAsync(
        MauiBranchHostUpdateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        MauiBranchHostBranch[] previousBranches = _branches.ToArray();
        MauiFlyoutBranchPresentation[] previousPresentations = _page.Branches.ToArray();
        Page? previousDetail = _page.Detail;
        string? previousSelected = SelectedBranchId;
        bool previousPresented = _page.IsPresented;
        _branches = context.Branches.ToArray();
        var update = new MauiFlyoutBranchHostUpdate(
            this,
            _nativeOperations,
            previousBranches,
            previousPresentations,
            previousDetail,
            previousSelected,
            previousPresented);
        _suppressSelectionChanged = true;
        try
        {
            var presentations = _branches.Select(branch =>
                new MauiFlyoutBranchPresentation(
                    branch.Id,
                    branch.Title,
                    branch.Page,
                    branch.Page.IconImageSource)).ToArray();
            _nativeOperations.SetFlyoutBranches(_page, presentations);
            Page? selectedPage = _branches.FirstOrDefault(branch =>
                StringComparer.Ordinal.Equals(branch.Id, context.SelectedBranchId))?.Page;
            if ((selectedPage ?? _branches.FirstOrDefault()?.Page) is { } detail)
                _nativeOperations.SetFlyoutDetail(_page, detail);
            _nativeOperations.SetSelectedFlyoutBranch(_page, context.SelectedBranchId);
            if (previousSelected is not null && !StringComparer.Ordinal.Equals(previousSelected, context.SelectedBranchId))
                _nativeOperations.SetFlyoutPresented(_page, false);
            return ValueTask.FromResult<IMauiBranchHostUpdate>(update);
        }
        catch (Exception applyException)
        {
            try
            {
                update.RollbackAsync().GetAwaiter().GetResult();
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("Branch-host update failed and could not be restored.",
                    applyException, rollbackException);
            }

            throw;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _page.BranchSelected -= HandleBranchSelected;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleBranchSelected(object? sender, MauiFlyoutBranchSelectedEventArgs e)
    {
        if (_disposed || _suppressSelectionChanged || _page.FindBranchPage(e.BranchId) is not { } selectedPage)
            return;

        if (StringComparer.Ordinal.Equals(SelectedBranchId, e.BranchId))
        {
            _nativeOperations.SetFlyoutPresented(_page, false);
            return;
        }

        _nativeOperations.SetFlyoutDetail(_page, selectedPage);
        _nativeOperations.SetSelectedFlyoutBranch(_page, e.BranchId);
        _nativeOperations.SetFlyoutPresented(_page, false);
        if (StringComparer.Ordinal.Equals(_page.SelectedBranchId, e.BranchId))
            SelectionChanged?.Invoke(this, new MauiBranchHostSelectionChangedEventArgs(e.BranchId));
    }

    private sealed class MauiFlyoutBranchHostUpdate(
        MauiFlyoutBranchHost host,
        IMauiNativeNavigationOperations nativeOperations,
        IReadOnlyList<MauiBranchHostBranch> branches,
        IReadOnlyList<MauiFlyoutBranchPresentation> presentations,
        Page? detail,
        string? selectedBranchId,
        bool isPresented) : IMauiBranchHostUpdate
    {
        private bool _completed;

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed)
                return ValueTask.CompletedTask;
            bool previousSuppression = host._suppressSelectionChanged;
            host._suppressSelectionChanged = true;
            try
            {
                host._branches = branches.ToArray();
                nativeOperations.SetFlyoutBranches(host._page, presentations);
                nativeOperations.SetFlyoutDetail(host._page, detail);
                nativeOperations.SetSelectedFlyoutBranch(host._page, selectedBranchId);
                nativeOperations.SetFlyoutPresented(host._page, isPresented);
                _completed = true;
            }
            finally
            {
                host._suppressSelectionChanged = previousSuppression;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
