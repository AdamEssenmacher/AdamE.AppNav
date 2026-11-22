namespace AdamE.AppNav.Navigation;

/// <summary>
/// The router's operation lock. A reconciliation takes its place in line when it is raised or submitted, and the
/// lock goes to every waiting reconciliation before any queued navigation, Back or refresh: the host already shows
/// the reconciliation's state, so an operation planned first would plan against state the host has left. Each kind
/// keeps arrival order, and a waiter keeps its place until the lock is handed to it.
/// </summary>
internal sealed class RouterOperationGate
{
    private readonly Lock _gate = new();
    private readonly LinkedList<TaskCompletionSource> _reconciliations = new();
    private readonly LinkedList<TaskCompletionSource> _operations = new();
    private bool _held;

    /// <summary>Waits for the lock behind every reconciliation and every operation already waiting.</summary>
    public Task EnterOperationAsync(CancellationToken cancellationToken)
    {
        LinkedListNode<TaskCompletionSource> node;
        lock (_gate)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }

            node = _operations.AddLast(NewWaiter());
        }

        return WaitAsync(node, cancellationToken);
    }

    /// <summary>
    /// Takes a reconciliation's place in line now. Await the reservation to take the lock. It never completes inline
    /// in the caller, which may be a presenter holding its own lock.
    /// </summary>
    public Reservation ReserveReconciliation()
    {
        lock (_gate)
        {
            if (!_held)
            {
                _held = true;
                return new Reservation(this, node: null);
            }

            return new Reservation(this, _reconciliations.AddLast(NewWaiter()));
        }
    }

    /// <summary>Hands the lock to the next reconciliation, else the next operation, else frees it.</summary>
    public void Exit()
    {
        TaskCompletionSource? next;
        lock (_gate)
        {
            if (!_held)
                throw new InvalidOperationException("The router operation lock is not held.");

            LinkedList<TaskCompletionSource> queue = _reconciliations.Count > 0 ? _reconciliations : _operations;
            next = queue.First?.Value;
            if (next is null)
            {
                _held = false;
                return;
            }

            queue.RemoveFirst();
        }

        // The waiter now owns the lock. Its continuation runs asynchronously.
        next.SetResult();
    }

    private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task WaitAsync(LinkedListNode<TaskCompletionSource> node, CancellationToken cancellationToken)
    {
        using (cancellationToken.UnsafeRegister(static (state, token) =>
               {
                   var (gate, waiter) = ((RouterOperationGate, LinkedListNode<TaskCompletionSource>))state!;
                   gate.Withdraw(waiter, token);
               }, (this, node)))
        {
            await node.Value.Task.ConfigureAwait(false);
        }
    }

    // A waiter that has not been handed the lock leaves the line. One already handed the lock keeps it, and its
    // operation releases it as usual.
    private void Withdraw(LinkedListNode<TaskCompletionSource> node, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (node.List is null)
                return;
            node.List.Remove(node);
        }

        node.Value.TrySetCanceled(cancellationToken);
    }

    /// <summary>A reconciliation's place in line, taken before its first await.</summary>
    internal sealed class Reservation
    {
        private readonly RouterOperationGate _owner;
        private readonly LinkedListNode<TaskCompletionSource>? _node;

        internal Reservation(RouterOperationGate owner, LinkedListNode<TaskCompletionSource>? node)
        {
            _owner = owner;
            _node = node;
        }

        /// <summary>Completes once the lock is this reconciliation's, always after the caller has returned.</summary>
        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            if (_node is null)
            {
                // Taken at reservation. Yield so the reconciliation never runs inline in its caller.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                return;
            }

            await _owner.WaitAsync(_node, cancellationToken).ConfigureAwait(false);
        }
    }
}
