using AdamE.MauiRouter.Planning;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class StackNavigationModelTests
{
    [Fact]
    public void CreateEntry_UsesRegisteredEntryIdAndSnapshotsMetadata()
    {
        var model = CreateModel();
        var metadata = new Dictionary<string, object?>
        {
            ["origin"] = "test"
        };

        var entry = model.CreateEntry(new DetailRoute("scope-1", "detail-1"), metadata);
        metadata["origin"] = "mutated";

        Assert.Equal("scope:scope-1:detail:detail-1", entry.Id);
        Assert.Equal("test", entry.Metadata!["origin"]);
    }

    [Fact]
    public void CreateCanonicalState_DefaultsToSelfWhenNoCanonicalRecipeIsProvided()
    {
        var model = CreateModel();

        var state = model.CreateCanonicalState(new RootRoute("scope-1"));

        AssertStackRoutes(state, typeof(RootRoute));
    }

    [Fact]
    public void CreateCanonicalState_UsesConfiguredParentChainWithoutPropagatingChildMetadataToParents()
    {
        var model = CreateModel();
        var metadata = new Dictionary<string, object?>
        {
            ["origin"] = "deep-link"
        };

        var state = model.CreateCanonicalState(new DetailRoute("scope-1", "detail-1"), metadata);

        AssertStack(
            state,
            entry =>
            {
                Assert.IsType<RootRoute>(entry.Route);
                Assert.Null(entry.Metadata);
            },
            entry =>
            {
                Assert.IsType<DetailRoute>(entry.Route);
                Assert.Equal("deep-link", entry.Metadata!["origin"]);
            });
    }

    [Fact]
    public void Create_RejectsDuplicateRouteRegistrations()
    {
        var error = Assert.Throws<InvalidOperationException>(() => StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "root");
            builder.Map<RootRoute>(recipe => recipe.EntryId(route => $"scope:{route.ScopeId}:root"));
            builder.Map<RootRoute>(recipe => recipe.EntryId(route => $"scope:{route.ScopeId}:root-duplicate"));
        }));

        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsMissingEntryIdFactory()
    {
        var error = Assert.Throws<InvalidOperationException>(() => StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "root");
            builder.Map<RootRoute>(_ => { });
        }));

        Assert.Contains("must define an entry id factory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCanonicalState_RejectsUnregisteredRoutesReferencedByRecipes()
    {
        var model = StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "root");
            builder.Map<BrokenRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:broken")
                .Canonical((route, _) =>
                [
                    Step(new UnregisteredRoute(route.ScopeId))
                ]));
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            model.CreateCanonicalState(new BrokenRoute("scope-1")));

        Assert.Contains("must be registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateContextualState_PushWithinMatchingScopeAppendsTail()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new DetailRoute("scope-1", "detail-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new SecondaryRoute("scope-1", "secondary-1"),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        AssertStackRoutes(nextState!, typeof(RootRoute), typeof(DetailRoute), typeof(SecondaryRoute));
    }

    [Fact]
    public void TryCreateContextualState_PushExistingEntryRewindsStack()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new DetailRoute("scope-1", "detail-1"));
        currentState = model.TryCreateContextualState(
            currentState,
            new SecondaryRoute("scope-1", "secondary-1"),
            ContextualStackMutationKind.Push)!;

        var nextState = model.TryCreateContextualState(
            currentState,
            new DetailRoute("scope-1", "detail-1"),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        AssertStackRoutes(nextState!, typeof(RootRoute), typeof(DetailRoute));
    }

    [Fact]
    public void TryCreateContextualState_PushSlotMatchReplacesExistingEntryBeforeAppendingTail()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new TabChildRoute("scope-1", "child-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new TabChildRoute("scope-1", "child-2"),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        AssertStack(
            nextState!,
            entry => Assert.IsType<RootRoute>(entry.Route),
            entry => Assert.IsType<TabTwoRoute>(entry.Route),
            entry =>
            {
                var route = Assert.IsType<TabChildRoute>(entry.Route);
                Assert.Equal("child-2", route.ChildId);
            });
    }

    [Fact]
    public void TryCreateContextualState_ReplaceWithCanonicalStackPreservesCurrentWindowAndStack()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(
            new DetailRoute("scope-1", "detail-1"),
            windowId: "secondary-window",
            stackId: "custom-stack");

        var nextState = model.TryCreateContextualState(
            currentState,
            new ReplacementRootRoute("scope-1"),
            ContextualStackMutationKind.Push);

        var window = Assert.IsType<WindowNode>(nextState!.ActiveWindow);
        var stack = Assert.IsType<StackNode>(window.Root);
        Assert.Equal("secondary-window", window.Id);
        Assert.Equal("custom-stack", stack.Id);
        Assert.Collection(stack.Entries, entry => Assert.IsType<ReplacementRootRoute>(entry.Route));
    }

    [Fact]
    public void TryCreateContextualState_ReplaceTopMergesByConfiguredSlotId()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new TabChildRoute("scope-1", "child-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new TabChildRoute("scope-1", "child-2"),
            ContextualStackMutationKind.ReplaceTop);

        Assert.NotNull(nextState);
        AssertStack(
            nextState!,
            entry => Assert.IsType<RootRoute>(entry.Route),
            entry => Assert.IsType<TabTwoRoute>(entry.Route),
            entry =>
            {
                var route = Assert.IsType<TabChildRoute>(entry.Route);
                Assert.Equal("child-2", route.ChildId);
            });
    }

    [Fact]
    public void TryCreateContextualState_ReplaceTopExistingEntryRewindsStack()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new DetailRoute("scope-1", "detail-1"));
        currentState = model.TryCreateContextualState(
            currentState,
            new SecondaryRoute("scope-1", "secondary-1"),
            ContextualStackMutationKind.Push)!;

        var nextState = model.TryCreateContextualState(
            currentState,
            new RootRoute("scope-1"),
            ContextualStackMutationKind.ReplaceTop);

        Assert.NotNull(nextState);
        AssertStackRoutes(nextState!, typeof(RootRoute));
    }

    [Fact]
    public void TryCreateContextualState_ReplaceTopSlotMatchRewindsBeforeAppendingTail()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new TabChildRoute("scope-1", "child-1"));
        currentState = model.TryCreateContextualState(
            currentState,
            new SecondaryRoute("scope-1", "secondary-1"),
            ContextualStackMutationKind.Push)!;

        var nextState = model.TryCreateContextualState(
            currentState,
            new TabChildRoute("scope-1", "child-2"),
            ContextualStackMutationKind.ReplaceTop);

        Assert.NotNull(nextState);
        AssertStack(
            nextState!,
            entry => Assert.IsType<RootRoute>(entry.Route),
            entry => Assert.IsType<TabTwoRoute>(entry.Route),
            entry =>
            {
                var route = Assert.IsType<TabChildRoute>(entry.Route);
                Assert.Equal("child-2", route.ChildId);
            });
    }

    [Fact]
    public void TryCreateContextualState_MatchingScopeMismatchReturnsNull()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new DetailRoute("scope-1", "detail-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new SecondaryRoute("scope-2", "secondary-1"),
            ContextualStackMutationKind.Push);

        Assert.Null(nextState);
    }

    [Fact]
    public void TryCreateContextualState_AnyScopeAllowsContextualCollapse()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new DetailRoute("scope-1", "detail-1"));

        var nextState = model.TryCreateContextualState(
            currentState,
            new HomeRoute(),
            ContextualStackMutationKind.Push);

        Assert.NotNull(nextState);
        AssertStackRoutes(nextState!, typeof(HomeRoute));
    }

    [Fact]
    public void TryCreateContextualState_ReplaceTopUsesEntryIdAsDefaultSlot()
    {
        var model = CreateModel();
        var currentState = model.CreateCanonicalState(new DraftRoute("scope-1", "draft-1"));
        var metadata = new Dictionary<string, object?>
        {
            ["resume"] = true
        };

        var nextState = model.TryCreateContextualState(
            currentState,
            new EditorRoute("scope-1"),
            ContextualStackMutationKind.ReplaceTop,
            metadata);

        Assert.NotNull(nextState);
        AssertStack(
            nextState!,
            entry => Assert.IsType<RootRoute>(entry.Route),
            entry =>
            {
                Assert.IsType<EditorRoute>(entry.Route);
                Assert.Equal(true, entry.Metadata!["resume"]);
            });
    }

    private static StackNavigationModel<TestRoute> CreateModel()
    {
        return StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "root");

            builder.Map<HomeRoute>(recipe => recipe
                .EntryId(_ => "home")
                .ContextualEligibility(ContextualStackEligibility.AnyScope)
                .ContextualPushBehavior(ContextualStackPushBehavior.ReplaceWithCanonicalStack));

            builder.Map<RootRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:root")
                .ScopeKey(route => route.ScopeId));

            builder.Map<DetailRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:detail:{route.DetailId}")
                .ScopeKey(route => route.ScopeId)
                .Canonical((route, metadata) =>
                [
                    Step(new RootRoute(route.ScopeId)),
                    Step(route, metadata)
                ]));

            builder.Map<SecondaryRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:secondary:{route.SecondaryId}")
                .ScopeKey(route => route.ScopeId));

            builder.Map<ReplacementRootRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:replacement-root")
                .ScopeKey(route => route.ScopeId)
                .ContextualPushBehavior(ContextualStackPushBehavior.ReplaceWithCanonicalStack));

            builder.Map<EditorRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:editor")
                .ScopeKey(route => route.ScopeId)
                .Canonical((route, metadata) =>
                [
                    Step(new RootRoute(route.ScopeId)),
                    Step(route, metadata)
                ]));

            builder.Map<DraftRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:draft:{route.DraftId}")
                .ScopeKey(route => route.ScopeId)
                .Canonical((route, metadata) =>
                [
                    Step(new RootRoute(route.ScopeId)),
                    Step(new EditorRoute(route.ScopeId)),
                    Step(route, metadata)
                ])
                .ContextualTail((route, metadata) =>
                [
                    Step(new EditorRoute(route.ScopeId)),
                    Step(route, metadata)
                ]));

            builder.Map<TabOneRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:tab-one")
                .ScopeKey(route => route.ScopeId)
                .SlotId(route => $"scope:{route.ScopeId}:tab-root"));

            builder.Map<TabTwoRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:tab-two")
                .ScopeKey(route => route.ScopeId)
                .SlotId(route => $"scope:{route.ScopeId}:tab-root"));

            builder.Map<TabChildRoute>(recipe => recipe
                .EntryId(route => $"scope:{route.ScopeId}:tab-child:{route.ChildId}")
                .ScopeKey(route => route.ScopeId)
                .Canonical((route, metadata) =>
                [
                    Step(new RootRoute(route.ScopeId)),
                    Step(new TabOneRoute(route.ScopeId)),
                    Step(route, metadata)
                ])
                .ContextualTail((route, metadata) =>
                [
                    Step(new TabTwoRoute(route.ScopeId)),
                    Step(route, metadata)
                ]));
        });
    }

    private static StackRouteStep<TestRoute> Step(
        TestRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new StackRouteStep<TestRoute>(route, metadata);
    }

    private static void AssertStackRoutes(NavigationState state, params Type[] routeTypes)
    {
        var stack = Assert.IsType<StackNode>(state.ActiveWindow?.Root);
        Assert.Equal(routeTypes, stack.Entries.Select(static entry => entry.Route.GetType()).ToArray());
    }

    private static void AssertStack(
        NavigationState state,
        params Action<RouteEntry>[] assertions)
    {
        var stack = Assert.IsType<StackNode>(state.ActiveWindow?.Root);
        Assert.Equal(assertions.Length, stack.Entries.Count);

        for (var i = 0; i < assertions.Length; i++)
        {
            assertions[i](stack.Entries[i]);
        }
    }

    private abstract record TestRoute : AppRoute;

    private abstract record ScopedRoute(string ScopeId) : TestRoute;

    private sealed record HomeRoute : TestRoute;

    private sealed record RootRoute(string ScopeId) : ScopedRoute(ScopeId);

    private sealed record DetailRoute(string ScopeId, string DetailId) : ScopedRoute(ScopeId);

    private sealed record SecondaryRoute(string ScopeId, string SecondaryId) : ScopedRoute(ScopeId);

    private sealed record ReplacementRootRoute(string ScopeId) : ScopedRoute(ScopeId);

    private sealed record EditorRoute(string ScopeId) : ScopedRoute(ScopeId);

    private sealed record DraftRoute(string ScopeId, string DraftId) : ScopedRoute(ScopeId);

    private sealed record TabOneRoute(string ScopeId) : ScopedRoute(ScopeId);

    private sealed record TabTwoRoute(string ScopeId) : ScopedRoute(ScopeId);

    private sealed record TabChildRoute(string ScopeId, string ChildId) : ScopedRoute(ScopeId);

    private sealed record BrokenRoute(string ScopeId) : ScopedRoute(ScopeId);

    private sealed record UnregisteredRoute(string ScopeId) : ScopedRoute(ScopeId);
}
