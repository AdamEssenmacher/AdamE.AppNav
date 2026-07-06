using System.Runtime.CompilerServices;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Maui.AppLinks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal sealed class MauiNavigationPresenter : INavigationPresenter, IMauiPresentationState, IDisposable
{
    private readonly IMauiRoutePageFactory _pageFactory;
    private readonly MauiRoutePresentationOptions _presentationOptions;
    private readonly IMauiPresentationVerifier _presentationVerifier;
    private readonly MauiExternalNavigationDispatcher? _externalNavigationDispatcher;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly Dictionary<NavigationPage, string> _navigationPageStackIds = new(PageReferenceComparer<NavigationPage>.Instance);
    private readonly Dictionary<NavigationPage, HashSet<Page>> _navigationPageKnownPages = new(PageReferenceComparer<NavigationPage>.Instance);
    private readonly HashSet<TabbedPage> _trackedTabbedPages = new(PageReferenceComparer<TabbedPage>.Instance);
    private readonly HashSet<Page> _trackedModalPages = new(PageReferenceComparer<Page>.Instance);
    private readonly HashSet<Page> _releasedPages = new(PageReferenceComparer<Page>.Instance);
    private NavigationState _lastState = NavigationState.Empty;
    private Window? _attachedWindow;
    private string? _attachedWindowId;
    private string _lifecycleOperationId = CreateOperationId();
    private string? _activeOperationId;
    private bool _suppressReconciliation;
    private bool _disposed;

    public MauiNavigationPresenter(
        IMauiRoutePageFactory pageFactory,
        MauiExternalNavigationDispatcher? externalNavigationDispatcher = null,
        NavigationDiagnostics? diagnostics = null,
        MauiRoutePresentationOptions? presentationOptions = null,
        IMauiPresentationVerifier? presentationVerifier = null)
    {
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
        _presentationOptions = presentationOptions ?? new MauiRoutePresentationOptions();
        _presentationVerifier = presentationVerifier ?? MauiPresentationVerifier.Instance;
        _externalNavigationDispatcher = externalNavigationDispatcher;
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;
    }

    public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested;

    public event EventHandler<Page?>? RootPageChanged;

    public Page? CurrentPage { get; private set; }

    public Window? AttachedWindow => _attachedWindow;

    public string? AttachedWindowId => _attachedWindowId;

    public Page? RootPage => CurrentPage;

    public Page? GetTopPresentedPage()
    {
        return ResolveTopPresentedPage(CurrentPage);
    }

    public bool IsModalPresented(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return CurrentPage?.Navigation.ModalStack.Any(candidate => ReferenceEquals(candidate, page)) == true ||
               _attachedWindow?.Page?.Navigation.ModalStack.Any(candidate => ReferenceEquals(candidate, page)) == true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _suppressReconciliation = true;
        _activeOperationId = null;

        var currentPage = CurrentPage;
        CurrentPage = null;
        SetAttachedWindowPage(null);
        RootPageChanged?.Invoke(this, null);

        if (currentPage is not null)
        {
            DetachPageTree(currentPage);
        }

        foreach (var navigationPage in _navigationPageStackIds.Keys.ToArray())
        {
            UntrackNavigationPage(navigationPage);
        }

        foreach (var tabbedPage in _trackedTabbedPages.ToArray())
        {
            UntrackTabbedPage(tabbedPage);
        }

        foreach (var modalPage in _trackedModalPages.ToArray())
        {
            UntrackModalPage(modalPage);
        }

        if (_attachedWindow is not null)
        {
            UnsubscribeWindowLifecycle(_attachedWindow);
            _externalNavigationDispatcher?.SetForegrounded(false);
        }

        _attachedWindow = null;
        _attachedWindowId = null;
        _navigationPageStackIds.Clear();
        _navigationPageKnownPages.Clear();
        _trackedTabbedPages.Clear();
        _trackedModalPages.Clear();
        _releasedPages.Clear();
        _lastState = NavigationState.Empty;

        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationPresenterDisposed,
            LifecycleOperationId(),
            "MAUI navigation presenter was disposed.");
    }

    public void AttachWindow(Window window, string windowId = "main")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);

        if (_attachedWindow is not null)
        {
            UnsubscribeWindowLifecycle(_attachedWindow);
        }

        _attachedWindow = window;
        _attachedWindowId = windowId;
        SubscribeWindowLifecycle(window);

        if (CurrentPage is not null)
        {
            window.Page = CurrentPage;
        }

        _externalNavigationDispatcher?.SetForegrounded(true);
        _externalNavigationDispatcher?.MarkReady();
    }

    public void DetachWindow(Window window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);

        if (ReferenceEquals(_attachedWindow, window))
        {
            UnsubscribeWindowLifecycle(window);
            _externalNavigationDispatcher?.SetForegrounded(false);
            _attachedWindow = null;
            _attachedWindowId = null;
        }
    }

    private void SubscribeWindowLifecycle(Window window)
    {
        window.Activated += HandleWindowActivated;
        window.Deactivated += HandleWindowDeactivated;
        window.Stopped += HandleWindowStopped;
        window.Resumed += HandleWindowResumed;
        window.Destroying += HandleWindowDestroying;
    }

    private void UnsubscribeWindowLifecycle(Window window)
    {
        window.Activated -= HandleWindowActivated;
        window.Deactivated -= HandleWindowDeactivated;
        window.Stopped -= HandleWindowStopped;
        window.Resumed -= HandleWindowResumed;
        window.Destroying -= HandleWindowDestroying;
    }

    private void HandleWindowActivated(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(true);
    }

    private void HandleWindowDeactivated(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(false);
    }

    private void HandleWindowStopped(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(false);
    }

    private void HandleWindowResumed(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(true);
    }

    private void HandleWindowDestroying(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(false);
    }

    public async ValueTask ApplyAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        if (MainThread.IsMainThread)
        {
            await ApplyOnMainThreadAsync(plan, context, cancellationToken);
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() => ApplyOnMainThreadAsync(plan, context, cancellationToken));
    }

    private async Task ApplyOnMainThreadAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var window = plan.TargetState.ActiveWindow;
        if (window is null || (window.Root is null && window.Modals.Count == 0))
        {
            _suppressReconciliation = true;
            _activeOperationId = context.OperationId;
            try
            {
                SetCurrentPage(null);
                VerifyPresentation(plan.TargetState, context.OperationId);
                _lastState = plan.TargetState;
            }
            finally
            {
                _activeOperationId = null;
                _suppressReconciliation = false;
            }

            return;
        }

        _suppressReconciliation = true;
        _activeOperationId = context.OperationId;
        try
        {
            var nextRoot = window.Root is null
                ? CreateOrReuseEmptyRootHost(CurrentPage)
                : await MaterializeNodeAsync(
                    window.Root,
                    CurrentPage,
                    context.OperationId,
                    isNavigationTarget: window.Modals.Count == 0,
                    cancellationToken);

            SetCurrentPage(nextRoot);
            await ApplyModalsAsync(nextRoot, window.Modals, context.OperationId, cancellationToken);
            VerifyPresentation(plan.TargetState, context.OperationId);
            _lastState = plan.TargetState;
        }
        finally
        {
            _activeOperationId = null;
            _suppressReconciliation = false;
        }
    }

    private async Task<Page> MaterializeNodeAsync(
        NavigationNode node,
        Page? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return node switch
        {
            StackNode stack => await MaterializeStackAsync(
                stack,
                existingPage as NavigationPage,
                operationId,
                isNavigationTarget,
                cancellationToken),
            BranchHostNode branchHost => await MaterializeTabbedBranchHostAsync(
                branchHost,
                existingPage as TabbedPage,
                operationId,
                isNavigationTarget,
                cancellationToken),
            ModalNode modal => modal.Content is null
                ? CreateRoutePage(modal.RouteEntry)
                : await MaterializeNodeAsync(
                    modal.Content,
                    existingPage,
                    operationId,
                    isNavigationTarget,
                    cancellationToken),
            _ => throw new NotSupportedException($"Navigation node '{node.GetType().Name}' is not supported by the MAUI presenter.")
        };
    }

    private async Task<Page> MaterializeStackAsync(
        StackNode stack,
        NavigationPage? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken)
    {
        if (stack.Entries.Count == 0)
        {
            return CreateEmptyPage();
        }

        if (existingPage is not null &&
            StringComparer.Ordinal.Equals(GetHostId(existingPage), stack.Id) &&
            StackRootMatches(existingPage, stack))
        {
            TrackNavigationPage(existingPage, stack.Id);
            await ReconcileNavigationStackAsync(
                existingPage,
                stack,
                isNavigationTarget,
                cancellationToken);
            UpdateKnownNavigationPages(existingPage);
            return existingPage;
        }

        var root = CreateRoutePage(stack.Entries[0]);
        var navigationPage = new NavigationPage(root);
        SetHostId(navigationPage, stack.Id);
        SetRouteEntryId(root, stack.Entries[0].Id);
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, navigationPage, "NavigationPage was created.");
        TrackNavigationPage(navigationPage, stack.Id);

        for (var i = 1; i < stack.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = CreateRoutePage(stack.Entries[i]);
            await navigationPage.Navigation.PushAsync(page, animated: false);
        }

        UpdateKnownNavigationPages(navigationPage);
        return navigationPage;
    }

    private async Task ReconcileNavigationStackAsync(
        NavigationPage navigationPage,
        StackNode stack,
        bool isNavigationTarget,
        CancellationToken cancellationToken)
    {
        var currentStack = navigationPage.Navigation.NavigationStack;
        var previousStackCount = currentStack.Count;
        var commonCount = CommonRoutePrefix(currentStack, stack.Entries);

        while (navigationPage.Navigation.NavigationStack.Count > commonCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = await navigationPage.Navigation.PopAsync(animated: false);
            if (removed is not null)
            {
                DetachPageTree(removed);
            }
        }

        for (var i = navigationPage.Navigation.NavigationStack.Count; i < stack.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = CreateRoutePage(stack.Entries[i]);
            await navigationPage.Navigation.PushAsync(page, animated: false);
        }

        UpdateReusedStackPages(
            navigationPage.Navigation.NavigationStack,
            stack.Entries,
            commonCount,
            isNavigationTarget,
            previousStackCount);
        UpdateKnownNavigationPages(navigationPage);
    }

    private static bool StackRootMatches(NavigationPage navigationPage, StackNode stack)
    {
        return navigationPage.Navigation.NavigationStack.Count > 0 &&
               stack.Entries.Count > 0 &&
               StringComparer.Ordinal.Equals(GetRouteEntryId(navigationPage.Navigation.NavigationStack[0]), stack.Entries[0].Id);
    }

    private static int CommonRoutePrefix(IReadOnlyList<Page> pages, IReadOnlyList<RouteEntry> entries)
    {
        var count = Math.Min(pages.Count, entries.Count);
        var common = 0;
        for (var i = 0; i < count; i++)
        {
            if (!StringComparer.Ordinal.Equals(GetRouteEntryId(pages[i]), entries[i].Id))
            {
                break;
            }

            common++;
        }

        return common;
    }

    private async Task<Page> MaterializeTabbedBranchHostAsync(
        BranchHostNode branchHost,
        TabbedPage? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken)
    {
        var tabbedPage = existingPage is not null && StringComparer.Ordinal.Equals(GetHostId(existingPage), branchHost.Id)
            ? existingPage
            : new TabbedPage();
        var createdTabbedPage = !ReferenceEquals(tabbedPage, existingPage);

        SetHostId(tabbedPage, branchHost.Id);

        if (createdTabbedPage)
        {
            WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, tabbedPage, "TabbedPage was created.");
        }

        TrackTabbedPage(tabbedPage);

        var desiredBranchIds = branchHost.Branches
            .Select(branch => branch.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stalePage in tabbedPage.Children
                     .Where(child => GetBranchId(child) is not { } branchId || !desiredBranchIds.Contains(branchId))
                     .ToArray())
        {
            tabbedPage.Children.Remove(stalePage);
            DetachPageTree(stalePage);
        }

        Page? selectedPage = null;
        for (var i = 0; i < branchHost.Branches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var branch = branchHost.Branches[i];
            var existingBranchPage = tabbedPage.Children.FirstOrDefault(child =>
                StringComparer.Ordinal.Equals(GetBranchId(child), branch.Id));
            var page = await MaterializeNodeAsync(
                branch.Content,
                existingBranchPage,
                operationId,
                isNavigationTarget && StringComparer.Ordinal.Equals(branch.Id, branchHost.SelectedBranchId),
                cancellationToken);
            ApplyBranchChrome(page, branch);
            SetBranchId(page, branch.Id);

            if (existingBranchPage is not null && !ReferenceEquals(existingBranchPage, page))
            {
                var existingIndex = tabbedPage.Children.IndexOf(existingBranchPage);
                tabbedPage.Children.RemoveAt(existingIndex);
                DetachPageTree(existingBranchPage);
            }

            var currentIndex = tabbedPage.Children.IndexOf(page);
            if (currentIndex < 0)
            {
                tabbedPage.Children.Insert(Math.Min(i, tabbedPage.Children.Count), page);
            }
            else if (currentIndex != i)
            {
                tabbedPage.Children.RemoveAt(currentIndex);
                tabbedPage.Children.Insert(Math.Min(i, tabbedPage.Children.Count), page);
            }

            if (StringComparer.Ordinal.Equals(branch.Id, branchHost.SelectedBranchId))
            {
                selectedPage = page;
            }
        }

        tabbedPage.CurrentPage = selectedPage ?? tabbedPage.Children.FirstOrDefault();
        return tabbedPage;
    }

    private static void ApplyBranchChrome(Page page, NavigationBranch branch)
    {
        page.Title = branch.Title;

        if (page is NavigationPage navigationPage &&
            navigationPage.Navigation.NavigationStack.Count > 0)
        {
            navigationPage.IconImageSource = navigationPage.Navigation.NavigationStack[0].IconImageSource;
        }
    }

    private async Task ApplyModalsAsync(
        Page root,
        IReadOnlyList<ModalNode> modals,
        string operationId,
        CancellationToken cancellationToken)
    {
        var modalStack = root.Navigation.ModalStack;
        var previousModalCount = modalStack.Count;
        var commonCount = CommonModalPrefix(modalStack, modals);

        while (root.Navigation.ModalStack.Count > commonCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = await root.Navigation.PopModalAsync(animated: false);
            if (removed is not null)
            {
                DetachPageTree(removed);
            }
        }

        for (var i = root.Navigation.ModalStack.Count; i < modals.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modalPage = modals[i].Content is null
                ? CreateRoutePage(modals[i].RouteEntry)
                : await MaterializeNodeAsync(
                    modals[i].Content!,
                    null,
                    operationId,
                    isNavigationTarget: i == modals.Count - 1,
                    cancellationToken);
            SetModalId(modalPage, modals[i].Id);
            TrackModalPage(modalPage);
            await root.Navigation.PushModalAsync(modalPage, animated: false);
        }

        await UpdateReusedModalPagesAsync(
            root.Navigation.ModalStack,
            modals,
            commonCount,
            previousModalCount,
            operationId,
            cancellationToken);
    }

    private int CommonModalPrefix(IReadOnlyList<Page> pages, IReadOnlyList<ModalNode> modals)
    {
        var count = Math.Min(pages.Count, modals.Count);
        var common = 0;
        for (var i = 0; i < count; i++)
        {
            if (!ModalPageMatches(pages[i], modals[i]))
            {
                break;
            }

            common++;
        }

        return common;
    }

    private bool ModalPageMatches(Page page, ModalNode modal)
    {
        if (!StringComparer.Ordinal.Equals(GetModalId(page), modal.Id))
        {
            return false;
        }

        return modal.Content is null
            ? StringComparer.Ordinal.Equals(GetRouteEntryId(page), modal.RouteEntry.Id)
            : CanReuseNodePage(modal.Content, page);
    }

    private bool CanReuseNodePage(NavigationNode node, Page? existingPage)
    {
        return node switch
        {
            StackNode stack => existingPage is NavigationPage navigationPage &&
                               StringComparer.Ordinal.Equals(GetHostId(navigationPage), stack.Id) &&
                               StackRootMatches(navigationPage, stack),
            BranchHostNode branchHost => existingPage is TabbedPage tabbedPage &&
                                         StringComparer.Ordinal.Equals(GetHostId(tabbedPage), branchHost.Id),
            ModalNode modal => modal.Content is null
                ? existingPage is not null &&
                  StringComparer.Ordinal.Equals(GetRouteEntryId(existingPage), modal.RouteEntry.Id)
                : CanReuseNodePage(modal.Content, existingPage),
            _ => false
        };
    }

    private Page CreateRoutePage(RouteEntry entry)
    {
        var page = _pageFactory.CreatePage(entry);
        SetRouteEntryId(page, entry.Id);
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, page, "Route page was created.");
        return page;
    }

    private void UpdateReusedStackPages(
        IReadOnlyList<Page> pages,
        IReadOnlyList<RouteEntry> entries,
        int commonCount,
        bool isNavigationTarget,
        int previousStackCount)
    {
        var count = Math.Min(commonCount, Math.Min(pages.Count, entries.Count));
        for (var i = 0; i < count; i++)
        {
            UpdateRoutePage(
                pages[i],
                entries[i],
                new MauiRoutePageUpdateContext(
                    ClassifyReuseKind(
                        isNavigationTarget && i == entries.Count - 1,
                        previousStackCount > entries.Count)));
        }
    }

    private async Task UpdateReusedModalPagesAsync(
        IReadOnlyList<Page> pages,
        IReadOnlyList<ModalNode> modals,
        int commonCount,
        int previousModalCount,
        string operationId,
        CancellationToken cancellationToken)
    {
        var count = Math.Min(commonCount, Math.Min(pages.Count, modals.Count));
        for (var i = 0; i < count; i++)
        {
            if (modals[i].Content is null)
            {
                UpdateRoutePage(
                    pages[i],
                    modals[i].RouteEntry,
                    new MauiRoutePageUpdateContext(
                        ClassifyReuseKind(
                            i == modals.Count - 1,
                            previousModalCount > modals.Count)));
                continue;
            }

            await MaterializeNodeAsync(
                modals[i].Content!,
                pages[i],
                operationId,
                isNavigationTarget: i == modals.Count - 1,
                cancellationToken);
        }
    }

    private static MauiRoutePageReuseKind ClassifyReuseKind(bool isNavigationTarget, bool wasResurfaced)
    {
        if (!isNavigationTarget)
        {
            return MauiRoutePageReuseKind.NonTargetReuse;
        }

        return wasResurfaced
            ? MauiRoutePageReuseKind.ResurfacedTarget
            : MauiRoutePageReuseKind.ExplicitTarget;
    }

    private void UpdateRoutePage(Page page, RouteEntry entry, MauiRoutePageUpdateContext context)
    {
        SetRouteEntryId(page, entry.Id);
        _pageFactory.UpdatePage(page, entry, context);
    }

    private Page CreateOrReuseEmptyRootHost(Page? existingPage)
    {
        return existingPage is not null &&
               existingPage is not NavigationPage &&
               existingPage is not TabbedPage
            ? existingPage
            : CreateEmptyPage();
    }

    private Page CreateEmptyPage()
    {
        var page = new ContentPage
        {
            Title = "Empty",
            Content = new Grid()
        };
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, page, "Empty page was created.");
        return page;
    }

    private void SetCurrentPage(Page? page)
    {
        if (ReferenceEquals(CurrentPage, page))
        {
            SetAttachedWindowPage(page);

            return;
        }

        var previous = CurrentPage;
        CurrentPage = page;

        SetAttachedWindowPage(page);

        RootPageChanged?.Invoke(this, page);

        if (previous is not null && !ReferenceEquals(previous, page))
        {
            DetachPageTree(previous);
        }
    }

    private void SetAttachedWindowPage(Page? page)
    {
        if (_attachedWindow is null || ReferenceEquals(_attachedWindow.Page, page))
        {
            return;
        }

        _attachedWindow.Page = page;
    }

    private static Page? ResolveTopPresentedPage(Page? page)
    {
        var current = page;

        while (current is not null)
        {
            var topModal = current.Navigation.ModalStack.LastOrDefault();
            if (topModal is not null && !ReferenceEquals(topModal, current))
            {
                current = topModal;
                continue;
            }

            var next = current switch
            {
                NavigationPage navigationPage when navigationPage.CurrentPage is not null => navigationPage.CurrentPage,
                TabbedPage tabbedPage when tabbedPage.CurrentPage is not null => tabbedPage.CurrentPage,
                _ => null
            };

            if (next is null || ReferenceEquals(next, current))
            {
                return current;
            }

            current = next;
        }

        return null;
    }

    private void UpdateKnownNavigationPages(NavigationPage navigationPage)
    {
        _navigationPageKnownPages[navigationPage] = navigationPage.Navigation.NavigationStack
            .ToHashSet(PageReferenceComparer<Page>.Instance);
    }

    private void ReleaseNavigationPagesRemovedFromNativeStack(NavigationPage navigationPage)
    {
        var currentPages = navigationPage.Navigation.NavigationStack
            .ToHashSet(PageReferenceComparer<Page>.Instance);

        if (_navigationPageKnownPages.TryGetValue(navigationPage, out var knownPages))
        {
            foreach (var removedPage in knownPages.Where(page => !currentPages.Contains(page)).ToArray())
            {
                DetachPageTree(removedPage);
            }
        }

        _navigationPageKnownPages[navigationPage] = currentPages;
    }

    private void TrackNavigationPage(NavigationPage navigationPage, string stackId)
    {
        var wasTracked = _navigationPageStackIds.ContainsKey(navigationPage);
        _navigationPageStackIds[navigationPage] = stackId;
        navigationPage.Popped -= OnNavigationPagePopped;
        navigationPage.Popped += OnNavigationPagePopped;
        navigationPage.PoppedToRoot -= OnNavigationPagePoppedToRoot;
        navigationPage.PoppedToRoot += OnNavigationPagePoppedToRoot;
        UpdateKnownNavigationPages(navigationPage);

        if (!wasTracked)
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerAttached,
                navigationPage,
                "NavigationPage.Popped/PoppedToRoot",
                "Navigation stack handlers were attached.");
        }
    }

    private void UntrackNavigationPage(NavigationPage navigationPage)
    {
        var wasTracked = _navigationPageStackIds.Remove(navigationPage);
        _navigationPageKnownPages.Remove(navigationPage);
        navigationPage.Popped -= OnNavigationPagePopped;
        navigationPage.PoppedToRoot -= OnNavigationPagePoppedToRoot;

        if (wasTracked)
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerDetached,
                navigationPage,
                "NavigationPage.Popped/PoppedToRoot",
                "Navigation stack handlers were detached.");
        }
    }

    private void TrackTabbedPage(TabbedPage tabbedPage)
    {
        var wasTracked = _trackedTabbedPages.Contains(tabbedPage);
        tabbedPage.CurrentPageChanged -= OnTabbedPageCurrentPageChanged;
        tabbedPage.CurrentPageChanged += OnTabbedPageCurrentPageChanged;

        if (_trackedTabbedPages.Add(tabbedPage) && !wasTracked)
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerAttached,
                tabbedPage,
                "TabbedPage.CurrentPageChanged",
                "TabbedPage selection handler was attached.");
        }
    }

    private void UntrackTabbedPage(TabbedPage tabbedPage)
    {
        tabbedPage.CurrentPageChanged -= OnTabbedPageCurrentPageChanged;

        if (_trackedTabbedPages.Remove(tabbedPage))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerDetached,
                tabbedPage,
                "TabbedPage.CurrentPageChanged",
                "TabbedPage selection handler was detached.");
        }
    }

    private void TrackModalPage(Page modalPage)
    {
        var wasTracked = _trackedModalPages.Contains(modalPage);
        modalPage.Disappearing -= OnModalPageDisappearing;
        modalPage.Disappearing += OnModalPageDisappearing;

        if (_trackedModalPages.Add(modalPage) && !wasTracked)
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerAttached,
                modalPage,
                "Page.Disappearing",
                "Modal dismissal handler was attached.");
        }
    }

    private void UntrackModalPage(Page modalPage)
    {
        modalPage.Disappearing -= OnModalPageDisappearing;

        if (_trackedModalPages.Remove(modalPage))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerDetached,
                modalPage,
                "Page.Disappearing",
                "Modal dismissal handler was detached.");
        }
    }

    private void DetachPageTree(Page page)
    {
        DetachPageTree(page, new HashSet<Page>(PageReferenceComparer<Page>.Instance));
    }

    private void DetachPageTree(Page page, HashSet<Page> visited)
    {
        if (!visited.Add(page))
        {
            return;
        }

        var shouldRelease = MarkPageReleased(page);
        UntrackModalPage(page);

        foreach (var modalPage in page.Navigation.ModalStack.ToArray())
        {
            if (!ReferenceEquals(modalPage, page))
            {
                DetachPageTree(modalPage, visited);
            }
        }

        switch (page)
        {
            case NavigationPage navigationPage:
                UntrackNavigationPage(navigationPage);
                foreach (var child in navigationPage.Navigation.NavigationStack.ToArray())
                {
                    DetachPageTree(child, visited);
                }

                break;
            case TabbedPage tabbedPage:
                UntrackTabbedPage(tabbedPage);
                foreach (var child in tabbedPage.Children.ToArray())
                {
                    DetachPageTree(child, visited);
                }

                break;
            default:
                if (shouldRelease)
                {
                    _pageFactory.ReleasePage(page);
                }

                break;
        }
    }

    private bool MarkPageReleased(Page page)
    {
        if (!_releasedPages.Add(page))
        {
            return false;
        }

        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageReleased, page, "Page was released.");
        return true;
    }

    private void OnNavigationPagePopped(object? sender, NavigationEventArgs e)
    {
        if (_suppressReconciliation || sender is not NavigationPage navigationPage)
        {
            return;
        }

        if (!_navigationPageStackIds.TryGetValue(navigationPage, out var stackId))
        {
            return;
        }

        ReleaseNavigationPagesRemovedFromNativeStack(navigationPage);
        ReconcileStackFromNative(stackId, navigationPage);
    }

    private void OnNavigationPagePoppedToRoot(object? sender, NavigationEventArgs e)
    {
        OnNavigationPagePopped(sender, e);
    }

    private void OnTabbedPageCurrentPageChanged(object? sender, EventArgs e)
    {
        if (_suppressReconciliation || sender is not TabbedPage tabbedPage || tabbedPage.CurrentPage is null)
        {
            return;
        }

        var selectedBranchId = GetBranchId(tabbedPage.CurrentPage);
        var branchHostId = GetHostId(tabbedPage);
        if (string.IsNullOrWhiteSpace(selectedBranchId) || string.IsNullOrWhiteSpace(branchHostId))
        {
            return;
        }

        var updatedWindow = UpdateWindowForPresentedNode(
            tabbedPage,
            node => UpdateBranchHostSelection(node, branchHostId, selectedBranchId));
        if (updatedWindow is null)
        {
            return;
        }

        var updatedState = _lastState.ReplaceWindow(updatedWindow);
        RequestReconciliation(updatedState, NavigationReconciliationSource.TabChanged, "Native tab selection changed.");
    }

    private void OnModalPageDisappearing(object? sender, EventArgs e)
    {
        if (_suppressReconciliation || _disposed || sender is not Page page)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => ReconcileModalDismissalIfRemoved(page));
    }

    private void ReconcileModalDismissalIfRemoved(Page page)
    {
        if (_suppressReconciliation || _disposed || IsModalPresented(page))
        {
            return;
        }

        var modalId = GetModalId(page);
        var window = _lastState.ActiveWindow;
        if (string.IsNullOrWhiteSpace(modalId) || window is null)
        {
            return;
        }

        var remainingModals = window.Modals
            .Where(modal => !StringComparer.Ordinal.Equals(modal.Id, modalId))
            .ToArray();

        if (remainingModals.Length == window.Modals.Count)
        {
            return;
        }

        DetachPageTree(page);
        var updatedState = _lastState.ReplaceWindow(window with { Modals = remainingModals });
        RequestReconciliation(updatedState, NavigationReconciliationSource.ModalDismissed, "Native modal dismissal changed.");
    }

    private void ReconcileStackFromNative(string stackId, NavigationPage navigationPage)
    {
        var remainingRouteEntryIds = navigationPage.Navigation.NavigationStack
            .Select(GetRouteEntryId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();

        var updatedWindow = UpdateWindowForPresentedNode(
            navigationPage,
            node => UpdateStackFromNative(node, stackId, remainingRouteEntryIds));
        if (updatedWindow is null)
        {
            return;
        }

        var ownerModalId = FindOwningModalId(navigationPage);
        var updatedState = _lastState.ReplaceWindow(updatedWindow);
        var route = FindTopRouteForPresentedNode(updatedWindow, ownerModalId);
        RequestReconciliation(updatedState, NavigationReconciliationSource.NativeBackGesture, "Native stack pop changed.", route);
    }

    private WindowNode? UpdateWindowForPresentedNode(
        Page ownerPage,
        Func<NavigationNode, NavigationNode?> update)
    {
        var window = _lastState.ActiveWindow;
        if (window?.Root is null)
        {
            return null;
        }

        return UpdateWindowContent(window, FindOwningModalId(ownerPage), update);
    }

    private static WindowNode? UpdateWindowContent(
        WindowNode window,
        string? ownerModalId,
        Func<NavigationNode, NavigationNode?> update)
    {
        if (string.IsNullOrWhiteSpace(ownerModalId))
        {
            var updatedRoot = window.Root is null ? null : update(window.Root);
            return updatedRoot is null ? null : window with { Root = updatedRoot };
        }

        var updatedModals = window.Modals.ToArray();
        for (var i = 0; i < updatedModals.Length; i++)
        {
            if (!StringComparer.Ordinal.Equals(updatedModals[i].Id, ownerModalId))
            {
                continue;
            }

            if (updatedModals[i].Content is null)
            {
                return null;
            }

            var updatedContent = update(updatedModals[i].Content!);
            if (updatedContent is null)
            {
                return null;
            }

            updatedModals[i] = updatedModals[i] with { Content = updatedContent };
            return window with { Modals = updatedModals };
        }

        return null;
    }

    private string? FindOwningModalId(Page page)
    {
        var modalId = GetModalId(page);
        if (!string.IsNullOrWhiteSpace(modalId))
        {
            return modalId;
        }

        var root = CurrentPage ?? _attachedWindow?.Page;
        if (root is null)
        {
            return null;
        }

        foreach (var modalPage in root.Navigation.ModalStack.Reverse())
        {
            var candidateModalId = GetModalId(modalPage);
            if (string.IsNullOrWhiteSpace(candidateModalId))
            {
                continue;
            }

            if (ContainsPageInStructuralTree(modalPage, page))
            {
                return candidateModalId;
            }
        }

        return null;
    }

    private static bool ContainsPageInStructuralTree(Page root, Page target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        return root switch
        {
            NavigationPage navigationPage => navigationPage.Navigation.NavigationStack.Any(page => ContainsPageInStructuralTree(page, target)),
            TabbedPage tabbedPage => tabbedPage.Children.Any(page => ContainsPageInStructuralTree(page, target)),
            _ => false
        };
    }

    private static AppRoute? FindTopRouteForPresentedNode(WindowNode window, string? ownerModalId)
    {
        if (!string.IsNullOrWhiteSpace(ownerModalId))
        {
            var modal = window.Modals.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, ownerModalId));
            if (modal is not null)
            {
                return modal.Content is null
                    ? modal.RouteEntry.Route
                    : FindTopRoute(modal.Content) ?? modal.RouteEntry.Route;
            }
        }

        return window.Root is null ? null : FindTopRoute(window.Root);
    }

    private void RequestReconciliation(
        NavigationState state,
        NavigationReconciliationSource source,
        string reason,
        AppRoute? route = null)
    {
        _lastState = state;
        ReconciliationRequested?.Invoke(
            this,
            new NavigationReconciliationRequestedEventArgs(new NavigationReconciliation(state, source, route, reason)));
    }

    private static NavigationNode? UpdateBranchHostSelection(NavigationNode node, string branchHostId, string selectedBranchId)
    {
        return node switch
        {
            BranchHostNode branchHost when StringComparer.Ordinal.Equals(branchHost.Id, branchHostId) =>
                branchHost.Branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, selectedBranchId))
                    ? branchHost with { SelectedBranchId = selectedBranchId }
                    : null,
            BranchHostNode branchHost => UpdateSelectedBranch(branchHost, child => UpdateBranchHostSelection(child, branchHostId, selectedBranchId)),
            ModalNode modal when modal.Content is not null =>
                UpdateBranchHostSelection(modal.Content, branchHostId, selectedBranchId) is { } updated
                    ? modal with { Content = updated }
                    : null,
            _ => null
        };
    }

    private static NavigationNode? UpdateStackFromNative(
        NavigationNode node,
        string stackId,
        IReadOnlyList<string> remainingRouteEntryIds)
    {
        return node switch
        {
            StackNode stack when StringComparer.Ordinal.Equals(stack.Id, stackId) =>
                UpdateStackEntriesFromNative(stack, remainingRouteEntryIds),
            BranchHostNode branchHost => UpdateSelectedBranch(branchHost, child => UpdateStackFromNative(child, stackId, remainingRouteEntryIds)),
            ModalNode modal when modal.Content is not null =>
                UpdateStackFromNative(modal.Content, stackId, remainingRouteEntryIds) is { } updated
                    ? modal with { Content = updated }
                    : null,
            _ => null
        };
    }

    private static StackNode? UpdateStackEntriesFromNative(
        StackNode stack,
        IReadOnlyList<string> remainingRouteEntryIds)
    {
        if (remainingRouteEntryIds.Count == 0)
        {
            return stack.Entries.Count == 0
                ? stack
                : stack with { Entries = Array.Empty<RouteEntry>() };
        }

        var updatedEntries = new List<RouteEntry>(remainingRouteEntryIds.Count);
        foreach (var entryId in remainingRouteEntryIds)
        {
            var entry = stack.Entries.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, entryId));
            if (entry is null)
            {
                return null;
            }

            updatedEntries.Add(entry);
        }

        return stack with { Entries = updatedEntries };
    }

    private static NavigationNode? UpdateSelectedBranch(BranchHostNode branchHost, Func<NavigationNode, NavigationNode?> update)
    {
        var selectedBranch = branchHost.SelectedBranch;
        if (selectedBranch is null)
        {
            return null;
        }

        var updatedContent = update(selectedBranch.Content);
        return updatedContent is null
            ? null
            : branchHost.ReplaceBranch(selectedBranch with { Content = updatedContent });
    }

    private static AppRoute? FindTopRoute(NavigationNode node)
    {
        return node switch
        {
            StackNode stack => stack.Top?.Route,
            BranchHostNode branchHost when branchHost.SelectedBranch is not null => FindTopRoute(branchHost.SelectedBranch.Content),
            ModalNode modal => modal.RouteEntry.Route,
            _ => null
        };
    }

    private void VerifyPresentation(NavigationState targetState, string operationId)
    {
        var mismatch = _presentationVerifier.Verify(new MauiPresentationVerificationContext(
            targetState,
            CurrentPage,
            _attachedWindow,
            _presentationOptions));
        if (mismatch is null)
        {
            return;
        }

        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.PresentationPath] = mismatch.Path,
            [NavigationDiagnosticDataKeys.PresentationExpected] = mismatch.Expected,
            [NavigationDiagnosticDataKeys.PresentationActual] = mismatch.Actual
        };
        AddIfPresent(data, NavigationDiagnosticDataKeys.WindowId, targetState.ActiveWindowId);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationVerificationFailed,
            operationId,
            $"Presentation verification failed at '{mismatch.Path}'.",
            data);

        throw new InvalidOperationException(
            $"Presentation verification failed at '{mismatch.Path}'. Expected '{mismatch.Expected}', actual '{mismatch.Actual}'.");
    }

    private void WritePageLifecycle(
        NavigationDiagnosticEventKind kind,
        Page page,
        string message)
    {
        _diagnostics.Write(
            kind,
            LifecycleOperationId(),
            message,
            PageDiagnosticData(page));
    }

    private void WriteHandlerLifecycle(
        NavigationDiagnosticEventKind kind,
        Page page,
        string handlerName,
        string message)
    {
        var data = PageDiagnosticData(page);
        data[NavigationDiagnosticDataKeys.HandlerName] = handlerName;

        _diagnostics.Write(
            kind,
            LifecycleOperationId(),
            message,
            data);
    }

    private Dictionary<string, object?> PageDiagnosticData(Page page)
    {
        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.PageType] = page.GetType().FullName
        };

        AddIfPresent(data, NavigationDiagnosticDataKeys.HostId, GetHostId(page));
        AddIfPresent(data, NavigationDiagnosticDataKeys.BranchId, GetBranchId(page));
        AddIfPresent(data, NavigationDiagnosticDataKeys.RouteEntryId, GetRouteEntryId(page));
        AddIfPresent(data, NavigationDiagnosticDataKeys.ModalId, GetModalId(page));

        return data;
    }

    private static void AddIfPresent(Dictionary<string, object?> data, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            data[key] = value;
        }
    }

    private string LifecycleOperationId()
    {
        if (!string.IsNullOrWhiteSpace(_activeOperationId))
        {
            return _activeOperationId;
        }

        if (string.IsNullOrWhiteSpace(_lifecycleOperationId))
        {
            _lifecycleOperationId = CreateOperationId();
        }

        return _lifecycleOperationId;
    }

    private static string CreateOperationId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static void SetHostId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetHostId(bindableObject, id);
    }

    private static string? GetHostId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetHostId(bindableObject);
    }

    private static void SetBranchId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetBranchId(bindableObject, id);
    }

    private static string? GetBranchId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetBranchId(bindableObject);
    }

    private static void SetRouteEntryId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetRouteEntryId(bindableObject, id);
    }

    private static string? GetRouteEntryId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetRouteEntryId(bindableObject);
    }

    private static void SetModalId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetModalId(bindableObject, id);
    }

    private static string? GetModalId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetModalId(bindableObject);
    }

    private sealed class PageReferenceComparer<TPage> : IEqualityComparer<TPage>
        where TPage : Page
    {
        public static PageReferenceComparer<TPage> Instance { get; } = new();

        public bool Equals(TPage? x, TPage? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(TPage obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
