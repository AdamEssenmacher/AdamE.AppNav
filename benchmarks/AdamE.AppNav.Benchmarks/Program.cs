using System.Text.Json;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace AdamE.AppNav.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--write-budgets"])
            return AllocationBudgetRunner.WriteBudgets();

        if (args is ["--check-budgets"])
            return AllocationBudgetRunner.CheckBudgets();

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}

[MemoryDiagnoser]
public class RouteMatchingBenchmarks : RouteBenchmarkBase
{
    [Benchmark]
    public RouteMatchResult MatchExplicit_LastRoute()
    {
        return ExplicitRoutes.Match(TargetUri);
    }

    [Benchmark]
    public RouteMatchResult MatchConvention_LastRoute()
    {
        return ConventionRoutes.Match(TargetUri);
    }
}

[MemoryDiagnoser]
public class RouteFormattingBenchmarks : RouteBenchmarkBase
{
    [Benchmark]
    public string FormatExplicit_LastRoute()
    {
        return ExplicitRoutes.Format(ExplicitTargetRoute);
    }

    [Benchmark]
    public string FormatConvention_LastRoute()
    {
        return ConventionRoutes.Format(ConventionTargetRoute);
    }
}

[MemoryDiagnoser]
public class NavigationBenchmarks : RouteBenchmarkBase
{
    private IRouterNavigator _navigator = null!;
    private RouterNavigationRequest _uriBackedRequest = null!;

    [GlobalSetup]
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _navigator = RouterNavigatorFactory.Create(
            ExplicitRoutes,
            BenchmarkNavigationPlanner.Instance,
            BenchmarkNavigationPresenter.Instance,
            new RouterNavigatorFactoryOptions
            {
                MaxHistoryEntries = 1
            });
        _uriBackedRequest = RouterNavigationRequest.FromUri(TargetUri, NavigationRequestSource.Test);
    }

    [Benchmark]
    public NavigationResult NavigateUriBacked_LastRoute()
    {
        return _navigator.NavigateAsync(_uriBackedRequest).GetAwaiter().GetResult();
    }

    [Benchmark]
    public NavigationResult NavigateRouteBacked()
    {
        return _navigator.NavigateAsync(ExplicitTargetRoute).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _navigator.Dispose();
    }
}

[MemoryDiagnoser]
public class DeferredSerializationBenchmarks : RouteBenchmarkBase
{
    private DeferredNavigationRequestSerializer _serializer = null!;
    private RouterNavigationRequest _routeBackedRequest = null!;
    private RouterNavigationRequest _uriBackedRequest = null!;
    private DeferredNavigationRequestStoreSnapshot _snapshot = null!;
    private string _snapshotJson = null!;

    [GlobalSetup]
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _serializer = new DeferredNavigationRequestSerializer(
            ExplicitRoutes,
            new DeferredNavigationRequestPersistenceOptions
            {
                BaseUri = new Uri("https://benchmarks.appnav.invalid/")
            });
        _routeBackedRequest = RouterNavigationRequest.FromRoute(
            ExplicitTargetRoute,
            NavigationRequestSource.InAppCommand);
        _uriBackedRequest = RouterNavigationRequest.FromUri(
            TargetUri,
            NavigationRequestSource.AppLink);
        _snapshot = _serializer.CreateSnapshot([_uriBackedRequest]);
        _snapshotJson = JsonSerializer.Serialize(_snapshot, JsonOptions);
    }

    [Benchmark]
    public DeferredNavigationRequestStoreSnapshot CreateSnapshot_RouteBacked()
    {
        return _serializer.CreateSnapshot([_routeBackedRequest]);
    }

    [Benchmark]
    public DeferredNavigationRequestStoreSnapshot CreateSnapshot_UriBacked_LastRoute()
    {
        return _serializer.CreateSnapshot([_uriBackedRequest]);
    }

    [Benchmark]
    public IReadOnlyList<RouterNavigationRequest> RestoreSnapshot_LastRoute()
    {
        return _serializer.Restore(_snapshot);
    }

    [Benchmark]
    public DeferredNavigationRequestStoreSnapshot JsonRoundTripSnapshot()
    {
        string json = JsonSerializer.Serialize(_snapshot, JsonOptions);
        return JsonSerializer.Deserialize<DeferredNavigationRequestStoreSnapshot>(json, JsonOptions)
               ?? throw new InvalidOperationException("Snapshot JSON round-trip returned null.");
    }

    internal DeferredNavigationRequestStoreSnapshot DeserializePrebuiltSnapshotJson()
    {
        return JsonSerializer.Deserialize<DeferredNavigationRequestStoreSnapshot>(_snapshotJson, JsonOptions)
               ?? throw new InvalidOperationException("Snapshot JSON deserialize returned null.");
    }
}

