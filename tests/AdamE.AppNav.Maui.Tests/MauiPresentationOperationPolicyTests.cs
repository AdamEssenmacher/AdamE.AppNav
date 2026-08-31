using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiPresentationOperationPolicyTests
{
    [Fact]
    public void SelectorFindsSingularStackMutationInSelectedBranchWhileIgnoringInactiveContent()
    {
        NavigationState current = State(Branches(
            "catalog",
            Stack("home", "home"),
            Stack("catalog", "catalog")));
        NavigationState target = State(Branches(
            "catalog",
            Stack("home", "home", "inactive-change"),
            Stack("catalog", "catalog", "detail")));

        MauiPresentationOperationCandidate? candidate = MauiPresentationOperationSelector.Select(
            current,
            new NavigationPlan(target));

        Assert.NotNull(candidate);
        Assert.Equal(MauiPresentationOperationKind.StackPush, candidate.Kind);
        Assert.Equal("catalog", candidate.HostId);
        Assert.Equal("catalog", candidate.SourceEntry?.Id);
        Assert.Equal("detail", candidate.TargetEntry?.Id);
    }

    [Fact]
    public void SelectorFindsSingularMutationInsideRetainedTopModal()
    {
        NavigationState current = State(
            Stack("root", "home"),
            Modal("editor", Stack("editor-stack", "edit")));
        NavigationState target = State(
            Stack("root", "home"),
            Modal("editor", Stack("editor-stack", "edit", "review")));

        MauiPresentationOperationCandidate? candidate = MauiPresentationOperationSelector.Select(
            current,
            new NavigationPlan(target));

        Assert.NotNull(candidate);
        Assert.Equal(MauiPresentationOperationKind.StackPush, candidate.Kind);
        Assert.Equal("editor-stack", candidate.HostId);
        Assert.Equal("review", candidate.TargetEntry?.Id);
    }

    [Theory]
    [InlineData(NavigationPlanKind.Navigate)]
    [InlineData(NavigationPlanKind.Back)]
    public void SelectorFindsSingularModalPush(NavigationPlanKind kind)
    {
        NavigationState current = State(Stack("root", "home"));
        NavigationState target = State(Stack("root", "home"), Modal("cart"));

        MauiPresentationOperationCandidate? candidate = MauiPresentationOperationSelector.Select(
            current,
            new NavigationPlan(target, kind));

        Assert.NotNull(candidate);
        Assert.Equal(MauiPresentationOperationKind.ModalPush, candidate.Kind);
        Assert.Equal("cart", candidate.HostId);
        Assert.Equal("home", candidate.SourceEntry?.Id);
        Assert.Equal("cart-route", candidate.TargetEntry?.Id);
    }

    [Fact]
    public void SelectorRejectsReconciliationAndVisibleCompositeChanges()
    {
        NavigationState current = State(Stack("root", "home"));
        NavigationState pushed = State(Stack("root", "home", "detail"));
        NavigationState replaced = State(Stack("root", "replacement"));

        Assert.Null(MauiPresentationOperationSelector.Select(
            current,
            new NavigationPlan(pushed, NavigationPlanKind.Reconcile)));
        Assert.Null(MauiPresentationOperationSelector.Select(
            current,
            new NavigationPlan(replaced)));
        Assert.Null(MauiPresentationOperationSelector.Select(
            NavigationState.Empty,
            new NavigationPlan(current)));
    }

    [Fact]
    public async Task DefaultPolicyAnimatesSingularStackPushAndCustomPolicyCanSuppressIt()
    {
        var defaultNative = new RecordingNativeOperations();
        var defaultPresenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: defaultNative);
        NavigationState home = State(Stack("root", "home"));
        NavigationState detail = State(Stack("root", "home", "detail"));
        await defaultPresenter.ApplyAsync(new NavigationPlan(home), Context("home", NavigationState.Empty));
        await defaultPresenter.ApplyAsync(new NavigationPlan(detail), Context("detail", home));

        Assert.Equal([true], defaultNative.StackPushAnimations);

        var policy = new RecordingPolicy(MauiPresentationMotion.Suppressed);
        var suppressedNative = new RecordingNativeOperations();
        var suppressedPresenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: suppressedNative,
            presentationOperationPolicy: policy);
        await suppressedPresenter.ApplyAsync(new NavigationPlan(home), Context("home", NavigationState.Empty));
        NavigationPlan detailPlan = new(detail);
        NavigationPresentationContext detailContext = Context("detail", home);
        await suppressedPresenter.ApplyAsync(detailPlan, detailContext);

        Assert.Equal([false], suppressedNative.StackPushAnimations);
        MauiPresentationOperationContext resolved = Assert.Single(policy.Contexts);
        Assert.Same(detailPlan, resolved.Plan);
        Assert.Same(detailContext, resolved.PresentationContext);
        Assert.Equal(MauiPresentationOperationKind.StackPush, resolved.OperationKind);
        Assert.Equal("home", resolved.SourceEntry?.Id);
        Assert.Equal("detail", resolved.TargetEntry?.Id);

        await defaultPresenter.StartShutdown();
        await suppressedPresenter.StartShutdown();
    }

    [Fact]
    public async Task DefaultPolicyAnimatesSingularModalPushAndPop()
    {
        var native = new RecordingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: native);
        NavigationState home = State(Stack("root", "home"));
        NavigationState cart = State(Stack("root", "home"), Modal("cart"));
        await presenter.ApplyAsync(new NavigationPlan(home), Context("home", NavigationState.Empty));
        await presenter.ApplyAsync(new NavigationPlan(cart), Context("cart", home));
        await presenter.ApplyAsync(
            new NavigationPlan(home, NavigationPlanKind.Back),
            Context("home", cart));

        Assert.Equal([true], native.ModalPushAnimations);
        Assert.Equal([true], native.ModalPopAnimations);

        await presenter.StartShutdown();
    }

    [Fact]
    public async Task BackPlanAnimatesSingularLogicalStackPop()
    {
        var policy = new RecordingPolicy(MauiPresentationMotion.Automatic);
        var native = new RecordingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: native,
            presentationOperationPolicy: policy);
        NavigationState detail = State(Stack("root", "home", "detail"));
        NavigationState home = State(Stack("root", "home"));
        await presenter.ApplyAsync(new NavigationPlan(detail), Context("detail", NavigationState.Empty));
        NavigationPlan backPlan = new(home, NavigationPlanKind.Back);
        await presenter.ApplyAsync(backPlan, Context("home", detail));

        Assert.Equal([true], native.StackPopAnimations);
        MauiPresentationOperationContext resolved = Assert.Single(policy.Contexts);
        Assert.Same(backPlan, resolved.Plan);
        Assert.Equal(MauiPresentationOperationKind.StackPop, resolved.OperationKind);
        Assert.Equal("detail", resolved.SourceEntry?.Id);
        Assert.Equal("home", resolved.TargetEntry?.Id);

        await presenter.StartShutdown();
    }

    [Fact]
    public async Task RouteOwnedPagesMakeLogicalPopIneligibleForAnimation()
    {
        var policy = new RecordingPolicy(MauiPresentationMotion.PlatformDefault);
        var native = new RecordingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: native,
            presentationOperationPolicy: policy);
        NavigationState detail = State(Stack("root", "home", "detail"));
        NavigationState home = State(Stack("root", "home"));
        await presenter.ApplyAsync(new NavigationPlan(detail), Context("detail", NavigationState.Empty));
        await presenter.PushAsync<TestPresentationPage>(
            "settings",
            new MauiRoutePresentationPageOptions { Animated = false });
        await presenter.ApplyAsync(
            new NavigationPlan(home, NavigationPlanKind.Back),
            Context("home", detail));

        Assert.Empty(policy.Contexts);
        Assert.Equal([false, false], native.StackPopAnimations);

        await presenter.StartShutdown();
    }

    [Fact]
    public async Task PolicyFailureRollsBackPresentationAndReleasesStagedPage()
    {
        var factory = new InstrumentedRoutePageFactory();
        var presenter = new MauiNavigationPresenter(
            factory,
            presentationOperationPolicy: new ThrowingPolicy());
        NavigationState home = State(Stack("root", "home"));
        NavigationState detail = State(Stack("root", "home", "detail"));
        await presenter.ApplyAsync(new NavigationPlan(home), Context("home", NavigationState.Empty));
        Page homePage = Assert.Single(factory.CreatedPages);

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.ApplyAsync(
            new NavigationPlan(detail),
            Context("detail", home)).AsTask());

        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Assert.Same(homePage, Assert.Single(navigationPage.Navigation.NavigationStack));
        Assert.Equal(0, factory.ReleaseCountFor(homePage));
        Page stagedPage = Assert.Single(factory.CreatedPages.Skip(1));
        Assert.Equal(1, factory.ReleaseCountFor(stagedPage));

        await presenter.StartShutdown();
    }

    [Fact]
    public async Task CompositeAndReconciliationMutationsRemainUnanimatedWithoutInvokingPolicy()
    {
        var policy = new RecordingPolicy(MauiPresentationMotion.PlatformDefault);
        var native = new RecordingNativeOperations();
        var presenter = new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            nativeOperations: native,
            presentationOperationPolicy: policy);
        NavigationState initial = State(Stack("root", "home", "old"));
        NavigationState replacement = State(Stack("root", "home", "new"));
        NavigationState reconciled = State(Stack("root", "home", "new", "latest"));
        await presenter.ApplyAsync(new NavigationPlan(initial), Context("old", NavigationState.Empty));
        await presenter.ApplyAsync(new NavigationPlan(replacement), Context("new", initial));
        await presenter.ApplyAsync(
            new NavigationPlan(reconciled, NavigationPlanKind.Reconcile),
            Context("latest", replacement));

        Assert.Empty(policy.Contexts);
        Assert.All(native.StackPushAnimations, Assert.False);
        Assert.All(native.StackPopAnimations, Assert.False);

        await presenter.StartShutdown();
    }

    private static NavigationPresentationContext Context(string routeName, NavigationState currentState)
    {
        var route = new TestPageRoute(routeName);
        return new NavigationPresentationContext(
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            currentState,
            Guid.NewGuid().ToString("N"));
    }

    private static NavigationState State(NavigationNode root, params ModalNode[] modals) =>
        new([new WindowNode("main", root, modals)], "main");

    private static StackNode Stack(string id, params string[] entries) =>
        new(id, entries.Select(Entry).ToArray());

    private static RouteEntry Entry(string id) => new(id, new TestPageRoute(id));

    private static ModalNode Modal(string id, NavigationNode? content = null) =>
        new(id, Entry($"{id}-route"), content);

    private static BranchHostNode Branches(
        string selected,
        StackNode home,
        StackNode catalog) =>
        new(
            "tabs",
            [new NavigationBranch("home", "Home", home), new NavigationBranch("catalog", "Catalog", catalog)],
            selected,
            "home");

    private sealed class RecordingPolicy(MauiPresentationMotion motion) : IMauiPresentationOperationPolicy
    {
        public List<MauiPresentationOperationContext> Contexts { get; } = [];

        public MauiPresentationOperationOptions Resolve(MauiPresentationOperationContext context)
        {
            Contexts.Add(context);
            return new MauiPresentationOperationOptions { Motion = motion };
        }
    }

    private sealed class ThrowingPolicy : IMauiPresentationOperationPolicy
    {
        public MauiPresentationOperationOptions Resolve(MauiPresentationOperationContext context) =>
            throw new InvalidOperationException("Policy failure.");
    }

    private sealed class TestPresentationPage : ContentPage;

    private sealed class RecordingNativeOperations : IMauiNativeNavigationOperations
    {
        public List<bool> StackPushAnimations { get; } = [];
        public List<bool> StackPopAnimations { get; } = [];
        public List<bool> ModalPushAnimations { get; } = [];
        public List<bool> ModalPopAnimations { get; } = [];

        public async Task PushAsync(NavigationPage navigationPage, Page page, bool animated)
        {
            StackPushAnimations.Add(animated);
            await MauiNativeNavigationOperations.Instance.PushAsync(navigationPage, page, animated);
        }

        public async Task<Page?> PopAsync(NavigationPage navigationPage, bool animated)
        {
            StackPopAnimations.Add(animated);
            return await MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated);
        }

        public async Task PushModalAsync(Page host, Page page, bool animated)
        {
            ModalPushAnimations.Add(animated);
            await MauiNativeNavigationOperations.Instance.PushModalAsync(host, page, animated);
        }

        public async Task<Page?> PopModalAsync(Page host, bool animated)
        {
            ModalPopAnimations.Add(animated);
            return await MauiNativeNavigationOperations.Instance.PopModalAsync(host, animated);
        }

        public void InsertTab(TabbedPage tabbedPage, int index, Page page) =>
            MauiNativeNavigationOperations.Instance.InsertTab(tabbedPage, index, page);

        public void RemoveTab(TabbedPage tabbedPage, Page page) =>
            MauiNativeNavigationOperations.Instance.RemoveTab(tabbedPage, page);

        public void SetCurrentTab(TabbedPage tabbedPage, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetCurrentTab(tabbedPage, page);

        public void SetFlyoutDetail(FlyoutPage flyoutPage, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetFlyoutDetail(flyoutPage, page);

        public void SetFlyoutPresented(FlyoutPage flyoutPage, bool isPresented) =>
            MauiNativeNavigationOperations.Instance.SetFlyoutPresented(flyoutPage, isPresented);

        public void SetWindowPage(Window window, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetWindowPage(window, page);
    }
}
