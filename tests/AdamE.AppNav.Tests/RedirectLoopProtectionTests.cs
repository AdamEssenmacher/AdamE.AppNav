using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Tests;

public sealed class RedirectLoopProtectionTests
{
    [Fact]
    public async Task TransformerTargetChangesRestartInRegistrationOrderAndEmitDiagnostics()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var firstTransformer = new DelegateRequestTransformer(context =>
            context.Request.Route is RedirectRoute { Value: "legacy" }
                ? context.Request.WithTarget(new RedirectRoute("normalized"))
                : context.Request);
        var secondTransformer = new DelegateRequestTransformer(context =>
            context.Request.Route is RedirectRoute { Value: "normalized" }
                ? context.Request.WithTarget(new RedirectRoute("final"))
                : context.Request);
        var provenance = new NavigationRequestProvenance("test-provider", correlationId: "correlation-1");
        var planner = new RecordingPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                RequestTransformers = [firstTransformer, secondTransformer]
            });

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new RedirectRoute("legacy"),
            NavigationRequestSource.Test,
            provenance: provenance));

        Assert.Equal(new RedirectRoute("final"), planner.LastRoute);
        Assert.Equal(provenance, planner.LastRequest!.Provenance);
        Assert.Equal(3, firstTransformer.CallCount);
        Assert.Equal(2, secondTransformer.CallCount);
        Assert.Equal(2, events.Count(diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.RequestRedirected));
        NavigationDiagnosticEvent[] transformStarted = events
            .Where(diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.RequestTransformStarted)
            .ToArray();
        Assert.Equal(5, transformStarted.Length);
        Assert.All(transformStarted, diagnosticEvent =>
        {
            Assert.Equal(
                typeof(DelegateRequestTransformer).FullName,
                diagnosticEvent.Data[NavigationDiagnosticDataKeys.RequestTransformerType]);
            Assert.Equal(NavigationDiagnosticPhase.RequestTransformation, diagnosticEvent.Phase);
        });
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.RequestTransformCompleted &&
            diagnosticEvent.Phase == NavigationDiagnosticPhase.RequestTransformation);
    }

    [Fact]
    public async Task TransformerAndPolicyShareRedirectHistory()
    {
        var transformer = new DelegateRequestTransformer(context =>
            context.Request.Route is RedirectRoute { Value: "legacy" }
                ? context.Request.WithTarget(new RedirectRoute("normalized"))
                : context.Request);
        var policy = new DelegateRequestPolicy(context =>
            context.Route is RedirectRoute { Value: "normalized" }
                ? context.Request.WithTarget(new RedirectRoute("legacy"))
                : context.Request);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RecordingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                RequestTransformers = [transformer],
                RequestPolicies = [policy]
            });

        var exception = await Assert.ThrowsAsync<RouteRedirectLoopException>(() => navigator
            .NavigateAsync(new RedirectRoute("legacy"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Equal(2, exception.Redirects.Count);
        Assert.Equal(new RedirectRoute("legacy"), exception.LastRequest.Route);
        Assert.Empty(navigator.History.Entries);
    }

    [Fact]
    public async Task PolicyRedirectRestartsRequestPolicyPipeline()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var firstPolicy = new DelegateRequestPolicy(context =>
            context.Route is RedirectRoute { Value: "closed" }
                ? RouterNavigationRequest.FromRoute(
                    new RedirectRoute("open"),
                    context.Request.Source,
                    context.Request.WindowId,
                    context.Request.Metadata)
                : context.Request);
        var secondPolicy = new DelegateRequestPolicy(context => context.Request);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RecordingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                RequestPolicies = new INavigationRequestPolicy[] { firstPolicy, secondPolicy }
            });

        var result = await navigator.NavigateAsync(new RedirectRoute("closed"), NavigationRequestSource.Test);

        var route = Assert.IsType<RedirectRoute>(result.Route);
        Assert.Equal("open", route.Value);
        Assert.Equal(2, firstPolicy.CallCount);
        Assert.Equal(1, secondPolicy.CallCount);

        var redirected = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.RequestRedirected);
        Assert.False(string.IsNullOrWhiteSpace(redirected.OperationId));
        Assert.Equal(NavigationDiagnosticPhase.RequestPolicy, redirected.Phase);
        Assert.Equal(LogLevel.Information, redirected.Severity);
        Assert.Equal(1, redirected.Data[NavigationDiagnosticDataKeys.RedirectCount]);
        Assert.Contains("closed", Assert.IsType<string>(redirected.Data[NavigationDiagnosticDataKeys.RedirectFrom]));
        Assert.Contains("open", Assert.IsType<string>(redirected.Data[NavigationDiagnosticDataKeys.RedirectTo]));
        Assert.Contains("closed", Assert.IsType<string>(redirected.Data[NavigationDiagnosticDataKeys.RedirectTrace]));
        Assert.Equal(typeof(DelegateRequestPolicy).FullName, redirected.Data[NavigationDiagnosticDataKeys.PolicyType]);
        Assert.Contains(NavigationDiagnosticEventKind.RequestPolicyStarted, events.Select(diagnosticEvent => diagnosticEvent.Kind));
        Assert.Contains(NavigationDiagnosticEventKind.RequestPolicyCompleted, events.Select(diagnosticEvent => diagnosticEvent.Kind));
    }

    [Fact]
    public async Task TwoPolicyRedirectLoopThrowsAndDoesNotMutateStateOrHistory()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var firstPolicy = new DelegateRequestPolicy(context =>
            context.Route is RedirectRoute { Value: "a" }
                ? RouterNavigationRequest.FromRoute(
                    new RedirectRoute("b"),
                    context.Request.Source,
                    context.Request.WindowId,
                    context.Request.Metadata)
                : context.Request);
        var secondPolicy = new DelegateRequestPolicy(context =>
            context.Route is RedirectRoute { Value: "b" }
                ? RouterNavigationRequest.FromRoute(
                    new RedirectRoute("a"),
                    context.Request.Source,
                    context.Request.WindowId,
                    context.Request.Metadata)
                : context.Request);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RecordingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                RequestPolicies = new INavigationRequestPolicy[] { firstPolicy, secondPolicy }
            });

        var exception = await Assert.ThrowsAsync<RouteRedirectLoopException>(() => navigator
            .NavigateAsync(new RedirectRoute("a"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Equal(new RedirectRoute("a"), exception.InitialRequest.Route);
        Assert.Equal(new RedirectRoute("a"), exception.LastRequest.Route);
        Assert.Equal(2, exception.Redirects.Count);
        Assert.Contains("loop", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(navigator.CurrentState.ActiveWindow);
        Assert.Empty(navigator.History.Entries);

        var loopDetected = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.RequestRedirectLoopDetected);
        Assert.False(string.IsNullOrWhiteSpace(loopDetected.OperationId));
        Assert.Equal(NavigationDiagnosticPhase.RequestPolicy, loopDetected.Phase);
        Assert.Equal(LogLevel.Error, loopDetected.Severity);
        Assert.Equal(typeof(DelegateRequestPolicy).FullName, loopDetected.Data[NavigationDiagnosticDataKeys.PolicyType]);
        Assert.Equal(2, loopDetected.Data[NavigationDiagnosticDataKeys.RedirectCount]);
        Assert.Contains("b", Assert.IsType<string>(loopDetected.Data[NavigationDiagnosticDataKeys.RedirectFrom]));
        Assert.Contains("a", Assert.IsType<string>(loopDetected.Data[NavigationDiagnosticDataKeys.RedirectTo]));
        Assert.Contains("a", Assert.IsType<string>(loopDetected.Data[NavigationDiagnosticDataKeys.RedirectTrace]));
        Assert.Contains(NavigationDiagnosticEventKind.NavigationFailed, events.Select(diagnosticEvent => diagnosticEvent.Kind));
    }

    [Fact]
    public async Task RedirectChainLongerThanMaxRedirectsThrows()
    {
        var policy = new DelegateRequestPolicy(context =>
            context.Route is CountRoute countRoute
                ? RouterNavigationRequest.FromRoute(
                    new CountRoute(countRoute.Value + 1),
                    context.Request.Source,
                    context.Request.WindowId,
                    context.Request.Metadata)
                : context.Request);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RecordingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                MaxRedirects = 2,
                RequestPolicies = new INavigationRequestPolicy[] { policy }
            });

        var exception = await Assert.ThrowsAsync<RouteRedirectLoopException>(() => navigator
            .NavigateAsync(new CountRoute(0), NavigationRequestSource.Test)
            .AsTask());

        Assert.Equal(3, exception.Redirects.Count);
        Assert.Equal(new CountRoute(3), exception.LastRequest.Route);
        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(navigator.CurrentState.ActiveWindow);
        Assert.Empty(navigator.History.Entries);
    }

    [Fact]
    public async Task MaxRedirectsZeroRejectsFirstTargetChangingPolicyResult()
    {
        var policy = new DelegateRequestPolicy(context =>
            RouterNavigationRequest.FromRoute(
                new RedirectRoute("blocked"),
                context.Request.Source,
                context.Request.WindowId,
                context.Request.Metadata));
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RecordingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                MaxRedirects = 0,
                RequestPolicies = new INavigationRequestPolicy[] { policy }
            });

        var exception = await Assert.ThrowsAsync<RouteRedirectLoopException>(() => navigator
            .NavigateAsync(new RedirectRoute("start"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Single(exception.Redirects);
        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(navigator.History.Entries);
    }

    [Fact]
    public async Task MetadataOnlyPolicyChangesDoNotCountAsRedirectsOrRestartPipeline()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var planner = new RecordingPlanner();
        var metadataPolicy = new DelegateRequestPolicy(context =>
        {
            RouterNavigationRequest request = context.Request;
            var metadata = new Dictionary<string, object?>(request.Metadata)
            {
                ["normalized"] = true
            };

            return request with
            {
                Metadata = metadata,
                Timestamp = request.Timestamp.AddMinutes(5)
            };
        });
        var nextPolicy = new DelegateRequestPolicy(context => context.Request);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                RequestPolicies = new INavigationRequestPolicy[] { metadataPolicy, nextPolicy }
            });

        await navigator.NavigateAsync(new RedirectRoute("stable"), NavigationRequestSource.Test);

        Assert.Equal(1, metadataPolicy.CallCount);
        Assert.Equal(1, nextPolicy.CallCount);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.RequestRedirected, events);
        Assert.NotNull(planner.LastRequest);
        Assert.True(Assert.IsType<bool>(planner.LastRequest.Metadata["normalized"]));
    }

    [Fact]
    public async Task FallbackSelectedRouteCanBeRedirectedThroughRequestPolicy()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var policy = new DelegateRequestPolicy(context =>
            context.Route is MissingRoute
                ? RouterNavigationRequest.FromRoute(
                    new TestRoutes.StoreRoute("northwind"),
                    context.Request.Source,
                    context.Request.WindowId,
                    context.Request.Metadata)
                : context.Request);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RecordingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                FallbackRouteFactory = context => new MissingRoute(context.Request.Uri!),
                RequestPolicies = new INavigationRequestPolicy[] { policy }
            });

        var result = await navigator.NavigateAsync(
            new Uri("https://example.com/missing/product"),
            NavigationRequestSource.Test);

        var route = Assert.IsType<TestRoutes.StoreRoute>(result.Route);
        Assert.Equal("northwind", route.StoreId);
        Assert.Contains(NavigationDiagnosticEventKind.RouteFallbackSelected, events);
        Assert.Contains(NavigationDiagnosticEventKind.RequestRedirected, events);
        Assert.Equal(route, navigator.History.Current!.Route);
    }

    [Fact]
    public void NegativeMaxRedirectsThrowsDuringNavigatorConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RouterNavigator(
            TestRoutes.CreateTable(),
            new RecordingPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { MaxRedirects = -1 }));
    }

    private sealed record RedirectRoute(string Value) : AppRoute;

    private sealed record CountRoute(int Value) : AppRoute;

    private sealed record MissingRoute(Uri Uri) : AppRoute;

    private sealed class DelegateRequestPolicy : INavigationRequestPolicy
    {
        private readonly Func<NavigationRequestPolicyContext, RouterNavigationRequest> _apply;

        public DelegateRequestPolicy(Func<NavigationRequestPolicyContext, RouterNavigationRequest> apply)
        {
            _apply = apply;
        }

        public int CallCount { get; private set; }

        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_apply(context));
        }
    }

    private sealed class DelegateRequestTransformer : INavigationRequestTransformer
    {
        private readonly Func<NavigationRequestTransformContext, RouterNavigationRequest> _transform;

        public DelegateRequestTransformer(Func<NavigationRequestTransformContext, RouterNavigationRequest> transform)
        {
            _transform = transform;
        }

        public int CallCount { get; private set; }

        public ValueTask<RouterNavigationRequest> TransformAsync(
            NavigationRequestTransformContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_transform(context));
        }
    }

    private sealed class RecordingPlanner : IAppNavigationPlanner
    {
        public RouterNavigationRequest? LastRequest { get; private set; }

        public AppRoute? LastRoute { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            LastRequest = context.Request;
            LastRoute = context.Route;
            var state = new NavigationState(new[]
            {
                new WindowNode("main", new StackNode("stack", new[] { new RouteEntry("route", context.Route) }))
            }, "main");

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }
}
