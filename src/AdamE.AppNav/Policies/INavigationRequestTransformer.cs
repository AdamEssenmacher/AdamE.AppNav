using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Policies;

/// <summary>
/// Transforms a navigation request before route matching.
/// </summary>
public interface INavigationRequestTransformer
{
    /// <summary>
    /// Transforms the supplied request before the router attempts to match it.
    /// </summary>
    ValueTask<RouterNavigationRequest> TransformAsync(
        NavigationRequestTransformContext context,
        CancellationToken cancellationToken = default);
}
