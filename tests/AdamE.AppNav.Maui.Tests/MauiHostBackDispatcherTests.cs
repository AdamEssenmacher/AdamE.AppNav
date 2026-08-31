using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiHostBackDispatcherTests
{
    [Fact]
    public async Task PresentationPageIsPoppedBeforeLogicalBack()
    {
        var runtime = new RecordingRuntime();
        var presentation = new RecordingPresentationNavigator { PopResult = true };
        using var dispatcher = new MauiHostBackDispatcher(
            runtime,
            presentation,
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);

        MauiHostBackResult result = await dispatcher.BackAsync("main");

        Assert.Equal(MauiHostBackStatus.PresentationPagePopped, result.Status);
        Assert.Equal(1, presentation.PopCalls);
        Assert.Equal(0, runtime.BackCalls);
    }

    [Theory]
    [InlineData(BackNavigationStatus.Completed, MauiHostBackStatus.Completed)]
    [InlineData(BackNavigationStatus.Canceled, MauiHostBackStatus.Canceled)]
    [InlineData(BackNavigationStatus.Unhandled, MauiHostBackStatus.Unhandled)]
    public async Task LogicalBackResultIsMappedAndUsesHostSource(
        BackNavigationStatus coreStatus,
        MauiHostBackStatus expectedStatus)
    {
        var runtime = new RecordingRuntime { Result = CoreResult(coreStatus) };
        var presentation = new RecordingPresentationNavigator();
        using var dispatcher = new MauiHostBackDispatcher(
            runtime,
            presentation,
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);

        MauiHostBackResult result = await dispatcher.BackAsync("secondary");

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal("secondary", runtime.Request?.WindowId);
        Assert.Equal(BackNavigationSource.Host, runtime.Request?.Source);
        Assert.Equal(coreStatus == BackNavigationStatus.Completed, result.NavigationResult is not null);
    }

    [Fact]
    public async Task ExplicitDifferentWindowBypassesAttachedPresentationStack()
    {
        var runtime = new RecordingRuntime();
        var presentation = new RecordingPresentationNavigator { PopResult = true };
        using var dispatcher = new MauiHostBackDispatcher(
            runtime,
            presentation,
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);

        MauiHostBackResult result = await dispatcher.BackAsync("secondary");

        Assert.Equal(MauiHostBackStatus.Unhandled, result.Status);
        Assert.Equal(0, presentation.PopCalls);
        Assert.Equal(1, runtime.BackCalls);
        Assert.Equal("secondary", runtime.Request?.WindowId);
    }

    [Fact]
    public async Task TryBackCoalescesWhileQueuedOperationIsPending()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentation = new RecordingPresentationNavigator
        {
            OnPopAsync = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return false;
            }
        };
        var runtime = new RecordingRuntime();
        using var dispatcher = new MauiHostBackDispatcher(
            runtime,
            presentation,
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);

        Assert.True(dispatcher.TryBack("main"));
        await entered.Task;
        Assert.True(dispatcher.TryBack("main"));
        Assert.Equal(1, presentation.PopCalls);

        release.SetResult();
        await runtime.BackCalled.Task;
        Assert.Equal(1, runtime.BackCalls);
    }

    [Fact]
    public async Task TryBackInvokesUnhandledFallbackOnMainThread()
    {
        var fallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime { Result = BackNavigationResult.Unhandled };
        var presentation = new RecordingPresentationNavigator();
        using var dispatcher = new MauiHostBackDispatcher(
            runtime,
            presentation,
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);

        Assert.True(dispatcher.TryBack("main", () => fallback.TrySetResult(MainThread.IsMainThread)));

        Assert.True(await fallback.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TryBackDoesNotInvokeFallbackWhenBackIsCanceled()
    {
        var fallbackCalled = false;
        var runtime = new RecordingRuntime { Result = BackNavigationResult.Canceled };
        var presentation = new RecordingPresentationNavigator();
        using var dispatcher = new MauiHostBackDispatcher(
            runtime,
            presentation,
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);

        Assert.True(dispatcher.TryBack("main", () => fallbackCalled = true));
        await runtime.BackCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();

        Assert.False(fallbackCalled);
    }

    [Fact]
    public void TryBackRejectsDisposedDispatcherOrRuntime()
    {
        var runtime = new RecordingRuntime();
        var dispatcher = new MauiHostBackDispatcher(
            runtime,
            new RecordingPresentationNavigator(),
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);

        dispatcher.Dispose();
        Assert.False(dispatcher.TryBack());

        runtime = new RecordingRuntime { IsDisposed = true };
        using var runtimeDisposedDispatcher = new MauiHostBackDispatcher(
            runtime,
            new RecordingPresentationNavigator(),
            new RecordingPresentationState("main"),
            NavigationDiagnostics.None);
        Assert.False(runtimeDisposedDispatcher.TryBack());
    }

    [Fact]
    public async Task QueuedFailureIsObservedAndReported()
    {
        var diagnostics = new NavigationDiagnostics();
        var failed = new TaskCompletionSource<NavigationDiagnosticEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        diagnostics.EventWritten += (_, diagnosticEvent) =>
        {
            if (diagnosticEvent.Kind == NavigationDiagnosticEventKind.BackFailed &&
                diagnosticEvent.Message == "Queued MAUI host Back failed.")
            {
                failed.TrySetResult(diagnosticEvent);
            }
        };
        var presentation = new RecordingPresentationNavigator
        {
            OnPopAsync = _ => ValueTask.FromException<bool>(new InvalidOperationException("failed"))
        };
        using var dispatcher = new MauiHostBackDispatcher(
            new RecordingRuntime(),
            presentation,
            new RecordingPresentationState("main"),
            diagnostics);

        Assert.True(dispatcher.TryBack());

        NavigationDiagnosticEvent diagnosticEvent = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(typeof(InvalidOperationException).FullName,
            diagnosticEvent.Data[NavigationDiagnosticDataKeys.ExceptionType]);
    }

    private static BackNavigationResult CoreResult(BackNavigationStatus status)
    {
        if (status == BackNavigationStatus.Canceled)
            return BackNavigationResult.Canceled;
        if (status == BackNavigationStatus.Unhandled)
            return BackNavigationResult.Unhandled;

        var route = new TestPageRoute("completed");
        var state = new NavigationState(
            [new WindowNode("main", new StackNode("stack", [new RouteEntry("route", route)]))],
            "main");
        var plan = new NavigationPlan(state, NavigationPlanKind.Back);
        return BackNavigationResult.CompletedBy(new NavigationResult(route, plan, state, true));
    }

    private sealed class RecordingPresentationNavigator : IMauiRoutePresentationNavigator
    {
        public bool PopResult { get; init; }

        public Func<CancellationToken, ValueTask<bool>>? OnPopAsync { get; init; }

        public int PopCalls { get; private set; }

        public ValueTask PushAsync<TPage>(
            string key,
            MauiRoutePresentationPageOptions? options = null,
            CancellationToken cancellationToken = default)
            where TPage : Microsoft.Maui.Controls.Page => throw new NotSupportedException();

        public ValueTask<bool> PopAsync(
            bool animated = true,
            CancellationToken cancellationToken = default)
        {
            PopCalls++;
            return OnPopAsync?.Invoke(cancellationToken) ?? ValueTask.FromResult(PopResult);
        }
    }

    private sealed class RecordingPresentationState(string? attachedWindowId) : IMauiPresentationState
    {
        event EventHandler<Page?>? IMauiPresentationState.RootPageChanged
        {
            add { }
            remove { }
        }

        public Window? AttachedWindow => null;

        public string? AttachedWindowId => attachedWindowId;

        public Page? RootPage => null;

        public Page? GetTopPresentedPage() => null;

        public bool IsModalPresented(Page page) => false;
    }

    private sealed class RecordingRuntime : IAppNavRuntime
    {
        public bool IsDisposed { get; set; }

        public BackNavigationResult Result { get; init; } = BackNavigationResult.Unhandled;

        public BackNavigationRequest? Request { get; private set; }

        public int BackCalls { get; private set; }

        public TaskCompletionSource BackCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NavigationState CurrentState => NavigationState.Empty;

        public NavigationHistory History => NavigationHistory.Empty;

        public ValueTask<BackNavigationResult> BackAsync(
            BackNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            BackCalls++;
            BackCalled.TrySetResult();
            return ValueTask.FromResult(Result);
        }

        public ValueTask<BackNavigationResult> BackAsync(
            string? windowId = null,
            CancellationToken cancellationToken = default) =>
            BackAsync(new BackNavigationRequest(windowId), cancellationToken);

        public ValueTask<NavigationResult> NavigateAsync(
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<NavigationResult> ReconcileAsync(
            NavigationReconciliation reconciliation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask AttachWindowAsync(
            Microsoft.Maui.Controls.Window window,
            string windowId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
