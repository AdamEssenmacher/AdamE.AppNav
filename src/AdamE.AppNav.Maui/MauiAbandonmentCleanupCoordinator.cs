namespace AdamE.AppNav.Maui;

internal sealed class MauiAbandonmentCleanupCoordinator(
    Action<MauiPageAbandonment, Exception>? failureObserver = null)
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private bool _sealed;

    public void Enqueue(IEnumerable<MauiPageAbandonment> abandonments)
    {
        EnqueueAfter(Task.CompletedTask, abandonments);
    }

    public void EnqueueAfter(Task prerequisite, IEnumerable<MauiPageAbandonment> abandonments)
    {
        ArgumentNullException.ThrowIfNull(prerequisite);
        ArgumentNullException.ThrowIfNull(abandonments);
        MauiPageAbandonment[] captured = abandonments.ToArray();

        lock (_gate)
        {
            if (_sealed)
                throw new InvalidOperationException("Native page abandonment cleanup is already sealed.");

            Task preceding = _tail;
            _tail = Task.Run(
                () => DisposeAfterAsync(preceding, prerequisite, captured),
                CancellationToken.None);
        }
    }

    public Task SealAndDrainAsync()
    {
        lock (_gate)
        {
            _sealed = true;
            return _tail;
        }
    }

    private async Task DisposeAfterAsync(
        Task preceding,
        Task prerequisite,
        IReadOnlyList<MauiPageAbandonment> abandonments)
    {
        await preceding.ConfigureAwait(false);
        try
        {
            await prerequisite.ConfigureAwait(false);
        }
        catch
        {
            // Resource disposal must continue even if the operation-drain observer failed.
        }

        foreach (MauiPageAbandonment abandonment in abandonments)
        {
            try
            {
                await abandonment.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    failureObserver?.Invoke(abandonment, ex);
                }
                catch
                {
                    // Cleanup must continue even when diagnostic observers fail.
                }
            }
        }
    }
}