public abstract class RouteBenchmarkBase
{
    protected static readonly Uri TargetUri = new("/routes/target/items/42", UriKind.Relative);
    protected static readonly ExplicitTargetRoute ExplicitTargetRoute = new(42);
    protected static readonly ConventionTargetRoute ConventionTargetRoute = new(42);
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Params(10, 100, 1000)]
    public int RouteCount { get; set; }

    protected RouteTable ExplicitRoutes { get; private set; } = null!;

    protected RouteTable ConventionRoutes { get; private set; } = null!;

    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        ExplicitRoutes = CreateExplicitRoutes(RouteCount);
        ConventionRoutes = CreateConventionRoutes(RouteCount);
    }

    internal void SetupForBudget(int routeCount)
    {
        RouteCount = routeCount;
        GlobalSetup();
    }

    private static RouteTable CreateExplicitRoutes(int routeCount)
    {
        return RouteTable.Create(routes =>
        {
            for (var i = 0; i < routeCount - 1; i++)
                SyntheticRouteMapper.Create(i).MapExplicit(routes, i);

            routes.Map(
                "/routes/target/items/{Id:int}",
                match => new ExplicitTargetRoute(match.Path<int>("Id")),
                format => format.PathParam("Id", route => route.Id));
        });
    }

    private static RouteTable CreateConventionRoutes(int routeCount)
    {
        return RouteTable.Create(routes =>
        {
            for (var i = 0; i < routeCount - 1; i++)
                SyntheticRouteMapper.Create(i).MapConvention(routes, i);

            routes.MapRoute<ConventionTargetRoute>("/routes/target/items/{Id:int}");
        });
    }
}

internal interface ISyntheticRouteMapper
{
    void MapExplicit(RouteTableBuilder routes, int index);

    void MapConvention(RouteTableBuilder routes, int index);
}

internal static class SyntheticRouteMapper
{
    public static ISyntheticRouteMapper Create(int index)
    {
        Type markerType = CreateMarkerType(index);
        Type mapperType = typeof(SyntheticRouteMapper<>).MakeGenericType(markerType);
        return (ISyntheticRouteMapper)Activator.CreateInstance(mapperType)!;
    }

    private static Type CreateMarkerType(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        Type markerType = typeof(SyntheticRouteMarkerEnd);
        do
        {
            Type markerDefinition = (index & 1) == 0
                ? typeof(SyntheticRouteMarkerZero<>):
                typeof(SyntheticRouteMarkerOne<>);
            markerType = markerDefinition.MakeGenericType(markerType);
            index >>= 1;
        } while (index != 0);

        return markerType;
    }
}

internal sealed class SyntheticRouteMapper<TMarker> : ISyntheticRouteMapper
{
    public void MapExplicit(RouteTableBuilder routes, int index)
    {
        routes.Map(
            $"/routes/non-{index}/items/{{Id:int}}",
            match => new ExplicitNonTargetRoute<TMarker>(match.Path<int>("Id")),
            format => format.PathParam("Id", route => route.Id));
    }

    public void MapConvention(RouteTableBuilder routes, int index)
    {
        routes.MapRoute<ConventionNonTargetRoute<TMarker>>($"/routes/non-{index}/items/{{Id:int}}");
    }
}

internal sealed class SyntheticRouteMarkerEnd;

internal sealed class SyntheticRouteMarkerZero<TMarker>;

