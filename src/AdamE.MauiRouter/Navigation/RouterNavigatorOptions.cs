using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Navigation;

internal sealed class RouterNavigatorOptions
{
    public NavigationState? InitialState { get; set; }

    public NavigationHistory? InitialHistory { get; set; }

    public IReadOnlyList<INavigationRequestPolicy> RequestPolicies { get; set; } = Array.Empty<INavigationRequestPolicy>();

    public IReadOnlyList<INavigationPlanPolicy> PlanPolicies { get; set; } = Array.Empty<INavigationPlanPolicy>();

    public Func<NavigationFallbackContext, AppRoute?>? FallbackRouteFactory { get; set; }

    public NavigationDiagnostics? Diagnostics { get; set; }

    public IBackNavigator? BackNavigator { get; set; }

    public int MaxRedirects { get; set; } = 16;

    public int MaxHistoryEntries { get; set; } = 128;

    public NavigationPersistenceOptions? Persistence { get; set; }

    public ILogger? Logger { get; set; }

    public ILoggerFactory? LoggerFactory { get; set; }
}
