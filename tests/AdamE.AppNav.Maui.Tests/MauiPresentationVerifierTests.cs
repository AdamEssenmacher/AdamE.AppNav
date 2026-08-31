using System.Reflection;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiPresentationVerifierTests
{
    [Fact]
    public async Task VerifyAcceptsMatchingStack()
    {
        var state = State(Stack("stack", Entry("home"), Entry("detail")));
        var root = RoutePage("home");
        var navigationPage = new NavigationPage(root);
        MauiPresentationMetadata.SetHostId(navigationPage, "stack");
        await navigationPage.Navigation.PushAsync(RoutePage("detail"), animated: false);

        var mismatch = Verify(state, navigationPage);

        Assert.Null(mismatch);
    }

    [Fact]
    public async Task VerifyAcceptsRouteOwnedPresentationPagesBetweenLogicalEntries()
    {
        var state = State(Stack("stack", Entry("home"), Entry("create"), Entry("detail")));
        var root = RoutePage("home");
        var navigationPage = new NavigationPage(root);
        MauiPresentationMetadata.SetHostId(navigationPage, "stack");
        await navigationPage.Navigation.PushAsync(RoutePage("create"), animated: false);
        await navigationPage.Navigation.PushAsync(PresentationPage("create", "setting"), animated: false);
        await navigationPage.Navigation.PushAsync(PresentationPage("create", "vibe"), animated: false);
        await navigationPage.Navigation.PushAsync(RoutePage("detail"), animated: false);

        var mismatch = Verify(state, navigationPage);

        Assert.Null(mismatch);
    }

    [Fact]
    public async Task VerifyAcceptsMatchingTabbedBranchHost()
    {
        var branchHost = BranchHost("tabs", "catalog");
        var tabbedPage = new TabbedPage();
        MauiPresentationMetadata.SetHostId(tabbedPage, "tabs");
        var home = StackPage("home-stack", "home");
        MauiPresentationMetadata.SetBranchId(home, "home");
        var catalog = StackPage("catalog-stack", "catalog");
        MauiPresentationMetadata.SetBranchId(catalog, "catalog");
        var host = new MauiTabbedBranchHost(tabbedPage);
        IMauiBranchHostUpdate update = await host.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            [
                new MauiBranchHostBranch("home", "Home", home),
                new MauiBranchHostBranch("catalog", "Catalog", catalog)
            ],
            "catalog",
            Context(new TestPageRoute("catalog"))),
            CancellationToken.None);
        await update.CommitAsync();

        var mismatch = Verify(
            State(branchHost),
            tabbedPage,
            branchHosts: new Dictionary<Page, IMauiBranchHost> { [tabbedPage] = host });

        Assert.Null(mismatch);
    }

    [Fact]
    public async Task VerifyAcceptsMatchingFlyoutBranchHostAndReportsWrongDetail()
    {
        BranchHostNode branchHost = BranchHost("branches", "catalog");
        var options = new MauiRoutePresentationOptions();
        var flyoutFactory = new MauiFlyoutBranchHostFactory("Menu", FlyoutLayoutBehavior.Default, true);
        options.BranchHosts.Add("branches", new MauiBranchHostRegistration(flyoutFactory));
        var host = new MauiFlyoutBranchHost("Menu", FlyoutLayoutBehavior.Default, true);
        var flyoutPage = Assert.IsType<MauiBranchFlyoutPage>(host.Page);
        MauiPresentationMetadata.SetHostId(flyoutPage, "branches");
        var home = StackPage("home-stack", "home");
        MauiPresentationMetadata.SetBranchId(home, "home");
        var catalog = StackPage("catalog-stack", "catalog");
        MauiPresentationMetadata.SetBranchId(catalog, "catalog");
        IMauiBranchHostUpdate update = await host.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            [
                new MauiBranchHostBranch("home", "Home", home),
                new MauiBranchHostBranch("catalog", "Catalog", catalog)
            ],
            "catalog",
            Context(new TestPageRoute("catalog"))),
            CancellationToken.None);
        await update.CommitAsync();
        var branchHosts = new Dictionary<Page, IMauiBranchHost> { [flyoutPage] = host };

        Assert.Null(Verify(State(branchHost), flyoutPage, options, branchHosts: branchHosts));

        flyoutPage.Detail = home;
        MauiPresentationVerificationMismatch? mismatch = Verify(
            State(branchHost),
            flyoutPage,
            options,
            branchHosts: branchHosts);
        Assert.NotNull(mismatch);
        Assert.Equal("$.root.selectedBranchPage", mismatch.Path);
        Assert.Equal("catalog", mismatch.Expected);
        Assert.Equal("home", mismatch.Actual);
    }

    [Fact]
    public async Task VerifyReportsWrongBranchPageMetadata()
    {
        BranchHostNode branchHost = BranchHost("branches", "catalog");
        var options = new MauiRoutePresentationOptions();
        var factory = new MauiTabbedBranchHostFactory();
        options.BranchHosts.Add("branches", new MauiBranchHostRegistration(factory));
        var tabbedPage = new TabbedPage();
        MauiPresentationMetadata.SetHostId(tabbedPage, "branches");
        var home = StackPage("home-stack", "home");
        MauiPresentationMetadata.SetBranchId(home, "wrong");
        var catalog = StackPage("catalog-stack", "catalog");
        MauiPresentationMetadata.SetBranchId(catalog, "catalog");
        tabbedPage.Children.Add(home);
        tabbedPage.Children.Add(catalog);
        tabbedPage.CurrentPage = catalog;

        var host = new MauiTabbedBranchHost(tabbedPage);
        IMauiBranchHostUpdate update = await host.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            [
                new MauiBranchHostBranch("home", "Home", home),
                new MauiBranchHostBranch("catalog", "Catalog", catalog)
            ],
            "catalog",
            Context(new TestPageRoute("catalog"))),
            CancellationToken.None);
        await update.CommitAsync();
        MauiPresentationMetadata.SetBranchId(home, "wrong");

        var mismatch = Verify(State(branchHost), tabbedPage, options, branchHosts: new Dictionary<Page, IMauiBranchHost>
        {
            [tabbedPage] = host
        });

        Assert.NotNull(mismatch);
        Assert.Equal("$.root.branches[0].page.branchId", mismatch.Path);
        Assert.Equal("home", mismatch.Expected);
        Assert.Equal("wrong", mismatch.Actual);
    }

    [Fact]
    public async Task VerifyAcceptsMatchingModal()
    {
        var root = Stack("stack", Entry("home"));
        var state = State(new WindowNode(
            "main",
            root,
            new[] { new ModalNode("cart-modal", Entry("cart")) }));
        var rootPage = StackPage("stack", "home");
        var modalPage = RoutePage("cart");
        MauiPresentationMetadata.SetModalId(modalPage, "cart-modal");
        await rootPage.Navigation.PushModalAsync(modalPage, animated: false);

        var mismatch = Verify(state, rootPage);

        Assert.Null(mismatch);
    }

    [Fact]
    public async Task VerifyAcceptsMatchingRootlessModal()
    {
        var state = State(new WindowNode(
            "main",
            Root: null,
            Modals: new[] { new ModalNode("cart-modal", Entry("cart")) }));
        var rootPage = new ContentPage();
        var modalPage = RoutePage("cart");
        MauiPresentationMetadata.SetModalId(modalPage, "cart-modal");
        await rootPage.Navigation.PushModalAsync(modalPage, animated: false);

        var mismatch = Verify(state, rootPage);

        Assert.Null(mismatch);
    }

    [Fact]
    public void VerifyReportsMissingStackEntry()
    {
        var state = State(Stack("stack", Entry("home"), Entry("detail")));
        var navigationPage = StackPage("stack", "home");

        var mismatch = Verify(state, navigationPage);

        Assert.NotNull(mismatch);
        Assert.Equal("$.root.entries.count", mismatch.Path);
        Assert.Equal("2", mismatch.Expected);
        Assert.Equal("1", mismatch.Actual);
    }

    [Fact]
    public async Task VerifyReportsPresentationPageWithWrongOwner()
    {
        var state = State(Stack("stack", Entry("home"), Entry("create")));
        var navigationPage = StackPage("stack", "home", "create");
        await navigationPage.Navigation.PushAsync(PresentationPage("home", "setting"), animated: false);

        var mismatch = Verify(state, navigationPage);

        Assert.NotNull(mismatch);
        Assert.Equal("$.root.nativeStack[2]", mismatch.Path);
        Assert.Contains("does not match preceding route entry", mismatch.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyReportsDuplicatePresentationKeyWithinOwner()
    {
        var state = State(Stack("stack", Entry("home"), Entry("create")));
        var navigationPage = StackPage("stack", "home", "create");
        await navigationPage.Navigation.PushAsync(PresentationPage("create", "setting"), animated: false);
        await navigationPage.Navigation.PushAsync(PresentationPage("create", "setting"), animated: false);

        var mismatch = Verify(state, navigationPage);

        Assert.NotNull(mismatch);
        Assert.Equal("$.root.nativeStack[3]", mismatch.Path);
        Assert.Contains("appears more than once", mismatch.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyReportsWrongSelectedTab()
    {
        var branchHost = BranchHost("tabs", "catalog");
        var tabbedPage = new TabbedPage();
        MauiPresentationMetadata.SetHostId(tabbedPage, "tabs");
        var home = StackPage("home-stack", "home");
        MauiPresentationMetadata.SetBranchId(home, "home");
        var catalog = StackPage("catalog-stack", "catalog");
        MauiPresentationMetadata.SetBranchId(catalog, "catalog");
        var host = new MauiTabbedBranchHost(tabbedPage);
        IMauiBranchHostUpdate update = await host.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            [
                new MauiBranchHostBranch("home", "Home", home),
                new MauiBranchHostBranch("catalog", "Catalog", catalog)
            ],
            "catalog",
            Context(new TestPageRoute("catalog"))),
            CancellationToken.None);
        await update.CommitAsync();
        tabbedPage.CurrentPage = home;

        var mismatch = Verify(
            State(branchHost),
            tabbedPage,
            branchHosts: new Dictionary<Page, IMauiBranchHost> { [tabbedPage] = host });

        Assert.NotNull(mismatch);
        Assert.Equal("$.root.selectedBranchId", mismatch.Path);
        Assert.Equal("catalog", mismatch.Expected);
        Assert.Equal("home", mismatch.Actual);
    }

    [Fact]
    public async Task VerifyReportsMissingModalId()
    {
        var state = State(new WindowNode(
            "main",
            Stack("stack", Entry("home")),
            new[] { new ModalNode("cart-modal", Entry("cart")) }));
        var rootPage = StackPage("stack", "home");
        await rootPage.Navigation.PushModalAsync(RoutePage("cart"), animated: false);

        var mismatch = Verify(state, rootPage);

        Assert.NotNull(mismatch);
        Assert.Equal("$.modals[0].modalId", mismatch.Path);
        Assert.Equal("cart-modal", mismatch.Expected);
        Assert.Equal("null", mismatch.Actual);
    }

    [Fact]
    public async Task VerifyReportsWrongRouteEntryId()
    {
        var state = State(Stack("stack", Entry("home"), Entry("detail")));
        var root = RoutePage("home");
        var navigationPage = new NavigationPage(root);
        MauiPresentationMetadata.SetHostId(navigationPage, "stack");
        await navigationPage.Navigation.PushAsync(RoutePage("wrong-detail"), animated: false);

        var mismatch = Verify(state, navigationPage);

        Assert.NotNull(mismatch);
        Assert.Equal("$.root.entries[1].routeEntryId", mismatch.Path);
        Assert.Equal("detail", mismatch.Expected);
        Assert.Equal("wrong-detail", mismatch.Actual);
    }

    [Fact]
    public void VerifyReportsAttachedWindowMismatch()
    {
        var state = State(Stack("stack", Entry("home")));
        var currentPage = StackPage("stack", "home");
        var window = new Window { Page = new ContentPage() };

        var mismatch = Verify(state, currentPage, attachedWindow: window);

        Assert.NotNull(mismatch);
        Assert.Equal("$.attachedWindow.Page", mismatch.Path);
    }

    [Fact]
    public void VerifyAcceptsHostOwnedAttachedPageForEmptyRouterState()
    {
        var hostOwnedPage = new ContentPage();
        var window = new Window(hostOwnedPage);

        var mismatch = Verify(NavigationState.Empty, currentPage: null, attachedWindow: window);

        Assert.Null(mismatch);
        Assert.Same(hostOwnedPage, window.Page);
    }

    [Fact]
    public void VerifyRejectsRouterOwnedAttachedPageForEmptyRouterState()
    {
        var routerOwnedPage = StackPage("stack", "home");
        var window = new Window(routerOwnedPage);

        var mismatch = Verify(NavigationState.Empty, currentPage: null, attachedWindow: window);

        Assert.NotNull(mismatch);
        Assert.Equal("$.attachedWindow.Page", mismatch.Path);
        Assert.Equal("null or host-owned page", mismatch.Expected);
    }

    [Fact]
    public async Task PresenterVerificationFailureEmitsDiagnosticAndDoesNotAdvanceLastState()
    {
        var verifier = new SequencedVerifier(
            null,
            new MauiPresentationVerificationMismatch("$.root", "expected", "actual"));
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        var observer = new RecordingNavigationDiagnosticObserver();
        diagnostics.AddObserver(observer);
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            diagnostics: diagnostics,
            presentationVerifier: verifier);
        var firstPlan = Plan(Stack("first-stack", Entry("first")));
        var secondPlan = Plan(Stack("second-stack", Entry("second")));

        await presenter.ApplyAsync(firstPlan, Context(new TestPageRoute("first")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.ApplyAsync(secondPlan, Context(new TestPageRoute("second"), firstPlan.TargetState)).AsTask());

        Assert.Equal(firstPlan.TargetState, LastState(presenter));
        var failure = observer.Single(NavigationDiagnosticEventKind.PresentationVerificationFailed);
        Assert.Equal("$.root", failure.Data[NavigationDiagnosticDataKeys.PresentationPath]);
        Assert.Equal("expected", failure.Data[NavigationDiagnosticDataKeys.PresentationExpected]);
        Assert.Equal("actual", failure.Data[NavigationDiagnosticDataKeys.PresentationActual]);

        _ = presenter.StartShutdown();
    }

    [Fact]
    public async Task RouterDoesNotCommitWhenMauiPresenterVerificationFails()
    {
        var verifier = new SequencedVerifier(new MauiPresentationVerificationMismatch("$.root", "expected", "actual"));
        var diagnostics = new NavigationDiagnostics();
        var observer = new RecordingNavigationDiagnosticObserver();
        diagnostics.AddObserver(observer);
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            diagnostics: diagnostics,
            presentationVerifier: verifier);
        await using IRouterNavigator navigator = RouterNavigatorFactory.Create(
            Routes(),
            new EchoPlanner(),
            presenter,
            new RouterNavigatorFactoryOptions { Diagnostics = diagnostics });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestPageRoute("home"), NavigationRequestSource.Test)).AsTask());

        Assert.Null(navigator.CurrentState.ActiveWindow);
        Assert.Empty(navigator.History.Entries);
        Assert.True(observer.Contains(NavigationDiagnosticEventKind.PresentationVerificationFailed));
        Assert.True(observer.Contains(NavigationDiagnosticEventKind.PresentationFailed));
        Assert.True(observer.Contains(NavigationDiagnosticEventKind.NavigationFailed));
        Assert.False(observer.Contains(NavigationDiagnosticEventKind.PresentationCompleted));

        _ = presenter.StartShutdown();
    }

    private static MauiPresentationVerificationMismatch? Verify(
        NavigationState state,
        Page? currentPage,
        MauiRoutePresentationOptions? options = null,
        Window? attachedWindow = null,
        IReadOnlyDictionary<Page, IMauiBranchHost>? branchHosts = null)
    {
        return MauiPresentationVerifier.Instance.Verify(new MauiPresentationVerificationContext(
            state,
            currentPage,
            attachedWindow,
            options ?? new MauiRoutePresentationOptions(),
            branchHosts));
    }

    private static NavigationPlan Plan(NavigationNode root)
    {
        return new NavigationPlan(State(root));
    }

    private static NavigationState State(NavigationNode root)
    {
        return State(new WindowNode("main", root));
    }

    private static NavigationState State(WindowNode window)
    {
        return new NavigationState(new[] { window }, window.Id);
    }

    private static NavigationPresentationContext Context(AppRoute route, NavigationState? currentState = null)
    {
        return new NavigationPresentationContext(
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            currentState ?? NavigationState.Empty,
            Guid.NewGuid().ToString("N"));
    }

    private static RouteTable Routes()
    {
        return RouteTable.Create(routes => routes.MapRoute<TestPageRoute>("/tests/{name}"));
    }

    private static BranchHostNode BranchHost(string id, string selectedBranchId)
    {
        return new BranchHostNode(
            id,
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            selectedBranchId,
            "home");
    }

    private static NavigationPage StackPage(string stackId, params string[] entryIds)
    {
        var root = RoutePage(entryIds[0]);
        var navigationPage = new NavigationPage(root);
        MauiPresentationMetadata.SetHostId(navigationPage, stackId);
        for (var i = 1; i < entryIds.Length; i++)
        {
            navigationPage.Navigation.PushAsync(RoutePage(entryIds[i]), animated: false).GetAwaiter().GetResult();
        }

        return navigationPage;
    }

    private static ContentPage RoutePage(string entryId)
    {
        var page = new ContentPage { Title = entryId };
        MauiPresentationMetadata.SetRouteEntryId(page, entryId);
        return page;
    }

    private static ContentPage PresentationPage(string ownerRouteEntryId, string key)
    {
        var page = new ContentPage { Title = key };
        MauiPresentationMetadata.SetPresentationOwnerRouteEntryId(page, ownerRouteEntryId);
        MauiPresentationMetadata.SetPresentationPageKey(page, key);
        return page;
    }

    private static StackNode Stack(string id, params RouteEntry[] entries)
    {
        return new StackNode(id, entries);
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private static NavigationState LastState(MauiNavigationPresenter presenter)
    {
        var field = typeof(MauiNavigationPresenter).GetField("_lastState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_lastState field was not found.");
        return (NavigationState)field.GetValue(presenter)!;
    }

    private sealed class SequencedVerifier(params MauiPresentationVerificationMismatch?[] results) : IMauiPresentationVerifier
    {
        private int _index;

        public MauiPresentationVerificationMismatch? Verify(MauiPresentationVerificationContext context)
        {
            if (_index >= results.Length)
            {
                return null;
            }

            return results[_index++];
        }
    }

    private sealed class EchoPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Plan(Stack("stack", new RouteEntry("route", context.Route))));
        }
    }
}
