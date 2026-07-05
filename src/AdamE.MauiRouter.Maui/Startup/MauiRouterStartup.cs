using System.Text.Json;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Requests;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

public interface IMauiRouterStartupService
{
    ValueTask<MauiRouterStartupResult> StartAsync(
        Window window,
        CancellationToken cancellationToken = default);

    ValueTask<MauiRouterStartupResult> StartAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken = default);
}

public sealed class MauiRouterStartupOptions
{
    public string WindowId { get; set; } = "main";

    public TimeSpan AppLinkGracePeriod { get; set; } = TimeSpan.FromMilliseconds(250);

    public Func<IServiceProvider, CancellationToken, ValueTask<RouterNavigationRequest?>>? FallbackRequestFactory { get; set; }
}

public enum MauiRouterStartupOutcome
{
    AppLinkPending,
    FallbackNavigated,
    NoNavigation,
    Failed
}

public sealed record MauiRouterStartupResult(
    MauiRouterStartupOutcome Outcome,
    NavigationResult? FallbackNavigationResult = null,
    Exception? Exception = null)
{
    public bool Succeeded => Outcome != MauiRouterStartupOutcome.Failed;
}

internal sealed class MauiRouterStartupService : IMauiRouterStartupService
{
    private readonly IRouterNavigator _navigator;
    private readonly IMauiWindowAttachment _windowAttachment;
    private readonly MauiExternalNavigationDispatcher _externalNavigationDispatcher;
    private readonly MauiRouterStartupOptions _options;
    private readonly IServiceProvider _services;
    private readonly NavigationDiagnostics _diagnostics;

    public MauiRouterStartupService(
        IRouterNavigator navigator,
        IMauiWindowAttachment windowAttachment,
        MauiExternalNavigationDispatcher externalNavigationDispatcher,
        MauiRouterStartupOptions options,
        IServiceProvider services,
        NavigationDiagnostics? diagnostics = null)
    {
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _windowAttachment = windowAttachment ?? throw new ArgumentNullException(nameof(windowAttachment));
        _externalNavigationDispatcher = externalNavigationDispatcher ?? throw new ArgumentNullException(nameof(externalNavigationDispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;

        if (_options.AppLinkGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "AppLinkGracePeriod cannot be negative.");
        }
    }

    public ValueTask<MauiRouterStartupResult> StartAsync(
        Window window,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(window, _options.WindowId, cancellationToken);
    }

    public async ValueTask<MauiRouterStartupResult> StartAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);

        if (MainThread.IsMainThread)
        {
            return await StartOnMainThreadAsync(window, windowId, cancellationToken).ConfigureAwait(false);
        }

        return await MainThread
            .InvokeOnMainThreadAsync(() => StartOnMainThreadAsync(window, windowId, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<MauiRouterStartupResult> StartOnMainThreadAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var attached = false;

        _diagnostics.Write(
            NavigationDiagnosticEventKind.StartupStarted,
            operationId,
            "MAUI router startup started.",
            StartupData(windowId));

        try
        {
            var hasPendingAppLink = await _externalNavigationDispatcher
                .WaitForPendingRequestAsync(_options.AppLinkGracePeriod, cancellationToken);

            if (hasPendingAppLink)
            {
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.StartupAppLinkPending,
                    operationId,
                    "A buffered app-link request is pending; fallback navigation was skipped.",
                    StartupData(
                        windowId,
                        (NavigationDiagnosticDataKeys.StartupOutcome, MauiRouterStartupOutcome.AppLinkPending.ToString())));

                Attach(window, windowId, ref attached);
                return Complete(operationId, windowId, MauiRouterStartupOutcome.AppLinkPending);
            }

            var hasDeferredRequests = false;
            if (_services.GetService(typeof(IDeferredNavigationRequestStore)) is IDeferredNavigationRequestStore deferredRequestStore)
            {
                hasDeferredRequests = await HasDeferredRequestsOrRecoverAsync(
                    deferredRequestStore,
                    operationId,
                    windowId,
                    cancellationToken).ConfigureAwait(false);

                if (hasDeferredRequests)
                {
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.StartupDeferredRequestPending,
                        operationId,
                        "A deferred protected navigation request is pending.",
                        StartupData(
                            windowId,
                            (NavigationDiagnosticDataKeys.StartupDeferredRequestPending, true)));
                }
            }

            if (_options.FallbackRequestFactory is not null)
            {
                var fallbackRequest = await _options
                    .FallbackRequestFactory(_services, cancellationToken);

                if (fallbackRequest is not null)
                {
                    var fallbackResult = await _navigator
                        .NavigateAsync(fallbackRequest, cancellationToken);

                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.StartupFallbackNavigated,
                        operationId,
                        "Startup fallback navigation completed.",
                        StartupData(
                            windowId,
                            (NavigationDiagnosticDataKeys.StartupOutcome, MauiRouterStartupOutcome.FallbackNavigated.ToString()),
                            (NavigationDiagnosticDataKeys.RequestSource, fallbackRequest.Source.ToString()),
                            (NavigationDiagnosticDataKeys.Uri, fallbackRequest.Uri?.ToString())));

                    Attach(window, windowId, ref attached);
                    return Complete(
                        operationId,
                        windowId,
                        MauiRouterStartupOutcome.FallbackNavigated,
                        fallbackResult);
                }
            }

