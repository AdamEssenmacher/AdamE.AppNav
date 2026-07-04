using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;

namespace AdamE.MauiRouter.Tests;

public sealed class RouterPipelineTests
{
    [Fact]
    public async Task RouteMatchingPlanningAndPresentationAreSeparatePhases()
    {
        var planner = new BranchAwarePlanner();
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(TestRoutes.CreateTable(), planner, presenter);

        var result = await navigator.NavigateAsync(
            new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring"),
            NavigationRequestSource.Test);

        var route = Assert.IsType<TestRoutes.ProductDetailRoute>(planner.ReceivedRoute);
        Assert.Equal(123, route.ProductId);

        var window = result.State.ActiveWindow!;
        var branchHost = Assert.IsType<BranchHostNode>(window.Root);
        Assert.Equal("catalog", branchHost.SelectedBranchId);

        var catalogBranch = Assert.Single(branchHost.Branches, branch => branch.Id == "catalog");
        var catalogStack = Assert.IsType<StackNode>(catalogBranch.Content);
        Assert.Equal(2, catalogStack.Entries.Count);
        Assert.Same(result.Plan, presenter.LastPlan);
        Assert.Equal(1, presenter.ApplyCount);
    }

    [Fact]
    public async Task UriRouteMetadataFlowsIntoEffectiveRequest()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route.QueryMetadata(MissionIdMetadata)));
        var planner = new MetadataCapturingPlanner();
        var navigator = new RouterNavigator(table, planner, NullNavigationPresenter.Instance);

        await navigator.NavigateAsync(
            new Uri("https://example.com/stores/northwind?missionId=mission-1"),
            NavigationRequestSource.Test);

        Assert.Equal("mission-1", planner.Metadata[MissionIdMetadata.Name]);
    }

    [Theory]
    [InlineData(RouterNavigationDisposition.Contextual)]
    [InlineData(RouterNavigationDisposition.Canonical)]
    [InlineData(RouterNavigationDisposition.ReplaceCurrent)]
    public async Task RequestDispositionFlowsIntoPlanner(RouterNavigationDisposition disposition)
    {
        var planner = new DispositionCapturingPlanner();
        var navigator = new RouterNavigator(TestRoutes.CreateTable(), planner, NullNavigationPresenter.Instance);

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.InAppCommand,
            disposition: disposition));

        Assert.Equal(disposition, planner.Disposition);
    }

    [Fact]
    public async Task RedirectedRequestPreservesIncomingDispositionWhenPolicyDoesNotOverrideIt()
    {
        var planner = new DispositionCapturingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                RequestPolicies =
                [
                    new RedirectingPolicy()
                ]
            });

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.InAppCommand,
            disposition: RouterNavigationDisposition.ReplaceCurrent));

        Assert.Equal(RouterNavigationDisposition.ReplaceCurrent, planner.Disposition);
        Assert.IsType<TestRoutes.CatalogRoute>(planner.Route);
    }

    [Fact]
    public async Task RedirectedRequestInheritsIncomingProvenanceWhenPolicyDoesNotSetIt()
    {
        var provenance = new NavigationRequestProvenance(
            provider: "branch",
            originalUri: new Uri("https://example.com/stores/northwind"),
            correlationId: "correlation-1");
        var planner = new RequestCapturingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                RequestPolicies =
                [
                    new RedirectingPolicy()
                ]
            });

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.AppLink,
            provenance: provenance));

        Assert.Equal(provenance, planner.Provenance);
        Assert.Equal(provenance, navigator.History.Current!.Request.Provenance);
    }

    [Fact]
    public async Task RedirectedRequestCanReplaceIncomingProvenance()
    {
        var incomingProvenance = new NavigationRequestProvenance(
            provider: "branch",
            originalUri: new Uri("https://example.com/stores/northwind"));
        var redirectProvenance = new NavigationRequestProvenance(
            provider: "compatibility-rewrite",
            originalUri: new Uri("https://example.com/catalog/northwind"));
        var planner = new RequestCapturingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                RequestPolicies =
                [
                    new ProvenanceReplacingRedirectPolicy(redirectProvenance)
                ]
            });

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.AppLink,
            provenance: incomingProvenance));

        Assert.Equal(redirectProvenance, planner.Provenance);
        Assert.Equal(redirectProvenance, navigator.History.Current!.Request.Provenance);
    }

    [Fact]
    public async Task AppRouteRequestNavigatorOverloadMatchesRawRequestBehavior()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route.QueryMetadata(MissionIdMetadata)));
        var routeRequest = AppRouteRequest
            .For(new TestRoutes.StoreRoute("northwind"))
            .WithMetadata(MissionIdMetadata, "mission-1");
        var rawPlanner = new RequestCapturingPlanner();
        var appRouteRequestPlanner = new RequestCapturingPlanner();
        var rawNavigator = new RouterNavigator(table, rawPlanner, NullNavigationPresenter.Instance);
        var appRouteRequestNavigator = new RouterNavigator(table, appRouteRequestPlanner, NullNavigationPresenter.Instance);

        await rawNavigator.NavigateAsync(RouterNavigationRequest.FromRouteRequest(
            routeRequest,
            NavigationRequestSource.InAppCommand,
            disposition: RouterNavigationDisposition.ReplaceCurrent));
        await appRouteRequestNavigator.NavigateAsync(
            routeRequest,
            NavigationRequestSource.InAppCommand,
            RouterNavigationDisposition.ReplaceCurrent);

        Assert.Equal(rawPlanner.Route, appRouteRequestPlanner.Route);
        Assert.Equal(rawPlanner.Source, appRouteRequestPlanner.Source);
        Assert.Equal(rawPlanner.Disposition, appRouteRequestPlanner.Disposition);
        Assert.Equal(
            rawPlanner.Metadata.OrderBy(static pair => pair.Key),
            appRouteRequestPlanner.Metadata.OrderBy(static pair => pair.Key));
        Assert.Equal(rawNavigator.History.Current!.Request.Source, appRouteRequestNavigator.History.Current!.Request.Source);
        Assert.Equal(
            rawNavigator.History.Current!.Request.Disposition,
            appRouteRequestNavigator.History.Current!.Request.Disposition);
        Assert.Equal(
            rawNavigator.History.Current!.Request.Metadata.OrderBy(static pair => pair.Key),
            appRouteRequestNavigator.History.Current!.Request.Metadata.OrderBy(static pair => pair.Key));
        Assert.Equal(rawNavigator.History.Current!.Route, appRouteRequestNavigator.History.Current!.Route);
    }

    private sealed class BranchAwarePlanner : IAppNavigationPlanner
    {
        public AppRoute? ReceivedRoute { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            ReceivedRoute = context.Route;
            var detail = Assert.IsType<TestRoutes.ProductDetailRoute>(context.Route);

            var state = new NavigationState(new[]
            {
                new WindowNode(
                    "main",
                    new BranchHostNode(
                        "store-branchHost",
                        new[]
                        {
                            new NavigationBranch(
                                "home",
                                "Home",
                                new StackNode("home-stack", new[]
                                {
                                    new RouteEntry("home", new TestRoutes.StoreRoute(detail.StoreId))
                                })),
                            new NavigationBranch(
                                "catalog",
                                "Catalog",
                                new StackNode("catalog-stack", new[]
                                {
                                    new RouteEntry("catalog", new TestRoutes.CatalogRoute(detail.StoreId)),
                                    new RouteEntry("product-detail", detail)
                                }))
                        },
                        SelectedBranchId: "catalog",
                        DefaultBranchId: "home"))
            }, "main");

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    private sealed class MetadataCapturingPlanner : IAppNavigationPlanner
    {
        public IReadOnlyDictionary<string, object?> Metadata { get; private set; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            Metadata = context.Request.Metadata;
            return ValueTask.FromResult(new NavigationPlan(new NavigationState(new[]
            {
                new WindowNode(
                    "main",
                    new StackNode("main-stack", new[]
                    {
                        new RouteEntry("store", context.Route)
                    }))
            }, "main")));
        }
    }

    private sealed class DispositionCapturingPlanner : IAppNavigationPlanner
    {
        public RouterNavigationDisposition Disposition { get; private set; }

        public AppRoute? Route { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            Disposition = context.Request.Disposition;
            Route = context.Route;
            return ValueTask.FromResult(new NavigationPlan(new NavigationState(new[]
            {
                new WindowNode(
                    "main",
                    new StackNode("main-stack", new[]
                    {
                        new RouteEntry("route", context.Route)
                    }))
            }, "main")));
        }
    }

    private sealed class RequestCapturingPlanner : IAppNavigationPlanner
    {
        public RouterNavigationDisposition Disposition { get; private set; }

        public NavigationRequestSource Source { get; private set; }

        public AppRoute? Route { get; private set; }

        public IReadOnlyDictionary<string, object?> Metadata { get; private set; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        public NavigationRequestProvenance? Provenance { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            Disposition = context.Request.Disposition;
            Source = context.Request.Source;
            Route = context.Route;
            Metadata = context.Request.Metadata;
            Provenance = context.Request.Provenance;
            return ValueTask.FromResult(new NavigationPlan(new NavigationState(new[]
            {
                new WindowNode(
                    "main",
                    new StackNode("main-stack", new[]
                    {
                        new RouteEntry("route", context.Route)
                    }))
            }, "main")));
        }
    }

    private sealed class RedirectingPolicy : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            return request.Route is TestRoutes.StoreRoute storeRoute
                ? ValueTask.FromResult(RouterNavigationRequest.FromRoute(
                    new TestRoutes.CatalogRoute(storeRoute.StoreId),
                    request.Source,
                    request.WindowId,
                    request.Metadata))
                : ValueTask.FromResult(request);
        }
    }

    private sealed class ProvenanceReplacingRedirectPolicy(NavigationRequestProvenance provenance) : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            return request.Route is TestRoutes.StoreRoute storeRoute
                ? ValueTask.FromResult(RouterNavigationRequest.FromRoute(
                    new TestRoutes.CatalogRoute(storeRoute.StoreId),
                    request.Source,
                    request.WindowId,
                    request.Metadata,
                    provenance: provenance))
                : ValueTask.FromResult(request);
        }
    }

    private static readonly RouteMetadataKey<string> MissionIdMetadata = new("missionId");
}
