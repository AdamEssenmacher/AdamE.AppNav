namespace AdamE.AppNav.Leases;

/// <summary>
/// The UI dispatcher that runs entry-lease retirement work and releases, and the boundary that receives their failures.
/// </summary>
/// <remarks>
/// The host supplies it when it creates the router. A UI host runs <see cref="InvokeAsync"/> on its UI dispatcher; core
/// never calls it inline from a navigation operation.
/// </remarks>
public interface IRouteEntryLeaseHost
{
    /// <summary>Runs <paramref name="work"/> on the host's dispatcher and completes when it has finished.</summary>
    Task InvokeAsync(Func<ValueTask> work);

    /// <summary>
    /// Reports a survived defect, such as a disposer or retirement work that threw. The release continued.
    /// </summary>
    void ReportDefect(RouteEntryLeaseReleaseException defect);

    /// <summary>
    /// Surfaces a release that could not complete through the host's fatal boundary. The lease and its ancestors kept
    /// their resources and will not release them.
    /// </summary>
    void ReportFatal(RouteEntryLeaseReleaseException failure);
}
