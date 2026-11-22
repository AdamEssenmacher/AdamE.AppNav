using AdamE.AppNav.Navigation;

namespace AdamE.AppNav.Tests;

public sealed class RouterOperationGateTests
{
    [Fact]
    public async Task WaitingReconciliationsGoFirstAndEachKindKeepsArrivalOrder()
    {
        var gate = new RouterOperationGate();
        await gate.EnterOperationAsync(CancellationToken.None);
        var order = new List<string>();
        Task Run(Task entered, string name) => entered.ContinueWith(_ =>
        {
            lock (order) order.Add(name);
            gate.Exit();
        }, TaskScheduler.Default);

        Task a = Run(gate.EnterOperationAsync(CancellationToken.None), "a");
        Task b = Run(gate.EnterOperationAsync(CancellationToken.None), "b");
        Task r1 = Run(gate.ReserveReconciliation().EnterAsync(CancellationToken.None), "r1");
        Task r2 = Run(gate.ReserveReconciliation().EnterAsync(CancellationToken.None), "r2");
        gate.Exit();
        await Task.WhenAll(a, b, r1, r2);

        Assert.Equal(["r1", "r2", "a", "b"], order);
    }

    [Fact]
    public async Task ReservedReconciliationKeepsItsPlaceBeforeItIsAwaited()
    {
        var gate = new RouterOperationGate();
        await gate.EnterOperationAsync(CancellationToken.None);
        Task operation = gate.EnterOperationAsync(CancellationToken.None);
        RouterOperationGate.Reservation reservation = gate.ReserveReconciliation();

        gate.Exit();
        Assert.False(operation.IsCompleted);
        await reservation.EnterAsync(CancellationToken.None);
        Assert.False(operation.IsCompleted);
        gate.Exit();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledWaiterLeavesTheLine()
    {
        var gate = new RouterOperationGate();
        await gate.EnterOperationAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task cancelled = gate.ReserveReconciliation().EnterAsync(cancellation.Token);
        Task operation = gate.EnterOperationAsync(CancellationToken.None);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Exit();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Exit();
        await gate.EnterOperationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconciliationTakingAFreeLockNeverRunsInlineInItsCaller()
    {
        var gate = new RouterOperationGate();
        var sync = new object();
        var callerReturned = false;
        Task entered;

        async Task ReconcileAsync()
        {
            await gate.ReserveReconciliation().EnterAsync(CancellationToken.None);
            // Inline, this would run on the caller's thread inside its lock and see the caller still running.
            lock (sync)
                Assert.True(callerReturned);
        }

        lock (sync)
        {
            entered = ReconcileAsync();
            callerReturned = true;
        }

        await entered.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
