using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Requests;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

public interface IAppNavStartupService
{
    /// <summary>
    /// Schedules observed AppNav startup for the supplied MAUI window.
    /// </summary>
    void Start(Window window, string windowId);

#pragma warning disable RS0026 // The default-window and explicit-window advanced coordination overloads are intentional.
    ValueTask<AppNavStartupResult> StartAsync(
        Window window,
        CancellationToken cancellationToken = default);

    ValueTask<AppNavStartupResult> StartAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken = default);
#pragma warning restore RS0026
}

public sealed class AppNavStartupOptions
{
    public string WindowId { get; set; } = "main";

    public TimeSpan AppLinkGracePeriod { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Creates the typed fallback route used when no external or deferred request is pending.
    /// The route is wrapped in an in-app, canonical request for the startup window.
    /// </summary>
    public Func<IServiceProvider, CancellationToken, ValueTask<AppRoute?>>? FallbackRouteFactory { get; set; }

    /// <summary>
    /// Creates a complete fallback request envelope for advanced startup coordination.
    /// </summary>
    public Func<IServiceProvider, CancellationToken, ValueTask<RouterNavigationRequest?>>? FallbackRequestFactory { get; set; }
}

public enum AppNavStartupOutcome
{
    AppLinkPending,
    FallbackNavigated,
    NoNavigation,
    Failed,
    AppLinkNavigated = 4
}

public sealed record AppNavStartupResult(
    AppNavStartupOutcome Outcome,
    NavigationResult? FallbackNavigationResult = null,
    Exception? Exception = null)
{
    public bool Succeeded => Outcome != AppNavStartupOutcome.Failed;
}

internal sealed class AppNavStartupService : IAppNavStartupService
{
    private readonly IRouterNavigator _navigator;
    private readonly IMauiWindowAttachment _windowAttachment;
    private readonly MauiExternalNavigationDispatcher _externalNavigationDispatcher;
    private readonly AppNavStartupOptions _options;
    private readonly IServiceProvider _services;
    private readonly NavigationDiagnostics _diagnostics;

