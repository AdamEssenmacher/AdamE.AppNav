#if IOS || MACCATALYST
using Microsoft.DotNet.XHarness.TestRunners.Common;
using Microsoft.DotNet.XHarness.TestRunners.Xunit;
using System.Text;
using UIKit;

namespace AdamE.AppNav.Maui.Tests;

internal sealed class XHarnessAppleTestEntryPoint : iOSApplicationEntryPoint
{
    internal const string ResultXmlBase64Prefix = "APPNAV_XHARNESS_RESULT_XML_BASE64:";

    private bool _deferTermination;

    protected override bool LogExcludedTests => true;

    protected override int? MaxParallelThreads => Environment.ProcessorCount;

    protected override IDevice Device { get; } = new PlatformTestDevice();

    protected override IEnumerable<TestAssemblyInfo> GetTestAssemblies()
    {
        var assembly = typeof(MauiNavigationPresenterLifecycleTests).Assembly;
        AppleTestDiagnostics.Write($"Discovering tests from '{assembly.FullName}' at '{assembly.Location}'.");
        yield return new TestAssemblyInfo(assembly, assembly.Location);
    }

    public override async Task RunAsync()
    {
        AppleTestDiagnostics.Write(
            $"Starting Apple XHarness entrypoint. " +
            $"MacCatalyst={OperatingSystem.IsMacCatalyst()}, " +
            $"Host={Environment.GetEnvironmentVariable("NUNIT_HOSTNAME")}, " +
            $"Port={Environment.GetEnvironmentVariable("NUNIT_HOSTPORT")}, " +
            $"Xml={Environment.GetEnvironmentVariable("NUNIT_ENABLE_XML_OUTPUT")}.");

        await RunFileBackedAsync().ConfigureAwait(false);
    }

    protected override void TerminateWithSuccess()
    {
        if (_deferTermination)
        {
            AppleTestDiagnostics.Write("Apple XHarness termination was deferred until result files were flushed.");
            return;
        }

        Console.WriteLine("XHarness test run completed successfully.");
        AppleTestDiagnostics.Write("Terminating Apple XHarness test app with success.");
        var selector = new ObjCRuntime.Selector("terminateWithSuccess");
        UIApplication.SharedApplication.PerformSelector(selector, UIApplication.SharedApplication, 0);
    }

    private async Task RunFileBackedAsync()
    {
        var resultPath = AppleTestDiagnostics.ResultPath;
        var options = ApplicationOptions.Current;
        var exitCode = 1;
        _deferTermination = true;

        AppleTestDiagnostics.Write($"Writing Apple XHarness results. ResultPath='{resultPath}'.");

        var xml = string.Empty;
        if (OperatingSystem.IsMacCatalyst())
        {
            await using var resultsFile = File.CreateText(resultPath);
            var runner = await InternalRunAsync(options, Console.Out, resultsFile).ConfigureAwait(false);
            exitCode = runner.FailedTests == 0 ? 0 : 1;
            await resultsFile.FlushAsync().ConfigureAwait(false);
        }
        else
        {
            using var resultsWriter = new StringWriter();
            var runner = await InternalRunAsync(options, Console.Out, resultsWriter).ConfigureAwait(false);
            exitCode = runner.FailedTests == 0 ? 0 : 1;
            xml = resultsWriter.ToString();
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
            Console.WriteLine($"{ResultXmlBase64Prefix}{encoded}");
        }

        _deferTermination = false;
        AppleTestDiagnostics.Write(
            OperatingSystem.IsMacCatalyst()
                ? $"Apple XHarness result file flushed to '{resultPath}'."
                : $"Apple XHarness result XML streamed to application log. Length={xml.Length}.");
        Environment.Exit(exitCode);
    }
}
#endif
