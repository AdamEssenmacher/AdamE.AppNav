using AdamE.AppNav.History;
using AdamE.AppNav.Back;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;
using System.Runtime.ExceptionServices;

namespace AdamE.AppNav.Maui;

internal interface IMauiWindowAttachment
{
    ValueTask AttachWindowAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the presenter already holds logical presentation state for <paramref name="windowId"/>.
    /// </summary>
    bool HasPresentedWindow(string windowId);
}

internal interface IAppNavRuntime : IRouterNavigator
{
    bool IsDisposed { get; }

    ValueTask AttachWindowAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken = default);
}

internal sealed class AppNavRuntime(
    IRouterNavigator navigator,
    MauiNavigationPresenter presenter)
    : IAppNavRuntime, IMauiWindowAttachment
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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

    public ValueTask<BackNavigationResult> BackAsync(
        BackNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        return navigator.BackAsync(request, cancellationToken);
    }

    public ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default)
    {
        return navigator.ReconcileAsync(reconciliation, cancellationToken);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
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
        Interlocked.Exchange(ref _disposed, 1);
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

    public ValueTask AttachWindowAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken = default)
    {
        return presenter.AttachWindowAsync(window, windowId, cancellationToken);
    }

    public bool HasPresentedWindow(string windowId) => presenter.HasPresentedWindow(windowId);
}