    public AppNavStartupService(
        IRouterNavigator navigator,
        IMauiWindowAttachment windowAttachment,
        MauiExternalNavigationDispatcher externalNavigationDispatcher,
        AppNavStartupOptions options,
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

        if (_options.FallbackRouteFactory is not null && _options.FallbackRequestFactory is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(AppNavStartupOptions.FallbackRouteFactory)} and " +
                $"{nameof(AppNavStartupOptions.FallbackRequestFactory)} are mutually exclusive.");
        }
    }

    public void Start(Window window, string windowId)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);

        _ = ObserveScheduledStartAsync(window, windowId);
    }

    public ValueTask<AppNavStartupResult> StartAsync(
        Window window,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(window, _options.WindowId, cancellationToken);
    }

    public async ValueTask<AppNavStartupResult> StartAsync(
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

    private async Task<AppNavStartupResult> StartOnMainThreadAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var attached = false;

        _diagnostics.Write(
            NavigationDiagnosticEventKind.StartupStarted,
            operationId,
            "AppNav startup started.",
            StartupData(windowId));

        try
        {
            MauiExternalNavigationPendingEpoch? pendingAppLinkEpoch = await _externalNavigationDispatcher
                .WaitForPendingEpochAsync(_options.AppLinkGracePeriod, cancellationToken);

            if (pendingAppLinkEpoch is not null)
            {
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.StartupAppLinkPending,
                    operationId,
                    "A buffered app-link request is pending; startup will observe its dispatch outcome.",
                    StartupData(
                        windowId,
                        (NavigationDiagnosticDataKeys.StartupOutcome, AppNavStartupOutcome.AppLinkPending.ToString())));

                Attach(window, windowId, ref attached);
                MauiExternalNavigationPendingEpochOutcome appLinkOutcome = await pendingAppLinkEpoch
                    .Completion
                    .WaitAsync(cancellationToken);
                if (appLinkOutcome == MauiExternalNavigationPendingEpochOutcome.Navigated)
                {
                    return Complete(operationId, windowId, AppNavStartupOutcome.AppLinkNavigated);
                }
            }

            var hasDeferredRequests = false;
            if (_services.GetService(typeof(IDeferredNavigationRequestStore)) is IDeferredNavigationRequestStore deferredRequestStore)
            {
                hasDeferredRequests = await HasDeferredRequestsOrRecoverAsync(
                    deferredRequestStore,
                    operationId,
                    windowId,
                    cancellationToken);

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

            RouterNavigationRequest? fallbackRequest = await CreateFallbackRequestAsync(
                windowId,
                cancellationToken);
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
                        (NavigationDiagnosticDataKeys.StartupOutcome, AppNavStartupOutcome.FallbackNavigated.ToString()),
                        (NavigationDiagnosticDataKeys.RequestSource, fallbackRequest.Source.ToString()),
                        (NavigationDiagnosticDataKeys.Uri, fallbackRequest.Uri?.ToString())));

                Attach(window, windowId, ref attached);
                return Complete(
                    operationId,
                    windowId,
                    AppNavStartupOutcome.FallbackNavigated,
                    fallbackResult);
            }

            Attach(window, windowId, ref attached);
            return Complete(
                operationId,
                windowId,
                AppNavStartupOutcome.NoNavigation);
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
                "AppNav startup failed.",
                StartupData(
                    windowId,
                    (NavigationDiagnosticDataKeys.StartupOutcome, AppNavStartupOutcome.Failed.ToString()),
                    (NavigationDiagnosticDataKeys.ExceptionType, ex.GetType().FullName),
                    (NavigationDiagnosticDataKeys.ExceptionMessage, ex.Message)));

            return new AppNavStartupResult(
                AppNavStartupOutcome.Failed,
                FallbackNavigationResult: null,
                ex);
        }
    }

    private async Task ObserveScheduledStartAsync(Window window, string windowId)
    {
        try
        {
            _ = await StartAsync(window, windowId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.StartupFailed,
                Guid.NewGuid().ToString("N"),
                "Scheduled AppNav startup failed.",
                StartupData(
                    windowId,
                    (NavigationDiagnosticDataKeys.StartupOutcome, AppNavStartupOutcome.Failed.ToString()),
                    (NavigationDiagnosticDataKeys.ExceptionType, ex.GetType().FullName),
                    (NavigationDiagnosticDataKeys.ExceptionMessage, ex.Message)));
        }
    }

    private async ValueTask<RouterNavigationRequest?> CreateFallbackRequestAsync(
        string windowId,
        CancellationToken cancellationToken)
    {
        if (_options.FallbackRouteFactory is not null)
        {
            AppRoute? route = await _options.FallbackRouteFactory(_services, cancellationToken);
            return route is null
                ? null
                : RouterNavigationRequest.FromRoute(
                    route,
                    NavigationRequestSource.InAppCommand,
                    windowId,
                    disposition: RouterNavigationDisposition.Canonical);
        }

        return _options.FallbackRequestFactory is null
            ? null
            : await _options.FallbackRequestFactory(_services, cancellationToken);
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
        catch (UnsupportedDeferredNavigationRequestSchemaException ex)
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

    private AppNavStartupResult Complete(
        string operationId,
        string windowId,
        AppNavStartupOutcome outcome,
        NavigationResult? fallbackNavigationResult = null)
    {
        _diagnostics.Write(
            NavigationDiagnosticEventKind.StartupCompleted,
            operationId,
            $"AppNav startup completed with outcome '{outcome}'.",
            StartupData(
                windowId,
                (NavigationDiagnosticDataKeys.StartupOutcome, outcome.ToString())));

        return new AppNavStartupResult(
            outcome,
            fallbackNavigationResult,
            Exception: null);
    }

    private void Attach(Window window, string windowId, ref bool attached)
    {
        if (attached)
            return;

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
