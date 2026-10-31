using AdamE.AppNav.Leases;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Maui.Tests;

internal static class TestEntryLeases
{
    // Presenter-only tests commit states themselves, so the registry is bound as a router would bind it.
    public static RouteEntryLeaseRegistry Create(NavigationState? committed = null) =>
        Bound(new RouteEntryLeaseRegistry(new InlineHost()), committed);

    public static RouteEntryLeaseRegistry Bound(RouteEntryLeaseRegistry leases, NavigationState? committed = null)
    {
        leases.Bind(committed ?? NavigationState.Empty);
        return leases;
    }

    public static MauiRoutePageFactory Factory(
        IServiceProvider provider,
        MauiRoutePresentationOptions options,
        RouteEntryLeaseRegistry? leases = null)
    {
        return new MauiRoutePageFactory(provider, options, leases ?? Create());
    }

    public static async Task RetireAndReleaseAsync(RouteEntryLeaseRegistry leases, string entryId)
    {
        Assert.True(leases.TryGet(entryId, out RouteEntryLease? lease));
        lease.Retire();
        await lease.Released.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class InlineHost : IRouteEntryLeaseHost
    {
        public Task InvokeAsync(Func<ValueTask> work) => work().AsTask();

        public void ReportDefect(RouteEntryLeaseReleaseException defect)
        {
        }

        public void ReportFatal(RouteEntryLeaseReleaseException failure)
        {
            // Tests that release unsafely assert on the lease, which never completes Released.
        }
    }
}
