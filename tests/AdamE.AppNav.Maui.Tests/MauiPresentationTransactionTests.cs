using System.Reflection;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using DeviceRunners.UITesting.Xunit3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiPresentationTransactionTests
{
    [Fact]
    public async Task NoOpNativeFlyoutSelectionFailsVerificationAndRollsBack()
    {
        NavigationState previousState = BranchState("catalog", "catalog", "orders");
        NavigationState targetState = BranchState("orders", "catalog", "orders");
        var nativeOperations = new FaultingNativeOperations();
        var options = new MauiRoutePresentationOptions();
        options.BranchHosts.Add(
            "main-tabs",
            new MauiBranchHostRegistration(new MauiFlyoutBranchHostFactory("Main")));
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            presentationOptions: options,
            nativeOperations: nativeOperations);
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("catalog", NavigationState.Empty));
        var flyoutPage = Assert.IsType<MauiBranchFlyoutPage>(presenter.CurrentPage);
        NativePresentationSnapshot previousPresentation = CapturePresentation(presenter, null);
        Assert.Equal("catalog", flyoutPage.SelectedBranchId);
        nativeOperations.IgnoreNextSelectedFlyoutBranchMutation = true;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.ApplyAsync(
                new NavigationPlan(targetState),
                Context("orders", previousState)).AsTask());

        Assert.Contains("selectedBranchId", exception.Message, StringComparison.Ordinal);
        AssertPresentation(previousPresentation, presenter, null);
        Assert.Equal("catalog", flyoutPage.SelectedBranchId);
        await presenter.StartShutdown();
    }

    [Fact]
    public async Task StructuralRollbackFailureStillFinalizesBranchHostUpdate()
    {
        var nativeOperations = new FaultingNativeOperations();
        var branchHostFactory = new TrackingBranchHostFactory();
        var options = new MauiRoutePresentationOptions();
        options.BranchHosts.Add(
            "main-tabs",
            new MauiBranchHostRegistration(branchHostFactory));
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            presentationOptions: options,
            nativeOperations: nativeOperations);
        NavigationState previousState = BranchState("catalog", "catalog", "orders");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("catalog", NavigationState.Empty));
        BranchHostNode root = Assert.IsType<BranchHostNode>(previousState.ActiveWindow?.Root);
        var targetState = new NavigationState(
            [new WindowNode(
                "main",
                root with { SelectedBranchId = "orders" },
                [new ModalNode("details", new RouteEntry("details-entry", new TestRoute("details")))])],
            "main");
        nativeOperations.FaultAfterMutation = NativeMutation.PushModal;
        nativeOperations.PopModalFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(targetState),
            Context("orders", previousState)).AsTask());

        TrackingBranchHostUpdate failedUpdate = branchHostFactory.Updates[1];
        Assert.Equal(1, failedUpdate.RollbackCount);
        Assert.Equal(1, failedUpdate.DisposeCount);
        Assert.Equal("catalog", Assert.IsType<BranchHostNode>(previousState.ActiveWindow?.Root).SelectedBranchId);
        await presenter.StartShutdown();
    }

    [Theory]
    [InlineData(NativeMutation.RemoveTab)]
    [InlineData(NativeMutation.InsertTab)]
    public async Task NoOpNativeTabMutationFailsVerificationAndRollsBack(NativeMutation mutation)
    {
        (NavigationState previousState, NavigationState targetState) = StatesFor(mutation);
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("previous", NavigationState.Empty));
        NativePresentationSnapshot previousPresentation = CapturePresentation(presenter, null);
        int createdPageCount = factory.CreatedPages.Count;
        if (mutation == NativeMutation.RemoveTab)
            nativeOperations.IgnoreRemoveTabMutation(callsUntilNoOp: 1);
        else
            nativeOperations.IgnoreInsertTabMutation(callsUntilNoOp: 1);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.ApplyAsync(
                new NavigationPlan(targetState),
                Context("target", previousState)).AsTask());

        Assert.Contains("did not retain", exception.Message, StringComparison.Ordinal);
        AssertPresentation(previousPresentation, presenter, null);
        Assert.All(
            factory.CreatedPages.Skip(createdPageCount),
            page => Assert.Equal(1, factory.ReleaseCountFor(page)));
        await presenter.StartShutdown();
    }

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
            await presenter.AttachWindowAsync(window);
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
        await presenter.StartShutdown();
    }

    [Fact]
    public async Task FlyoutDetailFailureAfterMutationRestoresCachedBranchesAndSelection()
    {
        NavigationState previousState = BranchState("catalog", "catalog", "orders");
        NavigationState targetState = BranchState("orders", "catalog", "orders");
        var initialIcon = new FontImageSource { Glyph = "initial" };
        var targetIcon = new FontImageSource { Glyph = "target" };
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory(
            createPage: entry => new ContentPage
            {
                Title = entry.Id,
                IconImageSource = initialIcon
            },
            updatePage: (page, _, _) => page.IconImageSource = targetIcon);
        var options = new MauiRoutePresentationOptions();
        options.BranchHosts.Add(
            "main-tabs",
            new MauiBranchHostRegistration(new MauiFlyoutBranchHostFactory("Main", FlyoutLayoutBehavior.Default, true)));
        var presenter = new MauiNavigationPresenter(
            factory,
            presentationOptions: options,
            nativeOperations: nativeOperations);
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("catalog", NavigationState.Empty));
        var flyoutPage = Assert.IsType<MauiBranchFlyoutPage>(presenter.CurrentPage);
        Button[] menuButtons = FlyoutMenuButtons(flyoutPage);
        Assert.All(menuButtons, button => Assert.Same(initialIcon, button.ImageSource));
        NativePresentationSnapshot previousPresentation = CapturePresentation(presenter, null);
        Page[] previousPages = factory.CreatedPages.ToArray();
        nativeOperations.FaultAfterMutation = NativeMutation.SetFlyoutDetail;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(targetState),
            Context("orders", previousState)).AsTask());

        AssertPresentation(previousPresentation, presenter, null);
        Assert.Equal(menuButtons.Length, FlyoutMenuButtons(flyoutPage).Length);
        Assert.All(FlyoutMenuButtons(flyoutPage), button => Assert.Same(initialIcon, button.ImageSource));
        Assert.All(previousPages, page => Assert.Equal(0, factory.ReleaseCountFor(page)));
        await presenter.StartShutdown();
    }

    [Fact]
    public async Task FirstFlyoutDetailFailureRestoresInitialInfrastructureDetail()
    {
        NavigationState targetState = BranchState("catalog", "catalog", "orders");
        var branchHost = Assert.IsType<BranchHostNode>(targetState.ActiveWindow?.Root);
        var creationContext = new MauiBranchHostCreationContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            Context("catalog", NavigationState.Empty),
            new ServiceCollection().BuildServiceProvider());
        IMauiBranchHost host = await new MauiFlyoutBranchHostFactory("Main").CreateAsync(creationContext);
        var nativeOperations = new FaultingNativeOperations
        {
            FaultAfterMutation = NativeMutation.SetFlyoutDetail
        };
        Assert.IsAssignableFrom<IMauiBranchHostNativeOperations>(host).SetNativeOperations(nativeOperations);
        var flyoutPage = Assert.IsType<MauiBranchFlyoutPage>(host.Page);
        Page initialDetail = flyoutPage.Detail;

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ApplyAsync(
            new MauiBranchHostUpdateContext(
                branchHost,
                MauiBranchHostPlacement.WindowRoot,
                [
                    new MauiBranchHostBranch("catalog", "Catalog", new ContentPage()),
                    new MauiBranchHostBranch("orders", "Orders", new ContentPage())
                ],
                "catalog",
                creationContext.PresentationContext)).AsTask());

        Assert.Same(initialDetail, flyoutPage.Detail);
        Assert.Empty(host.Branches);
        Assert.Null(host.SelectedBranchId);
        Assert.Null(host.SelectedBranchPage);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task TabHostRollbackRestoresBranchPageChromeAndMetadata()
    {
        NavigationState targetState = BranchState("catalog", "catalog");
        var branchHost = Assert.IsType<BranchHostNode>(targetState.ActiveWindow?.Root);
        var page = new ContentPage { Title = "Original" };
        MauiPresentationMetadata.SetBranchId(page, "original-branch");
        var host = new MauiTabbedBranchHost();

        IMauiBranchHostUpdate update = await host.ApplyAsync(
            new MauiBranchHostUpdateContext(
                branchHost,
                MauiBranchHostPlacement.WindowRoot,
                [new MauiBranchHostBranch("catalog", "Catalog", page)],
                "catalog",
                Context("catalog", NavigationState.Empty)));
        Assert.Equal("Catalog", page.Title);
        Assert.Equal("catalog", MauiPresentationMetadata.GetBranchId(page));

        await update.RollbackAsync();

        Assert.Equal("Original", page.Title);
        Assert.Equal("original-branch", MauiPresentationMetadata.GetBranchId(page));
        Assert.Empty(host.Branches);
        await update.DisposeAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task TabHostApplyWithUnchangedTopologyDoesNotMutateTabs()
    {
        BranchHostNode branchHost = Assert.IsType<BranchHostNode>(BranchState("home", "home", "catalog").ActiveWindow?.Root);
        var home = new ContentPage();
        var catalog = new ContentPage();
        var branches = new MauiBranchHostBranch[]
        {
            new("home", "Home", home),
            new("catalog", "Catalog", catalog)
        };
        var nativeOperations = new FaultingNativeOperations();
        var host = new MauiTabbedBranchHost();
        Assert.IsAssignableFrom<IMauiBranchHostNativeOperations>(host).SetNativeOperations(nativeOperations);
        var context = Context("home", NavigationState.Empty);

        IMauiBranchHostUpdate initialUpdate = await host.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            branches,
            "home",
            context));
        await initialUpdate.CommitAsync();
        await initialUpdate.DisposeAsync();
        nativeOperations.InsertTabCount = 0;
        nativeOperations.RemoveTabCount = 0;
        Page[] initialChildren = Assert.IsType<TabbedPage>(host.Page).Children.ToArray();

        IMauiBranchHostUpdate unchangedUpdate = await host.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            branches,
            "catalog",
            context));
        await unchangedUpdate.CommitAsync();
        await unchangedUpdate.DisposeAsync();

        Assert.Equal(0, nativeOperations.InsertTabCount);
        Assert.Equal(0, nativeOperations.RemoveTabCount);
        Assert.Equal(initialChildren, Assert.IsType<TabbedPage>(host.Page).Children);
        Assert.Same(home, host.Branches[0].Page);
        Assert.Same(catalog, host.Branches[1].Page);
        await host.DisposeAsync();
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
        await presenter.AttachWindowAsync(originalWindow);
        var replacementPage = new ContentPage();
        var replacementWindow = new Window(replacementPage);
        nativeOperations.FailWindowPageAfterMutation(
            faultSourceWindow ? originalWindow : replacementWindow);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.AttachWindowAsync(replacementWindow).AsTask());

        Assert.Same(currentPage, originalWindow.Page);
        Assert.Same(replacementPage, replacementWindow.Page);
        Assert.Same(currentPage, presenter.CurrentPage);
        Assert.Same(originalWindow, presenter.AttachedWindow);
        Assert.Equal("main", presenter.AttachedWindowId);

        await presenter.StartShutdown();
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
        await presenter.AttachWindowAsync(window);
        nativeOperations.FailWindowPageAfterMutation(window);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.DetachWindowAsync(window).AsTask());

        Assert.Same(currentPage, window.Page);
        Assert.Same(currentPage, presenter.CurrentPage);
        Assert.Same(window, presenter.AttachedWindow);
        Assert.Equal("main", presenter.AttachedWindowId);

        await presenter.StartShutdown();
    }

    [Fact]
    public async Task EmptyStateVerificationRejectsStaleRouterOwnedWindowPage()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState previousState = StackState("home");
        await presenter.ApplyAsync(
            new NavigationPlan(previousState),
            Context("home", NavigationState.Empty));
        Page currentPage = Assert.IsAssignableFrom<Page>(presenter.CurrentPage);
        var window = new Window();
        await presenter.AttachWindowAsync(window);
        nativeOperations.IgnoreNextWindowPageMutation(window);
        var emptyState = new NavigationState([new WindowNode("main")], "main");

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(emptyState),
            Context("empty", previousState)).AsTask());

        Assert.Same(currentPage, presenter.CurrentPage);
        Assert.Same(currentPage, window.Page);
        Assert.Equal(0, factory.ReleaseCountFor(currentPage));
        await presenter.StartShutdown();
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
        await presenter.AttachWindowAsync(originalWindow);
        var replacementPage = new ContentPage();
        var replacementWindow = new Window(replacementPage);
        nativeOperations.FailWindowPageAfterMutation(replacementWindow, replacementWindow);

        MauiPresentationConsistencyException failure = await Assert.ThrowsAsync<MauiPresentationConsistencyException>(
            () => presenter.AttachWindowAsync(replacementWindow).AsTask());
        MauiPresentationConsistencyException subsequent = await Assert.ThrowsAsync<MauiPresentationConsistencyException>(
            () => presenter.AttachWindowAsync(new Window()).AsTask());

        Assert.Same(failure, subsequent);
        Assert.Same(currentPage, originalWindow.Page);
        Assert.Same(replacementPage, replacementWindow.Page);
        Assert.Same(originalWindow, presenter.AttachedWindow);
        Assert.Equal("main", presenter.AttachedWindowId);

        await presenter.StartShutdown();
    }

    [UIFact]
    public async Task ReplacementAttachmentWaitsForFailingPresentationRollback()
    {
        var factory = new GatedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        var originalPlaceholder = new ContentPage { Title = "original-placeholder" };
        var replacementPlaceholder = new ContentPage { Title = "replacement-placeholder" };
        var originalWindow = new Window(originalPlaceholder);
        var replacementWindow = new Window(replacementPlaceholder);
        await presenter.AttachWindowAsync(originalWindow);
        using var cancellation = new CancellationTokenSource();

        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home")),
            Context("home", NavigationState.Empty),
            cancellation.Token).AsTask();
        await factory.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Task attachReplacement = presenter.AttachWindowAsync(replacementWindow).AsTask();
        await Task.Yield();

        Assert.False(attachReplacement.IsCompleted);
        Assert.Same(originalWindow, presenter.AttachedWindow);
        Assert.Same(originalPlaceholder, originalWindow.Page);
        Assert.Same(replacementPlaceholder, replacementWindow.Page);

        cancellation.Cancel();
        factory.ReleaseCreate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        await attachReplacement.WaitAsync(TimeSpan.FromSeconds(5));

        Page failedPage = Assert.Single(factory.CreatedPages);
        Assert.Equal(1, factory.ReleaseCountFor(failedPage));
        Assert.Null(presenter.CurrentPage);
        Assert.Same(originalPlaceholder, originalWindow.Page);
        Assert.Same(replacementPlaceholder, replacementWindow.Page);
        Assert.Same(replacementWindow, presenter.AttachedWindow);

        await presenter.ApplyAsync(
            new NavigationPlan(StackState("recovered")),
            Context("recovered", NavigationState.Empty));

        Assert.Same(presenter.CurrentPage, replacementWindow.Page);
        Assert.Same(originalPlaceholder, originalWindow.Page);
        await presenter.StartShutdown();
    }

    [UIFact]
    public async Task ReplacementAttachmentWaitsForSuccessfulPresentationCommit()
    {
        var factory = new GatedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        var originalWindow = new Window(new ContentPage { Title = "original-placeholder" });
        var replacementWindow = new Window(new ContentPage { Title = "replacement-placeholder" });
        await presenter.AttachWindowAsync(originalWindow);

        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home")),
            Context("home", NavigationState.Empty)).AsTask();
        await factory.CreateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Task attachReplacement = presenter.AttachWindowAsync(replacementWindow).AsTask();
        await Task.Yield();

        Assert.False(attachReplacement.IsCompleted);
        Assert.Same(originalWindow, presenter.AttachedWindow);

        factory.ReleaseCreate();
        await apply.WaitAsync(TimeSpan.FromSeconds(5));
        await attachReplacement.WaitAsync(TimeSpan.FromSeconds(5));

        Page committedRoot = Assert.IsAssignableFrom<Page>(presenter.CurrentPage);
        Assert.Null(originalWindow.Page);
        Assert.Same(committedRoot, replacementWindow.Page);
        Assert.Same(replacementWindow, presenter.AttachedWindow);
        Assert.Empty(factory.ReleasedPages);
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
    }

    [Fact]
    public async Task DirectPushRollbackFailureRebuildsBranchHostWithRetainedPresentationContext()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var creationPlacements = new List<MauiBranchHostPlacement>();
        var presentationOptions = new MauiRoutePresentationOptions();
        presentationOptions.BranchHosts.Add(
            "main-tabs",
            new MauiBranchHostRegistration(new MauiTabbedBranchHostFactory(context =>
            {
                creationPlacements.Add(context.Placement);
                return new TabbedPage();
            })));
        var presenter = new MauiNavigationPresenter(
            factory,
            presentationOptions: presentationOptions,
            nativeOperations: nativeOperations);
        NavigationState previousState = BranchState("catalog", "catalog", "orders");
        await presenter.ApplyAsync(new NavigationPlan(previousState), Context("catalog", NavigationState.Empty));
        var previousRoot = Assert.IsType<TabbedPage>(presenter.CurrentPage);
        nativeOperations.FaultAfterMutation = NativeMutation.PushStack;
        nativeOperations.PopFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter
            .PushAsync<TestPresentationPage>(
                "settings",
                new MauiRoutePresentationPageOptions { Animated = false })
            .AsTask());

        var recoveredRoot = Assert.IsType<TabbedPage>(presenter.CurrentPage);
        Assert.NotSame(previousRoot, recoveredRoot);
        Assert.Equal(["catalog", "orders"], recoveredRoot.Children
            .Select(page => Assert.IsType<string>(MauiPresentationMetadata.GetBranchId(page)))
            .ToArray());
        Assert.Equal("catalog", MauiPresentationMetadata.GetBranchId(recoveredRoot.CurrentPage));
        Assert.Equal(
            [MauiBranchHostPlacement.WindowRoot, MauiBranchHostPlacement.WindowRoot],
            creationPlacements);
        Assert.Equal(1, factory.ReleaseCountFor(Assert.Single(factory.CreatedPresentationPages)));
        await presenter.StartShutdown();
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

        Task shutdown = presenter.StartShutdown();
        Assert.False(shutdown.IsCompleted);
        WaitForShutdownCancellationRequested(presenter);
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
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
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
        await presenter.StartShutdown();
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

        Task shutdown = presenter.StartShutdown();
        Assert.False(shutdown.IsCompleted);
        WaitForShutdownCancellationRequested(presenter);
        nativeOperations.ReleaseBlockedPush();

        Exception cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.IsNotType<ObjectDisposedException>(cancellation);
        await shutdown;
        Assert.False(cleanupRan);
        Assert.All(factory.CreatedPages, page => Assert.Equal(1, factory.ReleaseCountFor(page)));
    }

    [UIFact]
    public async Task PlannedPopThenExternalReplacementPopFoldsIntoEffectiveStateAndReleasesOnce()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState initialState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(initialState), Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page detailPage = navigationPage.Navigation.NavigationStack[^1];
        var reconciliations = new List<NavigationReconciliation>();
        var reconciliationPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.ReconciliationRequested += (_, args) =>
        {
            reconciliations.Add(args.Reconciliation);
            reconciliationPublished.TrySetResult();
        };
        nativeOperations.BlockNextPushAfterMutation = true;

        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "settings")),
            Context("settings", initialState)).AsTask();
        await nativeOperations.BlockedPushAfterMutationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Page settingsPage = navigationPage.Navigation.NavigationStack[^1];

        await MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated: false);
        nativeOperations.ReleaseBlockedPushAfterMutation();

        await apply.WaitAsync(TimeSpan.FromSeconds(5));
        await reconciliationPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        NavigationReconciliation reconciliation = Assert.Single(reconciliations);
        var effectiveStack = Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal(["home"], effectiveStack.Entries.Select(entry => entry.Id));
        Assert.Equal(["home"], NativeRouteEntryIds(navigationPage));
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.Equal(1, factory.ReleaseCountFor(detailPage));
        Assert.Equal(1, factory.ReleaseCountFor(settingsPage));

        await presenter.StartShutdown();
        Assert.Equal(1, factory.ReleaseCountFor(detailPage));
        Assert.Equal(1, factory.ReleaseCountFor(settingsPage));
    }

    [UIFact]
    public async Task SuppressedPopToRootCoalescesToOneHostBackAndReleasesEveryRemovedPageOnce()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState initialState = StackState("home", "catalog", "detail");
        await presenter.ApplyAsync(new NavigationPlan(initialState), Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] existingPages = navigationPage.Navigation.NavigationStack.Skip(1).ToArray();
        var reconciliations = new List<NavigationReconciliation>();
        var reconciliationPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.ReconciliationRequested += (_, args) =>
        {
            reconciliations.Add(args.Reconciliation);
            reconciliationPublished.TrySetResult();
        };
        nativeOperations.BlockNextPushAfterMutation = true;

        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "catalog", "detail", "receipt")),
            Context("receipt", initialState)).AsTask();
        await nativeOperations.BlockedPushAfterMutationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Page receiptPage = navigationPage.Navigation.NavigationStack[^1];

        await navigationPage.Navigation.PopToRootAsync(animated: false);
        nativeOperations.ReleaseBlockedPushAfterMutation();

        await apply.WaitAsync(TimeSpan.FromSeconds(5));
        await reconciliationPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        NavigationReconciliation reconciliation = Assert.Single(reconciliations);
        var effectiveStack = Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal(["home"], effectiveStack.Entries.Select(entry => entry.Id));
        Assert.Equal(["home"], NativeRouteEntryIds(navigationPage));
        Assert.All(existingPages, page => Assert.Equal(1, factory.ReleaseCountFor(page)));
        Assert.Equal(1, factory.ReleaseCountFor(receiptPage));

        await presenter.StartShutdown();
        Assert.All(existingPages, page => Assert.Equal(1, factory.ReleaseCountFor(page)));
        Assert.Equal(1, factory.ReleaseCountFor(receiptPage));
    }

    [UIFact]
    public async Task DirectPresentationPushAndPopDoNotPublishHostBackOrDoubleRelease()
    {
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        NavigationState state = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(state), Context("detail", NavigationState.Empty));
        var reconciliations = new List<NavigationReconciliation>();
        presenter.ReconciliationRequested += (_, args) => reconciliations.Add(args.Reconciliation);

        await presenter.PushAsync<TestPresentationPage>(
            "settings",
            new MauiRoutePresentationPageOptions { Animated = false });
        Page presentationPage = Assert.Single(factory.CreatedPresentationPages);

        Assert.True(await presenter.PopAsync(animated: false));

        Assert.Empty(reconciliations);
        Assert.Equal(1, factory.ReleaseCountFor(presentationPage));
        await presenter.StartShutdown();
        Assert.Empty(reconciliations);
        Assert.Equal(1, factory.ReleaseCountFor(presentationPage));
    }

    [UIFact]
    public async Task RoutePopDuringCommitIsDrainedAfterSuppressionAndPublishesOneHostBack()
    {
        var factory = new CommitPopRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        NavigationState initialState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(initialState), Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page detailPage = navigationPage.Navigation.NavigationStack[^1];
        factory.PopTopWhenReleased(detailPage, navigationPage);
        var reconciliations = new List<NavigationReconciliation>();
        var reconciliationPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.ReconciliationRequested += (_, args) =>
        {
            reconciliations.Add(args.Reconciliation);
            reconciliationPublished.TrySetResult();
        };

        await presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "settings")),
            Context("settings", initialState));
        await reconciliationPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Page settingsPage = factory.CreatedPages[^1];
        NavigationReconciliation reconciliation = Assert.Single(reconciliations);
        var effectiveStack = Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal(["home"], effectiveStack.Entries.Select(entry => entry.Id));
        Assert.Equal(["home"], NativeRouteEntryIds(navigationPage));
        Assert.Equal(1, factory.ReleaseCountFor(detailPage));
        Assert.Equal(1, factory.ReleaseCountFor(settingsPage));

        await presenter.StartShutdown();
        Assert.Equal(1, factory.ReleaseCountFor(detailPage));
        Assert.Equal(1, factory.ReleaseCountFor(settingsPage));
    }

    [UIFact]
    public async Task FailedApplyDiscardsSuppressedPopRecordsAfterRollback()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState initialState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(initialState), Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page[] previousPages = navigationPage.Navigation.NavigationStack.ToArray();
        var reconciliations = new List<NavigationReconciliation>();
        presenter.ReconciliationRequested += (_, args) => reconciliations.Add(args.Reconciliation);
        nativeOperations.BlockNextPushAfterMutation = true;
        nativeOperations.FaultAfterMutation = NativeMutation.PushStack;

        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "settings")),
            Context("settings", initialState)).AsTask();
        await nativeOperations.BlockedPushAfterMutationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated: false);
        nativeOperations.ReleaseBlockedPushAfterMutation();

        await Assert.ThrowsAsync<InvalidOperationException>(() => apply);

        Assert.Equal(previousPages, navigationPage.Navigation.NavigationStack.ToArray());
        Assert.Empty(reconciliations);
        Assert.All(previousPages, page => Assert.Equal(0, factory.ReleaseCountFor(page)));
        Page failedReplacement = factory.CreatedPages[^1];
        Assert.Equal(1, factory.ReleaseCountFor(failedReplacement));
        await presenter.StartShutdown();
        Assert.Empty(reconciliations);
        Assert.Equal(1, factory.ReleaseCountFor(failedReplacement));
    }

    [UIFact]
    public async Task DisposalWhileSuppressedExternalPopIsPendingReleasesEveryPageOnce()
    {
        var nativeOperations = new FaultingNativeOperations();
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory, nativeOperations: nativeOperations);
        NavigationState initialState = StackState("home", "detail");
        await presenter.ApplyAsync(new NavigationPlan(initialState), Context("detail", NavigationState.Empty));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        var reconciliations = new List<NavigationReconciliation>();
        presenter.ReconciliationRequested += (_, args) => reconciliations.Add(args.Reconciliation);
        nativeOperations.BlockNextPushAfterMutation = true;

        Task apply = presenter.ApplyAsync(
            new NavigationPlan(StackState("home", "detail", "settings")),
            Context("settings", initialState)).AsTask();
        await nativeOperations.BlockedPushAfterMutationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated: false);

        Task shutdown = presenter.StartShutdown();
        WaitForShutdownCancellationRequested(presenter);
        nativeOperations.ReleaseBlockedPushAfterMutation();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(reconciliations);
        Assert.All(factory.CreatedPages, page => Assert.Equal(1, factory.ReleaseCountFor(page)));
    }

    [UIFact]
    public async Task RealRouterNavigatorProcessesSuppressedHostBackWithoutDeadlock()
    {
        var nativeOperations = new FaultingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: nativeOperations);
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/tests/{id}"));
        var navigator = new RouterNavigator(routes, new StackPlanner(), presenter);
        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoute("detail"),
            NavigationRequestSource.Test));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        var reconciliationPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.ReconciliationRequested += (_, _) => reconciliationPublished.TrySetResult();
        nativeOperations.BlockNextPushAfterMutation = true;

        Task navigation = navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoute("settings"),
            NavigationRequestSource.Test)).AsTask();
        await nativeOperations.BlockedPushAfterMutationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated: false);
        nativeOperations.ReleaseBlockedPushAfterMutation();

        await navigation.WaitAsync(TimeSpan.FromSeconds(5));
        await reconciliationPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await navigator.WhenReconciliationIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var reconciledStack = Assert.IsType<StackNode>(navigator.CurrentState.ActiveWindow?.Root);
        Assert.Equal(["home", "detail"], reconciledStack.Entries.Select(entry => entry.Id));
        Assert.Equal(["home", "detail"], NativeRouteEntryIds(navigationPage));

        await navigator.DisposeAsync();
        await presenter.StartShutdown();
    }

    private static string[] NativeRouteEntryIds(NavigationPage navigationPage)
    {
        return navigationPage.Navigation.NavigationStack
            .Select(page => Assert.IsType<string>(MauiPresentationMetadata.GetRouteEntryId(page)))
            .ToArray();
    }

    private static void WaitForShutdownCancellationRequested(MauiNavigationPresenter presenter)
    {
        FieldInfo field = typeof(MauiNavigationPresenter).GetField(
            "_shutdownCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Shutdown cancellation source was not found.");
        var cancellation = Assert.IsType<CancellationTokenSource>(field.GetValue(presenter));
        Assert.True(SpinWait.SpinUntil(
            () => cancellation.IsCancellationRequested,
            TimeSpan.FromSeconds(5)));
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

    private static Button[] FlyoutMenuButtons(MauiBranchFlyoutPage flyoutPage)
    {
        var menuPage = Assert.IsType<ContentPage>(flyoutPage.Flyout);
        var scrollView = Assert.IsType<ScrollView>(menuPage.Content);
        var menuItems = Assert.IsType<VerticalStackLayout>(scrollView.Content);
        return menuItems.Children.Select(Assert.IsType<Button>).ToArray();
    }

    private static NativePageSnapshot CapturePage(Page page)
    {
        Page[] children = page switch
        {
            NavigationPage navigationPage => navigationPage.Navigation.NavigationStack.ToArray(),
            TabbedPage tabbedPage => tabbedPage.Children.ToArray(),
            MauiBranchFlyoutPage flyoutPage => flyoutPage.Branches.Select(static branch => branch.Page).ToArray(),
            _ => []
        };
        Page? currentPage = page switch
        {
            NavigationPage navigationPage => navigationPage.CurrentPage,
            TabbedPage tabbedPage => tabbedPage.CurrentPage,
            MauiBranchFlyoutPage flyoutPage => flyoutPage.Detail,
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
            MauiBranchFlyoutPage flyoutPage => flyoutPage.Branches.Select(static branch => branch.Page).ToArray(),
            _ => []
        };
        Page? actualCurrentPage = actual switch
        {
            NavigationPage navigationPage => navigationPage.CurrentPage,
            TabbedPage tabbedPage => tabbedPage.CurrentPage,
            MauiBranchFlyoutPage flyoutPage => flyoutPage.Detail,
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

    private sealed class StackPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var route = Assert.IsType<TestRoute>(context.Route);
            string[] entryIds = route.Id switch
            {
                "detail" => ["home", "detail"],
                "settings" => ["home", "detail", "settings"],
                _ => ["home"]
            };
            return ValueTask.FromResult(new NavigationPlan(StackState(entryIds)));
        }
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
        SetFlyoutDetail,
        SetFlyoutPresented,
        SetWindowPage
    }

    private sealed class CommitPopRoutePageFactory : IMauiRoutePageFactory
    {
        public MauiPageAbandonment? CaptureAbandonment(Page page) => null;

        private readonly InstrumentedRoutePageFactory _inner = new();
        private Page? _releaseTrigger;
        private NavigationPage? _navigationPage;

        public IReadOnlyList<Page> CreatedPages => _inner.CreatedPages;

        public void PopTopWhenReleased(Page releaseTrigger, NavigationPage navigationPage)
        {
            _releaseTrigger = releaseTrigger;
            _navigationPage = navigationPage;
        }

        public ValueTask<Page> CreatePageAsync(
            RouteEntry entry,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreatePageAsync(entry, cancellationToken);
        }

        public ValueTask<Page> CreatePresentationPageAsync(
            Type pageType,
            Page ownerRoutePage,
            bool inheritBindingContext,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreatePresentationPageAsync(
                pageType,
                ownerRoutePage,
                inheritBindingContext,
                cancellationToken);
        }

        public ValueTask UpdatePageAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            return _inner.UpdatePageAsync(page, entry, context, cancellationToken);
        }

        public async ValueTask ReleasePageAsync(Page page)
        {
            await _inner.ReleasePageAsync(page);
            if (!ReferenceEquals(page, _releaseTrigger) || _navigationPage is null)
                return;

            _releaseTrigger = null;
            await _navigationPage.Navigation.PopAsync(animated: false);
        }

        public ValueTask ReleasePresentationPageAsync(Page page)
        {
            return _inner.ReleasePresentationPageAsync(page);
        }

        public int ReleaseCountFor(Page page) => _inner.ReleaseCountFor(page);
    }

    private sealed class GatedRoutePageFactory : IMauiRoutePageFactory
    {
        public MauiPageAbandonment? CaptureAbandonment(Page page) => null;

        private readonly InstrumentedRoutePageFactory _inner = new();
        private readonly TaskCompletionSource _createStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCreate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _gateNextCreate = true;

        public Task CreateStarted => _createStarted.Task;

        public IReadOnlyList<Page> CreatedPages => _inner.CreatedPages;

        public IReadOnlyList<Page> ReleasedPages => _inner.ReleasedPages;

        public void ReleaseCreate()
        {
            _releaseCreate.TrySetResult();
        }

        public async ValueTask<Page> CreatePageAsync(
            RouteEntry entry,
            CancellationToken cancellationToken = default)
        {
            if (_gateNextCreate)
            {
                _gateNextCreate = false;
                _createStarted.TrySetResult();
                await _releaseCreate.Task;
            }

            return await _inner.CreatePageAsync(entry, cancellationToken);
        }

        public ValueTask<Page> CreatePresentationPageAsync(
            Type pageType,
            Page ownerRoutePage,
            bool inheritBindingContext,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreatePresentationPageAsync(
                pageType,
                ownerRoutePage,
                inheritBindingContext,
                cancellationToken);
        }

        public ValueTask UpdatePageAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            return _inner.UpdatePageAsync(page, entry, context, cancellationToken);
        }

        public ValueTask ReleasePageAsync(Page page)
        {
            return _inner.ReleasePageAsync(page);
        }

        public ValueTask ReleasePresentationPageAsync(Page page)
        {
            return _inner.ReleasePresentationPageAsync(page);
        }

        public int ReleaseCountFor(Page page)
        {
            return _inner.ReleaseCountFor(page);
        }
    }

    private sealed class FaultingNativeOperations : IMauiNativeNavigationOperations
    {
        private readonly TaskCompletionSource _blockedPushStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseBlockedPush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blockedPushAfterMutationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseBlockedPushAfterMutation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<Window> _windowPageNoOpTargets = new();
        private readonly Queue<Window> _windowPageFaultTargets = new();
        private int _insertTabCallsUntilNoOp;
        private int _removeTabCallsUntilNoOp;

        public int PushFailuresRemaining { get; set; }

        public int PopFailuresRemaining { get; set; }

        public int PopModalFailuresRemaining { get; set; }

        public bool AlwaysFailPush { get; set; }

        public bool BlockNextPush { get; set; }

        public bool BlockNextPushAfterMutation { get; set; }

        public NativeMutation? FaultAfterMutation { get; set; }

        public bool IgnoreNextSelectedFlyoutBranchMutation { get; set; }

        public int InsertTabCount { get; set; }

        public int RemoveTabCount { get; set; }

        public Task BlockedPushStarted => _blockedPushStarted.Task;

        public Task BlockedPushAfterMutationStarted => _blockedPushAfterMutationStarted.Task;

        public void FailWindowPageAfterMutation(params Window[] windows)
        {
            foreach (Window window in windows)
                _windowPageFaultTargets.Enqueue(window);
        }

        public void IgnoreNextWindowPageMutation(Window window)
        {
            _windowPageNoOpTargets.Enqueue(window);
        }

        public void IgnoreInsertTabMutation(int callsUntilNoOp) =>
            _insertTabCallsUntilNoOp = callsUntilNoOp;

        public void IgnoreRemoveTabMutation(int callsUntilNoOp) =>
            _removeTabCallsUntilNoOp = callsUntilNoOp;

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
            if (BlockNextPushAfterMutation)
            {
                BlockNextPushAfterMutation = false;
                _blockedPushAfterMutationStarted.TrySetResult();
                await _releaseBlockedPushAfterMutation.Task;
            }

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
            if (PopModalFailuresRemaining > 0)
            {
                PopModalFailuresRemaining--;
                throw new InvalidOperationException("Injected native modal pop failure.");
            }

            Page? page = await MauiNativeNavigationOperations.Instance.PopModalAsync(host, animated);
            ThrowAfterMutation(NativeMutation.PopModal);
            return page;
        }

        public void InsertTab(TabbedPage tabbedPage, int index, Page page)
        {
            InsertTabCount++;
            if (_insertTabCallsUntilNoOp > 0 && --_insertTabCallsUntilNoOp == 0)
                return;

            MauiNativeNavigationOperations.Instance.InsertTab(tabbedPage, index, page);
            ThrowAfterMutation(NativeMutation.InsertTab);
        }

        public void RemoveTab(TabbedPage tabbedPage, Page page)
        {
            RemoveTabCount++;
            if (_removeTabCallsUntilNoOp > 0 && --_removeTabCallsUntilNoOp == 0)
                return;

            MauiNativeNavigationOperations.Instance.RemoveTab(tabbedPage, page);
            ThrowAfterMutation(NativeMutation.RemoveTab);
        }

        public void SetCurrentTab(TabbedPage tabbedPage, Page? page)
        {
            MauiNativeNavigationOperations.Instance.SetCurrentTab(tabbedPage, page);
            ThrowAfterMutation(NativeMutation.SetCurrentTab);
        }

        public void SetFlyoutDetail(FlyoutPage flyoutPage, Page? page)
        {
            MauiNativeNavigationOperations.Instance.SetFlyoutDetail(flyoutPage, page);
            ThrowAfterMutation(NativeMutation.SetFlyoutDetail);
        }

        public void SetFlyoutPresented(FlyoutPage flyoutPage, bool isPresented)
        {
            MauiNativeNavigationOperations.Instance.SetFlyoutPresented(flyoutPage, isPresented);
            ThrowAfterMutation(NativeMutation.SetFlyoutPresented);
        }

        public void SetFlyoutBranches(
            MauiBranchFlyoutPage flyoutPage,
            IReadOnlyList<MauiFlyoutBranchPresentation> branches) =>
            MauiNativeNavigationOperations.Instance.SetFlyoutBranches(flyoutPage, branches);

        public void SetSelectedFlyoutBranch(MauiBranchFlyoutPage flyoutPage, string? branchId)
        {
            if (IgnoreNextSelectedFlyoutBranchMutation)
            {
                IgnoreNextSelectedFlyoutBranchMutation = false;
                return;
            }

            MauiNativeNavigationOperations.Instance.SetSelectedFlyoutBranch(flyoutPage, branchId);
        }

        public void SetWindowPage(Window window, Page? page)
        {
            if (_windowPageNoOpTargets.TryPeek(out Window? noOpTarget) && ReferenceEquals(noOpTarget, window))
            {
                _windowPageNoOpTargets.Dequeue();
                return;
            }

            MauiNativeNavigationOperations.Instance.SetWindowPage(window, page);
            if (_windowPageFaultTargets.TryPeek(out Window? faultTarget) && ReferenceEquals(faultTarget, window))
            {
                _windowPageFaultTargets.Dequeue();
                throw new InvalidOperationException("Injected SetWindowPage failure after mutation.");
            }

            ThrowAfterMutation(NativeMutation.SetWindowPage);
        }

        public void ReleaseBlockedPush() => _releaseBlockedPush.TrySetResult();

        public void ReleaseBlockedPushAfterMutation() => _releaseBlockedPushAfterMutation.TrySetResult();

        private void ThrowAfterMutation(NativeMutation mutation)
        {
            if (FaultAfterMutation != mutation)
                return;

            FaultAfterMutation = null;
            throw new InvalidOperationException($"Injected {mutation} failure after mutation.");
        }
    }

    private sealed class TrackingBranchHostFactory : IMauiBranchHostFactory
    {
        public MauiBranchHostPlacement SupportedPlacements => MauiBranchHostPlacement.All;

        public List<TrackingBranchHostUpdate> Updates { get; } = [];

        public ValueTask<IMauiBranchHost> CreateAsync(
            MauiBranchHostCreationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IMauiBranchHost>(new TrackingBranchHost(this));
        }
    }

    private sealed class TrackingBranchHost(TrackingBranchHostFactory factory) : IMauiBranchHost
    {
        private IReadOnlyList<MauiBranchHostBranch> _branches = [];

        public Page Page { get; } = new ContentPage();

        public IReadOnlyList<MauiBranchHostBranch> Branches => _branches;

        public string? SelectedBranchId { get; private set; }

        public Page? SelectedBranchPage => _branches.FirstOrDefault(branch =>
            StringComparer.Ordinal.Equals(branch.Id, SelectedBranchId))?.Page;

        public event EventHandler<MauiBranchHostSelectionChangedEventArgs>? SelectionChanged
        {
            add { }
            remove { }
        }

        public ValueTask<IMauiBranchHostUpdate> ApplyAsync(
            MauiBranchHostUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MauiBranchHostBranch> previousBranches = _branches;
            string? previousSelectedBranchId = SelectedBranchId;
            _branches = context.Branches.ToArray();
            SelectedBranchId = context.SelectedBranchId;
            var update = new TrackingBranchHostUpdate(
                this,
                previousBranches,
                previousSelectedBranchId);
            factory.Updates.Add(update);
            return ValueTask.FromResult<IMauiBranchHostUpdate>(update);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Restore(
            IReadOnlyList<MauiBranchHostBranch> branches,
            string? selectedBranchId)
        {
            _branches = branches;
            SelectedBranchId = selectedBranchId;
        }
    }

    private sealed class TrackingBranchHostUpdate(
        TrackingBranchHost host,
        IReadOnlyList<MauiBranchHostBranch> previousBranches,
        string? previousSelectedBranchId) : IMauiBranchHostUpdate
    {
        private bool _committed;

        public int RollbackCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _committed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackCount++;
            if (!_committed)
                host.Restore(previousBranches, previousSelectedBranchId);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPresentationPage : ContentPage;
}