internal sealed class SyntheticRouteMarkerOne<TMarker>;

public sealed record ExplicitNonTargetRoute<TMarker>(int Id) : AppRoute;

public sealed record ExplicitTargetRoute(int Id) : AppRoute;

public sealed record ConventionNonTargetRoute<TMarker>(int Id) : AppRoute;

public sealed record ConventionTargetRoute(int Id) : AppRoute;

internal sealed class BenchmarkNavigationPlanner : IAppNavigationPlanner
{
    public static BenchmarkNavigationPlanner Instance { get; } = new();

    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        var state = new NavigationState(
            [
                new WindowNode(
                    "main",
                    new StackNode(
                        "root",
                        [new RouteEntry("target", context.Route)]))
            ],
            "main");

        return ValueTask.FromResult(new NavigationPlan(state));
    }
}

internal sealed class BenchmarkNavigationPresenter : INavigationPresenter
{
    public static BenchmarkNavigationPresenter Instance { get; } = new();

    public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
    {
        add { }
        remove { }
    }

    public ValueTask ApplyAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class AllocationBudgetFile
{
    public int Version { get; init; } = 1;

    public int OperationsPerBatch { get; init; } = AllocationBudgetRunner.OperationsPerBatch;

    public int Batches { get; init; } = AllocationBudgetRunner.Batches;

    public SortedDictionary<string, long> Budgets { get; init; } = new(StringComparer.Ordinal);
}

internal static class AllocationBudgetRunner
{
    public const int OperationsPerBatch = 100;
    public const int Batches = 5;
    private const int Version = 1;
    private static object? _sink;

    private static string BudgetFilePath =>
        Path.Combine(ProjectDirectory, "allocation-budgets.json");

    private static string ProjectDirectory
    {
        get
        {
            string? directory = Directory.GetCurrentDirectory();
            while (directory is not null)
            {
                string repoRelativeProject = Path.Combine(
                    directory,
                    "benchmarks",
                    "AdamE.AppNav.Benchmarks",
                    "AdamE.AppNav.Benchmarks.csproj");
                if (File.Exists(repoRelativeProject))
                    return Path.GetDirectoryName(repoRelativeProject)!;

                string localProject = Path.Combine(directory, "AdamE.AppNav.Benchmarks.csproj");
                if (File.Exists(localProject))
                    return directory;

                directory = Directory.GetParent(directory)?.FullName;
            }

            return AppContext.BaseDirectory;
        }
    }

