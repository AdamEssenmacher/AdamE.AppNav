using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Configures a router navigator created through <see cref="RouterNavigatorFactory"/>.
/// </summary>
/// <remarks>
/// These options are intended for host adapters and advanced composition roots that need to create
/// the core router without depending on its concrete implementation type.
/// </remarks>
public sealed class RouterNavigatorFactoryOptions
{
    /// <summary>
    /// Gets the initial router state, or <see langword="null"/> to start with an empty state.
    /// </summary>
    public NavigationState? InitialState { get; init; }

    /// <summary>
    /// Gets the initial logical navigation history, or <see langword="null"/> to start with empty history.
    /// </summary>
    public NavigationHistory? InitialHistory { get; init; }

    /// <summary>
    /// Gets the request policies applied before route planning.
    /// </summary>
    public IReadOnlyList<INavigationRequestPolicy> RequestPolicies { get; init; } =
        Array.Empty<INavigationRequestPolicy>();

    /// <summary>
    /// Gets the plan policies applied after route planning and before presentation.
    /// </summary>
    public IReadOnlyList<INavigationPlanPolicy> PlanPolicies { get; init; } = Array.Empty<INavigationPlanPolicy>();

    /// <summary>
    /// Gets the factory used to select a fallback route when URI route matching reports an unmatched route.
    /// </summary>
    public Func<NavigationFallbackContext, AppRoute?>? FallbackRouteFactory { get; init; }

    /// <summary>
    /// Gets the diagnostics pipeline used by the navigator, or <see langword="null"/> to create one
    /// from logging options.
    /// </summary>
    public NavigationDiagnostics? Diagnostics { get; init; }

    /// <summary>
    /// Gets the back-navigation strategy used by the navigator, or <see langword="null"/> to use the default strategy.
    /// </summary>
    public IBackNavigator? BackNavigator { get; init; }

    /// <summary>
    /// Gets the maximum number of request-policy redirects allowed during one navigation operation.
    /// </summary>
    public int MaxRedirects { get; init; } = 16;

    /// <summary>
    /// Gets the maximum number of entries retained in logical navigation history.
    /// </summary>
    public int MaxHistoryEntries { get; init; } = 128;

    /// <summary>
    /// Gets the persistence configuration used for navigation snapshots.
    /// </summary>
    public NavigationPersistenceOptions? Persistence { get; init; }

    /// <summary>
    /// Gets the logger used by navigator diagnostics when <see cref="Diagnostics"/> is not supplied.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// Gets the logger factory used to create navigator diagnostics when <see cref="Diagnostics"/> and
    /// <see cref="Logger"/> are not supplied.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
