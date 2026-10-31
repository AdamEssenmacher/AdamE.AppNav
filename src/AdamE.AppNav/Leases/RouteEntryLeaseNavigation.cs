using AdamE.AppNav.Navigation;
using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Leases;

/// <summary>Sends a request that presents the entry of an application-opened lease.</summary>
public static class RouteEntryLeaseNavigation
{
    /// <summary>
    /// Navigates with <paramref name="request"/>, which presents <paramref name="lease"/>'s entry. When the request
    /// ends without a commit that contains the entry, for any reason, the lease is abandoned.
    /// </summary>
    /// <remarks>
    /// That covers a failed or cancelled presentation, a policy rejection, a planner failure, a request cancelled while
    /// queued, and router disposal while it waits. A lease is sent once.
    /// </remarks>
    public static async ValueTask<NavigationResult> NavigateAsync(
        this IRouterNavigator navigator,
        RouterNavigationRequest request,
        RouteEntryLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);

        lease.Registry.Send(lease);
        try
        {
            return await navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Registry.EndRequest(lease);
        }
    }
}
