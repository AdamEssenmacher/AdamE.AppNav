using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Requests;
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

    /// <summary>
    /// Appends a request policy that rejects external URI requests unless their origin is trusted.
    /// </summary>
    /// <param name="origins">Trusted absolute URI origins.</param>
    /// <returns>The same options instance for configuration chaining.</returns>
    public RouterNavigatorOptions RequireTrustedUriOrigins(params Uri[] origins)
    {
        return RequireTrustedUriOrigins((IEnumerable<Uri>)origins);
    }

    /// <summary>
    /// Appends a request policy that rejects URI requests from the selected sources unless their origin is trusted.
    /// </summary>
    /// <param name="origins">Trusted absolute URI origins.</param>
    /// <param name="sources">Request sources guarded by the policy. Defaults to app links, push links, and QR links.</param>
    /// <returns>The same options instance for configuration chaining.</returns>
    public RouterNavigatorOptions RequireTrustedUriOrigins(
        IEnumerable<Uri> origins,
        IEnumerable<NavigationRequestSource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(origins);

        RequestPolicies = RequestPolicies
            .Concat(new[] { new AllowedUriOriginPolicy(origins, sources) })
            .ToArray();
        return this;
    }
}
