using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiNavigationPresenterTransitionTests
{
    [Fact]
    public async Task StackPushUsesIncomingRouteEntryTransitionBeforePlanTransition()
    {
        var presenter = CreatePresenter(out var observer);

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"))),
            Context(new TestPageRoute("catalog")));

        observer.Clear();

        await presenter.ApplyAsync(
            Plan(
                Stack(
                    Entry("catalog"),
                    Entry("product", new PlatformDefaultNavigationTransition())),
                new NoNavigationTransition()),
            Context(new TestPageRoute("product")));

        var started = observer.Single(NavigationDiagnosticEventKind.PresentationTransitionStarted);
        Assert.Equal(typeof(PlatformDefaultNavigationTransition).FullName, started.Data[NavigationDiagnosticDataKeys.TransitionType]);
        Assert.Equal(MauiNavigationTransitionOperation.StackPush.ToString(), started.Data[NavigationDiagnosticDataKeys.TransitionOperation]);

        presenter.Dispose();
    }

    [Fact]
    public async Task StackPopUsesOutgoingRouteEntryTransitionBeforePlanTransition()
    {
        var presenter = CreatePresenter(out var observer);

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"))),
            Context(new TestPageRoute("catalog")));

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"), Entry("product", new PlatformDefaultNavigationTransition()))),
            Context(new TestPageRoute("product")));

        observer.Clear();

        await presenter.ApplyAsync(
            Plan(
                Stack(Entry("catalog")),
                new NoNavigationTransition()),
            Context(new TestPageRoute("catalog")));

        var started = observer.Single(NavigationDiagnosticEventKind.PresentationTransitionStarted);
        Assert.Equal(typeof(PlatformDefaultNavigationTransition).FullName, started.Data[NavigationDiagnosticDataKeys.TransitionType]);
        Assert.Equal(MauiNavigationTransitionOperation.StackPop.ToString(), started.Data[NavigationDiagnosticDataKeys.TransitionOperation]);

        presenter.Dispose();
    }

    [Fact]
    public async Task StackPushUsesPlanTransitionWhenEntryTransitionIsAbsent()
    {
        var presenter = CreatePresenter(out var observer);

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"))),
            Context(new TestPageRoute("catalog")));

        observer.Clear();

        await presenter.ApplyAsync(
            Plan(
                Stack(Entry("catalog"), Entry("product")),
                new PlatformDefaultNavigationTransition()),
            Context(new TestPageRoute("product")));

        var started = observer.Single(NavigationDiagnosticEventKind.PresentationTransitionStarted);
        Assert.Equal(typeof(PlatformDefaultNavigationTransition).FullName, started.Data[NavigationDiagnosticDataKeys.TransitionType]);
        Assert.Equal(MauiNavigationTransitionOperation.StackPush.ToString(), started.Data[NavigationDiagnosticDataKeys.TransitionOperation]);

        presenter.Dispose();
    }

    [Fact]
    public async Task BulkStackReconciliationUsesNoNavigationTransitionInsteadOfPlanTransition()
    {
        var presenter = CreatePresenter(out var observer);

        await presenter.ApplyAsync(
            Plan(Stack(Entry("catalog"))),
            Context(new TestPageRoute("catalog")));

        observer.Clear();

        await presenter.ApplyAsync(
            Plan(
                Stack(Entry("catalog"), Entry("product-1"), Entry("product-2")),
                new PlatformDefaultNavigationTransition()),
            Context(new TestPageRoute("product-2")));

        var started = observer.EventsOfKind(NavigationDiagnosticEventKind.PresentationTransitionStarted);
        Assert.NotEmpty(started);
        Assert.All(started, diagnosticEvent =>
            Assert.Equal(typeof(NoNavigationTransition).FullName, diagnosticEvent.Data[NavigationDiagnosticDataKeys.TransitionType]));

        presenter.Dispose();
    }

    private static MauiNavigationPresenter CreatePresenter(out RecordingNavigationObserver observer)
    {
        observer = new RecordingNavigationObserver();
        var diagnostics = new NavigationDiagnostics();
        diagnostics.AddObserver(observer);
        var transitions = new MauiNavigationTransitionService(diagnostics);

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
}
