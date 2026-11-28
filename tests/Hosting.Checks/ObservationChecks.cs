using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu.Hosting;

internal static class ObservationChecks
{
    internal static async Task RunAsync()
    {
        static Task Dispatch(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        var observationFailure = new InvalidOperationException("Observation failed.");
        var reported = new List<Exception>();
        var received = new List<long>();
        var leases = new RouteEntryLeaseRegistry(new LeaseHost(Dispatch));
        await using var shell = new NavigationShellViewModel(leases,
            (_, _, _) => throw new InvalidOperationException("The fixture does not mount screens."),
            Dispatch, observation =>
            {
                received.Add(observation.Revision);
                return observation.Revision == 1 ? Task.FromException(observationFailure) : Task.CompletedTask;
            }, failure =>
            {
                reported.Add(failure);
                throw new InvalidOperationException("Failure reporter failed.");
            });

        var previous = SynchronizationContext.Current;
        var context = new RecordingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        Task first, second;
        try
        {
            first = shell.Report(new(1, "home", [], "", [], [], ""));
            second = shell.Report(new(2, "home", [], "", [], [], ""));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        if (reported.Count != 1 || !ReferenceEquals(reported[0], observationFailure) ||
            !received.SequenceEqual([1L, 2L]) || context.PostCount != 0 || !first.IsCompletedSuccessfully ||
            !second.IsCompletedSuccessfully)
            throw new InvalidOperationException("A throwing failure reporter must stay contained and later observations must still run.");
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state) => PostCount++;
    }
}
