using AdamE.AppNav.History;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;
using System.Runtime.ExceptionServices;

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
        try
        {
            navigator.Dispose();
        }
        finally
        {
            _ = presenter.StartShutdown();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ValueTask navigatorShutdown = navigator.DisposeAsync();
        Task presenterShutdown = presenter.StartShutdown();
        Exception? navigatorFailure = null;
        try
        {
            await navigatorShutdown.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            navigatorFailure = ex;
        }

        try
        {
            await presenterShutdown.ConfigureAwait(false);
        }
        catch (Exception presenterFailure) when (navigatorFailure is not null)
        {
            throw new AggregateException(navigatorFailure, presenterFailure);
        }

        if (navigatorFailure is not null)
            ExceptionDispatchInfo.Capture(navigatorFailure).Throw();
    }

    public void AttachWindow(Window window, string windowId)
    {
        presenter.AttachWindow(window, windowId);
    }
}
