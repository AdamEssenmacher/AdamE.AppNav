using System.Reflection;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using DeviceRunners.UITesting.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

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

        _ = fixture.Presenter.StartShutdown();
        _ = fixture.Presenter.StartShutdown();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            fixture.Presenter.AttachWindowAsync(new Window()).AsTask());
        await fixture.Presenter.StartShutdown();

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
        var replacementWindow = new Window();

        await fixture.Presenter.AttachWindowAsync(window);
        Assert.Same(currentPage, window.Page);

        await fixture.Presenter.DetachWindowAsync(window);

        Assert.Null(window.Page);
        Assert.Same(currentPage, fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.ReleasedPages);

        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        Assert.Same(currentPage, replacementWindow.Page);
        Assert.Same(replacementWindow, fixture.Presenter.AttachedWindow);
        Assert.Empty(fixture.Factory.ReleasedPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowReplacementTransfersCurrentPageAndLifecycleHandlers()
    {
        var fixture = new PresenterFixture();
        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        Page currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var originalWindow = new Window();
        var replacementWindow = new Window();
        var originalHandlerCounts = WindowLifecycleEventNames.ToDictionary(
            eventName => eventName,
            eventName => EventHandlerCount(originalWindow, eventName),
            StringComparer.Ordinal);
        var replacementHandlerCounts = WindowLifecycleEventNames.ToDictionary(
            eventName => eventName,
            eventName => EventHandlerCount(replacementWindow, eventName),
            StringComparer.Ordinal);

        await fixture.Presenter.AttachWindowAsync(originalWindow);
        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        Assert.Null(originalWindow.Page);
        Assert.Same(currentPage, replacementWindow.Page);
        Assert.Same(currentPage, fixture.Presenter.CurrentPage);
        Assert.Same(replacementWindow, fixture.Presenter.AttachedWindow);
        Assert.Equal("main", fixture.Presenter.AttachedWindowId);
        Assert.Empty(fixture.Factory.ReleasedPages);
        foreach (string eventName in WindowLifecycleEventNames)
        {
            Assert.Equal(originalHandlerCounts[eventName], EventHandlerCount(originalWindow, eventName));
            Assert.Equal(replacementHandlerCounts[eventName] + 1, EventHandlerCount(replacementWindow, eventName));
        }

        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowReplacementDoesNotClearPageNotOwnedByPresenter()
    {
        var fixture = new PresenterFixture();
        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        Page currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var originalWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(originalWindow);
        var hostOwnedPage = new ContentPage();
        originalWindow.Page = hostOwnedPage;
        var replacementWindow = new Window();

        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        Assert.Same(hostOwnedPage, originalWindow.Page);
        Assert.Same(currentPage, replacementWindow.Page);
        Assert.Same(currentPage, fixture.Presenter.CurrentPage);

        await fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task CancelledAttachmentQueuedForMainThreadDoesNotMutateWindowOwnership()
    {
        Assert.True(MainThread.IsMainThread);
        var fixture = new PresenterFixture();
        var originalPlaceholder = new ContentPage { Title = "original-placeholder" };
        var replacementPlaceholder = new ContentPage { Title = "replacement-placeholder" };
        var originalWindow = new Window(originalPlaceholder);
        var replacementWindow = new Window(replacementPlaceholder);
        await fixture.Presenter.AttachWindowAsync(originalWindow);
        using var cancellation = new CancellationTokenSource();
        SemaphoreSlim operationLock = PresentationOperationLock(fixture.Presenter);

        Task attachment = Task.Run(() => fixture.Presenter
            .AttachWindowAsync(replacementWindow, cancellationToken: cancellation.Token)
            .AsTask());
        Assert.True(SpinWait.SpinUntil(
            () => operationLock.CurrentCount == 0,
            TimeSpan.FromSeconds(5)));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment);
        Assert.Same(originalWindow, fixture.Presenter.AttachedWindow);
        Assert.Same(originalPlaceholder, originalWindow.Page);
        Assert.Same(replacementPlaceholder, replacementWindow.Page);
        await fixture.Presenter.StartShutdown();
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

        await fixture.Presenter.AttachWindowAsync(window, "main");

        Assert.Same(rootPage, presentationState.RootPage);
        Assert.Same(rootPage, fixture.Presenter.CurrentPage);
        Assert.Same(window, presentationState.AttachedWindow);
        Assert.Equal("main", presentationState.AttachedWindowId);

        await fixture.Presenter.DetachWindowAsync(window);

        Assert.Same(rootPage, presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);

        _ = fixture.Presenter.StartShutdown();
        await fixture.Presenter.StartShutdown();

        Assert.Null(presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);
    }

    [Fact]
    public async Task AttachWindowSameWindowDoesNotDuplicateLifecycleHandlers()
    {
        var fixture = new PresenterFixture();
        var window = new Window();
        var initialHandlerCounts = WindowLifecycleEventNames.ToDictionary(
            eventName => eventName,
            eventName => EventHandlerCount(window, eventName),
            StringComparer.Ordinal);

        await fixture.Presenter.AttachWindowAsync(window);
        await fixture.Presenter.AttachWindowAsync(window);

        foreach (var eventName in WindowLifecycleEventNames)
        {
            Assert.Equal(initialHandlerCounts[eventName] + 1, EventHandlerCount(window, eventName));
        }

        await fixture.Presenter.DetachWindowAsync(window);

        foreach (var eventName in WindowLifecycleEventNames)
        {
            Assert.Equal(initialHandlerCounts[eventName], EventHandlerCount(window, eventName));
        }

        _ = fixture.Presenter.StartShutdown();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AttachWindowRejectsMissingWindowId(string? windowId)
    {
        var fixture = new PresenterFixture();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Presenter.AttachWindowAsync(new Window(), windowId!).AsTask());

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowRejectsPresentedWindowMismatchBeforeMutatingEitherAttachment()
    {
        var fixture = new PresenterFixture();
        var originalWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(originalWindow, "main");
        int originalActivatedHandlers = EventHandlerCount(originalWindow, "Activated");

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));
        Page currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var replacementWindow = new Window();
        int replacementActivatedHandlers = EventHandlerCount(replacementWindow, "Activated");

        var exception = await Assert.ThrowsAsync<AppNavigationConfigurationException>(() =>
            fixture.Presenter.AttachWindowAsync(replacementWindow, "secondary").AsTask());

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Same(originalWindow, fixture.Presenter.AttachedWindow);
        Assert.Equal("main", fixture.Presenter.AttachedWindowId);
        Assert.Same(currentPage, originalWindow.Page);
        Assert.Null(replacementWindow.Page);
        Assert.Equal(originalActivatedHandlers, EventHandlerCount(originalWindow, "Activated"));
        Assert.Equal(replacementActivatedHandlers, EventHandlerCount(replacementWindow, "Activated"));

        _ = fixture.Presenter.StartShutdown();
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowRejectsMissingIdWithoutDetachingExistingWindow()
    {
        var fixture = new PresenterFixture();
        var originalWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(originalWindow, "main");
        int originalActivatedHandlers = EventHandlerCount(originalWindow, "Activated");

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Presenter.AttachWindowAsync(new Window(), " ").AsTask());

        Assert.Same(originalWindow, fixture.Presenter.AttachedWindow);
        Assert.Equal(originalActivatedHandlers, EventHandlerCount(originalWindow, "Activated"));
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachedWindowMismatchIsRejectedBeforeNativeMutation()
    {
        var fixture = new PresenterFixture();
        var window = new Window();
        await fixture.Presenter.AttachWindowAsync(window, "main");

        var exception = await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => fixture.Presenter
            .ApplyAsync(
                Plan(new WindowNode("secondary", Stack("stack", Entry("detail")))),
                Context(new TestPageRoute("detail")))
            .AsTask());

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Null(window.Page);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.CreatedPages);
    }

    [Fact]
    public async Task AttachedWindowRejectsWindowlessPlanBeforeNativeMutation()
    {
        var fixture = new PresenterFixture();
        var window = new Window();
        await fixture.Presenter.AttachWindowAsync(window, "main");

        var exception = await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => fixture.Presenter
            .ApplyAsync(
                new NavigationPlan(NavigationState.Empty),
                Context(new TestPageRoute("empty")))
            .AsTask());

        Assert.Contains("must contain", exception.Message, StringComparison.Ordinal);
        Assert.Null(window.Page);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.CreatedPages);
    }

    [Fact]
    public async Task MultiWindowPlanIsRejectedBeforeNativeMutation()
    {
        var fixture = new PresenterFixture();
        var state = new NavigationState(
            [
                new WindowNode("main", Stack("main-stack", Entry("main"))),
                new WindowNode("secondary", Stack("secondary-stack", Entry("secondary")))
            ],
            "main");

        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Presenter
            .ApplyAsync(
                new NavigationPlan(state),
                Context(new TestPageRoute("main")))
            .AsTask());

        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.CreatedPages);
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

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task GetTopPresentedPageReturnsLeafPageForSelectedBranch()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "catalog",
            "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("catalog")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var selectedBranch = Assert.IsType<NavigationPage>(tabbedPage.CurrentPage);

        Assert.Same(selectedBranch.CurrentPage, presentationState.GetTopPresentedPage());

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostAppliesStackRootIconsToGeneratedNavigationPageTabs()
    {
        var homeIcon = new FontImageSource { Glyph = "home" };
        var catalogIcon = new FontImageSource { Glyph = "catalog" };
        var fixture = new PresenterFixture(
            createPage: entry => new ContentPage
            {
                Title = $"Root {entry.Id}",
                IconImageSource = entry.Id == "home" ? homeIcon : catalogIcon,
                Content = new Label { Text = entry.Id }
            });
        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "catalog",
            "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("catalog")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        var catalogBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[1]);

        Assert.Same(homeIcon, homeBranch.IconImageSource);
        Assert.Same(catalogIcon, catalogBranch.IconImageSource);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostKeepsBranchTitlesAuthoritativeWhenApplyingRootIcons()
    {
        var fixture = new PresenterFixture(
            createPage: entry => new ContentPage
            {
                Title = $"Root {entry.Id}",
                IconImageSource = new FontImageSource { Glyph = entry.Id },
                Content = new Label { Text = entry.Id }
            });
        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "catalog",
            "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("catalog")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        var catalogBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[1]);

        Assert.Equal("Home", homeBranch.Title);
        Assert.Equal("Catalog", catalogBranch.Title);
        Assert.Equal("Root home", homeBranch.Navigation.NavigationStack[0].Title);
        Assert.Equal("Root catalog", catalogBranch.Navigation.NavigationStack[0].Title);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostRefreshesGeneratedNavigationPageTabIconWhenRootPageIsReused()
    {
        var initialIcon = new FontImageSource { Glyph = "home-initial" };
        var updatedIcon = new FontImageSource { Glyph = "home-updated" };
        var fixture = new PresenterFixture(
            createPage: entry => new ContentPage
            {
                Title = entry.Id,
                IconImageSource = initialIcon,
                Content = new Label { Text = entry.Id }
            },
            updatePage: (page, entry, _) =>
            {
                if (entry.Id == "home")
                {
                    page.IconImageSource = updatedIcon;
                }
            });
        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("home")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        Assert.Same(initialIcon, homeBranch.IconImageSource);

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("home"), fixture.PresenterState));

        Assert.Same(homeBranch, tabbedPage.Children[0]);
        Assert.Same(updatedIcon, homeBranch.IconImageSource);

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
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
                            new BranchHostNode(
                                "cart-branchHost",
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

        _ = fixture.Presenter.StartShutdown();
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

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopReconcilesToActualRemainingStackEntries()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await navigationPage.Navigation.PopAsync(animated: false));

        var stack = Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal(new[] { "schools", "school-riverside" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.Single(fixture.Factory.ReleasedPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RouteOwnedPresentationPagePushesWithoutChangingLogicalState()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;
        var state = Plan(Stack("home", Entry("home"), Entry("create")));

        await fixture.Presenter.ApplyAsync(state, Context(new TestPageRoute("create")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var ownerPage = navigationPage.Navigation.NavigationStack[^1];
        var bindingContext = new object();
        ownerPage.BindingContext = bindingContext;

        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var presentationPage = Assert.IsType<TestPresentationPage>(navigationPage.Navigation.NavigationStack[^1]);
        Assert.Null(MauiPresentationMetadata.GetRouteEntryId(presentationPage));
        Assert.Equal("create", MauiPresentationMetadata.GetPresentationOwnerRouteEntryId(presentationPage));
        Assert.Equal("setting", MauiPresentationMetadata.GetPresentationPageKey(presentationPage));
        Assert.Same(bindingContext, presentationPage.BindingContext);
        Assert.Null(reconciliation);
        Assert.Single(fixture.Factory.CreatedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RejectedPresentationNavigationContainerDisposesItsScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<PresentationScopeMarker>();
        RejectedPresentationNavigationPage? createdPage = null;
        services.AddTransient<RejectedPresentationNavigationPage>(serviceProvider =>
        {
            var page = new RejectedPresentationNavigationPage(
                serviceProvider.GetRequiredService<PresentationScopeMarker>());
            createdPage = page;
            return page;
        });
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPage<TestPageRoute>((_, _) => new ContentPage());
        var presenter = new MauiNavigationPresenter(
            new MauiRoutePageFactory(provider, options),
            presentationOptions: options);

        await presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        var originalStack = navigationPage.Navigation.NavigationStack.ToArray();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.PushAsync<RejectedPresentationNavigationPage>(
                "invalid",
                new MauiRoutePresentationPageOptions { Animated = false }).AsTask());

        var page = Assert.IsType<RejectedPresentationNavigationPage>(createdPage);
        Assert.Contains("cannot be a navigation container", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, page.Marker.DisposeCount);
        Assert.Equal(originalStack, navigationPage.Navigation.NavigationStack.ToArray());

        _ = presenter.StartShutdown();
        Assert.Equal(1, page.Marker.DisposeCount);
    }

    [Fact]
    public async Task RejectedAttachedPresentationPageDisposesItsScopeAndPreservesItsTree()
    {
        var services = new ServiceCollection();
        services.AddScoped<PresentationScopeMarker>();
        RejectedAttachedPresentationPage? createdPage = null;
        NavigationPage? externalNavigationPage = null;
        services.AddTransient<RejectedAttachedPresentationPage>(serviceProvider =>
        {
            var page = new RejectedAttachedPresentationPage(
                serviceProvider.GetRequiredService<PresentationScopeMarker>());
            createdPage = page;
            externalNavigationPage = new NavigationPage(page);
            return page;
        });
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPage<TestPageRoute>((_, _) => new ContentPage());
        var presenter = new MauiNavigationPresenter(
            new MauiRoutePageFactory(provider, options),
            presentationOptions: options);

        await presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));

        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        var originalStack = navigationPage.Navigation.NavigationStack.ToArray();
        originalStack[^1].BindingContext = new object();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.PushAsync<RejectedAttachedPresentationPage>(
                "invalid",
                new MauiRoutePresentationPageOptions
                {
                    Animated = false,
                    InheritBindingContext = true
                }).AsTask());

        var page = Assert.IsType<RejectedAttachedPresentationPage>(createdPage);
        var externalOwner = Assert.IsType<NavigationPage>(externalNavigationPage);
        Assert.Contains("already attached to a visual tree", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, page.Marker.DisposeCount);
        Assert.Null(page.BindingContext);
        Assert.Same(externalOwner, page.Parent);
        Assert.Same(page, Assert.Single(externalOwner.Navigation.NavigationStack));
        Assert.Equal(originalStack, navigationPage.Navigation.NavigationStack.ToArray());

        _ = presenter.StartShutdown();
        Assert.Equal(1, page.Marker.DisposeCount);
    }

    [UIFact]
    public async Task NativePopOfRouteOwnedPageDoesNotReconcileLogicalState()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var presentationPage = navigationPage.Navigation.NavigationStack[^1];
        await ReleasePresentationPageAfterNativeMutationAsync(
            fixture.Factory,
            presentationPage,
            async () => await navigationPage.Navigation.PopAsync(animated: false));

        Assert.Null(reconciliation);
        Assert.Equal(new[] { "home", "create" },
            navigationPage.Navigation.NavigationStack.Select(MauiPresentationMetadata.GetRouteEntryId));
        Assert.Contains(presentationPage, fixture.Factory.ReleasedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task LogicalRouteAboveOwnerPreservesOwnedPresentationSegment()
    {
        var fixture = new PresenterFixture();
        var createState = Plan(Stack("home", Entry("home"), Entry("create")));
        var coveredState = Plan(Stack("home", Entry("home"), Entry("create"), Entry("detail")));

        await fixture.Presenter.ApplyAsync(createState, Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var settingPage = navigationPage.Navigation.NavigationStack[^1];

        await fixture.Presenter.ApplyAsync(
            coveredState,
            Context(new TestPageRoute("detail"), createState.TargetState));

        Assert.Equal(4, navigationPage.Navigation.NavigationStack.Count);
        Assert.Same(settingPage, navigationPage.Navigation.NavigationStack[2]);
        Assert.Equal("detail", MauiPresentationMetadata.GetRouteEntryId(navigationPage.Navigation.NavigationStack[3]));

        await fixture.Presenter.ApplyAsync(
            createState,
            Context(new TestPageRoute("create"), coveredState.TargetState));

        Assert.Equal(3, navigationPage.Navigation.NavigationStack.Count);
        Assert.Same(settingPage, navigationPage.Navigation.NavigationStack[^1]);
        Assert.DoesNotContain(settingPage, fixture.Factory.ReleasedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RemovingOwnerRouteReleasesItsPresentationPages()
    {
        var fixture = new PresenterFixture();
        var createState = Plan(Stack("home", Entry("home"), Entry("create")));

        await fixture.Presenter.ApplyAsync(createState, Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var settingPage = navigationPage.Navigation.NavigationStack[^1];

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"))),
            Context(new TestPageRoute("home"), createState.TargetState));

        Assert.Single(navigationPage.Navigation.NavigationStack);
        Assert.Contains(settingPage, fixture.Factory.ReleasedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task PresentationKeysAreIdempotentAtTopAndUniqueWithinOwnerSegment()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));

        var options = new MauiRoutePresentationPageOptions { Animated = false };
        await fixture.Presenter.PushAsync<TestPresentationPage>("setting", options);
        await fixture.Presenter.PushAsync<TestPresentationPage>("setting", options);
        Assert.Single(fixture.Factory.CreatedPresentationPages);

        await fixture.Presenter.PushAsync<TestPresentationPage>("vibe", options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Presenter.PushAsync<TestPresentationPage>("setting", options).AsTask());

        Assert.Contains("already exists below the top", exception.Message, StringComparison.Ordinal);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task PresentationNavigatorPopStopsAtLogicalRoutePage()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        Assert.True(await fixture.Presenter.PopAsync(animated: false));
        Assert.False(await fixture.Presenter.PopAsync(animated: false));

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopToRootReconcilesToActualRootEntry()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await navigationPage.Navigation.PopToRootAsync(animated: false));

        var stack = Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal(new[] { "schools" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.Equal(2, fixture.Factory.ReleasedPages.Count);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideModalContentReconcilesOwningModalState()
    {
        var fixture = new PresenterFixture();

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
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await modalNavigationPage.Navigation.PopAsync(animated: false));

        var modal = Assert.Single(reconciliation.TargetState.ActiveWindow?.Modals ?? []);
        var stack = Assert.IsType<StackNode>(modal.Content);
        Assert.Equal(new[] { "cart", "cart-detail" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(new TestPageRoute("cart-detail"), reconciliation.Route);
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideNestedModalContentReconcilesToVisibleNestedRoute()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    Stack("schools", Entry("schools")),
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-modal-route", new TestPageRoute("outer-shell")),
                            new ModalNode(
                                "inner-modal",
                                new RouteEntry("inner-modal-route", new TestPageRoute("inner-shell")),
                                Stack("inner-stack", Entry("inner-root"), Entry("inner-detail"))))
                    })),
            Context(new TestPageRoute("inner-detail")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalNavigationPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await modalNavigationPage.Navigation.PopAsync(animated: false));

        var outerModal = Assert.Single(reconciliation.TargetState.ActiveWindow?.Modals ?? []);
        var innerModal = Assert.IsType<ModalNode>(outerModal.Content);
        var stack = Assert.IsType<StackNode>(innerModal.Content);
        Assert.Equal(new[] { "inner-root" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(new TestPageRoute("inner-root"), reconciliation.Route);
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideRootModalContentReconcilesToVisibleRoute()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    new ModalNode(
                        "root-modal",
                        new RouteEntry("root-modal-route", new TestPageRoute("root-shell")),
                        Stack("root-modal-stack", Entry("root"), Entry("detail"))))),
            Context(new TestPageRoute("detail")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await navigationPage.Navigation.PopAsync(animated: false));

        var rootModal = Assert.IsType<ModalNode>(reconciliation.TargetState.ActiveWindow?.Root);
        var stack = Assert.IsType<StackNode>(rootModal.Content);
        Assert.Equal(new[] { "root" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(new TestPageRoute("root"), reconciliation.Route);
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideRootlessModalContentReconcilesOwningModalState()
    {
        var nativeOperations = new CountingNativeNavigationOperations();
        var fixture = new PresenterFixture(nativeOperations: nativeOperations);
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    null,
                    new[]
                    {
                        new ModalNode(
                            "login-modal",
                            new RouteEntry("login-modal-route", new TestPageRoute("login-shell")),
                            Stack("login-stack", Entry("login"), Entry("challenge")))
                    })
            },
            "main");
        var reconciliations = new List<NavigationReconciliation>();
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliations.Add(args.Reconciliation);

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("challenge")));

        var rootHost = Assert.IsType<ContentPage>(fixture.Presenter.CurrentPage);
        var modalNavigationPage = Assert.IsType<NavigationPage>(Assert.Single(rootHost.Navigation.ModalStack));
        var loginPage = modalNavigationPage.Navigation.NavigationStack[0];
        var challengePage = modalNavigationPage.Navigation.NavigationStack[1];
        var createdPageCount = fixture.Factory.CreatedPages.Count;
        var stackPushCount = nativeOperations.StackPushCount;
        var modalPushCount = nativeOperations.ModalPushCount;

        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await modalNavigationPage.Navigation.PopAsync(animated: false));

        Assert.Same(reconciliation, Assert.Single(reconciliations));
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.Equal(new TestPageRoute("login"), reconciliation.Route);
        var updatedWindow = Assert.IsType<WindowNode>(reconciliation.TargetState.ActiveWindow);
        Assert.Null(updatedWindow.Root);
        var updatedModal = Assert.Single(updatedWindow.Modals);
        Assert.Equal("login-modal", updatedModal.Id);
        var updatedStack = Assert.IsType<StackNode>(updatedModal.Content);
        Assert.Equal(new[] { "login" }, updatedStack.Entries.Select(entry => entry.Id));
        Assert.Same(modalNavigationPage, Assert.Single(rootHost.Navigation.ModalStack));
        Assert.Same(loginPage, Assert.Single(modalNavigationPage.Navigation.NavigationStack));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(challengePage));

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(reconciliation.TargetState),
            Context(new TestPageRoute("login"), reconciliation.TargetState));

        Assert.Equal(createdPageCount, fixture.Factory.CreatedPages.Count);
        Assert.Equal(stackPushCount, nativeOperations.StackPushCount);
        Assert.Equal(modalPushCount, nativeOperations.ModalPushCount);
        Assert.Same(rootHost, fixture.Presenter.CurrentPage);
        Assert.Same(modalNavigationPage, Assert.Single(rootHost.Navigation.ModalStack));
        Assert.Same(loginPage, Assert.Single(modalNavigationPage.Navigation.NavigationStack));
        Assert.Single(reconciliations);

        await fixture.Presenter.StartShutdown();

        Assert.Equal(1, fixture.Factory.ReleaseCountFor(rootHost));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(loginPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(challengePage));
        Assert.Equal(3, fixture.Factory.ReleasedPages.Count);
        Assert.Single(reconciliations);
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

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchRootNavigationFromDeepBranchKeepsTabbedHost()
    {
        var fixture = new PresenterFixture();
        var initialRoot = StoreBranchHost("catalog");
        var initialState = new NavigationState(
            new[] { new WindowNode("main", initialRoot) },
            "main");
        var updatedState = new NavigationState(
            new[] { new WindowNode("main", StoreBranchHost("home")) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("product")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranchPage = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        var catalogBranchPage = Assert.IsType<NavigationPage>(tabbedPage.Children[1]);
        Assert.Same(catalogBranchPage, tabbedPage.CurrentPage);
        Assert.Equal(new[] { "catalog", "product" }, catalogBranchPage.Navigation.NavigationStack.Select(static page => page.Title).ToArray());

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("home"), initialState));

        var updatedTabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        Assert.Same(tabbedPage, updatedTabbedPage);
        Assert.Same(homeBranchPage, updatedTabbedPage.CurrentPage);
        Assert.Equal(new[] { "home" }, homeBranchPage.Navigation.NavigationStack.Select(static page => page.Title).ToArray());
        Assert.Equal(new[] { "catalog", "product" }, catalogBranchPage.Navigation.NavigationStack.Select(static page => page.Title).ToArray());
        Assert.Equal(3, fixture.Factory.CreatedPages.Count);
        Assert.Empty(fixture.Factory.ReleasedPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchRootNavigationWithNoTargetModalsDismissesModalAndKeepsTabbedHost()
    {
        var fixture = new PresenterFixture();
        var stateWithModal = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    StoreBranchHost("catalog"),
                    new[] { new ModalNode("cart-modal", Entry("cart-modal")) })
            },
            "main");
        var stateWithoutModal = new NavigationState(
            new[] { new WindowNode("main", StoreBranchHost("home")) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(tabbedPage.Navigation.ModalStack);
        Assert.True(fixture.Presenter.IsModalPresented(modalPage));

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithoutModal),
            Context(new TestPageRoute("home"), stateWithModal));

        var updatedTabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        Assert.Same(tabbedPage, updatedTabbedPage);
        Assert.Empty(tabbedPage.Navigation.ModalStack);
        Assert.False(fixture.Presenter.IsModalPresented(modalPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(modalPage));
        var selectedBranch = Assert.IsType<NavigationPage>(updatedTabbedPage.CurrentPage);
        Assert.Equal("home", Assert.Single(selectedBranch.Navigation.NavigationStack).Title);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeTabSelectionReconcilesSelectedBranch()
    {
        var fixture = new PresenterFixture();

        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("home")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            () =>
            {
                tabbedPage.CurrentPage = tabbedPage.Children[1];
                return Task.CompletedTask;
            });

        var updatedBranchHost = Assert.IsType<BranchHostNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal("catalog", updatedBranchHost.SelectedBranchId);
        Assert.Equal(NavigationReconciliationSource.BranchChanged, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeTabSelectionInsideModalContentReconcilesSelectedBranch()
    {
        var fixture = new PresenterFixture();

        var modalBranchHost = new BranchHostNode(
            "cart-branchHost",
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
                            modalBranchHost)
                    })),
            Context(new TestPageRoute("home")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalTabbedPage = Assert.IsType<TabbedPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            () =>
            {
                modalTabbedPage.CurrentPage = modalTabbedPage.Children[1];
                return Task.CompletedTask;
            });

        var modal = Assert.Single(reconciliation.TargetState.ActiveWindow?.Modals ?? []);
        var updatedBranchHost = Assert.IsType<BranchHostNode>(modal.Content);
        Assert.Equal("catalog", updatedBranchHost.SelectedBranchId);
        var activeWindow = Assert.IsType<WindowNode>(reconciliation.TargetState.ActiveWindow);
        Assert.IsType<StackNode>(activeWindow.Root);
        Assert.Equal(NavigationReconciliationSource.BranchChanged, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public void AddAppNavRegistersPresentationStateWithBlessedRuntime()
    {
        var services = new ServiceCollection();
        services.AddAppNav<ThrowingPlanner>(
            RouteTable.Create(routes => routes.MapRoute<TestPageRoute>("/tests/{name}")),
            pages => pages.MapPage<TestPageRoute>((_, _) => new ContentPage()));

        using var provider = services.BuildServiceProvider();
        var presenter = provider.GetRequiredService<MauiNavigationPresenter>();
        var navigator = provider.GetRequiredService<IRouterNavigator>();
        var presentationState = provider.GetRequiredService<IMauiPresentationState>();

        Assert.IsAssignableFrom<IAppNavRuntime>(navigator);
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

    private static readonly string[] WindowLifecycleEventNames =
    [
        "Activated",
        "Deactivated",
        "Stopped",
        "Resumed",
        "Destroying"
    ];

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

    private static BranchHostNode StoreBranchHost(string selectedBranchId)
    {
        return new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog"), Entry("product")))
            },
            selectedBranchId,
            "home");
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private static async Task<NavigationReconciliation> ReconcileAfterNativeMutationAsync(
        MauiNavigationPresenter presenter,
        Func<Task> nativeMutation)
    {
        var completion = new TaskCompletionSource<NavigationReconciliation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnReconciliationRequested(object? sender, NavigationReconciliationRequestedEventArgs args)
        {
            completion.TrySetResult(args.Reconciliation);
        }

        presenter.ReconciliationRequested += OnReconciliationRequested;

        try
        {
            await nativeMutation();
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            presenter.ReconciliationRequested -= OnReconciliationRequested;
        }
    }

    private static async Task ReleasePresentationPageAfterNativeMutationAsync(
        InstrumentedRoutePageFactory pageFactory,
        Page expectedPage,
        Func<Task> nativeMutation)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPresentationPageReleased(Page page)
        {
            if (ReferenceEquals(page, expectedPage))
                completion.TrySetResult();
        }

        pageFactory.PresentationPageReleased += OnPresentationPageReleased;

        try
        {
            await nativeMutation();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pageFactory.PresentationPageReleased -= OnPresentationPageReleased;
        }
    }

    private static SemaphoreSlim PresentationOperationLock(MauiNavigationPresenter presenter)
    {
        FieldInfo field = typeof(MauiNavigationPresenter).GetField(
            "_presentationOperationLock",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Presentation operation lock field was not found.");
        return Assert.IsType<SemaphoreSlim>(field.GetValue(presenter));
    }

    private sealed class PresenterFixture
    {
        public PresenterFixture(
            Action<MauiRoutePageRegistry>? configurePages = null,
            Func<RouteEntry, Page>? createPage = null,
            Action<Page, RouteEntry, MauiRoutePageUpdateContext>? updatePage = null,
            IMauiNativeNavigationOperations? nativeOperations = null)
        {
            Diagnostics = new NavigationDiagnostics();
            Diagnostics.AddObserver(Observer);
            PresentationOptions = new MauiRoutePresentationOptions();
            configurePages?.Invoke(PresentationOptions.Pages);
            Factory = new InstrumentedRoutePageFactory(createPage, updatePage);
            Presenter = new MauiNavigationPresenter(
                Factory,
                diagnostics: Diagnostics,
                presentationOptions: PresentationOptions,
                nativeOperations: nativeOperations);
        }

        public InstrumentedRoutePageFactory Factory { get; }

        public MauiRoutePresentationOptions PresentationOptions { get; }

        public RecordingNavigationDiagnosticObserver Observer { get; } = new();

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

    private sealed class CountingNativeNavigationOperations : IMauiNativeNavigationOperations
    {
        public int StackPushCount { get; private set; }

        public int ModalPushCount { get; private set; }

        public async Task PushAsync(NavigationPage navigationPage, Page page, bool animated)
        {
            StackPushCount++;
            await MauiNativeNavigationOperations.Instance.PushAsync(navigationPage, page, animated);
        }

        public Task<Page?> PopAsync(NavigationPage navigationPage, bool animated) =>
            MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated);

        public async Task PushModalAsync(Page host, Page page, bool animated)
        {
            ModalPushCount++;
            await MauiNativeNavigationOperations.Instance.PushModalAsync(host, page, animated);
        }

        public Task<Page?> PopModalAsync(Page host, bool animated) =>
            MauiNativeNavigationOperations.Instance.PopModalAsync(host, animated);

        public void InsertTab(TabbedPage tabbedPage, int index, Page page) =>
            MauiNativeNavigationOperations.Instance.InsertTab(tabbedPage, index, page);

        public void RemoveTab(TabbedPage tabbedPage, Page page) =>
            MauiNativeNavigationOperations.Instance.RemoveTab(tabbedPage, page);

        public void SetCurrentTab(TabbedPage tabbedPage, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetCurrentTab(tabbedPage, page);

        public void SetWindowPage(Window window, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetWindowPage(window, page);
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

    private sealed class RejectedPresentationNavigationPage(PresentationScopeMarker marker) : NavigationPage
    {
        public PresentationScopeMarker Marker { get; } = marker;
    }

    private sealed class RejectedAttachedPresentationPage(PresentationScopeMarker marker) : ContentPage
    {
        public PresentationScopeMarker Marker { get; } = marker;
    }

    private sealed class PresentationScopeMarker : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    public sealed class TestPresentationPage : ContentPage;
}