    public static int WriteBudgets()
    {
        SortedDictionary<string, long> budgets = new(StringComparer.Ordinal);
        foreach (AllocationScenario scenario in CreateScenarios())
        {
            long allocated = MeasureMedianAllocatedBytesPerOperation(scenario);
            long budget = Math.Max((long)Math.Ceiling(allocated * 1.20), allocated + 1024);
            budgets[scenario.Name] = budget;
            Console.WriteLine($"{scenario.Name}: {allocated:N0} B/op -> budget {budget:N0}");
        }

        var file = new AllocationBudgetFile
        {
            Version = Version,
            OperationsPerBatch = OperationsPerBatch,
            Batches = Batches,
            Budgets = budgets
        };
        string json = JsonSerializer.Serialize(file, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(BudgetFilePath, json + Environment.NewLine);
        Console.WriteLine($"Wrote allocation budgets to {BudgetFilePath}.");
        return 0;
    }

    public static int CheckBudgets()
    {
        if (!File.Exists(BudgetFilePath))
        {
            Console.Error.WriteLine($"Allocation budget file was not found at {BudgetFilePath}.");
            return 1;
        }

        AllocationBudgetFile file = JsonSerializer.Deserialize<AllocationBudgetFile>(
            File.ReadAllText(BudgetFilePath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidOperationException("Allocation budget file could not be deserialized.");

        if (file.Version != Version ||
            file.OperationsPerBatch != OperationsPerBatch ||
            file.Batches != Batches)
        {
            Console.Error.WriteLine("Allocation budget file measurement policy does not match this runner.");
            return 1;
        }

        var failed = false;
        foreach (AllocationScenario scenario in CreateScenarios())
        {
            if (!file.Budgets.TryGetValue(scenario.Name, out long budget))
            {
                Console.Error.WriteLine($"{scenario.Name}: missing budget.");
                failed = true;
                continue;
            }

            long allocated = MeasureMedianAllocatedBytesPerOperation(scenario);
            Console.WriteLine($"{scenario.Name}: {allocated:N0} B/op (budget {budget:N0})");
            if (allocated <= budget)
                continue;

            Console.Error.WriteLine($"{scenario.Name}: allocation budget exceeded.");
            failed = true;
        }

        if (failed)
            return 1;

        Console.WriteLine("Allocation budgets passed.");
        return 0;
    }

    private static IEnumerable<AllocationScenario> CreateScenarios()
    {
        foreach (int routeCount in new[] { 10, 100, 1000 })
        {
            var matching = new RouteMatchingBenchmarks();
            matching.SetupForBudget(routeCount);
            yield return new AllocationScenario(
                $"RouteMatchingBenchmarks.MatchExplicit_LastRoute/{routeCount}",
                () => matching.MatchExplicit_LastRoute());
            yield return new AllocationScenario(
                $"RouteMatchingBenchmarks.MatchConvention_LastRoute/{routeCount}",
                () => matching.MatchConvention_LastRoute());

            var formatting = new RouteFormattingBenchmarks();
            formatting.SetupForBudget(routeCount);
            yield return new AllocationScenario(
                $"RouteFormattingBenchmarks.FormatExplicit_LastRoute/{routeCount}",
                () => formatting.FormatExplicit_LastRoute());
            yield return new AllocationScenario(
                $"RouteFormattingBenchmarks.FormatConvention_LastRoute/{routeCount}",
                () => formatting.FormatConvention_LastRoute());

            var navigation = new NavigationBenchmarks();
            navigation.SetupForBudget(routeCount);
            yield return new AllocationScenario(
                $"NavigationBenchmarks.NavigateRouteBacked/{routeCount}",
                () => navigation.NavigateRouteBacked());
            yield return new AllocationScenario(
                $"NavigationBenchmarks.NavigateUriBacked_LastRoute/{routeCount}",
                () => navigation.NavigateUriBacked_LastRoute());

            var deferred = new DeferredSerializationBenchmarks();
            deferred.SetupForBudget(routeCount);
            yield return new AllocationScenario(
                $"DeferredSerializationBenchmarks.CreateSnapshot_RouteBacked/{routeCount}",
                () => deferred.CreateSnapshot_RouteBacked());
            yield return new AllocationScenario(
                $"DeferredSerializationBenchmarks.CreateSnapshot_UriBacked_LastRoute/{routeCount}",
                () => deferred.CreateSnapshot_UriBacked_LastRoute());
            yield return new AllocationScenario(
                $"DeferredSerializationBenchmarks.RestoreSnapshot_LastRoute/{routeCount}",
                () => deferred.RestoreSnapshot_LastRoute());
            yield return new AllocationScenario(
                $"DeferredSerializationBenchmarks.JsonRoundTripSnapshot/{routeCount}",
                () => deferred.JsonRoundTripSnapshot());
        }
    }

    private static long MeasureMedianAllocatedBytesPerOperation(AllocationScenario scenario)
    {
        WarmScenario(scenario);
        long[] batches = new long[Batches];
        for (var batch = 0; batch < Batches; batch++)
            batches[batch] = MeasureBatch(scenario);

        Array.Sort(batches);
        return batches[batches.Length / 2];
    }

    private static void WarmScenario(AllocationScenario scenario)
    {
        for (var i = 0; i < OperationsPerBatch; i++)
            _sink = scenario.Invoke();
    }

    private static long MeasureBatch(AllocationScenario scenario)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < OperationsPerBatch; i++)
            _sink = scenario.Invoke();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (long)Math.Ceiling(allocated / (double)OperationsPerBatch);
    }

    private sealed record AllocationScenario(
        string Name,
        Func<object?> Invoke);
}
