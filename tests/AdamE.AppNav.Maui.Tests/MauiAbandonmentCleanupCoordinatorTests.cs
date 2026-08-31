namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiAbandonmentCleanupCoordinatorTests
{
    [Fact]
    public async Task SealWaitsForQueuedCleanupAndContinuesAfterFailures()
    {
        var gate = new GatedAsyncDisposable();
        var completed = new CountingAsyncDisposable();
        var failures = new List<string>();
        var coordinator = new MauiAbandonmentCleanupCoordinator(
            (abandonment, _) => failures.Add(abandonment.PageTypeName));
        coordinator.Enqueue(
        [
            new MauiPageAbandonment(gate, "gated"),
            new MauiPageAbandonment(new ThrowingAsyncDisposable(), "failed"),
            new MauiPageAbandonment(completed, "completed")
        ]);

        Task drain = coordinator.SealAndDrainAsync();
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted);

        gate.Complete();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["failed"], failures);
        Assert.Equal(1, completed.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => coordinator.Enqueue(
            [new MauiPageAbandonment(null, "late")]));
    }

    private sealed class GatedAsyncDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Complete() => _completion.TrySetResult();

        public ValueTask DisposeAsync()
        {
            _started.TrySetResult();
            return new ValueTask(_completion.Task);
        }
    }

    private sealed class CountingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.FromException(new InvalidOperationException("Expected cleanup failure."));
        }
    }
}
