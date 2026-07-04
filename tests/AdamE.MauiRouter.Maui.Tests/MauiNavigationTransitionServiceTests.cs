using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;
using Microsoft.Extensions.Logging;
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
    public async Task NullTransitionResolvesToNoNavigationTransition()
    {
        var service = CreateService(out var observer);
        var animatedValues = new List<bool>();

        await service.ApplyAsync(
            transition: null,
            MauiNavigationTransitionOperation.ModalPush,
            new ContentPage(),
            new ContentPage(),
            Entry("source"),
            Entry("target"),
            "transition-null",
            (animated, _) =>
            {
                animatedValues.Add(animated);
                return ValueTask.FromResult<Page?>(new ContentPage());
            });

        Assert.Equal(new[] { false }, animatedValues);
        var started = observer.Single(NavigationDiagnosticEventKind.PresentationTransitionStarted);
        Assert.Equal(typeof(NoNavigationTransition).FullName, started.Data[NavigationDiagnosticDataKeys.TransitionType]);
        Assert.Equal(MauiNavigationTransitionOperation.ModalPush.ToString(), started.Data[NavigationDiagnosticDataKeys.TransitionOperation]);
    }

    [Fact]
    public async Task SharedElementMissingSourceWithPlatformDefaultFallbackUsesAnimatedNativeOperation()
    {
        var service = CreateService(out var observer);
        var animatedValues = new List<bool>();

        await service.ApplyAsync(
            new SharedElementNavigationTransition(
                new[] { SharedElementPair.SameId("missing-product-image") },
                Fallback: new PlatformDefaultNavigationTransition()),
            MauiNavigationTransitionOperation.StackPush,
            new ContentPage(),
            new ContentPage(),
            sourceEntry: Entry("source"),
            targetEntry: Entry("target"),
            operationId: "transition-shared-platform-fallback",
            executeNativeOperationAsync: (animated, _) =>
            {
                animatedValues.Add(animated);
                return ValueTask.FromResult<Page?>(new ContentPage());
            });

        Assert.Equal(new[] { true }, animatedValues);
        var fallback = observer.Single(NavigationDiagnosticEventKind.PresentationTransitionFallback);
        Assert.Equal(LogLevel.Warning, fallback.Severity);
        Assert.Equal(typeof(SharedElementNavigationTransition).FullName, fallback.Data[NavigationDiagnosticDataKeys.TransitionType]);
        Assert.Equal("missing-product-image->missing-product-image", fallback.Data[NavigationDiagnosticDataKeys.TransitionElementIds]);
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
        Assert.Equal(LogLevel.Warning, fallback.Severity);
        Assert.Equal(typeof(SharedElementNavigationTransition).FullName, fallback.Data[NavigationDiagnosticDataKeys.TransitionType]);
        Assert.Equal("missing-product-image->missing-product-image", fallback.Data[NavigationDiagnosticDataKeys.TransitionElementIds]);
    }

    private static MauiNavigationTransitionService CreateService(out RecordingNavigationDiagnosticObserver observer)
    {
        observer = new RecordingNavigationDiagnosticObserver();
        var diagnostics = new NavigationDiagnostics();
        diagnostics.AddObserver(observer);

        return new MauiNavigationTransitionService(diagnostics);
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }
}
