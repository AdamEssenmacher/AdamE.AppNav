using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiNavigationTransitionServiceTests
{
    [Fact]
    public async Task PlatformDefaultTransitionUsesAnimatedNativeOperation()
    {
        var service = CreateService(out var observer);
        var animatedValues = new List<bool>();

        await service.ApplyAsync(
            new PlatformDefaultNavigationTransition(),
            MauiNavigationTransitionOperation.StackPush,
            new ContentPage(),
            new ContentPage(),
            sourceEntry: null,
            targetEntry: Entry("target"),
            operationId: "transition-platform-default",
            executeNativeOperationAsync: (animated, _) =>
            {
                animatedValues.Add(animated);
                return ValueTask.FromResult<Page?>(new ContentPage());
            });

        Assert.Equal(new[] { true }, animatedValues);
        Assert.Contains(observer.Events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationTransitionStarted &&
            diagnosticEvent.Data[NavigationDiagnosticDataKeys.TransitionOperation]?.ToString() == MauiNavigationTransitionOperation.StackPush.ToString());
        Assert.Contains(observer.Events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationTransitionCompleted &&
            diagnosticEvent.Data.ContainsKey(NavigationDiagnosticDataKeys.TransitionDurationMs));
    }

    [Fact]
    public async Task CustomTransitionHandlerReceivesTransitionContext()
    {
        var handler = new RecordingTransitionHandler();
        var options = new MauiRoutePresentationOptions();
        options.Transitions.Map<TestNavigationTransition>(_ => handler);
        var service = CreateService(out _, options);

        await service.ApplyAsync(
            new TestNavigationTransition("entry"),
            MauiNavigationTransitionOperation.ModalPush,
            new ContentPage(),
            new ContentPage(),
            Entry("source"),
            Entry("target"),
            "transition-custom",
            (animated, _) => ValueTask.FromResult<Page?>(new ContentPage()));

        var call = Assert.Single(handler.Calls);
        Assert.Equal("entry", call.Transition.Name);
        Assert.Equal(MauiNavigationTransitionOperation.ModalPush, call.Operation);
        Assert.Equal("source", call.SourceEntry?.Id);
        Assert.Equal("target", call.TargetEntry?.Id);
    }

    [Fact]
    public async Task HandlerFailureEmitsTransitionFailureDiagnostic()
    {
        var handler = new RecordingTransitionHandler { ThrowOnApply = true };
        var options = new MauiRoutePresentationOptions();
        options.Transitions.Map<TestNavigationTransition>(_ => handler);
        var service = CreateService(out var observer, options);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ApplyAsync(
                new TestNavigationTransition("broken"),
                MauiNavigationTransitionOperation.StackPush,
                new ContentPage(),
                new ContentPage(),
                sourceEntry: null,
                targetEntry: Entry("target"),
                operationId: "transition-failure",
                executeNativeOperationAsync: (_, _) => ValueTask.FromResult<Page?>(new ContentPage())));

        var failed = observer.Single(NavigationDiagnosticEventKind.PresentationTransitionFailed);
        Assert.Equal(NavigationDiagnosticPhase.Presentation, failed.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Error, failed.Severity);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.Data[NavigationDiagnosticDataKeys.ExceptionType]);
        Assert.Equal(typeof(TestNavigationTransition).FullName, failed.Data[NavigationDiagnosticDataKeys.TransitionType]);
    }

    [Fact]
    public async Task SharedElementMissingElementsEmitFallbackDiagnosticAndCompleteNavigation()
    {
        var service = CreateService(out var observer);
        var nativeOperationCount = 0;

        await service.ApplyAsync(
            new SharedElementNavigationTransition(
                new[] { SharedElementPair.SameId("missing-product-image") },
                Fallback: new NoNavigationTransition()),
            MauiNavigationTransitionOperation.StackPush,
            new ContentPage(),
            new ContentPage(),
            sourceEntry: null,
            targetEntry: Entry("target"),
            operationId: "transition-shared-missing",
            executeNativeOperationAsync: (_, _) =>
            {
                nativeOperationCount++;
                return ValueTask.FromResult<Page?>(new ContentPage());
            });

        Assert.Equal(1, nativeOperationCount);
        var fallback = observer.Single(NavigationDiagnosticEventKind.PresentationTransitionFallback);
        Assert.Equal(NavigationDiagnosticSeverity.Warning, fallback.Severity);
        Assert.Equal(typeof(SharedElementNavigationTransition).FullName, fallback.Data[NavigationDiagnosticDataKeys.TransitionType]);
        Assert.Equal("missing-product-image->missing-product-image", fallback.Data[NavigationDiagnosticDataKeys.TransitionElementIds]);
    }

    private static MauiNavigationTransitionService CreateService(
        out RecordingNavigationObserver observer,
        MauiRoutePresentationOptions? options = null)
    {
        observer = new RecordingNavigationObserver();
        var diagnostics = new NavigationDiagnostics();
        diagnostics.AddObserver(observer);

        return new MauiNavigationTransitionService(
            new ServiceCollection().BuildServiceProvider(),
            options ?? new MauiRoutePresentationOptions(),
            diagnostics);
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private sealed record TestNavigationTransition(string Name) : NavigationTransition;

    private sealed class RecordingTransitionHandler : IMauiNavigationTransitionHandler<TestNavigationTransition>
    {
        public List<TransitionCall> Calls { get; } = new();

        public bool ThrowOnApply { get; set; }

        public async ValueTask ApplyAsync(
            MauiNavigationTransitionContext<TestNavigationTransition> context,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new TransitionCall(
                context.Transition,
                context.Operation,
                context.SourceEntry,
                context.TargetEntry));

            if (ThrowOnApply)
            {
                throw new InvalidOperationException("Transition failed.");
            }

            await context.ExecuteNativeOperationAsync(false, cancellationToken);
        }
    }

    private sealed record TransitionCall(
        TestNavigationTransition Transition,
        MauiNavigationTransitionOperation Operation,
        RouteEntry? SourceEntry,
        RouteEntry? TargetEntry);
}
