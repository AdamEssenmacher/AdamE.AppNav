using AdamE.AppNav.Planning;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class BranchHostNavigationModelTests
{
    [Fact]
    public void CreateCanonicalState_BuildsAllBranchesAndSelectsOwningBranch()
    {
        var model = CreateModel();
        var metadata = new Dictionary<string, object?>
        {
            ["origin"] = "deep-link"
        };

        var state = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"), metadata);

        var branchHost = AssertBranchHost(state, selectedBranchId: "catalog", "overview", "catalog", "orders");
        AssertBranchStack(
            branchHost,
            "overview",
            entry =>
            {
                Assert.IsType<OverviewRoute>(entry.Route);
                Assert.Null(entry.Metadata);
            });
        AssertBranchStack(
            branchHost,
            "catalog",
            entry =>
            {
                Assert.IsType<CatalogRootRoute>(entry.Route);
                Assert.Null(entry.Metadata);
            },
            entry =>
            {
                Assert.IsType<CatalogDetailRoute>(entry.Route);
                Assert.Equal("deep-link", entry.Metadata!["origin"]);
            });
        AssertBranchStack(
            branchHost,
            "orders",
            entry =>
            {
                Assert.IsType<OrdersRootRoute>(entry.Route);
                Assert.Null(entry.Metadata);
            });
    }

    [Fact]
    public void TryCreateContextualState_PushWithinSameBranchPreservesUnrelatedBranches()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new CatalogAccessoryRoute("scope-1", "accessory-1"),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        var branchHost = AssertBranchHost(nextState!, selectedBranchId: "catalog", "overview", "catalog", "orders");
        AssertBranchRoutes(branchHost, "overview", typeof(OverviewRoute));
        AssertBranchRoutes(branchHost, "catalog", typeof(CatalogRootRoute), typeof(CatalogDetailRoute), typeof(CatalogAccessoryRoute));
        AssertBranchRoutes(branchHost, "orders", typeof(OrdersRootRoute));
    }

    [Fact]
    public void TryCreateContextualState_CrossBranchNavigationSelectsOwningBranchAndPreservesSourceBranchStack()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new OrdersDetailRoute("scope-1", "order-1"),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        var branchHost = AssertBranchHost(nextState!, selectedBranchId: "orders", "overview", "catalog", "orders");
        AssertBranchRoutes(branchHost, "catalog", typeof(CatalogRootRoute), typeof(CatalogDetailRoute));
        AssertBranchRoutes(branchHost, "orders", typeof(OrdersRootRoute), typeof(OrdersDetailRoute));
    }

    [Fact]
    public void TryCreateContextualState_PushExistingEntryRewindsOwningBranchAndPreservesUnrelatedBranches()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"));
        currentState = model.TryCreateContextualState(
            currentState,
            new OrdersDetailRoute("scope-1", "order-1"),
            ContextualStackMutationKind.Push)!;
        currentState = model.TryCreateContextualState(
            currentState,
            new CatalogAccessoryRoute("scope-1", "accessory-1"),
            ContextualStackMutationKind.Push)!;

        var nextState = model.TryCreateContextualState(
            currentState,
            new CatalogDetailRoute("scope-1", "detail-1"),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        var branchHost = AssertBranchHost(nextState!, selectedBranchId: "catalog", "overview", "catalog", "orders");
        AssertBranchRoutes(branchHost, "catalog", typeof(CatalogRootRoute), typeof(CatalogDetailRoute));
        AssertBranchRoutes(branchHost, "orders", typeof(OrdersRootRoute), typeof(OrdersDetailRoute));
    }

    [Fact]
    public void TryCreateContextualState_ReplaceCurrentMutatesOnlyOwningBranch()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"));
        currentState = model.TryCreateContextualState(
            currentState,
            new OrdersDetailRoute("scope-1", "order-1"),
            ContextualStackMutationKind.Push)!;

        var nextState = model.TryCreateContextualState(
            currentState,
            new OrdersDetailRoute("scope-1", "order-2"),
            ContextualStackMutationKind.ReplaceTop);

        Assert.NotNull(nextState);
        var branchHost = AssertBranchHost(nextState!, selectedBranchId: "orders", "overview", "catalog", "orders");
        AssertBranchRoutes(branchHost, "catalog", typeof(CatalogRootRoute), typeof(CatalogDetailRoute));
        AssertBranchStack(
            branchHost,
            "orders",
            entry => Assert.IsType<OrdersRootRoute>(entry.Route),
            entry =>
            {
                var route = Assert.IsType<OrdersDetailRoute>(entry.Route);
                Assert.Equal("order-2", route.OrderId);
            });
    }

    [Fact]
    public void TryCreateContextualState_ReplaceTopExistingEntryRewindsOwningBranchAndPreservesUnrelatedBranches()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"));
        currentState = model.TryCreateContextualState(
            currentState,
            new OrdersDetailRoute("scope-1", "order-1"),
            ContextualStackMutationKind.Push)!;
        currentState = model.TryCreateContextualState(
            currentState,
            new CatalogAccessoryRoute("scope-1", "accessory-1"),
            ContextualStackMutationKind.Push)!;
        currentState = model.TryCreateContextualState(
            currentState,
            new CatalogAccessoryRoute("scope-1", "accessory-2"),
            ContextualStackMutationKind.Push)!;

        var nextState = model.TryCreateContextualState(
            currentState,
            new CatalogDetailRoute("scope-1", "detail-1"),
            ContextualStackMutationKind.ReplaceTop);

        Assert.NotNull(nextState);
        var branchHost = AssertBranchHost(nextState!, selectedBranchId: "catalog", "overview", "catalog", "orders");
        AssertBranchRoutes(branchHost, "catalog", typeof(CatalogRootRoute), typeof(CatalogDetailRoute));
        AssertBranchRoutes(branchHost, "orders", typeof(OrdersRootRoute), typeof(OrdersDetailRoute));
    }

    [Fact]
    public void TryCreateContextualState_BranchRootNavigationCollapsesOnlyOwningBranch()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"));
        currentState = model.TryCreateContextualState(
            currentState,
            new OrdersDetailRoute("scope-1", "order-1"),
            ContextualStackMutationKind.Push)!;

        var nextState = model.TryCreateContextualState(
            currentState,
            new CatalogRootRoute("scope-1"),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        var branchHost = AssertBranchHost(nextState!, selectedBranchId: "catalog", "overview", "catalog", "orders");
        AssertBranchRoutes(branchHost, "catalog", typeof(CatalogRootRoute));
        AssertBranchRoutes(branchHost, "orders", typeof(OrdersRootRoute), typeof(OrdersDetailRoute));
    }

    [Fact]
    public void TryCreateContextualState_DifferentScopeReturnsNull()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new CatalogDetailRoute("scope-1", "detail-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new OrdersDetailRoute("scope-2", "order-1"),
            ContextualStackMutationKind.Push);

        Assert.Null(nextState);
    }

    [Fact]
    public void Create_RejectsDuplicateBranches()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BranchHostNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "branchHost");
            builder.Branch("overview", "Overview", route => new OverviewRoute(route.ScopeId));
            builder.Branch("overview", "Overview Duplicate", route => new OverviewRoute(route.ScopeId));
        }));

        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsDuplicateRouteRegistrations()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BranchHostNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "branchHost");
            builder.Branch("overview", "Overview", route => new OverviewRoute(route.ScopeId));
            builder.Map<OverviewRoute>("overview", recipe => recipe.EntryId(route => $"scope:{route.ScopeId}:overview"));
            builder.Map<OverviewRoute>("overview", recipe => recipe.EntryId(route => $"scope:{route.ScopeId}:overview-duplicate"));
        }));

        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsMissingBranchOwnership()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BranchHostNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "branchHost");
            builder.Branch("overview", "Overview", route => new OverviewRoute(route.ScopeId));
            builder.Map<OverviewRoute>("missing", recipe => recipe.EntryId(route => $"scope:{route.ScopeId}:overview"));
        }));

        Assert.Contains("registered branch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCanonicalState_RejectsUnregisteredRoutesReferencedByRecipes()
    {
        var model = BranchHostNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "branchHost");
            builder.Branch("overview", "Overview", route => new OverviewRoute(route.ScopeId));
            builder.Map<BrokenRoute>("overview", recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:broken")
                .ScopeKey(route => route.ScopeId)
                .Canonical((route, _) =>
                [
                    Step(new UnregisteredRoute(route.ScopeId))
                ]));
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            model.CreateCanonicalState(new BrokenRoute("scope-1")));

        Assert.Contains("must be registered", error.Message, StringComparison.Ordinal);
    }

    private static BranchHostNavigationModel<TestRoute> CreateModel()
    {
        return BranchHostNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "branchHost");

            builder.Branch("overview", "Overview", route => new OverviewRoute(route.ScopeId));
            builder.Branch("catalog", "Catalog", route => new CatalogRootRoute(route.ScopeId));
            builder.Branch("orders", "Orders", route => new OrdersRootRoute(route.ScopeId));

            builder.Map<OverviewRoute>("overview", recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:overview")
                .ScopeKey(route => route.ScopeId));

            builder.Map<CatalogRootRoute>("catalog", recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:catalog")
                .ScopeKey(route => route.ScopeId));

            builder.Map<CatalogDetailRoute>("catalog", recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:catalog:{route.DetailId}")
                .ScopeKey(route => route.ScopeId)
                .Canonical((route, metadata) =>
                [
                    Step(new CatalogRootRoute(route.ScopeId)),
                    Step(route, metadata)
                ]));

            builder.Map<CatalogAccessoryRoute>("catalog", recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:catalog-accessory:{route.AccessoryId}")
                .ScopeKey(route => route.ScopeId));

            builder.Map<OrdersRootRoute>("orders", recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:orders")
                .ScopeKey(route => route.ScopeId));

            builder.Map<OrdersDetailRoute>("orders", recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:orders:{route.OrderId}")
                .ScopeKey(route => route.ScopeId)
                .SlotId(route => $"scope:{route.ScopeId}:orders:detail")
                .Canonical((route, metadata) =>
                [
                    Step(new OrdersRootRoute(route.ScopeId)),
                    Step(route, metadata)
                ]));
        });
    }

    private static BranchHostNode AssertBranchHost(
        NavigationState state,
        string selectedBranchId,
        params string[] expectedBranchIds)
    {
        var branchHost = Assert.IsType<BranchHostNode>(state.ActiveWindow?.Root);
        Assert.Equal(selectedBranchId, branchHost.SelectedBranchId);
        Assert.Equal(expectedBranchIds, branchHost.Branches.Select(static branch => branch.Id).ToArray());
        return branchHost;
    }

    private static void AssertBranchRoutes(BranchHostNode branchHost, string branchId, params Type[] routeTypes)
    {
        var stack = AssertBranchStackNode(branchHost, branchId);
        Assert.Equal(routeTypes, stack.Entries.Select(static entry => entry.Route.GetType()).ToArray());
    }

    private static void AssertBranchStack(
        BranchHostNode branchHost,
        string branchId,
        params Action<RouteEntry>[] assertions)
    {
        var stack = AssertBranchStackNode(branchHost, branchId);
        Assert.Equal(assertions.Length, stack.Entries.Count);

        for (var i = 0; i < assertions.Length; i++)
        {
            assertions[i](stack.Entries[i]);
        }
    }

    private static StackNode AssertBranchStackNode(BranchHostNode branchHost, string branchId)
    {
        var branch = Assert.Single(branchHost.Branches, branch => StringComparer.Ordinal.Equals(branch.Id, branchId));
        return Assert.IsType<StackNode>(branch.Content);
    }

    private static StackRouteStep<TestRoute> Step(
        TestRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new StackRouteStep<TestRoute>(route, metadata);
    }

    private abstract record TestRoute(string ScopeId) : AppRoute;

    private sealed record OverviewRoute(string ScopeId) : TestRoute(ScopeId);

    private sealed record CatalogRootRoute(string ScopeId) : TestRoute(ScopeId);

    private sealed record CatalogDetailRoute(string ScopeId, string DetailId) : TestRoute(ScopeId);

    private sealed record CatalogAccessoryRoute(string ScopeId, string AccessoryId) : TestRoute(ScopeId);

    private sealed record OrdersRootRoute(string ScopeId) : TestRoute(ScopeId);

    private sealed record OrdersDetailRoute(string ScopeId, string OrderId) : TestRoute(ScopeId);

    private sealed record BrokenRoute(string ScopeId) : TestRoute(ScopeId);

    private sealed record UnregisteredRoute(string ScopeId) : TestRoute(ScopeId);
}
