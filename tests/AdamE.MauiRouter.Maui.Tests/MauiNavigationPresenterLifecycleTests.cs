using System.Reflection;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Maui.DependencyInjection;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiNavigationPresenterLifecycleTests
{
    [Fact]
    public async Task DisposeReleasesCurrentStackPagesAndDetachesHandlersOnce()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"))),
            Context(new TestPageRoute("school-riverside")));

        var routePages = fixture.Factory.CreatedPages.ToArray();
        Assert.Equal(2, routePages.Length);

        fixture.Presenter.Dispose();
        fixture.Presenter.Dispose();

        Assert.All(routePages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));
        Assert.Equal(2, fixture.Factory.ReleasedPages.Count);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Contains(fixture.Observer.Events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationHandlerDetached &&
            diagnosticEvent.Data[NavigationDiagnosticDataKeys.HandlerName]?.ToString() == "NavigationPage.Popped/PoppedToRoot");
        Assert.Contains(fixture.Observer.Events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationPresenterDisposed);
    }

    [Fact]
    public async Task DetachWindowKeepsCurrentPageAlive()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        var currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var window = new Window();

        fixture.Presenter.AttachWindow(window);
        fixture.Presenter.DetachWindow(window);

        Assert.Same(currentPage, fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.ReleasedPages);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task PresentationStateTracksRootPageAndAttachedWindow()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        Assert.Null(presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        var rootPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var window = new Window();

        fixture.Presenter.AttachWindow(window, "secondary");

        Assert.Same(rootPage, presentationState.RootPage);
        Assert.Same(rootPage, fixture.Presenter.CurrentPage);
        Assert.Same(window, presentationState.AttachedWindow);
        Assert.Equal("secondary", presentationState.AttachedWindowId);

        fixture.Presenter.DetachWindow(window);

        Assert.Same(rootPage, presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);

        fixture.Presenter.Dispose();

        Assert.Null(presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);
    }

    [Fact]
    public void AttachWindowSameWindowDoesNotDuplicateLifecycleHandlers()
    {
        var fixture = new PresenterFixture();
        var window = new Window();

        fixture.Presenter.AttachWindow(window);
        fixture.Presenter.AttachWindow(window);
        fixture.Presenter.DetachWindow(window);

        Assert.Equal(0, EventHandlerCount(window, "Activated"));
        Assert.Equal(0, EventHandlerCount(window, "Deactivated"));
        Assert.Equal(0, EventHandlerCount(window, "Stopped"));
        Assert.Equal(0, EventHandlerCount(window, "Resumed"));
        Assert.Equal(0, EventHandlerCount(window, "Destroying"));

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task EmptyStateClearsCurrentPageAndReleasesPreviousTree()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"))),
            Context(new TestPageRoute("school-riverside")));

        var routePages = fixture.Factory.CreatedPages.ToArray();

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(NavigationState.Empty),
            Context(new TestPageRoute("empty"), fixture.PresenterState));

        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.All(routePages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));
    }

    [Fact]
    public async Task GetTopPresentedPageReturnsLeafPageForNavigationStack()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);

        Assert.Same(navigationPage.CurrentPage, presentationState.GetTopPresentedPage());

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task GetTopPresentedPageReturnsLeafPageForSelectedTabBranch()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        var tabs = new TabsNode(
            "store-tabs",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "catalog",
            "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(tabs),
            Context(new TestPageRoute("catalog")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var selectedBranch = Assert.IsType<NavigationPage>(tabbedPage.CurrentPage);

        Assert.Same(selectedBranch.CurrentPage, presentationState.GetTopPresentedPage());

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task GetTopPresentedPageReturnsLeafPageForFlyoutDetailBranch()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        var branches = new[]
        {
            new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
            new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
        };
        var flyout = new FlyoutNode("store-flyout", branches, "catalog", "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(flyout),
            Context(new TestPageRoute("catalog")));

        var flyoutPage = Assert.IsType<FlyoutPage>(fixture.Presenter.CurrentPage);
        var detailPage = Assert.IsType<NavigationPage>(flyoutPage.Detail);

        Assert.Same(detailPage.CurrentPage, presentationState.GetTopPresentedPage());

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task GetTopPresentedPagePrefersTopModalPage()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;
        var root = Stack("schools", Entry("schools"));
        var stateWithModal = new NavigationState(
            new[] { new WindowNode("main", root, new[] { new ModalNode("cart-modal", Entry("cart-modal")) }) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(navigationPage.Navigation.ModalStack);

        Assert.Same(modalPage, presentationState.GetTopPresentedPage());

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task RootlessWindowWithModalUsesSyntheticHostAndPresentsModal()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;
        var state = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    null,
                    new[]
                    {
                        new ModalNode("cart-modal", Entry("cart-modal"))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(state),
            Context(new TestPageRoute("cart-modal")));

        var rootPage = Assert.IsType<ContentPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(rootPage.Navigation.ModalStack);
        Assert.NotSame(rootPage, modalPage);
        Assert.Same(modalPage, presentationState.GetTopPresentedPage());

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReplacingStackRootReleasesOldStackPages()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"))),
            Context(new TestPageRoute("school-riverside")));

        var firstStackPages = fixture.Factory.CreatedPages.ToArray();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("account", Entry("account"))),
            Context(new TestPageRoute("account"), fixture.PresenterState));

        Assert.All(firstStackPages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));
        Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReusedStackPageReceivesUpdatedRouteEntryWithoutRecreation()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("play", new RouteEntry("play-root", new TestPageRoute("hub")))),
            Context(new TestPageRoute("hub")));

        var firstNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedPage = Assert.Single(firstNavigationPage.Navigation.NavigationStack);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("play", new RouteEntry("play-root", new TestPageRoute("missions")))),
            Context(new TestPageRoute("missions"), fixture.PresenterState));

        var secondNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedPage, Assert.Single(secondNavigationPage.Navigation.NavigationStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedPage));
        Assert.Equal("missions", Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedPage)!.Route).Name);
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ExplicitTarget, fixture.Factory.LastUpdateContextFor(retainedPage)?.ReuseKind);
        Assert.Single(fixture.Factory.CreatedPages);
        Assert.Empty(fixture.Factory.ReleasedPages);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task RetainedParentStackPageIsNotMarkedAsNavigationTargetWhenChildIsPushed()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("play", new RouteEntry("play-root", new TestPageRoute("hub")))),
            Context(new TestPageRoute("hub")));

        var firstNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedPage = Assert.Single(firstNavigationPage.Navigation.NavigationStack);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack(
                "play",
                new RouteEntry("play-root", new TestPageRoute("hub")),
                Entry("mission-detail"))),
            Context(new TestPageRoute("mission-detail"), fixture.PresenterState));

        var secondNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedPage, secondNavigationPage.Navigation.NavigationStack[0]);
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedPage));
        Assert.False(fixture.Factory.LastUpdateContextFor(retainedPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.NonTargetReuse, fixture.Factory.LastUpdateContextFor(retainedPage)?.ReuseKind);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReusedTopStackPageIsMarkedAsResurfacedTargetWhenPageAboveItIsPopped()
    {
        var fixture = new PresenterFixture();
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    Stack("play", new RouteEntry("play-root", new TestPageRoute("hub")), Entry("mission-detail")))
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    Stack("play", new RouteEntry("play-root", new TestPageRoute("hub"))))
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("mission-detail"), NavigationState.Empty));

        var initialNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedPage = initialNavigationPage.Navigation.NavigationStack[0];

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("hub"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedPage, Assert.Single(updatedNavigationPage.Navigation.NavigationStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedPage));
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ResurfacedTarget, fixture.Factory.LastUpdateContextFor(retainedPage)?.ReuseKind);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReusedTopModalPageIsMarkedAsNavigationTarget()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-updated")))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart"), NavigationState.Empty));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = Assert.Single(navigationPage.Navigation.ModalStack);

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart-updated"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedModalPage, Assert.Single(updatedNavigationPage.Navigation.ModalStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedModalPage));
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedModalPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ExplicitTarget, fixture.Factory.LastUpdateContextFor(retainedModalPage)?.ReuseKind);
        Assert.Equal("cart-updated", Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedModalPage)!.Route).Name);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReusedModalIdWithDifferentRouteEntryIdRebuildsRouteOnlyModalPage()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-v1", new TestPageRoute("cart")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-v2", new TestPageRoute("cart-updated")))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart"), NavigationState.Empty));

        var initialNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var initialModalPage = Assert.Single(initialNavigationPage.Navigation.ModalStack);

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart-updated"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var updatedModalPage = Assert.Single(updatedNavigationPage.Navigation.ModalStack);
        Assert.NotSame(initialModalPage, updatedModalPage);
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(initialModalPage));

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReusedTopModalPageIsMarkedAsResurfacedTargetWhenModalAboveItIsDismissed()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart"))),
                        new ModalNode(
                            "detail-modal",
                            new RouteEntry("detail-modal-route", new TestPageRoute("detail")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart")))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("detail"), NavigationState.Empty));

        var initialNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = initialNavigationPage.Navigation.ModalStack[0];

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedModalPage, Assert.Single(updatedNavigationPage.Navigation.ModalStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedModalPage));
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedModalPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ResurfacedTarget, fixture.Factory.LastUpdateContextFor(retainedModalPage)?.ReuseKind);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReusedModalContentRootIsReconciledInPlaceWhenCompatibilityMatches()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack(
                                "cart-stack",
                                new RouteEntry("cart-root", new TestPageRoute("cart")),
                                new RouteEntry("cart-detail", new TestPageRoute("cart-detail"))))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack(
                                "cart-stack",
                                new RouteEntry("cart-root", new TestPageRoute("cart")),
                                new RouteEntry("cart-detail", new TestPageRoute("cart-detail-updated"))))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart-detail"), NavigationState.Empty));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var retainedDetailPage = retainedModalPage.Navigation.NavigationStack[1];

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart-detail-updated"), initialState));

        var updatedRootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var updatedModalPage = Assert.IsType<NavigationPage>(Assert.Single(updatedRootNavigationPage.Navigation.ModalStack));
        Assert.Same(retainedModalPage, updatedModalPage);
        Assert.Same(retainedDetailPage, updatedModalPage.Navigation.NavigationStack[1]);
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedDetailPage));
        Assert.Equal(
            "cart-detail-updated",
            Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedDetailPage)!.Route).Name);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ReusedModalIdWithIncompatibleContentRootRebuildsModalSuffix()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack(
                                "cart-stack",
                                new RouteEntry("cart-root", new TestPageRoute("cart")),
                                new RouteEntry("cart-detail", new TestPageRoute("cart-detail"))))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            new TabsNode(
                                "cart-tabs",
                                new[]
                                {
                                    new NavigationBranch("summary", "Summary", Stack("summary-stack", Entry("summary"))),
                                    new NavigationBranch("history", "History", Stack("history-stack", Entry("history")))
                                },
                                "summary",
                                "summary"))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart-detail"), NavigationState.Empty));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var initialModalPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var releasedPages = initialModalPage.Navigation.NavigationStack.ToArray();

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("summary"), initialState));

        var updatedRootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var updatedModalPage = Assert.IsType<TabbedPage>(Assert.Single(updatedRootNavigationPage.Navigation.ModalStack));
        Assert.NotSame(initialModalPage, updatedModalPage);
        Assert.All(releasedPages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task IsModalPresentedTracksNativeModalPresence()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;
        var root = Stack("schools", Entry("schools"));
        var stateWithModal = new NavigationState(
            new[] { new WindowNode("main", root, new[] { new ModalNode("cart-modal", Entry("cart-modal")) }) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(navigationPage.Navigation.ModalStack);

        Assert.True(presentationState.IsModalPresented(modalPage));

        await navigationPage.Navigation.PopModalAsync(animated: false);

        Assert.False(presentationState.IsModalPresented(modalPage));

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task NativeStackPopReconcilesToActualRemainingStackEntries()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        await navigationPage.Navigation.PopAsync(animated: false);

        var stack = Assert.IsType<StackNode>(reconciliation?.TargetState.ActiveWindow?.Root);
        Assert.Equal(new[] { "schools", "school-riverside" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(NavigationReconciliationSource.NativeBackGesture, reconciliation!.Source);
        Assert.Single(fixture.Factory.ReleasedPages);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task NativeStackPopToRootReconcilesToActualRootEntry()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        await navigationPage.Navigation.PopToRootAsync(animated: false);

        var stack = Assert.IsType<StackNode>(reconciliation?.TargetState.ActiveWindow?.Root);
        Assert.Equal(new[] { "schools" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(NavigationReconciliationSource.NativeBackGesture, reconciliation!.Source);
        Assert.Equal(2, fixture.Factory.ReleasedPages.Count);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task NativeStackPopInsideModalContentReconcilesOwningModalState()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    Stack("schools", Entry("schools")),
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack("cart-stack", Entry("cart"), Entry("cart-detail"), Entry("cart-receipt")))
                    })),
            Context(new TestPageRoute("cart-receipt")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalNavigationPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        await modalNavigationPage.Navigation.PopAsync(animated: false);

        var modal = Assert.Single(reconciliation?.TargetState.ActiveWindow?.Modals ?? []);
        var stack = Assert.IsType<StackNode>(modal.Content);
        Assert.Equal(new[] { "cart", "cart-detail" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(new TestPageRoute("cart-detail"), reconciliation!.Route);
        Assert.Equal(NavigationReconciliationSource.NativeBackGesture, reconciliation.Source);
        Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task ModalRemovalReleasesDismissedModalPage()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var stateWithModal = new NavigationState(
            new[] { new WindowNode("main", root, new[] { new ModalNode("cart-modal", Entry("cart-modal")) }) },
            "main");
        var stateWithoutModal = new NavigationState(
            new[] { new WindowNode("main", root) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(navigationPage.Navigation.ModalStack);
        Assert.True(fixture.Presenter.IsModalPresented(modalPage));

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithoutModal),
            Context(new TestPageRoute("schools"), stateWithModal));

        Assert.Empty(navigationPage.Navigation.ModalStack);
        Assert.False(fixture.Presenter.IsModalPresented(modalPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(modalPage));

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task NativeTabSelectionReconcilesSelectedTab()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        var tabs = new TabsNode(
            "store-tabs",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(
            Plan(tabs),
            Context(new TestPageRoute("home")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        tabbedPage.CurrentPage = tabbedPage.Children[1];

        var updatedTabs = Assert.IsType<TabsNode>(reconciliation?.TargetState.ActiveWindow?.Root);
        Assert.Equal("catalog", updatedTabs.SelectedTabId);
        Assert.Equal(NavigationReconciliationSource.TabChanged, reconciliation!.Source);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task NativeTabSelectionInsideModalContentReconcilesSelectedTab()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        var modalTabs = new TabsNode(
            "cart-tabs",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    Stack("schools", Entry("schools")),
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            modalTabs)
                    })),
            Context(new TestPageRoute("home")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalTabbedPage = Assert.IsType<TabbedPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        modalTabbedPage.CurrentPage = modalTabbedPage.Children[1];

        var modal = Assert.Single(reconciliation?.TargetState.ActiveWindow?.Modals ?? []);
        var updatedTabs = Assert.IsType<TabsNode>(modal.Content);
        Assert.Equal("catalog", updatedTabs.SelectedTabId);
        var activeWindow = Assert.IsType<WindowNode>(reconciliation!.TargetState.ActiveWindow);
        Assert.IsType<StackNode>(activeWindow.Root);
        Assert.Equal(NavigationReconciliationSource.TabChanged, reconciliation!.Source);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task NativeFlyoutSelectionReconcilesSelectedBranch()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        var branches = new[]
        {
            new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
            new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
        };
        var flyout = new FlyoutNode("store-flyout", branches, "home", "home");

        await fixture.Presenter.ApplyAsync(
            Plan(flyout),
            Context(new TestPageRoute("home")));

        var flyoutPage = Assert.IsType<FlyoutPage>(fixture.Presenter.CurrentPage);
        var menu = Assert.IsAssignableFrom<ContentPage>(flyoutPage.Flyout);
        var collectionView = Assert.IsType<CollectionView>(menu.Content);
        collectionView.SelectedItem = branches[1];

        var updatedFlyout = Assert.IsType<FlyoutNode>(reconciliation?.TargetState.ActiveWindow?.Root);
        Assert.Equal("catalog", updatedFlyout.SelectedItemId);
        Assert.Equal(NavigationReconciliationSource.OtherNativeEvent, reconciliation!.Source);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public async Task NativeFlyoutSelectionInsideModalContentReconcilesSelectedBranch()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        var branches = new[]
        {
            new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
            new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
        };

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    Stack("schools", Entry("schools")),
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            new FlyoutNode("cart-flyout", branches, "home", "home"))
                    })),
            Context(new TestPageRoute("home")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalFlyoutPage = Assert.IsType<FlyoutPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var menu = Assert.IsAssignableFrom<ContentPage>(modalFlyoutPage.Flyout);
        var collectionView = Assert.IsType<CollectionView>(menu.Content);
        collectionView.SelectedItem = branches[1];

        var modal = Assert.Single(reconciliation?.TargetState.ActiveWindow?.Modals ?? []);
        var updatedFlyout = Assert.IsType<FlyoutNode>(modal.Content);
        Assert.Equal("catalog", updatedFlyout.SelectedItemId);
        var activeWindow = Assert.IsType<WindowNode>(reconciliation!.TargetState.ActiveWindow);
        Assert.IsType<StackNode>(activeWindow.Root);
        Assert.Equal(NavigationReconciliationSource.OtherNativeEvent, reconciliation!.Source);

        fixture.Presenter.Dispose();
    }

    [Fact]
    public void AddMauiRouterRegistersPresentationStateWithBlessedRuntime()
    {
        var services = new ServiceCollection();
        services.AddMauiRouter<ThrowingPlanner>(
            RouteTable.Create(routes => routes.MapRoute<TestPageRoute>("/tests/{name}")),
            pages => pages.MapPage<TestPageRoute>((_, _) => new ContentPage()));

        using var provider = services.BuildServiceProvider();
        var presenter = provider.GetRequiredService<MauiNavigationPresenter>();
        var navigator = provider.GetRequiredService<IRouterNavigator>();
        var presentationState = provider.GetRequiredService<IMauiPresentationState>();

        Assert.IsAssignableFrom<IMauiRouterRuntime>(navigator);
        Assert.Same(presenter, presentationState);
    }

    private static NavigationPresentationContext Context(
        AppRoute route,
        NavigationState? currentState = null)
    {
        return new NavigationPresentationContext(
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            currentState ?? NavigationState.Empty,
            Guid.NewGuid().ToString("N"));
    }

    private static NavigationPlan Plan(NavigationNode root)
    {
        var state = new NavigationState(
            new[] { new WindowNode("main", root) },
            "main");
        return new NavigationPlan(state);
    }

    private static NavigationPlan Plan(WindowNode window)
    {
        return new NavigationPlan(new NavigationState(new[] { window }, window.Id));
    }

    private static int EventHandlerCount(Window window, string eventName)
    {
        for (var type = window.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is null)
            {
                continue;
            }

            return field.GetValue(window) is MulticastDelegate handlers
                ? handlers.GetInvocationList().Length
                : 0;
        }

        throw new InvalidOperationException($"Window event backing field '{eventName}' was not found.");
    }

    private static StackNode Stack(string id, params RouteEntry[] entries)
    {
        return new StackNode(id, entries);
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private sealed class PresenterFixture
    {
        public PresenterFixture()
        {
            Diagnostics = new NavigationDiagnostics();
            Diagnostics.AddObserver(Observer);
            Presenter = new MauiNavigationPresenter(Factory, diagnostics: Diagnostics);
        }

        public InstrumentedRoutePageFactory Factory { get; } = new();

        public RecordingNavigationObserver Observer { get; } = new();

        public NavigationDiagnostics Diagnostics { get; }

        public MauiNavigationPresenter Presenter { get; }

        public NavigationState PresenterState
        {
            get
            {
                return Presenter.CurrentPage is null
                    ? NavigationState.Empty
                    : new NavigationState(new[] { new WindowNode("main") }, "main");
            }
        }
    }

    private sealed class ThrowingPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Lifecycle registration tests do not execute navigation.");
        }
    }
}
