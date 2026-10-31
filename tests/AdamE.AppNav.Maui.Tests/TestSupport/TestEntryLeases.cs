using AdamE.AppNav.Leases;

namespace AdamE.AppNav.Maui.Tests;

internal static class TestEntryLeases
{
    public static RouteEntryLeaseRegistry Create() => new(new InlineHost());

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
