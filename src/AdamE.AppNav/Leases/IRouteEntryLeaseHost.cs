namespace AdamE.AppNav.Leases;

/// <summary>
/// Runs entry-lease releases where the host's resources must be disposed, and receives releases that are unsafe.
/// </summary>
/// <remarks>
/// A UI host runs <see cref="InvokeAsync"/> on its UI dispatcher. <see cref="ReportFatal"/> must surface the failure
/// through the host's fatal boundary: the lease has kept its resources and will not release them.
/// </remarks>
public interface IRouteEntryLeaseHost
{
    /// <summary>Runs <paramref name="release"/> on the host's dispatcher and completes when it has finished.</summary>
    Task InvokeAsync(Func<ValueTask> release);

    /// <summary>Surfaces a release that could not be shown safe.</summary>
    void ReportFatal(RouteEntryLeaseReleaseException failure);
}
