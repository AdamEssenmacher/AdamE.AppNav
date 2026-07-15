using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Policies;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Navigation;

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
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public NavigationState? InitialState { get; init; }

    /// <summary>
    /// Gets the request transformers applied before route matching.
    /// </summary>
    public IReadOnlyList<INavigationRequestTransformer> RequestTransformers { get; init; } =
        [];

    /// <summary>
    /// Gets the request policies applied after route matching and before route planning.
    /// </summary>
    public IReadOnlyList<INavigationRequestPolicy> RequestPolicies { get; init; } =
        [];

    /// <summary>
    /// Gets the factory used to select a fallback route when URI route matching reports an unmatched route.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
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
    /// Gets the maximum number of request-target redirects allowed during one navigation operation.
    /// </summary>
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public int MaxRedirects { get; init; } = 16;

    /// <summary>
    /// Gets the maximum number of entries retained in logical navigation history.
    /// </summary>
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public int MaxHistoryEntries { get; init; } = 128;

    /// <summary>
    /// Gets the logger used by navigator diagnostics when <see cref="Diagnostics"/> is not supplied.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public ILogger? Logger { get; init; }

    /// <summary>
    /// Gets the logger factory used to create navigator diagnostics when <see cref="Diagnostics"/> and
    /// <see cref="Logger"/> are not supplied.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
