using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Navigation;

internal sealed class RouterNavigatorOptions
{
    public NavigationState? InitialState { get; init; }

    public NavigationHistory? InitialHistory { get; init; }

    public IReadOnlyList<INavigationRequestPolicy> RequestPolicies { get; init; } = [];

    public Func<NavigationFallbackContext, AppRoute?>? FallbackRouteFactory { get; init; }

    public NavigationDiagnostics? Diagnostics { get; init; }

    public IBackNavigator? BackNavigator { get; init; }

    public int MaxRedirects { get; init; } = 16;

    public int MaxHistoryEntries { get; init; } = 128;

    public ILogger? Logger { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }
}
