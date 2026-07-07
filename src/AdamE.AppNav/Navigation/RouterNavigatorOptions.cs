using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Back;
using AdamE.AppNav.History;
using AdamE.AppNav.Policies;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Navigation;

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
