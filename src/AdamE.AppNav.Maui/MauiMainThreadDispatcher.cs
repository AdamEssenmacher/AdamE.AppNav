using Microsoft.Maui.ApplicationModel;

namespace AdamE.AppNav.Maui;

internal interface IMauiMainThreadDispatcher
{
    bool IsMainThread { get; }

    Task InvokeAsync(Func<Task> callback);

    Task<T> InvokeAsync<T>(Func<Task<T>> callback);

    void BeginInvoke(Action callback);
}

internal sealed class MauiMainThreadDispatcher : IMauiMainThreadDispatcher
{
    public static MauiMainThreadDispatcher Instance { get; } = new();

    public bool IsMainThread => MainThread.IsMainThread;

    public Task InvokeAsync(Func<Task> callback) => MainThread.InvokeOnMainThreadAsync(callback);

    public Task<T> InvokeAsync<T>(Func<Task<T>> callback) => MainThread.InvokeOnMainThreadAsync(callback);

    public void BeginInvoke(Action callback) => MainThread.BeginInvokeOnMainThread(callback);
}
