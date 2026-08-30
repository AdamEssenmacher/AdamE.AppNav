using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiPresentationTransactionTests
{
    [Theory]
    [InlineData(NativeMutation.PopStack)]
    [InlineData(NativeMutation.PushStack)]
    [InlineData(NativeMutation.PopModal)]
    [InlineData(NativeMutation.PushModal)]
    [InlineData(NativeMutation.RemoveTab)]
    [InlineData(NativeMutation.InsertTab)]
    [InlineData(NativeMutation.SetCurrentTab)]
    [InlineData(NativeMutation.SetWindowPage)]
    public async Task FailureAfterNativeMutationRestoresExactPreviousPresentation(NativeMutation mutation)
    {
        (NavigationState previousState, NavigationState targetState) = StatesFor(mutation);
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("previous", NavigationState.Empty));

        Window? window = null;
        if (mutation == NativeMutation.SetWindowPage)
        {
            window = new Window(Assert.IsAssignableFrom<Page>(presenter.CurrentPage));
            presenter.AttachWindow(window);
        }

        NativePresentationSnapshot previousPresentation = CapturePresentation(presenter, window);
        Page[] previousRoutePages = factory.CreatedPages.ToArray();
        int createdPageCount = factory.CreatedPages.Count;
        nativeOperations.FaultAfterMutation = mutation;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(targetState),
            Context("target", previousState)).AsTask());

        AssertPresentation(previousPresentation, presenter, window);
        Assert.All(previousRoutePages, page => Assert.Equal(0, factory.ReleaseCountFor(page)));
        Assert.All(
            factory.CreatedPages.Skip(createdPageCount),
            page => Assert.Equal(1, factory.ReleaseCountFor(page)));
        await presenter.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttachWindowTransferFailureRestoresBothWindowsAndAttachment(bool faultSourceWindow)
    {
        var nativeOperations = new FaultingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: nativeOperations);
        NavigationState state = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(state), Context("detail", NavigationState.Empty));
        Page currentPage = Assert.IsAssignableFrom<Page>(presenter.CurrentPage);
        var originalWindow = new Window();
        presenter.AttachWindow(originalWindow);
        var replacementPage = new ContentPage();
        var replacementWindow = new Window(replacementPage);
        nativeOperations.FailWindowPageAfterMutation(
            faultSourceWindow ? originalWindow : replacementWindow);

        Assert.Throws<InvalidOperationException>(() => presenter.AttachWindow(replacementWindow));

        Assert.Same(currentPage, originalWindow.Page);
        Assert.Same(replacementPage, replacementWindow.Page);
        Assert.Same(currentPage, presenter.CurrentPage);
        Assert.Same(originalWindow, presenter.AttachedWindow);
        Assert.Equal("main", presenter.AttachedWindowId);

        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task DetachWindowFailureRestoresPageAndAttachment()
    {
        var nativeOperations = new FaultingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: nativeOperations);
        NavigationState state = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(state), Context("detail", NavigationState.Empty));
        Page currentPage = Assert.IsAssignableFrom<Page>(presenter.CurrentPage);
        var window = new Window();
        presenter.AttachWindow(window);
        nativeOperations.FailWindowPageAfterMutation(window);

        Assert.Throws<InvalidOperationException>(() => presenter.DetachWindow(window));

        Assert.Same(currentPage, window.Page);
        Assert.Same(currentPage, presenter.CurrentPage);
        Assert.Same(window, presenter.AttachedWindow);
        Assert.Equal("main", presenter.AttachedWindowId);

        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task AttachWindowRollbackFailureFaultsPresenterClosed()
    {
        var nativeOperations = new FaultingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: nativeOperations);
        NavigationState state = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(state), Context("detail", NavigationState.Empty));
        Page currentPage = Assert.IsAssignableFrom<Page>(presenter.CurrentPage);
        var originalWindow = new Window();
        presenter.AttachWindow(originalWindow);
        var replacementPage = new ContentPage();
        var replacementWindow = new Window(replacementPage);
        nativeOperations.FailWindowPageAfterMutation(replacementWindow, replacementWindow);

        MauiPresentationConsistencyException failure = Assert.Throws<MauiPresentationConsistencyException>(
            () => presenter.AttachWindow(replacementWindow));
        MauiPresentationConsistencyException subsequent = Assert.Throws<MauiPresentationConsistencyException>(
            () => presenter.AttachWindow(new Window()));

        Assert.Same(failure, subsequent);
        Assert.Same(currentPage, originalWindow.Page);
        Assert.Same(replacementPage, replacementWindow.Page);
        Assert.Same(originalWindow, presenter.AttachedWindow);
        Assert.Equal("main", presenter.AttachedWindowId);

        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task NativeMutationFailureRestoresExactPreviousStackAndReleasesOnlyNewPages()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(
            factory,
            diagnostics: diagnostics,
            nativeOperations: nativeOperations);
        NavigationState previousState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] previousPages = navigationPage.Navigation.NavigationStack.ToArray();
        nativeOperations.PushFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "settings")),
            Context("settings", previousState)).AsTask());

        Assert.Same(navigationPage, presenter.CurrentPage);
        Assert.Equal(previousPages, navigationPage.Navigation.NavigationStack.ToArray());
        Assert.All(previousPages, page => Assert.Equal(0, factory.ReleaseCountFor(page)));
        Page failedReplacement = factory.CreatedPages[^1];
        Assert.Equal(1, factory.ReleaseCountFor(failedReplacement));
        Assert.Contains(NavigationDiagnosticEventKind.PresentationRollbackStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationRollbackCompleted, events);
        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task DirectPushFailureAfterMutationRestoresStackAndReleasesCreatedPageOnce()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        await presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "detail")),
            Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] previousStack = navigationPage.Navigation.NavigationStack.ToArray();
        NavigationReconciliation? reconciliation = null;
        presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;
        nativeOperations.FaultAfterMutation = NativeMutation.PushStack;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter
            .PushAsync<TestPresentationPage>(
                "settings",
                new MauiRoutePresentationPageOptions { Animated = false })
            .AsTask());

        Page createdPage = Assert.Single(factory.CreatedPresentationPages);
        Assert.Equal(previousStack, navigationPage.Navigation.NavigationStack.ToArray());
        Assert.Equal(1, factory.ReleaseCountFor(createdPage));
        Assert.Null(reconciliation);
        await presenter.DisposeAsync();
        Assert.Equal(1, factory.ReleaseCountFor(createdPage));
    }

    [Fact]
    public async Task DirectPopFailureAfterMutationRestoresPresentationPageWithoutReleasingIt()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        await presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "detail")),
            Context("detail", NavigationState.Empty));
        await presenter.PushAsync<TestPresentationPage>(
            "settings",
            new MauiRoutePresentationPageOptions { Animated = false });
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] previousStack = navigationPage.Navigation.NavigationStack.ToArray();
        Page presentationPage = previousStack[^1];
        nativeOperations.FaultAfterMutation = NativeMutation.PopStack;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.PopAsync(animated: false).AsTask());

        Assert.Equal(previousStack, navigationPage.Navigation.NavigationStack.ToArray());
        Assert.Equal(0, factory.ReleaseCountFor(presentationPage));
        Assert.True(await presenter.PopAsync(animated: false));
        Assert.Equal(1, factory.ReleaseCountFor(presentationPage));
        await presenter.DisposeAsync();
        Assert.Equal(1, factory.ReleaseCountFor(presentationPage));
    }

    [Fact]
    public async Task DirectPushCancellationAfterMutationRestoresPreviousStack()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        await presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "detail")),
            Context("detail", NavigationState.Empty));
        nativeOperations.BlockNextPush = true;
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] previousStack = navigationPage.Navigation.NavigationStack.ToArray();
        using var cancellation = new CancellationTokenSource();
        Task push = presenter.PushAsync<TestPresentationPage>(
            "settings",
            new MauiRoutePresentationPageOptions { Animated = false },
            cancellation.Token).AsTask();
        await nativeOperations.BlockedPushStarted;

        cancellation.Cancel();
        nativeOperations.ReleaseBlockedPush();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => push);
        Page createdPage = Assert.Single(factory.CreatedPresentationPages);
        Assert.Equal(previousStack, navigationPage.Navigation.NavigationStack.ToArray());
        Assert.Equal(1, factory.ReleaseCountFor(createdPage));
        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task DirectPushRollbackFailureRebuildsPreviousLogicalState()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState previousState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("detail", NavigationState.Empty));
        var previousRoot = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        nativeOperations.FaultAfterMutation = NativeMutation.PushStack;
        nativeOperations.PopFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter
            .PushAsync<TestPresentationPage>(
                "settings",
                new MauiRoutePresentationPageOptions { Animated = false })
            .AsTask());

        var recoveredRoot = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Assert.NotSame(previousRoot, recoveredRoot);
        Assert.Equal(
            ["home", "detail"],
            recoveredRoot.Navigation.NavigationStack
                .Select(page => Assert.IsType<string>(MauiPresentationMetadata.GetRouteEntryId(page)))
                .ToArray());
        Assert.Equal(1, factory.ReleaseCountFor(Assert.Single(factory.CreatedPresentationPages)));
        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task AsyncShutdownCancelsBlockedDirectPushAfterRollbackCompletes()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        await presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "detail")),
            Context("detail", NavigationState.Empty));
        nativeOperations.BlockNextPush = true;
        Task push = presenter.PushAsync<TestPresentationPage>(
            "settings",
            new MauiRoutePresentationPageOptions { Animated = false }).AsTask();
        await nativeOperations.BlockedPushStarted;

        Task shutdown = presenter.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        nativeOperations.ReleaseBlockedPush();

        Exception cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => push);
        Assert.IsNotType<ObjectDisposedException>(cancellation);
        await shutdown;
        Assert.Equal(1, factory.ReleaseCountFor(Assert.Single(factory.CreatedPresentationPages)));
    }

    [Fact]
    public async Task RollbackFailureRebuildsAndVerifiesPreviousStateBeforeAllowingMoreNavigation()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState previousState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("detail", NavigationState.Empty));
        var previousRoot = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] previousPages = previousRoot.Navigation.NavigationStack.ToArray();
        nativeOperations.PushFailuresRemaining = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "settings")),
            Context("settings", previousState)).AsTask());

        var recoveredRoot = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Assert.NotSame(previousRoot, recoveredRoot);
        Assert.Equal(
            ["home", "detail"],
            recoveredRoot.Navigation.NavigationStack
                .Select(page => Assert.IsType<string>(MauiPresentationMetadata.GetRouteEntryId(page)))
                .ToArray());
        Assert.All(previousPages, page => Assert.Equal(1, factory.ReleaseCountFor(page)));

        NavigationState nextState = StackState("home", "catalog");
        await presenter.ApplyAsync(new NavigationPlan(nextState), Context("catalog", previousState));
        Assert.Equal("catalog", MauiPresentationMetadata.GetRouteEntryId(
            Assert.IsType<NavigationPage>(presenter.CurrentPage).CurrentPage));
        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task RollbackAndRecoveryFailureFaultPresenterClosed()
    {
        var nativeOperations = new FaultingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: nativeOperations);
        NavigationState previousState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("detail", NavigationState.Empty));
        nativeOperations.AlwaysFailPush = true;

        MauiPresentationConsistencyException failure = await Assert.ThrowsAsync<MauiPresentationConsistencyException>(
            () => presenter.ApplyAsync(
                new NavigationPlan(StackState("home", "settings")),
                Context("settings", previousState)).AsTask());
        MauiPresentationConsistencyException subsequent = await Assert.ThrowsAsync<MauiPresentationConsistencyException>(
            () => presenter.ApplyAsync(
                new NavigationPlan(previousState),
                Context("detail", previousState)).AsTask());

        Assert.Same(failure, subsequent);
        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task CancellationAfterNativeMutationRestoresPreviousState()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState previousState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] previousPages = navigationPage.Navigation.NavigationStack.ToArray();
        nativeOperations.BlockNextPush = true;
        using var cancellation = new CancellationTokenSource();
        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "settings")),
            Context("settings", previousState),
            cancellation.Token).AsTask();
        await nativeOperations.BlockedPushStarted;

        cancellation.Cancel();
        nativeOperations.ReleaseBlockedPush();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Same(navigationPage, presenter.CurrentPage);
        Assert.Equal(previousPages, navigationPage.Navigation.NavigationStack.ToArray());
        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task LifecycleUpdateFailureRunsCompensatingUpdateAndRestoresPageState()
    {
        var failNextUpdate = true;
        var factory = new InstrumentedRoutePageFactory(updatePage: (page, entry, _) =>
        {
            page.Title = Assert.IsType<TestRoute>(entry.Route).Id;
            if (!failNextUpdate)
                return;

            failNextUpdate = false;
            throw new InvalidOperationException("Injected lifecycle update failure.");
        });
        var presenter = new MauiNavigationPresenter(factory);
        NavigationState previousState = StableEntryState("previous-route");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("previous-route", NavigationState.Empty));
        Page page = Assert.IsType<NavigationPage>(presenter.CurrentPage).CurrentPage;
        string? previousTitle = page.Title;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(StableEntryState("target-route")),
            Context("target-route", previousState)).AsTask());

        Assert.Same(page, Assert.IsType<NavigationPage>(presenter.CurrentPage).CurrentPage);
        Assert.Equal(previousTitle, page.Title);
        Assert.Equal(2, factory.UpdateCountFor(page));
        Assert.Equal(
            new TestRoute("previous-route"),
            Assert.IsType<TestRoute>(factory.LastUpdatedEntryFor(page)?.Route));
        await presenter.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsyncCancelsMutationAndWaitsForRollbackAndFinalPageRelease()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState previousState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("detail", NavigationState.Empty));
        nativeOperations.BlockNextPush = true;
        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "settings")),
            Context("settings", previousState)).AsTask();
        await nativeOperations.BlockedPushStarted;
        var cleanupRan = false;
        typeof(MauiNavigationPresenter)
            .GetMethod("QueueNativeCleanup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(presenter, [(Func<Task>)(() =>
            {
                cleanupRan = true;
                return Task.CompletedTask;
            })]);

        Task shutdown = presenter.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        nativeOperations.ReleaseBlockedPush();

        Exception cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.IsNotType<ObjectDisposedException>(cancellation);
        await shutdown;
        Assert.False(cleanupRan);
        Assert.All(factory.CreatedPages, page => Assert.Equal(1, factory.ReleaseCountFor(page)));
    }

    private static NavigationState StackState(params string[] entryIds)
    {
        return StackStateWithHost("main-stack", entryIds);
    }

    private static NavigationState StackStateWithHost(string stackId, params string[] entryIds)
    {
        return new NavigationState(
            [
                new WindowNode(
                    "main",
                    new StackNode(
                        stackId,
                        entryIds.Select(id => new RouteEntry(id, new TestRoute(id))).ToArray()))
            ],
            "main");
    }

    private static NavigationState ModalState(string modalId)
    {
        return new NavigationState(
            [
                new WindowNode(
                    "main",
                    new StackNode(
                        "main-stack",
                        [new RouteEntry("home", new TestRoute("home"))]),
                    [new ModalNode(modalId, new RouteEntry($"{modalId}-entry", new TestRoute(modalId)))])
            ],
            "main");
    }

    private static NavigationState StableEntryState(string routeId)
    {
        return new NavigationState(
            [
                new WindowNode(
                    "main",
                    new StackNode(
                        "main-stack",
                        [new RouteEntry("stable-entry", new TestRoute(routeId))]))
            ],
            "main");
    }

    private static NavigationState BranchState(string selectedBranchId, params string[] branchIds)
    {
        return new NavigationState(
            [
                new WindowNode(
                    "main",
                    new BranchHostNode(
                        "main-tabs",
                        branchIds.Select(id => new NavigationBranch(
                            id,
                            id,
                            new StackNode(
                                $"{id}-stack",
                                [new RouteEntry($"{id}-entry", new TestRoute(id))])))
                            .ToArray(),
                        selectedBranchId))
            ],
            "main");
    }

    private static (NavigationState Previous, NavigationState Target) StatesFor(NativeMutation mutation)
    {
        return mutation switch
        {
            NativeMutation.PopStack or NativeMutation.PushStack =>
                (StackState("home", "detail"), StackState("home", "settings")),
            NativeMutation.PopModal or NativeMutation.PushModal =>
                (ModalState("old-modal"), ModalState("new-modal")),
            NativeMutation.RemoveTab =>
                (BranchState("catalog", "catalog", "orders"), BranchState("catalog", "catalog")),
            NativeMutation.InsertTab =>
                (BranchState("catalog", "catalog"), BranchState("catalog", "catalog", "orders")),
            NativeMutation.SetCurrentTab =>
                (BranchState("catalog", "catalog", "orders"), BranchState("orders", "catalog", "orders")),
            NativeMutation.SetWindowPage =>
                (StackStateWithHost("old-stack", "home"), StackStateWithHost("new-stack", "settings")),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
    }

    private static NativePresentationSnapshot CapturePresentation(
        MauiNavigationPresenter presenter,
        Window? window)
    {
        Page? root = presenter.CurrentPage;
        return new NativePresentationSnapshot(
            root,
            window?.Page,
            root is null ? null : CapturePage(root),
            root?.Navigation.ModalStack.Select(CapturePage).ToArray() ?? []);
    }

    private static NativePageSnapshot CapturePage(Page page)
    {
        Page[] children = page switch
        {
            NavigationPage navigationPage => navigationPage.Navigation.NavigationStack.ToArray(),
            TabbedPage tabbedPage => tabbedPage.Children.ToArray(),
            _ => []
        };
        Page? currentPage = page switch
        {
            NavigationPage navigationPage => navigationPage.CurrentPage,
            TabbedPage tabbedPage => tabbedPage.CurrentPage,
            _ => null
        };

        return new NativePageSnapshot(
            page,
            page.Title,
            page.IconImageSource,
            page.BindingContext,
            MauiPresentationMetadata.GetHostId(page),
            MauiPresentationMetadata.GetBranchId(page),
            MauiPresentationMetadata.GetRouteEntryId(page),
            MauiPresentationMetadata.GetModalId(page),
            MauiPresentationMetadata.GetPresentationOwnerRouteEntryId(page),
            MauiPresentationMetadata.GetPresentationPageKey(page),
            currentPage,
            children.Select(CapturePage).ToArray());
    }

    private static void AssertPresentation(
        NativePresentationSnapshot expected,
        MauiNavigationPresenter presenter,
        Window? window)
    {
        Assert.Same(expected.Root, presenter.CurrentPage);
        Assert.Same(expected.WindowPage, window?.Page);
        if (expected.RootTree is null)
        {
            Assert.Null(presenter.CurrentPage);
            return;
        }

        AssertPage(expected.RootTree, Assert.IsAssignableFrom<Page>(presenter.CurrentPage));
        Page[] actualModals = presenter.CurrentPage!.Navigation.ModalStack.ToArray();
        Assert.Equal(expected.Modals.Length, actualModals.Length);
        for (var index = 0; index < expected.Modals.Length; index++)
            AssertPage(expected.Modals[index], actualModals[index]);
    }

    private static void AssertPage(NativePageSnapshot expected, Page actual)
    {
        Assert.Same(expected.Page, actual);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Same(expected.IconImageSource, actual.IconImageSource);
        Assert.Same(expected.BindingContext, actual.BindingContext);
        Assert.Equal(expected.HostId, MauiPresentationMetadata.GetHostId(actual));
        Assert.Equal(expected.BranchId, MauiPresentationMetadata.GetBranchId(actual));
        Assert.Equal(expected.RouteEntryId, MauiPresentationMetadata.GetRouteEntryId(actual));
        Assert.Equal(expected.ModalId, MauiPresentationMetadata.GetModalId(actual));
        Assert.Equal(
            expected.PresentationOwnerRouteEntryId,
            MauiPresentationMetadata.GetPresentationOwnerRouteEntryId(actual));
        Assert.Equal(expected.PresentationPageKey, MauiPresentationMetadata.GetPresentationPageKey(actual));

        Page[] actualChildren = actual switch
        {
            NavigationPage navigationPage => navigationPage.Navigation.NavigationStack.ToArray(),
            TabbedPage tabbedPage => tabbedPage.Children.ToArray(),
            _ => []
        };
        Page? actualCurrentPage = actual switch
        {
            NavigationPage navigationPage => navigationPage.CurrentPage,
            TabbedPage tabbedPage => tabbedPage.CurrentPage,
            _ => null
        };
        Assert.Same(expected.CurrentPage, actualCurrentPage);
        Assert.Equal(expected.Children.Length, actualChildren.Length);
        for (var index = 0; index < expected.Children.Length; index++)
            AssertPage(expected.Children[index], actualChildren[index]);
    }

    private static NavigationPresentationContext Context(string routeId, NavigationState currentState)
    {
        var route = new TestRoute(routeId);
        return new NavigationPresentationContext(
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            currentState,
            Guid.NewGuid().ToString("N"));
    }

    private sealed record TestRoute(string Id) : AppRoute;

    private sealed record NativePresentationSnapshot(
        Page? Root,
        Page? WindowPage,
        NativePageSnapshot? RootTree,
        NativePageSnapshot[] Modals);

    private sealed record NativePageSnapshot(
        Page Page,
        string? Title,
        ImageSource? IconImageSource,
        object? BindingContext,
        string? HostId,
        string? BranchId,
        string? RouteEntryId,
        string? ModalId,
        string? PresentationOwnerRouteEntryId,
        string? PresentationPageKey,
        Page? CurrentPage,
        NativePageSnapshot[] Children);

    public enum NativeMutation
    {
        PopStack,
        PushStack,
        PopModal,
        PushModal,
        RemoveTab,
        InsertTab,
        SetCurrentTab,
        SetWindowPage
    }

    private sealed class FaultingNativeOperations : IMauiNativeNavigationOperations
    {
        private readonly TaskCompletionSource _blockedPushStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseBlockedPush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<Window> _windowPageFaultTargets = new();

        public int PushFailuresRemaining { get; set; }

        public int PopFailuresRemaining { get; set; }

        public bool AlwaysFailPush { get; set; }

        public bool BlockNextPush { get; set; }

        public NativeMutation? FaultAfterMutation { get; set; }

        public Task BlockedPushStarted => _blockedPushStarted.Task;

        public void FailWindowPageAfterMutation(params Window[] windows)
        {
            foreach (Window window in windows)
                _windowPageFaultTargets.Enqueue(window);
        }

        public async Task PushAsync(NavigationPage navigationPage, Page page, bool animated)
        {
            if (BlockNextPush)
            {
                BlockNextPush = false;
                _blockedPushStarted.TrySetResult();
                await _releaseBlockedPush.Task;
            }

            if (AlwaysFailPush || PushFailuresRemaining > 0)
            {
                if (PushFailuresRemaining > 0)
                    PushFailuresRemaining--;
                throw new InvalidOperationException("Injected native push failure.");
            }

            await MauiNativeNavigationOperations.Instance.PushAsync(navigationPage, page, animated);
            ThrowAfterMutation(NativeMutation.PushStack);
        }

        public async Task<Page?> PopAsync(NavigationPage navigationPage, bool animated)
        {
            if (PopFailuresRemaining > 0)
            {
                PopFailuresRemaining--;
                throw new InvalidOperationException("Injected native pop failure.");
            }

            Page? page = await MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated);
            ThrowAfterMutation(NativeMutation.PopStack);
            return page;
        }

        public async Task PushModalAsync(Page host, Page page, bool animated)
        {
            await MauiNativeNavigationOperations.Instance.PushModalAsync(host, page, animated);
            ThrowAfterMutation(NativeMutation.PushModal);
        }

        public async Task<Page?> PopModalAsync(Page host, bool animated)
        {
            Page? page = await MauiNativeNavigationOperations.Instance.PopModalAsync(host, animated);
            ThrowAfterMutation(NativeMutation.PopModal);
            return page;
        }

        public void InsertTab(TabbedPage tabbedPage, int index, Page page)
        {
            MauiNativeNavigationOperations.Instance.InsertTab(tabbedPage, index, page);
            ThrowAfterMutation(NativeMutation.InsertTab);
        }

        public void RemoveTab(TabbedPage tabbedPage, Page page)
        {
            MauiNativeNavigationOperations.Instance.RemoveTab(tabbedPage, page);
            ThrowAfterMutation(NativeMutation.RemoveTab);
        }

        public void SetCurrentTab(TabbedPage tabbedPage, Page? page)
        {
            MauiNativeNavigationOperations.Instance.SetCurrentTab(tabbedPage, page);
            ThrowAfterMutation(NativeMutation.SetCurrentTab);
        }

        public void SetWindowPage(Window window, Page? page)
        {
            MauiNativeNavigationOperations.Instance.SetWindowPage(window, page);
            if (_windowPageFaultTargets.TryPeek(out Window? faultTarget) && ReferenceEquals(faultTarget, window))
            {
                _windowPageFaultTargets.Dequeue();
                throw new InvalidOperationException("Injected SetWindowPage failure after mutation.");
            }

            ThrowAfterMutation(NativeMutation.SetWindowPage);
        }

        public void ReleaseBlockedPush() => _releaseBlockedPush.TrySetResult();

        private void ThrowAfterMutation(NativeMutation mutation)
        {
            if (FaultAfterMutation != mutation)
                return;

            FaultAfterMutation = null;
            throw new InvalidOperationException($"Injected {mutation} failure after mutation.");
        }
    }

    private sealed class TestPresentationPage : ContentPage;
}
