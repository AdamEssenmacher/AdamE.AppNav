using AdamE.AppNav.Internal;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Policies;

public sealed record NavigationRequestPolicyContext
{
    public NavigationRequestPolicyContext(
        RouterNavigationRequest request,
        AppRoute route,
        IReadOnlyDictionary<string, object?>? routeMetadata,
        NavigationState currentState,
        string operationId)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Route = route ?? throw new ArgumentNullException(nameof(route));
        RouteMetadata = CollectionSnapshot.MetadataDictionary(routeMetadata);
        CurrentState = currentState ?? throw new ArgumentNullException(nameof(currentState));
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
    }

    /// <summary>
    /// Gets the request envelope supplied to this policy. Its metadata contains only values
    /// explicitly carried by the request, not metadata produced by route matching.
    /// </summary>
    public RouterNavigationRequest Request { get; }

    /// <summary>
    /// Gets the route currently selected for the request target.
    /// </summary>
    public AppRoute Route { get; }

    /// <summary>
    /// Gets metadata produced while matching <see cref="Route"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?> RouteMetadata { get; }

    public NavigationState CurrentState { get; }

    public string OperationId { get; }
}
