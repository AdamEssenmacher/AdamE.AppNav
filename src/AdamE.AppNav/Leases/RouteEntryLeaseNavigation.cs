using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Leases;

/// <summary>Sends a request that presents the entry of an application-opened lease.</summary>
public static class RouteEntryLeaseNavigation
{
    // Links a request to the lease it presents, so the router can refuse a lease from another registry.
    private static readonly ConditionalWeakTable<RouterNavigationRequest, RouteEntryLease> s_requestLeases = new();

    /// <summary>
    /// Navigates with <paramref name="request"/>, which presents <paramref name="lease"/>'s entry. When the request
    /// ends without a commit that contains the entry, for any reason, the lease is abandoned.
    /// </summary>
    /// <remarks>
    /// That covers a failed or cancelled presentation, a policy rejection, a planner failure, a request cancelled while
    /// queued, and router disposal while it waits. A lease is sent once, and only through the router whose
    /// <c>EntryLeases</c> registry opened it; any other router refuses it before admission.
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
        s_requestLeases.AddOrUpdate(request, lease);
        try
        {
            return await navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_requestLeases.Remove(request);
            lease.Registry.EndRequest(lease);
        }
    }

    internal static bool TryGetLease(RouterNavigationRequest request, [NotNullWhen(true)] out RouteEntryLease? lease)
    {
        return s_requestLeases.TryGetValue(request, out lease);
    }
}
