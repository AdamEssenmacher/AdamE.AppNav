using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AdamE.MauiRouter.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task NavigatorEmitsPipelineDiagnostics()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);

        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new TestPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { Diagnostics = diagnostics });

        var incomingUri = new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring");
        await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            incomingUri,
            NavigationRequestSource.Test,
            provenance: new NavigationRequestProvenance(
                provider: "branch",
                originalUri: incomingUri,
                referrerUri: new Uri("https://referrer.example/invite"),
                correlationId: "correlation-1",
                isColdStart: true)));

        Assert.Contains(NavigationDiagnosticEventKind.RouteMatchingStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.RouteMatched, events);
        Assert.Contains(NavigationDiagnosticEventKind.PlanningStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.PlanningCompleted, events);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationCompleted, events);
    }

    [Fact]
    public async Task NavigatorEmitsStructuredDiagnostics()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);

        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new TestPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { Diagnostics = diagnostics });

        var incomingUri = new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring");
        await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            incomingUri,
            NavigationRequestSource.Test,
            provenance: new NavigationRequestProvenance(
                provider: "branch",
                originalUri: incomingUri,
                referrerUri: new Uri("https://referrer.example/invite"),
                correlationId: "correlation-1",
                isColdStart: true,
                attributes: new Dictionary<string, string?>
                {
                    ["campaign"] = "spring",
                    ["nullable"] = null
                })));

        Assert.All(events, diagnosticEvent =>
        {
            Assert.False(string.IsNullOrWhiteSpace(diagnosticEvent.OperationId));
            Assert.True(Enum.IsDefined(diagnosticEvent.Severity));
            Assert.True(Enum.IsDefined(diagnosticEvent.Phase));
        });

        var routeMatched = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.RouteMatched);
        Assert.Equal(NavigationDiagnosticPhase.RouteMatching, routeMatched.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Information, routeMatched.Severity);
        Assert.Equal("/stores/{storeId}/products/{productId:int}", routeMatched.Data[NavigationDiagnosticDataKeys.RouteTemplate]);
        Assert.Equal("branch", routeMatched.Data[NavigationDiagnosticDataKeys.ProvenanceProvider]);
        Assert.Equal(incomingUri.ToString(), routeMatched.Data[NavigationDiagnosticDataKeys.ProvenanceOriginalUri]);
        Assert.Equal("https://referrer.example/invite", routeMatched.Data[NavigationDiagnosticDataKeys.ProvenanceReferrerUri]);
        Assert.Equal("correlation-1", routeMatched.Data[NavigationDiagnosticDataKeys.ProvenanceCorrelationId]);
        Assert.True((bool)routeMatched.Data[NavigationDiagnosticDataKeys.ProvenanceIsColdStart]!);
        var provenanceAttributes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            routeMatched.Data[NavigationDiagnosticDataKeys.ProvenanceAttributes]);
        Assert.Equal("spring", provenanceAttributes["campaign"]);
        Assert.Null(provenanceAttributes["nullable"]);
        Assert.True(routeMatched.Data.ContainsKey(NavigationDiagnosticDataKeys.DurationMs));
    }

    [Fact]
    public async Task RouteMatchingFailureDiagnosticsIncludeRouteTableDetails()
    {
        var routeTable = RouteTable.Create(routes => routes.Map(
            "/products/{productId}",
            match => new InvalidProductRoute(match.Path<int>("productId")),
            format => format.PathParam("productId", route => route.ProductId)));
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RouterNavigator(
            routeTable,
            new TestPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { Diagnostics = diagnostics });

        await Assert.ThrowsAsync<RouteNotMatchedException>(() => navigator
            .NavigateAsync(new Uri("https://example.com/products/not-a-number"), NavigationRequestSource.Test)
            .AsTask());

        var notMatched = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.RouteNotMatched);
        Assert.Equal(NavigationDiagnosticPhase.RouteMatching, notMatched.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Warning, notMatched.Severity);
        Assert.Equal("route.value.invalid", notMatched.Data[NavigationDiagnosticDataKeys.RouteDiagnosticCode]);
        Assert.Equal("/products/{productId}", notMatched.Data[NavigationDiagnosticDataKeys.RouteTemplate]);
        Assert.Equal(typeof(InvalidProductRoute).FullName, notMatched.Data[NavigationDiagnosticDataKeys.RouteType]);
        Assert.True(notMatched.Data.ContainsKey(NavigationDiagnosticDataKeys.DurationMs));
    }

    [Fact]
    public async Task DiagnosticsMirrorEventsToLogger()
    {
        var logger = new CapturingLogger();
        var diagnostics = new NavigationDiagnostics(logger);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new TestPlanner(),
            new ThrowingPresenter(),
            new RouterNavigatorOptions { Diagnostics = diagnostics });

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator
            .NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains(nameof(NavigationDiagnosticEventKind.PresentationFailed), StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains(nameof(NavigationDiagnosticEventKind.NavigationFailed), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NavigatorEmitsActivityTagsAndEvents()
    {
        var activityLock = new object();
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NavigationActivitySources.DefaultName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (activityLock)
                {
                    stoppedActivities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var parentActivity = new Activity("RouterActivityTest").Start();

        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new TestPlanner(),
            NullNavigationPresenter.Instance);

        var incomingUri = new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring");
        await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            incomingUri,
            NavigationRequestSource.Test,
            provenance: new NavigationRequestProvenance(
                provider: "branch",
                originalUri: incomingUri,
                correlationId: "correlation-1",
                attributes: new Dictionary<string, string?>
                {
                    ["campaign"] = "spring"
                })));

        Activity[] stoppedActivitySnapshot;
        lock (activityLock)
        {
            stoppedActivitySnapshot = stoppedActivities.ToArray();
        }

        var activity = Assert.Single(
            stoppedActivitySnapshot,
            candidate => candidate.DisplayName == "Navigation.Navigate" &&
                         candidate.ParentId == parentActivity.Id);
        Assert.Equal("Test", activity.Tags.Single(tag => tag.Key == "navigation.source").Value);
        Assert.Equal("branch", activity.Tags.Single(tag => tag.Key == "navigation.provenance.provider").Value);
        Assert.Equal("spring", activity.Tags.Single(tag => tag.Key == "navigation.provenance.attribute.campaign").Value);
        Assert.Equal(typeof(TestRoutes.ProductDetailRoute).FullName, activity.Tags.Single(tag => tag.Key == "navigation.route_type").Value);
        Assert.Equal("/stores/{storeId}/products/{productId:int}", activity.Tags.Single(tag => tag.Key == "navigation.route_template").Value);
        Assert.Contains(activity.Events, activityEvent => activityEvent.Name == nameof(NavigationDiagnosticEventKind.RouteMatched));
    }

    [Fact]
    public async Task ReconciliationEmitsDiagnosticsAndUpdatesHistory()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);

        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new TestPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { Diagnostics = diagnostics });

        var state = new NavigationState(new[]
        {
            new WindowNode("main", new StackNode("stack", new[] { new RouteEntry("home", new TestRoutes.StoreRoute("northwind")) }))
        }, "main");

        await navigator.ReconcileAsync(new NavigationReconciliation(
            state,
            NavigationReconciliationSource.NativeBackGesture,
            new TestRoutes.StoreRoute("northwind"),
            "test reconciliation"));

        Assert.Contains(NavigationDiagnosticEventKind.PresentationStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationCompleted, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
        Assert.Single(navigator.History.Entries);
        Assert.Equal(NavigationRequestSource.NativeReconciliation, navigator.History.Current!.Request.Source);
    }

    [Fact]
    public void PresenterLifecycleDiagnosticsUsePresentationPhaseAndStableKeys()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);

        diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationPageCreated,
            "operation",
            "created",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.PageType] = "SamplePage",
                [NavigationDiagnosticDataKeys.HostId] = "root",
                [NavigationDiagnosticDataKeys.BranchId] = "catalog",
                [NavigationDiagnosticDataKeys.RouteEntryId] = "product",
                [NavigationDiagnosticDataKeys.ModalId] = "modal",
                [NavigationDiagnosticDataKeys.HandlerName] = "Page.Disappearing"
            });

        diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationPresenterDisposed,
            "operation",
            "disposed");

        var pageCreated = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationPageCreated);
        Assert.Equal(NavigationDiagnosticPhase.Presentation, pageCreated.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Debug, pageCreated.Severity);
        Assert.Equal("SamplePage", pageCreated.Data[NavigationDiagnosticDataKeys.PageType]);
        Assert.Equal("root", pageCreated.Data[NavigationDiagnosticDataKeys.HostId]);
        Assert.Equal("catalog", pageCreated.Data[NavigationDiagnosticDataKeys.BranchId]);
        Assert.Equal("product", pageCreated.Data[NavigationDiagnosticDataKeys.RouteEntryId]);
        Assert.Equal("modal", pageCreated.Data[NavigationDiagnosticDataKeys.ModalId]);
        Assert.Equal("Page.Disappearing", pageCreated.Data[NavigationDiagnosticDataKeys.HandlerName]);

        var presenterDisposed = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationPresenterDisposed);
        Assert.Equal(NavigationDiagnosticPhase.Presentation, presenterDisposed.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Information, presenterDisposed.Severity);
    }

    [Fact]
    public void StartupDiagnosticsUseStartupPhaseAndStableKeys()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);

        diagnostics.Write(
            NavigationDiagnosticEventKind.StartupStarted,
            "operation",
            "startup",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.WindowId] = "main",
                [NavigationDiagnosticDataKeys.AppLinkGraceMs] = 250d
            });

        diagnostics.Write(
            NavigationDiagnosticEventKind.StartupCompleted,
            "operation",
            "complete",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.WindowId] = "main",
                [NavigationDiagnosticDataKeys.StartupOutcome] = "Restored"
            });

        var startupStarted = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupStarted);
        Assert.Equal(NavigationDiagnosticPhase.Startup, startupStarted.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Debug, startupStarted.Severity);
        Assert.Equal("main", startupStarted.Data[NavigationDiagnosticDataKeys.WindowId]);
        Assert.Equal(250d, startupStarted.Data[NavigationDiagnosticDataKeys.AppLinkGraceMs]);

        var startupCompleted = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupCompleted);
        Assert.Equal(NavigationDiagnosticPhase.Startup, startupCompleted.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Information, startupCompleted.Severity);
        Assert.Equal("Restored", startupCompleted.Data[NavigationDiagnosticDataKeys.StartupOutcome]);
    }

    [Fact]
    public void TransitionDiagnosticsUsePresentationPhaseAndActivityTags()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        using var activity = new Activity("TransitionDiagnosticsTest").Start();

        diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationTransitionCompleted,
            "operation",
            "transition complete",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.TransitionType] = typeof(SharedElementNavigationTransition).FullName,
                [NavigationDiagnosticDataKeys.TransitionOperation] = "StackPush",
                [NavigationDiagnosticDataKeys.TransitionDurationMs] = 240d,
                [NavigationDiagnosticDataKeys.TransitionElementIds] = "product-123->product-123",
                [NavigationDiagnosticDataKeys.Platform] = "iOS"
            });

        var transition = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationTransitionCompleted);
        Assert.Equal(NavigationDiagnosticPhase.Presentation, transition.Phase);
        Assert.Equal(NavigationDiagnosticSeverity.Information, transition.Severity);
        Assert.Equal("StackPush", transition.Data[NavigationDiagnosticDataKeys.TransitionOperation]);
        Assert.Contains(activity.Tags, tag =>
            tag.Key == "navigation.transition_type" &&
            tag.Value == typeof(SharedElementNavigationTransition).FullName);
        Assert.Contains(activity.Events, activityEvent =>
            activityEvent.Name == nameof(NavigationDiagnosticEventKind.PresentationTransitionCompleted));
    }

    private sealed record InvalidProductRoute(int ProductId) : AppRoute;

    private sealed class TestPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var state = new NavigationState(new[]
            {
                new WindowNode("main", new StackNode("stack", new[] { new RouteEntry("route", context.Route) }))
            }, "main");

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    private sealed class ThrowingPresenter : INavigationPresenter
    {
        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add { }
            remove { }
        }

        public ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Presentation failed.");
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
