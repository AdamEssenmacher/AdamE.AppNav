using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class RedirectLoopProtectionTests
{
    [Fact]
    public async Task PolicyRedirectRestartsRequestPolicyPipeline()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var firstPolicy = new DelegateRequestPolicy((context, request) =>
            context.Route is RedirectRoute { Value: "closed" }
                ? RouterNavigationRequest.FromRoute(new RedirectRoute("open"), request.Source, request.WindowId, request.Metadata)
                : request);
        var secondPolicy = new DelegateRequestPolicy((_, request) => request);
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
        Assert.Equal(NavigationDiagnosticSeverity.Information, redirected.Severity);
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
        var firstPolicy = new DelegateRequestPolicy((context, request) =>
            context.Route is RedirectRoute { Value: "a" }
                ? RouterNavigationRequest.FromRoute(new RedirectRoute("b"), request.Source, request.WindowId, request.Metadata)
                : request);
        var secondPolicy = new DelegateRequestPolicy((context, request) =>
            context.Route is RedirectRoute { Value: "b" }
                ? RouterNavigationRequest.FromRoute(new RedirectRoute("a"), request.Source, request.WindowId, request.Metadata)
                : request);
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
        Assert.Equal(NavigationDiagnosticSeverity.Error, loopDetected.Severity);
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
        var policy = new DelegateRequestPolicy((context, request) =>
            context.Route is CountRoute countRoute
                ? RouterNavigationRequest.FromRoute(new CountRoute(countRoute.Value + 1), request.Source, request.WindowId, request.Metadata)
                : request);
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
        var policy = new DelegateRequestPolicy((_, request) =>
            RouterNavigationRequest.FromRoute(new RedirectRoute("blocked"), request.Source, request.WindowId, request.Metadata));
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
        var metadataPolicy = new DelegateRequestPolicy((_, request) =>
        {
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
        var nextPolicy = new DelegateRequestPolicy((_, request) => request);
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
        var policy = new DelegateRequestPolicy((context, request) =>
            context.Route is MissingRoute
                ? RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("northwind"), request.Source, request.WindowId, request.Metadata)
                : request);
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
        private readonly Func<NavigationRequestPolicyContext, RouterNavigationRequest, RouterNavigationRequest> _apply;

        public DelegateRequestPolicy(Func<NavigationRequestPolicyContext, RouterNavigationRequest, RouterNavigationRequest> apply)
        {
            _apply = apply;
        }

        public int CallCount { get; private set; }

        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_apply(context, request));
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
