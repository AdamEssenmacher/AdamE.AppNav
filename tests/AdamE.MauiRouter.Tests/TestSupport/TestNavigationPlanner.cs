using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class TestNavigationPlanner : IAppNavigationPlanner
{
    private readonly Func<NavigationPlanningContext, CancellationToken, ValueTask<NavigationPlan>> _createPlan;
    private readonly List<NavigationPlanningContext> _calls = new();

    public TestNavigationPlanner(
        Func<NavigationPlanningContext, CancellationToken, ValueTask<NavigationPlan>> createPlan)
    {
        _createPlan = createPlan ?? throw new ArgumentNullException(nameof(createPlan));
    }

    public IReadOnlyList<NavigationPlanningContext> Calls => _calls.ToArray();

    public NavigationPlanningContext? LastContext => _calls.Count == 0 ? null : _calls[^1];

    public AppRoute? LastRoute => LastContext?.Route;

    public RouterNavigationRequest? LastRequest => LastContext?.Request;

    public static TestNavigationPlanner ForState(NavigationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new TestNavigationPlanner((_, _) =>
            ValueTask.FromResult(new NavigationPlan(state)));
    }

    public static TestNavigationPlanner EchoStack(string windowId = "main", string stackId = "stack")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackId);

        return new TestNavigationPlanner((context, _) =>
        {
            var state = TestNavigationState.State(
                windowId,
                TestNavigationState.Window(
                    windowId,
                    TestNavigationState.Stack(
                        stackId,
                        TestNavigationState.Entry("route", context.Route))));

            return ValueTask.FromResult(new NavigationPlan(state));
        });
    }

    public async ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        _calls.Add(context);
        return await _createPlan(context, cancellationToken).ConfigureAwait(false);
    }
}
