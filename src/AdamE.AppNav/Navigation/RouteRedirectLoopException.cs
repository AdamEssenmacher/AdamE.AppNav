using AdamE.AppNav.Requests;
using JetBrains.Annotations;

namespace AdamE.AppNav.Navigation;

/// <summary>
/// Represents a request redirect chain that exceeded the router's redirect limit or repeated a prior target.
/// </summary>
/// <param name="initialRequest">The navigation request that started redirect evaluation.</param>
/// <param name="lastRequest">The final redirect target that caused loop detection or exceeded the redirect limit.</param>
/// <param name="redirects">The redirect targets followed after the initial request.</param>
/// <param name="message">The message that describes why redirect evaluation failed.</param>
/// <remarks>
/// The router throws this exception before mutating navigation state or history when request transformers or policies
/// cannot produce a stable target. Inspect <see cref="Redirects"/> to diagnose the redirect chain.
/// </remarks>
public sealed class RouteRedirectLoopException(
    RouterNavigationRequest initialRequest,
    RouterNavigationRequest lastRequest,
    IReadOnlyList<RouterNavigationRequest> redirects,
    string message)
    : Exception(message)
{
    /// <summary>
    /// Gets the navigation request that started redirect evaluation.
    /// </summary>
    [UsedImplicitly]
    public RouterNavigationRequest InitialRequest { get; } = initialRequest;

    /// <summary>
    /// Gets the redirect target that caused loop detection or exceeded the redirect limit.
    /// </summary>
    [UsedImplicitly]
    public RouterNavigationRequest LastRequest { get; } = lastRequest;

    /// <summary>
    /// Gets the redirect targets followed after the initial request.
    /// </summary>
    [UsedImplicitly]
    public IReadOnlyList<RouterNavigationRequest> Redirects { get; } = redirects.ToArray();
}
