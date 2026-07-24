using System.Xml.Linq;

namespace AdamE.AppNav.Tests;

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
                Path.Combine(root, "src", "AdamE.AppNav"),
                Path.Combine(root, "src", "AdamE.AppNav.Maui"),
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
    public void TargetFrameworksMatchNet10PlatformContract()
    {
        var root = RepositoryRoot();
        var coreProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav", "AdamE.AppNav.csproj"));
        var mauiProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav.Maui", "AdamE.AppNav.Maui.csproj"));
        var coreTestsProject = File.ReadAllText(
            Path.Combine(root, "tests", "AdamE.AppNav.Tests", "AdamE.AppNav.Tests.csproj"));
        var generatorTestsProject = File.ReadAllText(
            Path.Combine(root, "tests", "AdamE.AppNav.Generators.Tests", "AdamE.AppNav.Generators.Tests.csproj"));
        var solution = File.ReadAllText(Path.Combine(root, "AdamE.AppNav.slnx"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", coreProject, StringComparison.Ordinal);
        Assert.Contains("<TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>", mauiProject, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", coreTestsProject, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", generatorTestsProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-android", mauiProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-ios", mauiProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-maccatalyst", mauiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("windows", mauiProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netstandard", coreProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests/AdamE.AppNav.Maui.Tests/AdamE.AppNav.Maui.Tests.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void MauiProjectDoesNotSuppressTargetFrameworkEolWarnings()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot(), "src", "AdamE.AppNav.Maui", "AdamE.AppNav.Maui.csproj"));
        Assert.DoesNotContain(
            project.Root!.Elements("PropertyGroup"),
            group => string.Equals((string?)group.Element("CheckEolWorkloads"), "false", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreAssemblyDoesNotExposeInternalsToProductionMauiAdapter()
    {
        var assemblyInfo = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "AdamE.AppNav", "AssemblyInfo.cs"));

        Assert.DoesNotContain(
            "InternalsVisibleTo(\"AdamE.AppNav.Maui\")",
            assemblyInfo,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CorePackageIncludesGeneratorAnalyzerWithoutEvaluationTimeExistsGuard()
    {
        var coreProject = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "AdamE.AppNav", "AdamE.AppNav.csproj"));

        Assert.Contains("AdamE.AppNav.Generators.dll", coreProject, StringComparison.Ordinal);
        Assert.Contains("PackagePath=\"analyzers/dotnet/cs\"", coreProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Exists('..\\AdamE.AppNav.Generators", coreProject, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseConfidenceRunsGeneratorTests()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot(), ".github", "workflows", "release-confidence.yml"));

        Assert.Contains(
            "tests/AdamE.AppNav.Generators.Tests/AdamE.AppNav.Generators.Tests.csproj",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppLinkHelpersCreateAppLinkRequests()
    {
        var appLinksDirectory = Path.Combine(RepositoryRoot(), "src", "AdamE.AppNav.Maui");
        var appLinkSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appLinksDirectory, "*AppLink*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.Contains("NavigationRequestSource.AppLink", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromIntent", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromOpenUrl", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromUserActivity", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("UseAppNavAppLinks", appLinkSource, StringComparison.Ordinal);
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
        var platformFactoryFiles = new[]
        {
            Path.Combine(root, "src", "AdamE.AppNav.Maui", "AppLinks", "AppleAppLinkRequestFactory.cs"),
            Path.Combine(root, "src", "AdamE.AppNav.Maui", "Platforms", "Android", "AndroidAppLinkRequestFactory.cs")
        };
        var platformFactorySource = string.Join(
            Environment.NewLine,
            platformFactoryFiles.Select(File.ReadAllText));
        var genericFactorySource = File.ReadAllText(
            Path.Combine(root, "src", "AdamE.AppNav.Maui", "AppLinks", "MauiAppLinkRequestFactory.cs"));

        Assert.Contains("MauiAppLinkProvenanceProviders.MauiAppLink", genericFactorySource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkRequestFactory.TryFromUriString", platformFactorySource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkProvenanceProviders.IosOpenUrl", platformFactorySource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkProvenanceProviders.IosUserActivity", platformFactorySource, StringComparison.Ordinal);
        Assert.Contains("MauiAppLinkProvenanceProviders.AndroidIntent", platformFactorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("RouterNavigationRequest.FromUri", platformFactorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"maui-app-link\"", genericFactorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ios-open-url\"", platformFactorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ios-user-activity\"", platformFactorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"android-intent\"", platformFactorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleUsesProductionAppNavWiring()
    {
        var sampleDirectory = Path.Combine(RepositoryRoot(), "samples", "Commerce.Sample");
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(sampleDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        Assert.Contains("AddAppNav<CommerceNavigationPlanner>", source, StringComparison.Ordinal);
        Assert.Contains("AddAppNavFileDeferredNavigationRequests", source, StringComparison.Ordinal);
        Assert.Contains("AddAppNavStartup", source, StringComparison.Ordinal);
        Assert.Contains("UseAppNavAppLinks", source, StringComparison.Ordinal);
        Assert.Contains("IAppNavStartupService", source, StringComparison.Ordinal);
        Assert.Contains("StartAsync(window)", source, StringComparison.Ordinal);
        Assert.Contains("FallbackRequestFactory", source, StringComparison.Ordinal);
        Assert.Contains("CommerceRouteMetadata.RouteStateRegistry", source, StringComparison.Ordinal);
        Assert.Contains("AppRouteRequest", source, StringComparison.Ordinal);
        Assert.Contains("IRouterNavigator", source, StringComparison.Ordinal);
        Assert.Contains("CommerceNotFoundRoute", source, StringComparison.Ordinal);
        Assert.Contains("CommerceNotFoundPage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedElementNavigationTransition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppNavTransition.SetSharedElementId", source, StringComparison.Ordinal);
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
        Assert.Contains("AppNavQueryMetadata(typeof(CommerceRouteMetadata), nameof(CommerceRouteMetadata.Campaign))", sampleSource, StringComparison.Ordinal);
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
        var solution = File.ReadAllText(Path.Combine(root, "AdamE.AppNav.slnx"));

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
        Assert.DoesNotContain("AppNavTransition", readme, StringComparison.Ordinal);
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
        Assert.Contains("Use the public route table, planner, navigator factory, diagnostics, and state types directly from app tests.", readme, StringComparison.Ordinal);
        Assert.Contains("test fixtures and assertion helpers app-owned", readme, StringComparison.Ordinal);
        Assert.Contains("RouterNavigatorFactory.Create(", readme, StringComparison.Ordinal);
        Assert.Contains("NavigateAsync(Uri|AppRoute|AppRouteRequest|RouterNavigationRequest)", readme, StringComparison.Ordinal);
        Assert.Contains("`NavigateAsync(Uri...)` starts from a URL directly.", readme, StringComparison.Ordinal);
        Assert.Contains("`ReconcileAsync(...)` accepts an explicit `NavigationReconciliation`", readme, StringComparison.Ordinal);
        Assert.Contains("Deferred request serializer coverage:", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("AdamE.AppNav.Testing", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("RouterTestNavigator", readme, StringComparison.Ordinal);
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
        var packageVerifier = File.ReadAllText(Path.Combine(root, "eng", "verify-package-assets.sh"));
        var mauiTestsProject = File.ReadAllText(Path.Combine(root, "tests", "AdamE.AppNav.Maui.Tests", "AdamE.AppNav.Maui.Tests.csproj"));

        Assert.Contains("docs/release-checklist.md", readme, StringComparison.Ordinal);
        Assert.Contains("eng/run-maui-platform-tests.sh android", checklist, StringComparison.Ordinal);
        Assert.Contains("dotnet pack src/AdamE.AppNav/AdamE.AppNav.csproj", checklist, StringComparison.Ordinal);
        Assert.DoesNotContain("AdamE.AppNav.Testing", checklist, StringComparison.Ordinal);
        Assert.Contains("xharness android test", runner, StringComparison.Ordinal);
        Assert.Contains("xharness apple test", runner, StringComparison.Ordinal);
        Assert.Contains("--instrumentation", runner, StringComparison.Ordinal);
        Assert.Contains("require_test_results", runner, StringComparison.Ordinal);
        Assert.Contains("fail_on_unhandled_exceptions", runner, StringComparison.Ordinal);
        Assert.Contains("grep -Eq", packageVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("rg --quiet", packageVerifier, StringComparison.Ordinal);
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

        Assert.Contains("unit-api-allocation:", workflow, StringComparison.Ordinal);
        Assert.Contains("analyzer-pack:", workflow, StringComparison.Ordinal);
        Assert.Contains("maccatalyst-nativeaot:", workflow, StringComparison.Ordinal);
        Assert.Contains("android-full-trim:", workflow, StringComparison.Ordinal);
        Assert.Contains("maccatalyst-platform-tests:", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test tests/AdamE.AppNav.Tests/AdamE.AppNav.Tests.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build samples/Commerce.Sample/Commerce.Sample.csproj -c Release -f net10.0-maccatalyst", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet pack src/AdamE.AppNav/AdamE.AppNav.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/verify-package-assets.sh artifacts/packages", workflow, StringComparison.Ordinal);
        Assert.Contains("-f net10.0-android", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/run-maui-platform-tests.sh maccatalyst", workflow, StringComparison.Ordinal);
        Assert.Contains("run-ios-platform-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("run-android-platform-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("reactivecircus/android-emulator-runner", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: macos-26", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("runs-on: macos-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("10.0.302", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "DEVELOPER_DIR: /Applications/Xcode_26.6.app/Contents/Developer",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("actions/checkout@v7", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/setup-dotnet@v5", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v7", workflow, StringComparison.Ordinal);
        Assert.Contains("-warnnotaserror:IL2104,IL3053", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageMetadataIsConfiguredForReleaseValidation()
    {
        var root = RepositoryRoot();
        var buildProps = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var coreProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav", "AdamE.AppNav.csproj"));
        var mauiProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav.Maui", "AdamE.AppNav.Maui.csproj"));

        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<RepositoryUrl>https://github.com/AdamEssenmacher/AdamE.AppNav.git</RepositoryUrl>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<IncludeSymbols>true</IncludeSymbols>", buildProps, StringComparison.Ordinal);
        Assert.Contains("Microsoft.SourceLink.GitHub", buildProps, StringComparison.Ordinal);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", coreProject, StringComparison.Ordinal);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", mauiProject, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AdamE.AppNav.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