            Attach(window, windowId, ref attached);
            return Complete(
                operationId,
                windowId,
                MauiRouterStartupOutcome.NoNavigation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryAttach(window, windowId, ref attached);

            _diagnostics.Write(
                NavigationDiagnosticEventKind.StartupFailed,
                operationId,
                "MAUI router startup failed.",
                StartupData(
                    windowId,
                    (NavigationDiagnosticDataKeys.StartupOutcome, MauiRouterStartupOutcome.Failed.ToString()),
                    (NavigationDiagnosticDataKeys.ExceptionType, ex.GetType().FullName),
                    (NavigationDiagnosticDataKeys.ExceptionMessage, ex.Message)));

            return new MauiRouterStartupResult(
                MauiRouterStartupOutcome.Failed,
                FallbackNavigationResult: null,
                ex);
        }
    }

    private async Task<bool> HasDeferredRequestsOrRecoverAsync(
        IDeferredNavigationRequestStore store,
        string operationId,
        string windowId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.HasDeferredRequestsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableDeferredRequestStoreException(ex))
        {
            try
            {
                await store.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception clearException) when (clearException is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "Deferred navigation request store could not be cleared after invalid persisted data was detected.",
                    clearException);
            }

            _diagnostics.Write(
                NavigationDiagnosticEventKind.StartupStarted,
                operationId,
                "Invalid deferred navigation request store was cleared; startup will continue.",
                StartupData(
                    windowId,
                    (NavigationDiagnosticDataKeys.StartupDeferredRequestPending, false),
                    (NavigationDiagnosticDataKeys.ExceptionType, ex.GetType().FullName),
                    (NavigationDiagnosticDataKeys.ExceptionMessage, ex.Message)));

            return false;
        }
    }

    private static bool IsRecoverableDeferredRequestStoreException(Exception exception)
    {
        return exception is JsonException or InvalidOperationException or NotSupportedException or FormatException;
    }

    private MauiRouterStartupResult Complete(
        string operationId,
        string windowId,
        MauiRouterStartupOutcome outcome,
        NavigationResult? fallbackNavigationResult = null)
    {
        _diagnostics.Write(
            NavigationDiagnosticEventKind.StartupCompleted,
            operationId,
            $"MAUI router startup completed with outcome '{outcome}'.",
            StartupData(
                windowId,
                (NavigationDiagnosticDataKeys.StartupOutcome, outcome.ToString())));

        return new MauiRouterStartupResult(
            outcome,
            fallbackNavigationResult,
            Exception: null);
    }

    private void Attach(Window window, string windowId, ref bool attached)
    {
        _windowAttachment.AttachWindow(window, windowId);
        attached = true;
    }

    private void TryAttach(Window window, string windowId, ref bool attached)
    {
        if (attached)
        {
            return;
        }

        try
        {
            Attach(window, windowId, ref attached);
        }
        catch
        {
            // The original startup failure remains the actionable failure.
        }
    }

    private Dictionary<string, object?> StartupData(
        string windowId,
        params (string Key, object? Value)[] values)
    {
        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.WindowId] = windowId,
            [NavigationDiagnosticDataKeys.AppLinkGraceMs] = _options.AppLinkGracePeriod.TotalMilliseconds
        };

        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        return data;
    }
}
