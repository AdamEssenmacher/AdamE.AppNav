#if ANDROID
using Android.OS;
using Microsoft.DotNet.XHarness.TestRunners.Common;
using Microsoft.DotNet.XHarness.TestRunners.Xunit;
using Application = Android.App.Application;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class HeadlessTestRunner : AndroidApplicationEntryPoint
{
    private readonly string _resultsPath;

    public HeadlessTestRunner()
    {
        _resultsPath = Path.Combine(
            Application.Context.CacheDir!.AbsolutePath,
            "AdamE.MauiRouter.Maui.Tests.xml");
    }

    public override TextWriter Logger => Console.Out;

    public override string TestsResultsFinalPath => _resultsPath;

    protected override int? MaxParallelThreads => System.Environment.ProcessorCount;

    protected override IDevice Device { get; } = new PlatformTestDevice();

    protected override IEnumerable<TestAssemblyInfo> GetTestAssemblies()
    {
        var assembly = typeof(MauiNavigationPresenterLifecycleTests).Assembly;
        var path = Path.Combine(
            Application.Context.CacheDir!.AbsolutePath,
            $"{assembly.GetName().Name}.dll");

        if (!File.Exists(path))
        {
            File.Create(path).Dispose();
        }

        yield return new TestAssemblyInfo(assembly, path);
    }

    protected override void TerminateWithSuccess()
    {
    }

    public async Task<Bundle> RunTestsAsync()
    {
        var bundle = new Bundle();
        TestsCompleted += OnTestsCompleted;

        try
        {
            await RunAsync().ConfigureAwait(false);
        }
        finally
        {
            TestsCompleted -= OnTestsCompleted;
        }

        if (File.Exists(TestsResultsFinalPath))
        {
            bundle.PutString("test-results-path", TestsResultsFinalPath);
        }

        if (bundle.GetLong("return-code", -1) == -1)
        {
            bundle.PutLong("return-code", 1);
        }

        return bundle;

        void OnTestsCompleted(object? sender, TestRunResult results)
        {
            bundle.PutString(
                "test-execution-summary",
                $"Tests run: {results.ExecutedTests}; Passed: {results.PassedTests}; Failed: {results.FailedTests}; Skipped: {results.SkippedTests}");
            bundle.PutLong("return-code", results.FailedTests == 0 ? 0 : 1);
        }
    }
}
#endif
