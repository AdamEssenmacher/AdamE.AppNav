using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiNavigationPresenterTransitionTests
{
    [Fact]
    public async Task StackPushUsesIncomingRouteEntryTransitionBeforePlanTransition()
    {
        var handler = new RecordingTransitionHandler();
        var presenter = CreatePresenter(handler);

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"))),
            Context(new TestPageRoute("catalog")));

        handler.Calls.Clear();

        await presenter.ApplyAsync(
            Plan(
                Stack(
                    Entry("catalog"),
                    Entry("product", new TestTransition("entry"))),
                new TestTransition("plan")),
            Context(new TestPageRoute("product")));

        var call = Assert.Single(handler.Calls);
        Assert.Equal("entry", call.Transition.Name);
        Assert.Equal(MauiNavigationTransitionOperation.StackPush, call.Operation);

        presenter.Dispose();
    }

    [Fact]
    public async Task StackPopUsesOutgoingRouteEntryTransitionBeforePlanTransition()
    {
        var handler = new RecordingTransitionHandler();
        var presenter = CreatePresenter(handler);

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"))),
            Context(new TestPageRoute("catalog")));

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"), Entry("product", new TestTransition("entry")))),
            Context(new TestPageRoute("product")));

        handler.Calls.Clear();

        await presenter.ApplyAsync(
            Plan(
                Stack(Entry("catalog")),
                new TestTransition("plan")),
            Context(new TestPageRoute("catalog")));

        var call = Assert.Single(handler.Calls);
        Assert.Equal("entry", call.Transition.Name);
        Assert.Equal(MauiNavigationTransitionOperation.StackPop, call.Operation);

        presenter.Dispose();
    }

    [Fact]
    public async Task BulkStackReconciliationDoesNotUseCustomPlanTransition()
    {
        var handler = new RecordingTransitionHandler();
        var presenter = CreatePresenter(handler);

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"))),
            Context(new TestPageRoute("catalog")));

        handler.Calls.Clear();

        await presenter.ApplyAsync(
            Plan(
                Stack(Entry("catalog"), Entry("product-1"), Entry("product-2")),
                new TestTransition("plan")),
            Context(new TestPageRoute("product-2")));

        Assert.Empty(handler.Calls);

        presenter.Dispose();
    }

    private static MauiNavigationPresenter CreatePresenter(RecordingTransitionHandler handler)
    {
        var options = new MauiRoutePresentationOptions();
        options.Transitions.Map<TestTransition>(_ => handler);
        var diagnostics = new NavigationDiagnostics();
        var transitions = new MauiNavigationTransitionService(
            new ServiceCollection().BuildServiceProvider(),
            options,
            diagnostics);

        return new MauiNavigationPresenter(
            new InstrumentedRoutePageFactory(),
            diagnostics: diagnostics,
            transitions: transitions);
    }

    private static NavigationPresentationContext Context(AppRoute route)
    {
        return new NavigationPresentationContext(
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            NavigationState.Empty,
            Guid.NewGuid().ToString("N"));
    }

    private static NavigationPlan Plan(StackNode stack, NavigationTransition? transition = null)
    {
        return new NavigationPlan(
            new NavigationState(new[] { new WindowNode("main", stack) }, "main"),
            Transition: transition);
    }

    private static StackNode Stack(params RouteEntry[] entries)
    {
        return new StackNode("catalog-stack", entries);
    }

    private static RouteEntry Entry(string id, NavigationTransition? transition = null)
    {
        return new RouteEntry(id, new TestPageRoute(id), transition);
    }

    private sealed record TestTransition(string Name) : NavigationTransition;

    private sealed class RecordingTransitionHandler : IMauiNavigationTransitionHandler<TestTransition>
    {
        public List<TransitionCall> Calls { get; } = new();

        public async ValueTask ApplyAsync(
            MauiNavigationTransitionContext<TestTransition> context,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new TransitionCall(context.Transition, context.Operation));
            await context.ExecuteNativeOperationAsync(false, cancellationToken);
        }
    }

    private sealed record TransitionCall(
        TestTransition Transition,
        MauiNavigationTransitionOperation Operation);
}
