namespace AdamE.AppNav.Leases;

/// <summary>
/// The UI dispatcher that runs entry-lease retirement work and releases, and the boundary that receives their failures.
/// </summary>
/// <remarks>
/// <para>
/// The host supplies it when it creates the router. A UI host runs <see cref="InvokeAsync"/> on its UI dispatcher; core
/// never calls it inline from a navigation operation. Shutdown waits for releases that run here, so the host must not
/// block its UI thread waiting for the router's disposal.
/// </para>
/// <para>
/// <see cref="ReportDefect"/> and <see cref="ReportFatal"/> may be called from any thread and must return promptly:
/// record or post the failure, never block on the dispatcher. A host whose fatal boundary ends the process, as MAUI's
/// does by rethrowing on the main loop, should do so from <see cref="ReportFatal"/>. The adapter contract tests in the
/// repository include a host and a lease-aware presenter written against the public API alone.
/// </para>
/// </remarks>
public interface IRouteEntryLeaseHost
{
    /// <summary>Runs <paramref name="work"/> on the host's dispatcher and completes when it has finished.</summary>
    /// <remarks>
    /// The returned task completes only after the work's <see cref="ValueTask"/> has completed, and faults with its
    /// exception. Beware dispatcher overloads that take an <see cref="Action"/>: passing <c>() => work()</c> to one
    /// discards the <see cref="ValueTask"/>. When the caller is already on the dispatcher, the host may run the work
    /// inline. Core checks that the work finished: a task that faults, completes early, or never runs the work is a
    /// host failure, which core treats as fatal for that lease. The check can only see early completion when the work
    /// actually yields, so test a host with a resource whose disposal is asynchronous.
    /// </remarks>
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
