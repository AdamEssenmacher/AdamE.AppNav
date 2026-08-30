using System.Reflection;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class ConventionRouteNullabilityConcurrencyTests
{
    private const int WorkerCount = 8;
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentInspectionReturnsCorrectNullableStates()
    {
        ParameterInfo[] parameters = typeof(NullabilityProbe).GetConstructors().Single().GetParameters();
        ParameterInfo nullableParameter = parameters[0];
        ParameterInfo nonNullableParameter = parameters[1];

        await RunCoordinatedAsync(workerIndex =>
        {
            for (var iteration = 0; iteration < 128; iteration++)
            {
                bool inspectNullableFirst = (workerIndex + iteration) % 2 == 0;
                ParameterInfo first = inspectNullableFirst ? nullableParameter : nonNullableParameter;
                ParameterInfo second = inspectNullableFirst ? nonNullableParameter : nullableParameter;

                Assert.Equal(inspectNullableFirst, ConventionRouteNullability.IsNullable(first));
                Assert.Equal(!inspectNullableFirst, ConventionRouteNullability.IsNullable(second));
            }
        });
    }

    [Fact]
    public async Task ParallelConventionRouteTableCreationCompletesWithNullableBindings()
    {
        await RunCoordinatedAsync(_ =>
        {
            for (var iteration = 0; iteration < 32; iteration++)
            {
                RouteTable table = RouteTable.Create(routes => routes.MapRoute<NullableConventionRoute>(
                    "/values/{value?}",
                    route => route.Query(value => value.Filter)));

                RouteMatchResult match = table.Match(new Uri("/values", UriKind.Relative));
                Assert.Equal(new NullableConventionRoute(null, null), match.Route);
            }
        });
    }

    [Fact]
    public async Task ParallelConventionRouteTableCreationPreservesInvalidConfigurationErrors()
    {
        string expectedQueryError =
            $"Convention query binding 'filter' on route type '{typeof(NonNullableQueryRoute).FullName}' " +
            "targets constructor parameter 'Filter', but query values are always optional. " +
            "Make the parameter nullable or provide a default value.";
        string expectedPathError =
            $"Optional path binding 'value' on route type '{typeof(NonNullableOptionalPathRoute).FullName}' " +
            "targets constructor parameter 'Value', but the path value may be absent. " +
            "Make the parameter nullable or provide a default value.";

        await RunCoordinatedAsync(workerIndex =>
        {
            for (var iteration = 0; iteration < 32; iteration++)
            {
                if ((workerIndex + iteration) % 2 == 0)
                {
                    var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
                        routes.MapRoute<NonNullableQueryRoute>(
                            "/values/{value}",
                            route => route.Query(value => value.Filter))));

                    Assert.Equal(expectedQueryError, exception.Message);
                }
                else
                {
                    var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
                        routes.MapRoute<NonNullableOptionalPathRoute>("/values/{value?}")));

                    Assert.Equal(expectedPathError, exception.Message);
                }
            }
        });
    }

    private static async Task RunCoordinatedAsync(Action<int> action)
    {
        using var start = new Barrier(WorkerCount);
        Task[] workers = Enumerable.Range(0, WorkerCount)
            .Select(workerIndex => Task.Factory.StartNew(
                () =>
                {
                    if (!start.SignalAndWait(CompletionTimeout))
                        throw new TimeoutException("Concurrent test workers did not reach the start barrier in time.");

                    action(workerIndex);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        await Task.WhenAll(workers).WaitAsync(CompletionTimeout);
    }

    private sealed class NullabilityProbe(string? nullableValue, string nonNullableValue)
    {
        public string? NullableValue { get; } = nullableValue;

        public string NonNullableValue { get; } = nonNullableValue;
    }

    private sealed record NullableConventionRoute(string? Value, string? Filter) : AppRoute;

    private sealed record NonNullableQueryRoute(string Value, string Filter) : AppRoute;

    private sealed record NonNullableOptionalPathRoute(string Value) : AppRoute;
}
