using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Leases;

namespace AdamE.AppNav.Maui;

/// <summary>Runs route-entry lease work on the MAUI main thread, and rethrows fatal release failures there.</summary>
internal sealed class MauiRouteEntryLeaseHost(
    NavigationDiagnostics? diagnostics = null,
    IMauiMainThreadDispatcher? mainThreadDispatcher = null) : IRouteEntryLeaseHost
{
    private readonly IMauiMainThreadDispatcher _mainThreadDispatcher =
        mainThreadDispatcher ?? MauiMainThreadDispatcher.Instance;

    public Task InvokeAsync(Func<ValueTask> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return _mainThreadDispatcher.IsMainThread
            ? release().AsTask()
            : _mainThreadDispatcher.InvokeAsync(() => release().AsTask());
    }

    public void ReportDefect(RouteEntryLeaseReleaseException defect)
    {
        ArgumentNullException.ThrowIfNull(defect);
        Write(defect);
    }

    public void ReportFatal(RouteEntryLeaseReleaseException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Write(failure);

        // The resources were kept; an unhandled exception on the main loop is MAUI's fatal boundary.
        try
        {
            _mainThreadDispatcher.BeginInvoke(() => throw failure);
        }
        catch
        {
            // The dispatcher itself may be what failed; an unhandled thread-pool exception still ends the process.
            ThreadPool.UnsafeQueueUserWorkItem(static exception => throw exception, failure, preferLocal: false);
        }
    }

    private void Write(RouteEntryLeaseReleaseException failure)
    {
        diagnostics?.Write(
            NavigationDiagnosticEventKind.PresentationPageReleaseFailed,
            Guid.NewGuid().ToString("N"),
            failure.Message,
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.ExceptionType] = failure.InnerException?.GetType().FullName,
                [NavigationDiagnosticDataKeys.ExceptionMessage] = failure.InnerException?.Message
            });
    }
}
