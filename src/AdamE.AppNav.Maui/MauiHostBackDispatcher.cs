using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Maui;

internal sealed class MauiHostBackDispatcher(
    IAppNavRuntime runtime,
    IMauiRoutePresentationNavigator presentationNavigator,
    NavigationDiagnostics diagnostics)
    : IMauiHostBackDispatcher, IDisposable
{
    private readonly Lock _gate = new();
    private bool _queuedBackPending;
    private bool _disposed;

    public async ValueTask<MauiHostBackResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();

        if (await presentationNavigator.PopAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            return MauiHostBackResult.PresentationPagePopped;

        BackNavigationResult result = await runtime.BackAsync(
            new BackNavigationRequest(windowId, BackNavigationSource.Host),
            cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            BackNavigationStatus.Completed => MauiHostBackResult.CompletedBy(result.NavigationResult!),
            BackNavigationStatus.Canceled => MauiHostBackResult.Canceled,
            BackNavigationStatus.Unhandled => MauiHostBackResult.Unhandled,
            _ => throw new InvalidOperationException($"Unknown back-navigation status '{result.Status}'.")
        };
    }

    public bool TryBack(string? windowId = null)
    {
        lock (_gate)
        {
            if (_disposed || runtime.IsDisposed)
                return false;
            if (_queuedBackPending)
                return true;

            _queuedBackPending = true;
        }

        _ = ObserveQueuedBackAsync(windowId);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private async Task ObserveQueuedBackAsync(string? windowId)
    {
        try
        {
            await BackAsync(windowId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            diagnostics.Write(
                NavigationDiagnosticEventKind.BackFailed,
                Guid.NewGuid().ToString("N"),
                "Queued MAUI host Back failed.",
                new Dictionary<string, object?>
                {
                    [NavigationDiagnosticDataKeys.ExceptionType] = ex.GetType().FullName,
                    [NavigationDiagnosticDataKeys.ExceptionMessage] = ex.Message
                },
                LogLevel.Error);
        }
        finally
        {
            lock (_gate)
            {
                _queuedBackPending = false;
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (_gate)
        {
            if (_disposed || runtime.IsDisposed)
                throw new ObjectDisposedException(nameof(MauiHostBackDispatcher));
        }
    }
}
