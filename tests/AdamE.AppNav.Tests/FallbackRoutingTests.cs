using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Tests;

public sealed class FallbackRoutingTests
{
    [Fact]
    public async Task UnmatchedUriUsesFallbackRouteAndContinuesThroughPipeline()
    {
        NavigationFallbackContext? fallbackContext = null;
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var planner = new EchoPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                FallbackRouteFactory = context =>
                {
                    fallbackContext = context;
                    return new NotFoundRoute(context.Request.Uri!);
                }
            });
        var uri = new Uri("https://example.com/missing/page");

        var result = await navigator.NavigateAsync(uri, NavigationRequestSource.Test);

        var route = Assert.IsType<NotFoundRoute>(result.Route);
        Assert.Equal(uri, route.Uri);
        Assert.Same(result.Route, planner.LastRoute);
        Assert.Same(result.Route, navigator.History.Current!.Route);
        Assert.NotNull(fallbackContext);
        Assert.Equal(uri, fallbackContext.Request.Uri);
        Assert.Contains(fallbackContext.Diagnostics, diagnostic => diagnostic.Code == "route.not_matched");
        Assert.Equal("route.not_matched", fallbackContext.Diagnostics[0].Code);
        Assert.False(string.IsNullOrWhiteSpace(fallbackContext.OperationId));

        var fallbackSelected = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.RouteFallbackSelected);
        Assert.Equal(NavigationDiagnosticPhase.RouteMatching, fallbackSelected.Phase);
        Assert.Equal(LogLevel.Information, fallbackSelected.Severity);
        Assert.Equal("https://example.com", fallbackSelected.Data[NavigationDiagnosticDataKeys.Uri]);
        Assert.Equal("route.not_matched", fallbackSelected.Data[NavigationDiagnosticDataKeys.RouteDiagnosticCode]);
        Assert.Equal(typeof(NotFoundRoute).FullName, fallbackSelected.Data[NavigationDiagnosticDataKeys.RouteType]);
        Assert.Contains(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.RouteNotMatched);
    }

    [Fact]
    public async Task RouteValueFailuresDoNotUseFallbackRoute()
    {
        var fallbackCalled = false;
        var routeTable = RouteTable.Create(routes => routes.Map(
            "/products/{productId}",
            match => new ProductRoute(match.Path<int>("productId")),
            format => format.PathParam("productId", route => route.ProductId)));
        var navigator = new RouterNavigator(
            routeTable,
            new EchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                FallbackRouteFactory = _ =>
                {
                    fallbackCalled = true;
                    return new NotFoundRoute(new Uri("https://example.com/fallback"));
                }
            });

        await Assert.ThrowsAsync<RouteNotMatchedException>(() => navigator
            .NavigateAsync(new Uri("https://example.com/products/not-a-number"), NavigationRequestSource.Test)
            .AsTask());

        Assert.False(fallbackCalled);
        Assert.Empty(navigator.History.Entries);
    }

    [Fact]
    public async Task MissingFallbackFactoryPreservesRouteNotMatchedException()
    {
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new EchoPlanner(),
            NullNavigationPresenter.Instance);

        await Assert.ThrowsAsync<RouteNotMatchedException>(() => navigator
            .NavigateAsync(new Uri("https://example.com/missing/page"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Empty(navigator.History.Entries);
    }

    [Fact]
    public async Task NullFallbackRoutePreservesRouteNotMatchedException()
    {
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new EchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                FallbackRouteFactory = _ => null
            });

        await Assert.ThrowsAsync<RouteNotMatchedException>(() => navigator
            .NavigateAsync(new Uri("https://example.com/missing/page"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Empty(navigator.History.Entries);
    }

    [Fact]
    public async Task FallbackFactoryFailureDoesNotMutateStateOrHistory()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new EchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                FallbackRouteFactory = _ => throw new InvalidOperationException("Fallback failed.")
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator
            .NavigateAsync(new Uri("https://example.com/missing/page"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Null(navigator.CurrentState.ActiveWindow);
        Assert.Empty(navigator.History.Entries);
        Assert.Contains(NavigationDiagnosticEventKind.RouteMatchingFailed, events);
        Assert.Contains(NavigationDiagnosticEventKind.NavigationFailed, events);
    }

    private sealed class EchoPlanner : IAppNavigationPlanner
    {
        public AppRoute? LastRoute { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            LastRoute = context.Route;
            var state = new NavigationState(new[]
            {
                new WindowNode("main", new StackNode("stack", new[] { new RouteEntry("route", context.Route) }))
            }, "main");

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    private sealed record NotFoundRoute(Uri Uri) : AppRoute;

    private sealed record ProductRoute(int ProductId) : AppRoute;
}
