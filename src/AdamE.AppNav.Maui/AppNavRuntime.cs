using AdamE.AppNav.History;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal interface IMauiWindowAttachment
{
    void AttachWindow(Window window, string windowId);
}

internal interface IAppNavRuntime : IRouterNavigator
{
    void AttachWindow(Window window, string windowId);
}

internal sealed class AppNavRuntime(
    IRouterNavigator navigator,
    MauiNavigationPresenter presenter)
    : IAppNavRuntime, IMauiWindowAttachment
{
    public NavigationState CurrentState => navigator.CurrentState;

    public NavigationHistory History => navigator.History;

    public ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(uri, source, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(uri, source, disposition, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(uri, disposition, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(route, source, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(route, source, disposition, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(route, disposition, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(routeRequest, source, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(routeRequest, source, disposition, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(routeRequest, disposition, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(request, cancellationToken);
    }

    public ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        return navigator.BackAsync(windowId, cancellationToken);
    }

    public ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default)
    {
        return navigator.ReconcileAsync(reconciliation, cancellationToken);
    }

    public void Dispose()
    {
        navigator.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return navigator.DisposeAsync();
    }

    public void AttachWindow(Window window, string windowId)
    {
        presenter.AttachWindow(window, windowId);
    }
}
