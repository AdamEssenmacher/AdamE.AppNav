using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Planning;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class RouterPipelineTests
{
    [Fact]
    public async Task RouteMatchingPlanningAndPresentationAreSeparatePhases()
    {
        var planner = new BranchAwarePlanner();
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(TestRoutes.CreateTable(), planner, presenter);

        var result = await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring"),
            NavigationRequestSource.Test));

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

        await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind?missionId=mission-1"),
            NavigationRequestSource.Test));

        Assert.Equal("mission-1", planner.Metadata[MissionIdMetadata.Name]);
    }

    [Fact]
    public async Task RequestTransformerNormalizesUnmatchedUriBeforeRouteMatching()
    {
        var planner = new RequestCapturingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                RequestTransformers = [new LegacyProductUriTransformer()]
            });

        await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            new Uri("/p/123", UriKind.Relative), NavigationRequestSource.AppLink));

        Assert.Equal(new TestRoutes.ProductDetailRoute("northwind", 123), planner.Route);
        Assert.Equal(new Uri("/stores/northwind/products/123", UriKind.Relative), planner.Request!.Uri);
        Assert.Null(planner.Request.Route);
    }

    [Fact]
    public async Task UriRedirectPolicyReplacesTargetWithoutRetainingMatchedRoute()
    {
        var planner = new RequestCapturingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                RequestPolicies = [new StoreToCatalogUriPolicy()]
            });

        await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            new Uri("/stores/northwind", UriKind.Relative), NavigationRequestSource.InAppCommand));

        Assert.Equal(new TestRoutes.CatalogRoute("northwind"), planner.Route);
        Assert.Equal(new Uri("/stores/northwind/catalog", UriKind.Relative), planner.Request!.Uri);
        Assert.Null(planner.Request.Route);
    }

    [Fact]
    public async Task NonTargetPolicyChangesDoNotRestartRouteResolution()
    {
        var policy = new CountingDispositionPolicy();
        var planner = new RequestCapturingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { RequestPolicies = [policy] });

        await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            new Uri("/stores/northwind", UriKind.Relative), NavigationRequestSource.InAppCommand));

        Assert.Equal(1, policy.CallCount);
        Assert.Equal(RouterNavigationDisposition.ReplaceCurrent, planner.Disposition);
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
    public async Task RequestPolicyReceivesFullNavigationContext()
    {
        var policy = new ContextCapturingPolicy();
        var initialState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "catalog-stack",
                    TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")))));
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RequestCapturingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                InitialState = initialState,
                RequestPolicies =
                [
                    policy
                ]
            });
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.AppLink,
            "secondary",
            new Dictionary<string, object?> { ["origin"] = "policy-context" },
            RouterNavigationDisposition.ReplaceCurrent);

        await navigator.NavigateAsync(request);

        var context = policy.LastContext;
        Assert.NotNull(context);
        Assert.Equal(request.Source, context!.Request.Source);
        Assert.Equal(request.WindowId, context.Request.WindowId);
        Assert.Equal(request.Disposition, context.Request.Disposition);
        Assert.Equal("policy-context", context.Request.Metadata["origin"]);
        Assert.Equal(new TestRoutes.StoreRoute("northwind"), context.Route);
        Assert.Same(initialState, context.CurrentState);
        Assert.False(string.IsNullOrWhiteSpace(context.OperationId));
    }

    [Fact]
    public async Task RouteMetadataRemainsSeparateAcrossPolicyRedirectsAndExplicitMetadataWins()
    {
        var routeOnly = new RouteMetadataKey<string>("routeOnly");
        var collision = new RouteMetadataKey<string>("collision");
        var routes = RouteTable.Create(builder => builder
            .MapRoute<TestRoutes.StoreRoute>(
                "/stores/{storeId}",
                route => route.QueryMetadata(routeOnly).QueryMetadata(collision))
            .MapRoute<TestRoutes.CatalogRoute>("/stores/{storeId}/catalog"));
        var policy = new MetadataRedirectPolicy();
        var planner = new RequestCapturingPlanner();
        var navigator = new RouterNavigator(
            routes,
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { RequestPolicies = [policy] });
        var request = RouterNavigationRequest.FromUri(
            new Uri("/stores/northwind?routeOnly=old-route&collision=route", UriKind.Relative),
            NavigationRequestSource.AppLink,
            metadata: new Dictionary<string, object?>
            {
                [collision.Name] = "explicit",
                ["envelope"] = "preserved"
            });

        await navigator.NavigateAsync(request);

        NavigationRequestPolicyContext first = policy.Contexts[0];
        Assert.Equal("old-route", first.RouteMetadata[routeOnly.Name]);
        Assert.Equal("route", first.RouteMetadata[collision.Name]);
        Assert.False(first.Request.Metadata.ContainsKey(routeOnly.Name));
        Assert.Equal("explicit", first.Request.Metadata[collision.Name]);

        NavigationRequestPolicyContext redirected = policy.Contexts[^1];
        Assert.IsType<TestRoutes.CatalogRoute>(redirected.Route);
        Assert.Empty(redirected.RouteMetadata);
        Assert.False(planner.Metadata.ContainsKey(routeOnly.Name));
        Assert.Equal("explicit", planner.Metadata[collision.Name]);
        Assert.Equal("preserved", planner.Metadata["envelope"]);
    }

    [Fact]
    public async Task NewlyConstructedPolicyResultAuthoritativelyReplacesEnvelope()
    {
        var incomingProvenance = new NavigationRequestProvenance(provider: "branch");
        var planner = new RequestCapturingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { RequestPolicies = [new ReplacingEnvelopePolicy()] });

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.AppLink,
            metadata: new Dictionary<string, object?> { ["old"] = "metadata" },
            disposition: RouterNavigationDisposition.ReplaceCurrent,
            provenance: incomingProvenance));

        Assert.Equal(NavigationRequestSource.Test, planner.Source);
        Assert.Equal(RouterNavigationDisposition.Auto, planner.Disposition);
        Assert.Null(planner.Provenance);
        Assert.Empty(planner.Metadata);
        Assert.IsType<TestRoutes.CatalogRoute>(planner.Route);
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
        await ((IRouterNavigator)appRouteRequestNavigator).NavigateAsync(
            routeRequest,
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

    [Fact]
    public async Task ContextualBranchRootNavigationPreservesBranchHostTopologyAndOffscreenBranchStack()
    {
        var planner = new BranchHostModelPlanner();
        var navigator = new RouterNavigator(TestRoutes.CreateTable(), planner, NullNavigationPresenter.Instance);

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.ProductDetailRoute("northwind", 123),
            NavigationRequestSource.Test,
            disposition: RouterNavigationDisposition.Canonical));

        var result = await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.Test,
            disposition: RouterNavigationDisposition.Contextual));

        var branchHost = Assert.IsType<BranchHostNode>(result.State.ActiveWindow?.Root);
        Assert.Equal("store-branchHost", branchHost.Id);
        Assert.Equal("home", branchHost.SelectedBranchId);

        var homeStack = AssertBranchStack(branchHost, "home");
        Assert.Single(homeStack.Entries);
        Assert.IsType<TestRoutes.StoreRoute>(homeStack.Top!.Route);

        var catalogStack = AssertBranchStack(branchHost, "catalog");
        Assert.Equal(
            new[] { typeof(TestRoutes.CatalogRoute), typeof(TestRoutes.ProductDetailRoute) },
            catalogStack.Entries.Select(static entry => entry.Route.GetType()).ToArray());
    }

    [Fact]
    public async Task CanonicalBranchRootNavigationClearsExistingModalState()
    {
        var planner = new BranchHostModelPlanner();
        var initialRoot = planner.CreateCanonicalState(new TestRoutes.ProductDetailRoute("northwind", 123))
            .ActiveWindow!
            .Root!;
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    initialRoot,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal", new TestRoutes.StoreRoute("northwind")))
                    })
            },
            "main");
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { InitialState = initialState });

        var result = await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.Test,
            disposition: RouterNavigationDisposition.Canonical));

        Assert.NotNull(result.State.ActiveWindow);
        var window = result.State.ActiveWindow!;
        Assert.Empty(window.Modals);
        var branchHost = Assert.IsType<BranchHostNode>(window.Root);
        Assert.Equal("home", branchHost.SelectedBranchId);
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

    private sealed class BranchHostModelPlanner : IAppNavigationPlanner
    {
        private readonly BranchHostNavigationModel<AppRoute> _model = BranchHostNavigationModel<AppRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "store-branchHost");
            builder.Branch("home", "Home", route => new TestRoutes.StoreRoute(GetStoreId(route)));
            builder.Branch("catalog", "Catalog", route => new TestRoutes.CatalogRoute(GetStoreId(route)));

            builder.Map<TestRoutes.StoreRoute>("home", recipe => recipe
                .EntryId(route => $"store:{route.StoreId}:home")
                .ScopeKey(route => route.StoreId));
            builder.Map<TestRoutes.CatalogRoute>("catalog", recipe => recipe
                .EntryId(route => $"store:{route.StoreId}:catalog")
                .ScopeKey(route => route.StoreId));
            builder.Map<TestRoutes.ProductDetailRoute>("catalog", recipe => recipe
                .EntryId(route => $"store:{route.StoreId}:product:{route.ProductId}")
                .ScopeKey(route => route.StoreId)
                .Canonical((route, metadata) =>
                [
                    new StackRouteStep<AppRoute>(new TestRoutes.CatalogRoute(route.StoreId)),
                    new StackRouteStep<AppRoute>(route, metadata)
                ]));
        });

        public NavigationState CreateCanonicalState(AppRoute route)
        {
            return _model.CreateCanonicalState(route);
        }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var state = context.Request.Disposition switch
            {
                RouterNavigationDisposition.Contextual =>
                    _model.TryCreateContextualState(
                        context.CurrentState,
                        context.Route,
                        ContextualStackMutationKind.Push,
                        context.Request.Metadata) ??
                    _model.CreateCanonicalState(context.Route, context.Request.Metadata),
                RouterNavigationDisposition.ReplaceCurrent =>
                    _model.TryCreateContextualState(
                        context.CurrentState,
                        context.Route,
                        ContextualStackMutationKind.ReplaceTop,
                        context.Request.Metadata) ??
                    _model.CreateCanonicalState(context.Route, context.Request.Metadata),
                _ => _model.CreateCanonicalState(context.Route, context.Request.Metadata)
            };

            return ValueTask.FromResult(new NavigationPlan(state));
        }

        private static string GetStoreId(AppRoute route)
        {
            return route switch
            {
                TestRoutes.StoreRoute store => store.StoreId,
                TestRoutes.CatalogRoute catalog => catalog.StoreId,
                TestRoutes.ProductDetailRoute detail => detail.StoreId,
                _ => throw new NotSupportedException($"Route '{route.GetType().Name}' is not supported by the test planner.")
            };
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
        public RouterNavigationRequest? Request { get; private set; }

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
            Request = context.Request;
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
            CancellationToken cancellationToken = default)
        {
            RouterNavigationRequest request = context.Request;
            return context.Route is TestRoutes.StoreRoute storeRoute
                ? ValueTask.FromResult(request.WithTarget(new TestRoutes.CatalogRoute(storeRoute.StoreId)))
                : ValueTask.FromResult(request);
        }
    }

    private sealed class ContextCapturingPolicy : INavigationRequestPolicy
    {
        public NavigationRequestPolicyContext? LastContext { get; private set; }

        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTask.FromResult(context.Request);
        }
    }

    private sealed class MetadataRedirectPolicy : INavigationRequestPolicy
    {
        public List<NavigationRequestPolicyContext> Contexts { get; } = [];

        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return context.Route is TestRoutes.StoreRoute store
                ? ValueTask.FromResult(context.Request.WithTarget(
                    new Uri($"/stores/{store.StoreId}/catalog", UriKind.Relative)))
                : ValueTask.FromResult(context.Request);
        }
    }

    private sealed class ReplacingEnvelopePolicy : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            return context.Route is TestRoutes.StoreRoute store
                ? ValueTask.FromResult(RouterNavigationRequest.FromRoute(
                    new TestRoutes.CatalogRoute(store.StoreId),
                    NavigationRequestSource.Test))
                : ValueTask.FromResult(context.Request);
        }
    }

    private sealed class ProvenanceReplacingRedirectPolicy(NavigationRequestProvenance provenance) : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            RouterNavigationRequest request = context.Request;
            return context.Route is TestRoutes.StoreRoute storeRoute
                ? ValueTask.FromResult(request
                    .WithTarget(new TestRoutes.CatalogRoute(storeRoute.StoreId)) with
                { Provenance = provenance })
                : ValueTask.FromResult(request);
        }
    }

    private sealed class LegacyProductUriTransformer : INavigationRequestTransformer
    {
        public ValueTask<RouterNavigationRequest> TransformAsync(
            NavigationRequestTransformContext context,
            CancellationToken cancellationToken = default)
        {
            return context.Request.Uri?.OriginalString == "/p/123"
                ? ValueTask.FromResult(context.Request.WithTarget(
                    new Uri("/stores/northwind/products/123", UriKind.Relative)))
                : ValueTask.FromResult(context.Request);
        }
    }

    private sealed class StoreToCatalogUriPolicy : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            return context.Route is TestRoutes.StoreRoute store
                ? ValueTask.FromResult(context.Request.WithTarget(
                    new Uri($"/stores/{store.StoreId}/catalog", UriKind.Relative)))
                : ValueTask.FromResult(context.Request);
        }
    }

    private sealed class CountingDispositionPolicy : INavigationRequestPolicy
    {
        public int CallCount { get; private set; }

        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(context.Request with
            {
                Disposition = RouterNavigationDisposition.ReplaceCurrent
            });
        }
    }

    private static StackNode AssertBranchStack(BranchHostNode branchHost, string branchId)
    {
        var branch = Assert.Single(branchHost.Branches, branch => StringComparer.Ordinal.Equals(branch.Id, branchId));
        return Assert.IsType<StackNode>(branch.Content);
    }

    private static readonly RouteMetadataKey<string> MissionIdMetadata = new("missionId");
}
