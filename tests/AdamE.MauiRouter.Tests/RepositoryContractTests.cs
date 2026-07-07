namespace AdamE.MauiRouter.Tests;

public sealed class RepositoryContractTests
{
    [Fact]
    public void SamplesDoNotUseShell()
    {
        var sampleDirectory = Path.Combine(RepositoryRoot(), "samples");
        var files = Directory
            .EnumerateFiles(sampleDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                           path.EndsWith(".xaml", StringComparison.Ordinal) ||
                           path.EndsWith(".csproj", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.DoesNotContain("Shell", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.DoesNotContain("Prism", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CoreAdapterAndSampleDoNotDependOnShell()
    {
        var root = RepositoryRoot();
        var files = new[]
            {
                Path.Combine(root, "src", "AdamE.MauiRouter"),
                Path.Combine(root, "src", "AdamE.MauiRouter.Maui"),
                Path.Combine(root, "samples", "Commerce.Sample")
            }
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                           path.EndsWith(".xaml", StringComparison.Ordinal) ||
                           path.EndsWith(".csproj", StringComparison.Ordinal))
            .ToArray();

        foreach (var file in files)
        {
            Assert.DoesNotContain("Microsoft.Maui.Controls.Shell", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.DoesNotContain("<Shell", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.DoesNotContain("Prism", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SampleAndTestsDoNotUsePreviousSampleTheme()
    {
        var root = RepositoryRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(root, "samples"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.*", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                           path.EndsWith(".xaml", StringComparison.Ordinal) ||
                           path.EndsWith(".csproj", StringComparison.Ordinal) ||
                           path.EndsWith(".slnx", StringComparison.Ordinal))
            .ToArray();

        var forbidden = new[]
        {
            string.Concat("Hu", "nts.Sample"),
            string.Concat("Hu", "ntsNavigationPlanner"),
            string.Concat("Hu", "nt"),
            string.Concat("hu", "nts"),
            string.Concat("/or", "gs/"),
            string.Concat("ac", "me"),
            string.Concat("highlight=mis", "sion-")
        };

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var term in forbidden)
            {
                Assert.DoesNotContain(term, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TargetFrameworksMatchV1PlatformContract()
    {
        var root = RepositoryRoot();
        var coreProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.MauiRouter", "AdamE.MauiRouter.csproj"));
        var mauiProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.MauiRouter.Maui", "AdamE.MauiRouter.Maui.csproj"));
        var solution = File.ReadAllText(Path.Combine(root, "AdamE.MauiRouter.slnx"));

        Assert.Contains("<TargetFrameworks>net9.0;net10.0</TargetFrameworks>", coreProject, StringComparison.Ordinal);
        Assert.Contains("net9.0-android", mauiProject, StringComparison.Ordinal);
        Assert.Contains("net9.0-ios", mauiProject, StringComparison.Ordinal);
        Assert.Contains("net9.0-maccatalyst", mauiProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-android", mauiProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-ios", mauiProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-maccatalyst", mauiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("windows", mauiProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netstandard", coreProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests/AdamE.MauiRouter.Maui.Tests/AdamE.MauiRouter.Maui.Tests.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreAssemblyDoesNotExposeInternalsToProductionMauiAdapter()
    {
        var assemblyInfo = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "AdamE.MauiRouter", "AssemblyInfo.cs"));

        Assert.DoesNotContain(
            "InternalsVisibleTo(\"AdamE.MauiRouter.Maui\")",
            assemblyInfo,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppLinkHelpersCreateAppLinkRequests()
    {
        var appLinksDirectory = Path.Combine(RepositoryRoot(), "src", "AdamE.MauiRouter.Maui");
        var appLinkSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appLinksDirectory, "*AppLink*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.Contains("NavigationRequestSource.AppLink", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromIntent", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromOpenUrl", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromUserActivity", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("UseMauiRouterAppLinks", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("NavigationRequestProvenance", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkProvenanceProviders", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("android-intent", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("ios-open-url", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("ios-user-activity", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("maui-app-link", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("MauiExternalNavigationDispatcher", File.ReadAllText(Path.Combine(appLinksDirectory, "AppLinks", "MauiExternalNavigationDispatcher.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void AppLinkFactoriesUseProviderConstants()
    {
        var root = RepositoryRoot();
        var factoryFiles = new[]
        {
            Path.Combine(root, "src", "AdamE.MauiRouter.Maui", "AppLinks", "MauiAppLinkRequestFactory.cs"),
            Path.Combine(root, "src", "AdamE.MauiRouter.Maui", "AppLinks", "AppleAppLinkRequestFactory.cs"),
            Path.Combine(root, "src", "AdamE.MauiRouter.Maui", "Platforms", "Android", "AndroidAppLinkRequestFactory.cs")
        };
        var factorySource = string.Join(Environment.NewLine, factoryFiles.Select(File.ReadAllText));

        Assert.Contains("MauiAppLinkProvenanceProviders.MauiAppLink", factorySource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkProvenanceProviders.IosOpenUrl", factorySource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkProvenanceProviders.IosUserActivity", factorySource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkProvenanceProviders.AndroidIntent", factorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"maui-app-link\"", factorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ios-open-url\"", factorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ios-user-activity\"", factorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"android-intent\"", factorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleUsesProductionMauiRouterWiring()
    {
        var sampleDirectory = Path.Combine(RepositoryRoot(), "samples", "Commerce.Sample");
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(sampleDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        Assert.Contains("AddMauiRouter<CommerceNavigationPlanner>", source, StringComparison.Ordinal);
        Assert.Contains("AddMauiRouterFileDeferredNavigationRequests", source, StringComparison.Ordinal);
        Assert.Contains("AddMauiRouterStartup", source, StringComparison.Ordinal);
        Assert.Contains("UseMauiRouterAppLinks", source, StringComparison.Ordinal);
        Assert.Contains("IMauiRouterStartupService", source, StringComparison.Ordinal);
        Assert.Contains("StartAsync(window)", source, StringComparison.Ordinal);
        Assert.Contains("FallbackRequestFactory", source, StringComparison.Ordinal);
        Assert.Contains("CommerceRouteMetadata.RouteStateRegistry", source, StringComparison.Ordinal);
        Assert.Contains("AppRouteRequest", source, StringComparison.Ordinal);
        Assert.Contains("IRouterNavigator", source, StringComparison.Ordinal);
        Assert.Contains("CommerceNotFoundRoute", source, StringComparison.Ordinal);
        Assert.Contains("CommerceNotFoundPage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedElementNavigationTransition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MauiRouterTransition.SetSharedElementId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasPendingRequests", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreFromStoreAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".AttachWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentPage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplePagesUseBlessedPathNavigationSurface()
    {
        var samplePagesDirectory = Path.Combine(RepositoryRoot(), "samples", "Commerce.Sample", "Pages");
        var pageSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(samplePagesDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var sampleSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "samples", "Commerce.Sample"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        Assert.Contains("IRouterNavigator", pageSource, StringComparison.Ordinal);
        Assert.Contains("CommerceRouteFactory.", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RouterNavigationRequest", pageSource, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<![A-Za-z0-9_])RouterNavigator(?![A-Za-z0-9_])", pageSource);
        Assert.Contains("QueryMetadata(CommerceRouteMetadata.Campaign)", sampleSource, StringComparison.Ordinal);
        Assert.Contains("CommerceRouteMetadata.RouteStateRegistry", sampleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CommerceIsTheOnlySample()
    {
        var root = RepositoryRoot();
        var sampleDirectories = Directory
            .EnumerateDirectories(Path.Combine(root, "samples"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var solution = File.ReadAllText(Path.Combine(root, "AdamE.MauiRouter.slnx"));

        Assert.Equal(new[] { "Commerce.Sample" }, sampleDirectories);
        Assert.Contains("samples/Commerce.Sample/Commerce.Sample.csproj", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("Learning.Sample", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentationDoesNotAdvertiseTransitionSupport()
    {
        var root = RepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("Native user-driven back remains native-first", readme, StringComparison.Ordinal);
        Assert.Contains("Android predictive back is not implemented in v1", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationTransition", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedElementNavigationTransition", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("MauiRouterTransition", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("For custom transitions", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("custom handler", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("options.Transitions.Map", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Android back/predictive-back behavior", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("PredictiveBack", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata seam for future transition work", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Full transition execution is a future adapter feature", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadmeExamplesStayOnPublicSurface()
    {
        var root = RepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("FallbackRequestFactory", readme, StringComparison.Ordinal);
        Assert.Contains("RouterNavigationRequest.FromUri(uri, NavigationRequestSource.Test)", readme, StringComparison.Ordinal);
        Assert.Contains("Planner and navigator tests:", readme, StringComparison.Ordinal);
        Assert.Contains("Use `AdamE.MauiRouter.Testing` for framework-neutral route, planner, navigator, diagnostics, and state helpers.", readme, StringComparison.Ordinal);
        Assert.Contains("Framework-neutral route, planner, navigator, diagnostics, and state", readme, StringComparison.Ordinal);
        Assert.Contains("assertion helpers for consuming app tests.", readme, StringComparison.Ordinal);
        Assert.Contains("NavigateAsync(Uri|AppRoute|AppRouteRequest|RouterNavigationRequest)", readme, StringComparison.Ordinal);
        Assert.Contains("`NavigateAsync(Uri...)` starts from a URL directly.", readme, StringComparison.Ordinal);
        Assert.Contains("`ReconcileAsync(...)` accepts an explicit `NavigationReconciliation`", readme, StringComparison.Ordinal);
        Assert.Contains("Deferred request serializer coverage:", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationSnapshotTestSerializer", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreFromStoreAsync", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("options.InitialRequest", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Planner and presenter tests:", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Framework-neutral route, planner, presenter, diagnostics, and state test helpers.", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionReadinessInfrastructureIsDocumented()
    {
        var root = RepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var checklist = File.ReadAllText(Path.Combine(root, "docs", "release-checklist.md"));
        var runner = File.ReadAllText(Path.Combine(root, "eng", "run-maui-platform-tests.sh"));
        var mauiTestsProject = File.ReadAllText(Path.Combine(root, "tests", "AdamE.MauiRouter.Maui.Tests", "AdamE.MauiRouter.Maui.Tests.csproj"));

        Assert.Contains("docs/release-checklist.md", readme, StringComparison.Ordinal);
        Assert.Contains("eng/run-maui-platform-tests.sh android", checklist, StringComparison.Ordinal);
        Assert.Contains("dotnet pack src/AdamE.MauiRouter/AdamE.MauiRouter.csproj", checklist, StringComparison.Ordinal);
        Assert.Contains("xharness android test", runner, StringComparison.Ordinal);
        Assert.Contains("xharness apple test", runner, StringComparison.Ordinal);
        Assert.Contains("--instrumentation", runner, StringComparison.Ordinal);
        Assert.Contains("require_test_results", runner, StringComparison.Ordinal);
        Assert.Contains("fail_on_unhandled_exceptions", runner, StringComparison.Ordinal);
        Assert.Contains("net10.0-android", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-ios", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-maccatalyst", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DotNet.XHarness.TestRunners.Xunit", mauiTestsProject, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseConfidenceWorkflowDefinesRequiredGates()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-confidence.yml"));

        Assert.Contains("dotnet test tests/AdamE.MauiRouter.Tests/AdamE.MauiRouter.Tests.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build src/AdamE.MauiRouter.Maui/AdamE.MauiRouter.Maui.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build samples/Commerce.Sample/Commerce.Sample.csproj -f net10.0-maccatalyst", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet pack src/AdamE.MauiRouter/AdamE.MauiRouter.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/run-maui-platform-tests.sh maccatalyst", workflow, StringComparison.Ordinal);
        Assert.Contains("run-ios-platform-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("run-android-platform-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("reactivecircus/android-emulator-runner", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageMetadataIsConfiguredForReleaseValidation()
    {
        var root = RepositoryRoot();
        var buildProps = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var coreProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.MauiRouter", "AdamE.MauiRouter.csproj"));
        var mauiProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.MauiRouter.Maui", "AdamE.MauiRouter.Maui.csproj"));
        var testingProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.MauiRouter.Testing", "AdamE.MauiRouter.Testing.csproj"));

        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<RepositoryUrl>https://github.com/AdamEssenmacher/AdamE.MauiRouter.git</RepositoryUrl>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<IncludeSymbols>true</IncludeSymbols>", buildProps, StringComparison.Ordinal);
        Assert.Contains("Microsoft.SourceLink.GitHub", buildProps, StringComparison.Ordinal);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", coreProject, StringComparison.Ordinal);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", mauiProject, StringComparison.Ordinal);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", testingProject, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AdamE.MauiRouter.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
