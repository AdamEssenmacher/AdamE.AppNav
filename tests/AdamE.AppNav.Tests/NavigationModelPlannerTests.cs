using AdamE.AppNav.Planning;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class NavigationModelPlannerTests
{
    [Theory]
    [InlineData(NavigationRequestSource.InAppCommand, ContextualStackMutationKind.Push)]
    [InlineData(NavigationRequestSource.Test, ContextualStackMutationKind.Push)]
    public async Task AutoUsesContextualPushForInAppSources(
        NavigationRequestSource source,
        ContextualStackMutationKind expectedMutation)
    {
        var model = new RecordingModel { ContextualResult = State("contextual") };
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(Context(source, RouterNavigationDisposition.Auto));

        Assert.Same(model.ContextualResult, plan.TargetState);
        Assert.Equal(expectedMutation, model.Mutation);
        Assert.Equal(0, model.CanonicalCalls);
    }

    [Theory]
    [InlineData(NavigationRequestSource.AppLink)]
    [InlineData(NavigationRequestSource.Push)]
    [InlineData(NavigationRequestSource.QrCode)]
    [InlineData(NavigationRequestSource.Unknown)]
    public async Task AutoUsesCanonicalTopologyForExternalSources(NavigationRequestSource source)
    {
        var model = new RecordingModel { ContextualResult = State("contextual") };
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(Context(source, RouterNavigationDisposition.Auto));

        Assert.Equal("canonical", plan.TargetState.ActiveWindow!.Id);
        Assert.Null(model.Mutation);
        Assert.Equal(1, model.CanonicalCalls);
    }

    [Fact]
    public async Task ContextualFallsBackToCanonicalTopology()
    {
        var model = new RecordingModel();
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(
            Context(NavigationRequestSource.InAppCommand, RouterNavigationDisposition.Contextual));

        Assert.Equal(ContextualStackMutationKind.Push, model.Mutation);
        Assert.Equal("canonical", plan.TargetState.ActiveWindow!.Id);
    }

    [Fact]
    public async Task ContextualFallbackReportsDiscardedTopologyInPlanReason()
    {
        var model = new RecordingModel();
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(
            Context(NavigationRequestSource.InAppCommand, RouterNavigationDisposition.Contextual));

        Assert.Equal("canonical", plan.TargetState.ActiveWindow!.Id);
        Assert.NotNull(plan.Reason);
        Assert.Contains("fell back to canonical", plan.Reason, StringComparison.Ordinal);
        Assert.Contains("discarded", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SatisfiedContextualPlanDoesNotReportFallback()
    {
        var model = new RecordingModel { ContextualResult = State("contextual") };
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(
            Context(NavigationRequestSource.InAppCommand, RouterNavigationDisposition.Contextual));

        Assert.Same(model.ContextualResult, plan.TargetState);
        Assert.NotNull(plan.Reason);
        Assert.DoesNotContain("fell back", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalDispositionIsNotReportedAsAFallback()
    {
        var model = new RecordingModel();
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(
            Context(NavigationRequestSource.InAppCommand, RouterNavigationDisposition.Canonical));

        Assert.Equal("canonical", plan.TargetState.ActiveWindow!.Id);
        Assert.NotNull(plan.Reason);
        Assert.DoesNotContain("fell back", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceCurrentUsesReplaceTopAndFallsBackToCanonicalTopology()
    {
        var model = new RecordingModel();
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(
            Context(NavigationRequestSource.InAppCommand, RouterNavigationDisposition.ReplaceCurrent));

        Assert.Equal(ContextualStackMutationKind.ReplaceTop, model.Mutation);
        Assert.Equal("canonical", plan.TargetState.ActiveWindow!.Id);
    }

    [Fact]
    public async Task ExplicitCanonicalDoesNotAttemptContextualMutation()
    {
        var model = new RecordingModel { ContextualResult = State("contextual") };
        var planner = new NavigationModelPlanner<TestRoute>(model);

        var plan = await planner.CreatePlanAsync(
            Context(NavigationRequestSource.InAppCommand, RouterNavigationDisposition.Canonical));

        Assert.Equal("canonical", plan.TargetState.ActiveWindow!.Id);
        Assert.Null(model.Mutation);
    }

    private static NavigationPlanningContext Context(
        NavigationRequestSource source,
        RouterNavigationDisposition disposition)
    {
        var route = new TestRoute("detail");
        return new NavigationPlanningContext(
            RouterNavigationRequest.FromRoute(route, source, disposition: disposition),
            route,
            State("current"),
            "operation");
    }

    private static NavigationState State(string windowId)
    {
        return new NavigationState(
            [new WindowNode(windowId, new StackNode("stack", []))],
            windowId);
    }

    private sealed record TestRoute(string Id) : AppRoute;

    private sealed class RecordingModel : INavigationModel<TestRoute>
    {
        public NavigationState? ContextualResult { get; init; }

        public ContextualStackMutationKind? Mutation { get; private set; }

        public int CanonicalCalls { get; private set; }

        public NavigationState CreateCanonicalState(
            TestRoute route,
            IReadOnlyDictionary<string, object?>? metadata = null,
            string? windowId = null,
            string? hostId = null)
        {
            CanonicalCalls++;
            return State(windowId ?? "canonical");
        }

        public NavigationState? TryCreateContextualState(
            NavigationState currentState,
            TestRoute route,
            ContextualStackMutationKind mutation,
            IReadOnlyDictionary<string, object?>? metadata = null)
        {
            Mutation = mutation;
            return ContextualResult;
        }
    }
}
