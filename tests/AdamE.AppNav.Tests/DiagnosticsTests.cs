using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AdamE.AppNav.Tests;

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
        Assert.Equal(LogLevel.Information, routeMatched.Severity);
        Assert.Equal("/stores/{storeId}/products/{productId:int}", routeMatched.Data[NavigationDiagnosticDataKeys.RouteTemplate]);
        Assert.DoesNotContain(routeMatched.Data.Keys, static key =>
            key.StartsWith("provenance", StringComparison.Ordinal));
        Assert.True(routeMatched.Data.ContainsKey(NavigationDiagnosticDataKeys.DurationMs));
    }

    [Fact]
    public void SafeModeSanitizesBeforeObserversLoggersAndActivity()
    {
        var logger = new CapturingLogger();
        var diagnostics = new NavigationDiagnostics(logger);
        NavigationDiagnosticEvent? observed = null;
        diagnostics.EventWritten += (_, diagnosticEvent) => observed = diagnosticEvent;
        var observer = new CapturingObserver();
        diagnostics.AddObserver(observer);
        using var activity = new Activity("safe-diagnostics").Start();

        diagnostics.Write(
            NavigationDiagnosticEventKind.RouteMatchingFailed,
            "operation",
            "secret-message",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Uri] = "https://user:password@example.com/path-secret?token=query-secret#fragment-secret",
                [NavigationDiagnosticDataKeys.Path] = "/path-secret",
                [NavigationDiagnosticDataKeys.ExceptionType] = typeof(InvalidOperationException).FullName,
                [NavigationDiagnosticDataKeys.ExceptionMessage] = "exception-secret",
                [NavigationDiagnosticDataKeys.RouteDiagnosticMessage] = "route-secret",
                [NavigationDiagnosticDataKeys.ProvenanceCorrelationId] = "correlation-secret",
                [NavigationDiagnosticDataKeys.ProvenanceOriginalUri] = "myapp:original-secret",
                [NavigationDiagnosticDataKeys.ProvenanceReferrerUri] = "https://referrer.example/referrer-secret",
                [NavigationDiagnosticDataKeys.ProvenanceAttributes] = new Dictionary<string, string?>
                {
                    ["campaign"] = "attribute-secret"
                },
                [NavigationDiagnosticDataKeys.RedirectFrom] =
                    "uri=https://example.com/redirect-secret?token=redirect-query-secret [Test, disposition=Auto, window=window-secret]",
                [NavigationDiagnosticDataKeys.RedirectTo] =
                    "route=SecretRoute:route-value-secret [Test, disposition=Replace, window=window-secret]",
                [NavigationDiagnosticDataKeys.RedirectTrace] =
                    "uri=/relative-secret [Test, disposition=Auto] -> route=SecretRoute:trace-secret [Test, disposition=Replace]",
                [NavigationDiagnosticDataKeys.WindowId] = "window-secret",
                [NavigationDiagnosticDataKeys.HostId] = "host-secret",
                [NavigationDiagnosticDataKeys.BranchId] = "branch-secret",
                [NavigationDiagnosticDataKeys.RouteEntryId] = "entry-secret",
                [NavigationDiagnosticDataKeys.PresentationOwnerRouteEntryId] = "owner-secret",
                [NavigationDiagnosticDataKeys.PresentationPageKey] = "page-key-secret",
                [NavigationDiagnosticDataKeys.ModalId] = "modal-secret",
                [NavigationDiagnosticDataKeys.PresentationPath] = "presentation-path-secret",
                [NavigationDiagnosticDataKeys.PresentationExpected] = "expected-secret",
                [NavigationDiagnosticDataKeys.PresentationActual] = "actual-secret"
            });

        Assert.NotNull(observed);
        Assert.Same(observed, observer.Event);
        Assert.Equal("https://example.com", observed!.Data[NavigationDiagnosticDataKeys.Uri]);
        Assert.Equal(
            "uri=https://example.com [Test, disposition=Auto]",
            observed.Data[NavigationDiagnosticDataKeys.RedirectFrom]);
        Assert.Equal(
            "route=SecretRoute [Test, disposition=Replace]",
            observed.Data[NavigationDiagnosticDataKeys.RedirectTo]);
        Assert.Equal(
            "uri=<relative-uri> [Test, disposition=Auto] -> route=SecretRoute [Test, disposition=Replace]",
            observed.Data[NavigationDiagnosticDataKeys.RedirectTrace]);
        Assert.False(observed.Data.ContainsKey(NavigationDiagnosticDataKeys.ExceptionMessage));
        Assert.False(observed.Data.ContainsKey(NavigationDiagnosticDataKeys.RouteDiagnosticMessage));
        Assert.False(observed.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceCorrelationId));
        foreach (string omittedKey in new[]
                 {
                     NavigationDiagnosticDataKeys.Path,
                     NavigationDiagnosticDataKeys.ProvenanceProvider,
                     NavigationDiagnosticDataKeys.ProvenanceOriginalUri,
                     NavigationDiagnosticDataKeys.ProvenanceReferrerUri,
                     NavigationDiagnosticDataKeys.ProvenanceCorrelationId,
                     NavigationDiagnosticDataKeys.ProvenanceIsColdStart,
                     NavigationDiagnosticDataKeys.ProvenanceAttributes,
                     NavigationDiagnosticDataKeys.WindowId,
                     NavigationDiagnosticDataKeys.HostId,
                     NavigationDiagnosticDataKeys.BranchId,
                     NavigationDiagnosticDataKeys.RouteEntryId,
                     NavigationDiagnosticDataKeys.PresentationOwnerRouteEntryId,
                     NavigationDiagnosticDataKeys.PresentationPageKey,
                     NavigationDiagnosticDataKeys.ModalId,
                     NavigationDiagnosticDataKeys.PresentationPath,
                     NavigationDiagnosticDataKeys.PresentationExpected,
                     NavigationDiagnosticDataKeys.PresentationActual
                 })
        {
            Assert.False(observed.Data.ContainsKey(omittedKey));
        }

        string emitted = string.Join("|", logger.Entries.Select(static entry => entry.Message)) +
                         string.Join("|", activity.TagObjects.Select(static tag => tag.Value?.ToString())) +
                         observed + observer.Event;
        foreach (string secret in new[]
                 {
                     "password", "path-secret", "query-secret", "fragment-secret", "exception-secret",
                     "route-secret", "correlation-secret", "attribute-secret", "secret-message",
                     "original-secret", "referrer-secret", "redirect-secret", "redirect-query-secret",
                     "route-value-secret", "relative-secret", "trace-secret", "window-secret", "host-secret",
                     "branch-secret", "entry-secret", "owner-secret", "page-key-secret", "modal-secret",
                     "presentation-path-secret", "expected-secret", "actual-secret"
                 })
        {
            Assert.DoesNotContain(secret, emitted, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, observed.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FullModeExposesRawEventAndRedactorFailureFallsBackToSafeData()
    {
        var capturingRedactor = new CapturingRedactor();
        var fullDiagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full },
            redactor: capturingRedactor);
        fullDiagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            "operation",
            "raw-message",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Uri] = "https://example.com/path?token=raw-secret"
            });

        Assert.Equal("raw-message", capturingRedactor.Input!.Message);
        Assert.Contains("raw-secret", capturingRedactor.Input.Data[NavigationDiagnosticDataKeys.Uri]!.ToString());

        var failingDiagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full },
            redactor: new ThrowingRedactor());
        NavigationDiagnosticEvent? fallback = null;
        failingDiagnostics.EventWritten += (_, diagnosticEvent) => fallback = diagnosticEvent;
        failingDiagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            "operation",
            "raw-secret",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Uri] = "https://example.com/path?token=raw-secret"
            });

        Assert.NotNull(fallback);
        Assert.Equal("https://example.com", fallback!.Data[NavigationDiagnosticDataKeys.Uri]);
        Assert.DoesNotContain("raw-secret", fallback.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SafeModeSuppliesBuiltInSanitizedInputToCustomRedactor()
    {
        var redactor = new CapturingRedactor();
        var diagnostics = new NavigationDiagnostics(redactor: redactor);

        diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            "operation",
            "message-secret",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Uri] = "https://user:password@example.com/path?token=query-secret",
                [NavigationDiagnosticDataKeys.ExceptionMessage] = "exception-secret"
            });

        Assert.NotNull(redactor.Input);
        Assert.Equal("https://example.com", redactor.Input!.Data[NavigationDiagnosticDataKeys.Uri]);
        Assert.False(redactor.Input.Data.ContainsKey(NavigationDiagnosticDataKeys.ExceptionMessage));
        Assert.DoesNotContain("secret", redactor.Input.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SafeModeSanitizerFailureFallsBackWithoutAffectingTheCaller()
    {
        var diagnostics = new NavigationDiagnostics();
        NavigationDiagnosticEvent? observed = null;
        diagnostics.EventWritten += (_, diagnosticEvent) => observed = diagnosticEvent;

        diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            "operation",
            "raw-message",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Uri] = new ThrowingDiagnosticValue()
            });

        Assert.NotNull(observed);
        Assert.Equal("Navigation diagnostic: AppLinkReceived.", observed!.Message);
        Assert.Empty(observed.Data);
    }

    [Fact]
    public void LoggerFailureDoesNotPreventObserverDelivery()
    {
        var diagnostics = new NavigationDiagnostics(new ThrowingLogger());
        NavigationDiagnosticEvent? observed = null;
        diagnostics.EventWritten += (_, diagnosticEvent) => observed = diagnosticEvent;

        diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            "operation",
            "message");

        Assert.NotNull(observed);
        Assert.Equal(NavigationDiagnosticEventKind.AppLinkReceived, observed!.Kind);
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
            .NavigateAsync(RouterNavigationRequest.FromUri(
                new Uri("https://example.com/products/not-a-number"), NavigationRequestSource.Test))
            .AsTask());

        var notMatched = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.RouteNotMatched);
        Assert.Equal(NavigationDiagnosticPhase.RouteMatching, notMatched.Phase);
        Assert.Equal(LogLevel.Warning, notMatched.Severity);
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
            .NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test))
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
        Assert.DoesNotContain(activity.Tags, tag => tag.Key.StartsWith("navigation.provenance.", StringComparison.Ordinal));
        Assert.DoesNotContain(activity.TagObjects, tag =>
            tag.Key == "navigation.provenance.correlation_id");
        Assert.Equal(typeof(TestRoutes.ProductDetailRoute).FullName, activity.Tags.Single(tag => tag.Key == "navigation.route_type").Value);
        Assert.Equal("/stores/{storeId}/products/{productId:int}", activity.Tags.Single(tag => tag.Key == "navigation.route_template").Value);
        Assert.Contains(activity.Events, activityEvent => activityEvent.Name == nameof(NavigationDiagnosticEventKind.RouteMatched));
        string activityText = string.Join(
            "|",
            activity.TagObjects.Select(static tag => $"{tag.Key}:{tag.Value}")
                .Concat(activity.Events.SelectMany(static activityEvent =>
                    activityEvent.Tags.Select(tag => $"{tag.Key}:{tag.Value}"))));
        Assert.DoesNotContain("correlation-1", activityText, StringComparison.Ordinal);
        Assert.DoesNotContain("spring", activityText, StringComparison.Ordinal);
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
            NavigationReconciliationSource.HostBack,
            new TestRoutes.StoreRoute("northwind"),
            "test reconciliation"));

        Assert.Contains(NavigationDiagnosticEventKind.PresentationStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationCompleted, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
        Assert.Single(navigator.History.Entries);
        Assert.Equal(NavigationRequestSource.HostReconciliation, navigator.History.Current!.Request.Source);
    }

    [Fact]
    public void PresenterLifecycleDiagnosticsUsePresentationPhaseAndStableKeys()
    {
        var diagnostics = FullDiagnostics();
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
        Assert.Equal(LogLevel.Debug, pageCreated.Severity);
        Assert.Equal("SamplePage", pageCreated.Data[NavigationDiagnosticDataKeys.PageType]);
        Assert.Equal("root", pageCreated.Data[NavigationDiagnosticDataKeys.HostId]);
        Assert.Equal("catalog", pageCreated.Data[NavigationDiagnosticDataKeys.BranchId]);
        Assert.Equal("product", pageCreated.Data[NavigationDiagnosticDataKeys.RouteEntryId]);
        Assert.Equal("modal", pageCreated.Data[NavigationDiagnosticDataKeys.ModalId]);
        Assert.Equal("Page.Disappearing", pageCreated.Data[NavigationDiagnosticDataKeys.HandlerName]);

        var presenterDisposed = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationPresenterDisposed);
        Assert.Equal(NavigationDiagnosticPhase.Presentation, presenterDisposed.Phase);
        Assert.Equal(LogLevel.Information, presenterDisposed.Severity);
    }

    [Fact]
    public void StartupDiagnosticsUseStartupPhaseAndStableKeys()
    {
        var diagnostics = FullDiagnostics();
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
                [NavigationDiagnosticDataKeys.StartupOutcome] = "FallbackNavigated"
            });

        var startupStarted = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupStarted);
        Assert.Equal(NavigationDiagnosticPhase.Startup, startupStarted.Phase);
        Assert.Equal(LogLevel.Debug, startupStarted.Severity);
        Assert.Equal("main", startupStarted.Data[NavigationDiagnosticDataKeys.WindowId]);
        Assert.Equal(250d, startupStarted.Data[NavigationDiagnosticDataKeys.AppLinkGraceMs]);

        var startupCompleted = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupCompleted);
        Assert.Equal(NavigationDiagnosticPhase.Startup, startupCompleted.Phase);
        Assert.Equal(LogLevel.Information, startupCompleted.Severity);
        Assert.Equal("FallbackNavigated", startupCompleted.Data[NavigationDiagnosticDataKeys.StartupOutcome]);
    }

    private sealed record InvalidProductRoute(int ProductId) : AppRoute;

    private static NavigationDiagnostics FullDiagnostics() => new(
        options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });

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

    private sealed class CapturingRedactor : INavigationDiagnosticRedactor
    {
        public NavigationDiagnosticEvent? Input { get; private set; }

        public NavigationDiagnosticEvent Redact(NavigationDiagnosticEvent diagnosticEvent)
        {
            Input = diagnosticEvent;
            return diagnosticEvent;
        }
    }

    private sealed class CapturingObserver : INavigationDiagnosticObserver
    {
        public NavigationDiagnosticEvent? Event { get; private set; }

        public void OnNavigationDiagnosticEvent(NavigationDiagnosticEvent diagnosticEvent)
        {
            Event = diagnosticEvent;
        }
    }

    private sealed class ThrowingRedactor : INavigationDiagnosticRedactor
    {
        public NavigationDiagnosticEvent Redact(NavigationDiagnosticEvent diagnosticEvent) =>
            throw new InvalidOperationException("redactor-secret");
    }

    private sealed class ThrowingDiagnosticValue
    {
        public override string ToString() => throw new InvalidOperationException("value-secret");
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger-secret");
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
