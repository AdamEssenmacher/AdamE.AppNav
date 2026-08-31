using System.Xml.Linq;
using System.Text.RegularExpressions;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;

namespace AdamE.AppNav.Tests;

public sealed partial class RepositoryContractTests
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
    public void DocumentationRequiresOneNavigationOwnerAndRejectsShellAndPrismNavigation()
    {
        var root = RepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        string whyAppNav = File.ReadAllText(
            Path.Combine(root, "docs", "concepts", "00-why-appnav.md"));
        string mauiIntegration = File.ReadAllText(
            Path.Combine(root, "docs", "guides", "02-maui-integration.md"));

        Assert.Contains(
            "Navigation ownership: no Shell or Prism navigation",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppNav does not support `Microsoft.Maui.Controls.Shell`, Shell routing, or\nPrism navigation",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("## One navigation owner", whyAppNav, StringComparison.Ordinal);
        Assert.Contains(
            "## One navigation owner: no Shell or Prism navigation",
            mauiIntegration,
            StringComparison.Ordinal);
        Assert.Contains(
            "This rule is intentionally scoped to navigation ownership",
            mauiIntegration,
            StringComparison.Ordinal);
        Assert.Contains(
            "This preview does not include a\nShell or Prism bridge or a mixed-ownership migration mode",
            mauiIntegration,
            StringComparison.Ordinal);
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
        Assert.Contains(">28.0</SupportedOSPlatformVersion>", mauiProject, StringComparison.Ordinal);
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
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "release-confidence.yml"));
        var verifier = File.ReadAllText(Path.Combine(root, "eng", "verify.sh"));

        Assert.Contains("eng/verify.sh contracts", workflow, StringComparison.Ordinal);
        Assert.Contains("tests/AdamE.AppNav.Generators.Tests/AdamE.AppNav.Generators.Tests.csproj", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLinkHelpersCreateAppLinkRequests()
    {
        var appLinksDirectory = Path.Combine(RepositoryRoot(), "src", "AdamE.AppNav.Maui");
        var appLinkSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(appLinksDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.Contains("NavigationRequestSource.AppLink", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromIntent", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromOpenUrl", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("FromUserActivity", appLinkSource, StringComparison.Ordinal);
        Assert.Contains("UseAppNavExternalNavigation", appLinkSource, StringComparison.Ordinal);
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
        Assert.Contains("MauiAppLinkRequestFactory.ParseUriString", platformFactorySource, StringComparison.Ordinal);
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
        var program = File.ReadAllText(Path.Combine(sampleDirectory, "MauiProgram.cs"));
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(sampleDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        Assert.Contains("CommerceNavigationModel.Create()", source, StringComparison.Ordinal);
        Assert.Contains("BranchHostNavigationModel<AppRoute>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAppNavFileDeferredNavigationRequests", source, StringComparison.Ordinal);
        Assert.Contains("AddAppNavStartup", source, StringComparison.Ordinal);
        Assert.Contains("UseAppNavExternalNavigation", source, StringComparison.Ordinal);
        Assert.Contains("IAppNavStartupService", source, StringComparison.Ordinal);
        Assert.Contains("Start(window, \"main\")", source, StringComparison.Ordinal);
        Assert.Contains("FallbackRouteFactory", source, StringComparison.Ordinal);
        Assert.Contains("AppRouteRequest", source, StringComparison.Ordinal);
        Assert.Contains("IRouterNavigator", source, StringComparison.Ordinal);
        Assert.Contains("CommerceNotFoundRoute", source, StringComparison.Ordinal);
        Assert.Contains("CommerceNotFoundPage", source, StringComparison.Ordinal);
        Assert.Contains("options.FallbackRouteFactory = context", program, StringComparison.Ordinal);
        Assert.Contains("new CommerceNotFoundRoute", program, StringComparison.Ordinal);
        Assert.Contains("context.Request.Uri", program, StringComparison.Ordinal);
        Assert.Contains(".EntryId(route => $\"not-found:{route.Uri}\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedElementNavigationTransition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppNavTransition.SetSharedElementId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasPendingRequests", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreFromStoreAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".AttachWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentPage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CommerceSampleDeclaresRunnableDebugExternalIngress()
    {
        var sampleDirectory = Path.Combine(RepositoryRoot(), "samples", "Commerce.Sample");
        var androidActivity = File.ReadAllText(
            Path.Combine(sampleDirectory, "Platforms", "Android", "MainActivity.cs"));
        var appleManifest = File.ReadAllText(
            Path.Combine(sampleDirectory, "Platforms", "Apple", "DebugUrlScheme.plist"));
        var project = File.ReadAllText(Path.Combine(sampleDirectory, "Commerce.Sample.csproj"));
        var readme = File.ReadAllText(Path.Combine(sampleDirectory, "README.md"));

        Assert.Contains("#if DEBUG", androidActivity, StringComparison.Ordinal);
        Assert.Contains("IntentFilter", androidActivity, StringComparison.Ordinal);
        Assert.Contains("DataScheme = \"appnav-commerce\"", androidActivity, StringComparison.Ordinal);
        Assert.Contains("DataHost = \"shop\"", androidActivity, StringComparison.Ordinal);
        Assert.Contains("'$(Configuration)' == 'Debug'", project, StringComparison.Ordinal);
        Assert.Contains("PartialAppManifest", project, StringComparison.Ordinal);
        Assert.Contains("<string>appnav-commerce</string>", appleManifest, StringComparison.Ordinal);

        Assert.Contains("BranchHostNavigationModel<AppRoute>", readme, StringComparison.Ordinal);
        Assert.Contains("FallbackRouteFactory", readme, StringComparison.Ordinal);
        Assert.Contains("independent tab stacks", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`Auto`", readme, StringComparison.Ordinal);
        Assert.Contains("`Contextual`", readme, StringComparison.Ordinal);
        Assert.Contains("`ReplaceCurrent`", readme, StringComparison.Ordinal);
        Assert.Contains("`Canonical`", readme, StringComparison.Ordinal);
        Assert.Contains("adb shell am start", readme, StringComparison.Ordinal);
        Assert.Contains("xcrun simctl openurl", readme, StringComparison.Ordinal);
        Assert.Contains("campaign=warm", readme, StringComparison.Ordinal);
        Assert.Contains("HostBack", readme, StringComparison.Ordinal);
        Assert.Contains("BranchChanged", readme, StringComparison.Ordinal);
        Assert.Contains("HostReconciliation", readme, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Routes.cs", "getting-started-routes")]
    [InlineData("NavigationModel.cs", "getting-started-model")]
    [InlineData("Pages.cs", "getting-started-typed-navigation")]
    [InlineData("MauiProgram.cs", "getting-started-registration-services")]
    [InlineData("App.cs", "getting-started-window-start")]
    public void ReadmeSnippetsComeFromBuildableGettingStartedRegions(string fileName, string regionName)
    {
        var root = RepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var gettingStarted = File.ReadAllText(
            Path.Combine(root, "docs", "guides", "01-getting-started.md"));
        var sample = File.ReadAllText(
            Path.Combine(root, "samples", "GettingStarted.Sample", fileName));

        string region = ReadRegion(sample, regionName);
        Assert.Contains(region, readme, StringComparison.Ordinal);
        Assert.Contains(region, gettingStarted, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentationLinksAndHeadingFragmentsResolve()
    {
        var root = RepositoryRoot();
        string[] documents = DocumentationFiles(root).ToArray();

        Assert.NotEmpty(documents);
        foreach (string document in documents)
        foreach (Match match in MarkdownLinkPattern().Matches(File.ReadAllText(document)).Cast<Match>())
        {
            string target = match.Groups["target"].Value.Trim();
            if (target.Length == 0 || IsExternalLink(target))
                continue;

            int fragmentIndex = target.IndexOf('#', StringComparison.Ordinal);
            string pathPart = fragmentIndex >= 0 ? target[..fragmentIndex] : target;
            string? fragment = fragmentIndex >= 0 ? target[(fragmentIndex + 1)..] : null;
            string targetFile = pathPart.Length == 0
                ? document
                : Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(document)!,
                        Uri.UnescapeDataString(pathPart)));

            Assert.True(
                File.Exists(targetFile),
                $"Markdown link '{target}' in '{Path.GetRelativePath(root, document)}' does not resolve.");

            if (!string.IsNullOrEmpty(fragment))
            {
                string[] anchors = HeadingPattern()
                    .Matches(File.ReadAllText(targetFile))
                    .Cast<Match>()
                    .Select(static heading => HeadingAnchor(heading.Groups["heading"].Value))
                    .ToArray();
                Assert.Contains(Uri.UnescapeDataString(fragment), anchors, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DocumentationHomeIndexesEveryDocumentationPage()
    {
        var root = RepositoryRoot();
        string documentationRoot = Path.Combine(root, "docs");
        string homePath = Path.Combine(documentationRoot, "index.md");
        string home = File.ReadAllText(homePath);
        var indexedFiles = MarkdownLinkPattern()
            .Matches(home)
            .Cast<Match>()
            .Select(match => match.Groups["target"].Value.Split('#')[0])
            .Where(static target => !string.IsNullOrWhiteSpace(target) && !IsExternalLink(target))
            .Select(target => Path.GetFullPath(Path.Combine(documentationRoot, Uri.UnescapeDataString(target))))
            .ToHashSet(StringComparer.Ordinal);

        string[] pages = Directory
            .EnumerateFiles(documentationRoot, "*.md", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, homePath, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(pages);
        foreach (string page in pages)
            Assert.Contains(page, indexedFiles);
    }

    [Fact]
    public void DocumentationFilesAreIncludedInTheSolutionFolder()
    {
        var root = RepositoryRoot();
        string documentationRoot = Path.Combine(root, "docs");
        string[] expected = Directory
            .EnumerateFiles(documentationRoot, "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = XDocument
            .Load(Path.Combine(root, "AdamE.AppNav.slnx"))
            .Root!
            .Elements("Folder")
            .Where(folder => ((string?)folder.Attribute("Name"))?.StartsWith(
                "/docs/",
                StringComparison.Ordinal) == true)
            .Elements("File")
            .Select(file => (string)file.Attribute("Path")!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UserGuidesHaveNavigationAndMaintainerMaterialIsNotOnTheOnboardingPath()
    {
        var root = RepositoryRoot();
        string[] guides = Directory
            .EnumerateFiles(Path.Combine(root, "docs", "guides"), "*.md", SearchOption.TopDirectoryOnly)
            .ToArray();
        string[] concepts = Directory
            .EnumerateFiles(Path.Combine(root, "docs", "concepts"), "*.md", SearchOption.TopDirectoryOnly)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "01-getting-started.md",
                "02-maui-integration.md",
                "03-application-architecture-and-testing.md",
                "04-navigation-outcomes-and-failure-handling.md",
                "05-external-navigation.md",
                "06-deferred-navigation.md",
                "07-troubleshooting.md"
            },
            guides.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                "00-why-appnav.md",
                "01-routing-and-metadata.md",
                "02-topology-and-planning.md",
                "03-requests-and-provenance.md"
            },
            concepts.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (string document in guides.Concat(concepts))
        {
            string text = File.ReadAllText(document);
            Assert.Contains("[Documentation home](../index.md)", text, StringComparison.Ordinal);
            Assert.Contains("## Next steps", text, StringComparison.Ordinal);
        }

        string onboarding = string.Join(
            Environment.NewLine,
            File.ReadAllText(Path.Combine(root, "README.md")),
            File.ReadAllText(Path.Combine(root, "docs", "guides", "01-getting-started.md")),
            File.ReadAllText(Path.Combine(root, "samples", "GettingStarted.Sample", "README.md")));
        Assert.DoesNotContain("maintainers/", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("dogfood checkpoint", onboarding, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchitectureDocumentationMatchesPackageBoundaries()
    {
        var root = RepositoryRoot();
        XDocument coreProject = XDocument.Load(
            Path.Combine(root, "src", "AdamE.AppNav", "AdamE.AppNav.csproj"));
        XDocument mauiProject = XDocument.Load(
            Path.Combine(root, "src", "AdamE.AppNav.Maui", "AdamE.AppNav.Maui.csproj"));

        Assert.Equal("net10.0", coreProject.Descendants("TargetFramework").Single().Value);
        Assert.Empty(coreProject.Descendants("UseMaui"));

        string[] mauiFrameworks = mauiProject
            .Descendants("TargetFrameworks")
            .Single()
            .Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            new[] { "net10.0", "net10.0-android", "net10.0-ios", "net10.0-maccatalyst" },
            mauiFrameworks);
        Assert.Equal("true", mauiProject.Descendants("UseMaui").Single().Value);
        Assert.Contains(
            mauiProject.Descendants("ProjectReference"),
            reference => ((string?)reference.Attribute("Include"))?
                .Replace('\\', '/') == "../AdamE.AppNav/AdamE.AppNav.csproj");

        string whyAppNav = File.ReadAllText(
            Path.Combine(root, "docs", "concepts", "00-why-appnav.md"));
        string architectureGuide = File.ReadAllText(
            Path.Combine(root, "docs", "guides", "03-application-architecture-and-testing.md"));

        Assert.Contains("`AdamE.AppNav` targets plain `net10.0`", whyAppNav, StringComparison.Ordinal);
        Assert.Contains(
            "`AdamE.AppNav.Maui` is the production adapter supplied by this preview",
            architectureGuide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentationDoesNotDescribeViewModelsAsNavigationUnits()
    {
        var root = RepositoryRoot();
        string[] forbiddenPhrases =
        [
            "view-model navigation",
            "view model navigation",
            "viewmodel navigation",
            "navigation-aware view model",
            "view model navigates",
            "view-model-keyed navigation"
        ];

        foreach (string document in DocumentationFiles(root))
        {
            string text = File.ReadAllText(document);
            foreach (string phrase in forbiddenPhrases)
            {
                Assert.DoesNotContain(
                    phrase,
                    text,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DiagnosticsReferenceMatchesRuntimeIdentifiers()
    {
        var root = RepositoryRoot();
        string reference = File.ReadAllText(
            Path.Combine(root, "docs", "reference", "diagnostics.md"));
        string serviceRegistration = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "AdamE.AppNav.Maui",
                "DependencyInjection",
                "AppNavServiceCollectionExtensions.cs"));
        string navigator = File.ReadAllText(
            Path.Combine(root, "src", "AdamE.AppNav", "Navigation", "RouterNavigator.cs"));

        Assert.Equal(
            NavigationDiagnosticDataMode.Safe,
            new NavigationDiagnosticsOptions().DataMode);
        Assert.Contains("Safe data mode is the default", reference, StringComparison.Ordinal);

        const string loggerCategory = "AdamE.AppNav.Diagnostics";
        Assert.Contains($"CreateLogger(\"{loggerCategory}\")", serviceRegistration, StringComparison.Ordinal);
        Assert.Contains($"`{loggerCategory}`", reference, StringComparison.Ordinal);

        Assert.Equal("AdamE.AppNav", NavigationActivitySources.DefaultName);
        Assert.Contains("`AdamE.AppNav`", reference, StringComparison.Ordinal);

        foreach (string activityName in new[]
                 {
                     "Navigation.Navigate",
                     "Navigation.Back",
                     "Navigation.Reconcile"
                 })
        {
            Assert.Contains($"StartActivity(\"{activityName}\"", navigator, StringComparison.Ordinal);
            Assert.Contains($"`{activityName}`", reference, StringComparison.Ordinal);
        }

        const string logTemplate =
            "Navigation {Kind} ({Phase}) operation {OperationId}: {Message} {@Data}";
        string diagnostics = File.ReadAllText(
            Path.Combine(root, "src", "AdamE.AppNav", "Diagnostics", "NavigationDiagnostics.cs"));
        Assert.Contains(logTemplate, diagnostics, StringComparison.Ordinal);
        Assert.Contains(logTemplate, reference, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationOutcomeGuideMatchesPublicResultContracts()
    {
        var root = RepositoryRoot();
        string guide = File.ReadAllText(
            Path.Combine(
                root,
                "docs",
                "guides",
                "04-navigation-outcomes-and-failure-handling.md"));

        Type result = typeof(NavigationResult);
        Assert.Equal(typeof(AppRoute), result.GetProperty(nameof(NavigationResult.Route))!.PropertyType);
        Assert.Equal(
            typeof(AdamE.AppNav.Plans.NavigationPlan),
            result.GetProperty(nameof(NavigationResult.Plan))!.PropertyType);
        Assert.Equal(
            typeof(AdamE.AppNav.State.NavigationState),
            result.GetProperty(nameof(NavigationResult.State))!.PropertyType);
        Assert.Equal(typeof(bool), result.GetProperty(nameof(NavigationResult.Presented))!.PropertyType);

        Assert.True(typeof(BackNavigationResult).IsValueType);
        Assert.False(BackNavigationResult.Unhandled.Handled);
        Assert.Null(BackNavigationResult.Unhandled.HandledNavigationResult);

        Assert.Contains(
            "`NavigationResult` is not a success/failure union",
            guide,
            StringComparison.Ordinal);
        Assert.Contains("`Presented == false`", guide, StringComparison.Ordinal);
        Assert.Contains("`Handled == false` is not an error", guide, StringComparison.Ordinal);
        Assert.Contains(
            "commit state and history -> return result",
            guide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GlossaryDefinesCorePublicVocabularyAndDistinctions()
    {
        var root = RepositoryRoot();
        string glossary = File.ReadAllText(
            Path.Combine(root, "docs", "reference", "glossary.md"));

        foreach (string heading in new[]
                 {
                     "### `AppRoute`",
                     "### `AppRouteRequest`",
                     "### Navigation plan",
                     "### Navigation result",
                     "### Navigation state",
                     "### Presenter",
                     "### Provenance",
                     "### Reconciliation",
                     "### Route entry",
                     "### `RouterNavigationRequest`",
                     "### Semantic destination",
                     "### Topology"
                 })
        {
            Assert.Contains(heading, glossary, StringComparison.Ordinal);
        }

        foreach (string lifetime in Enum.GetNames<RouteStateLifetime>())
            Assert.Contains($"### {lifetime} metadata", glossary, StringComparison.Ordinal);

        foreach (string disposition in Enum.GetNames<AdamE.AppNav.Requests.RouterNavigationDisposition>())
            Assert.Contains($"`{disposition}`", glossary, StringComparison.Ordinal);

        Assert.Contains("Route and page", glossary, StringComparison.Ordinal);
        Assert.Contains("Route and view model", glossary, StringComparison.Ordinal);
        Assert.Contains("Canonical URI and canonical navigation", glossary, StringComparison.Ordinal);
        Assert.Contains("Diagnostics and outcomes", glossary, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceGeneratorDiagnosticReferenceMatchesDeclaredDiagnostics()
    {
        var root = RepositoryRoot();
        string declared = string.Join(
            Environment.NewLine,
            File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav.Generators", "AppNavDiagnostics.cs")),
            File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav.Maui.Generators", "MauiPageDiagnostics.cs")));
        string reference = File.ReadAllText(
            Path.Combine(root, "docs", "reference", "source-generator-diagnostics.md"));

        string[] declaredIds = DiagnosticIdPattern().Matches(declared).Cast<Match>().Select(static match => match.Value).Distinct().Order().ToArray();
        string[] documentedIds = DiagnosticIdPattern().Matches(reference).Cast<Match>().Select(static match => match.Value).Distinct().Order().ToArray();

        Assert.Equal(declaredIds, documentedIds);
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
    }

    [Fact]
    public void RepositoryContainsMinimalAndAdvancedMauiSamples()
    {
        var root = RepositoryRoot();
        var sampleDirectories = Directory
            .EnumerateDirectories(Path.Combine(root, "samples"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var solution = File.ReadAllText(Path.Combine(root, "AdamE.AppNav.slnx"));

        Assert.Equal(new[] { "Commerce.Sample", "GettingStarted.Sample" }, sampleDirectories);
        Assert.Contains("samples/Commerce.Sample/Commerce.Sample.csproj", solution, StringComparison.Ordinal);
        Assert.Contains("samples/GettingStarted.Sample/GettingStarted.Sample.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentationDoesNotAdvertiseTransitionSupport()
    {
        var root = RepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("Native user actions reconcile", readme, StringComparison.Ordinal);
        Assert.Contains("transition or shared-element system", readme, StringComparison.Ordinal);
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

        Assert.Contains("FallbackRouteFactory", readme, StringComparison.Ordinal);
        Assert.Contains("FallbackRequestFactory", readme, StringComparison.Ordinal);
        Assert.Contains("There are no URI/source convenience overloads", readme, StringComparison.Ordinal);
        Assert.Contains("RouterNavigatorExtensions", readme, StringComparison.Ordinal);
        Assert.Contains("ReconcileAsync", File.ReadAllText(Path.Combine(root, "docs", "advanced", "adapter-contract.md")), StringComparison.Ordinal);
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
        var docsHome = File.ReadAllText(Path.Combine(root, "docs", "index.md"));
        var checklist = File.ReadAllText(Path.Combine(root, "docs", "maintainers", "release-checklist.md"));
        var runner = File.ReadAllText(Path.Combine(root, "eng", "run-maui-platform-tests.sh"));
        var packageVerifier = File.ReadAllText(Path.Combine(root, "eng", "verify-package-assets.sh"));
        var releaseVerifier = File.ReadAllText(Path.Combine(root, "eng", "verify.sh"));
        var consumerVerifier = File.ReadAllText(Path.Combine(root, "eng", "verify-package-consumer.sh"));
        var releaseNotes = File.ReadAllText(Path.Combine(root, "docs", "release-notes", "0.1.0-preview.1.md"));
        var mauiTestsProject = File.ReadAllText(Path.Combine(root, "tests", "AdamE.AppNav.Maui.Tests", "AdamE.AppNav.Maui.Tests.csproj"));

        Assert.Contains("maintainers/release-checklist.md", docsHome, StringComparison.Ordinal);
        Assert.DoesNotContain("maintainers/", readme, StringComparison.Ordinal);
        Assert.Contains("eng/run-maui-platform-tests.sh android", checklist, StringComparison.Ordinal);
        Assert.Contains("eng/verify.sh release", checklist, StringComparison.Ordinal);
        Assert.DoesNotContain("AdamE.AppNav.Testing", checklist, StringComparison.Ordinal);
        Assert.Contains("dotnet pack", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("samples/GettingStarted.Sample/GettingStarted.Sample.csproj", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("verify-package-assets.sh", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("verify-package-consumer.sh", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("NUGET_PACKAGES", consumerVerifier, StringComparison.Ordinal);
        Assert.Contains("--no-restore", consumerVerifier, StringComparison.Ordinal);
        Assert.Contains("dotnet test", runner, StringComparison.Ordinal);
        Assert.Contains("--logger \"trx;LogFileName=test-results.trx\"", runner, StringComparison.Ordinal);
        Assert.Contains("-p:TreatWarningsAsErrors=true", runner, StringComparison.Ordinal);
        Assert.Contains("DeviceRunnersDevice", runner, StringComparison.Ordinal);
        Assert.Contains("require_test_results", runner, StringComparison.Ordinal);
        Assert.Contains("fail_on_runtime_markers", runner, StringComparison.Ordinal);
        Assert.Contains("grep -Eq", packageVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("rg --quiet", packageVerifier, StringComparison.Ordinal);
        Assert.Contains("unzip -tq", packageVerifier, StringComparison.Ordinal);
        Assert.Contains("BSJB", packageVerifier, StringComparison.Ordinal);
        Assert.Contains("RouterNavigator.cs", packageVerifier, StringComparison.Ordinal);
        Assert.Contains("MauiNavigationPresenter.cs", packageVerifier, StringComparison.Ordinal);
        Assert.Contains("net10.0-android", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-ios", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("net10.0-maccatalyst", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("DeviceRunners.Testing.Targets", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("DeviceRunners.VisualRunners.Maui", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("DeviceRunners.UITesting.Xunit3", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("DeviceRunners.VisualRunners.Xunit3", mauiTestsProject, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceRunners.UITesting.Xunit\"", mauiTestsProject, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceRunners.VisualRunners.Xunit\"", mauiTestsProject, StringComparison.Ordinal);
        Assert.DoesNotContain("xunit.runner.utility", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains(".AddXunit3()", File.ReadAllText(Path.Combine(root, "tests", "AdamE.AppNav.Maui.Tests", "MauiProgram.cs")), StringComparison.Ordinal);
        Assert.Contains("0.1.0-preview.12", mauiTestsProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.DotNet.XHarness", mauiTestsProject, StringComparison.Ordinal);
        Assert.Contains("schema 3", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("Breaking preview changes", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("`NativeBackGesture`, `TabChanged`, and `NativeReconciliation`", releaseNotes, StringComparison.Ordinal);
        Assert.DoesNotContain("HostPopped", releaseNotes, StringComparison.Ordinal);
        Assert.DoesNotContain("BranchSelectionChanged", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("does not rebuild packages or publish to NuGet.org", releaseNotes, StringComparison.Ordinal);
    }

    [Fact]
    public void TestProjectsUseXunitV3()
    {
        var testProjects = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "tests"), "*.Tests.csproj", SearchOption.AllDirectories)
            .ToArray();

        Assert.Equal(5, testProjects.Length);

        foreach (var testProject in testProjects)
        {
            var source = File.ReadAllText(testProject);
            Assert.Contains("<PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<PackageReference Include=\"xunit\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("xunit.runner.utility\"", source, StringComparison.Ordinal);

            if (!testProject.EndsWith("AdamE.AppNav.Maui.Tests.csproj", StringComparison.Ordinal))
            {
                Assert.Contains("<OutputType>Exe</OutputType>", source, StringComparison.Ordinal);
                Assert.Contains("<PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ReleaseConfidenceWorkflowDefinesRequiredGates()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-confidence.yml"));
        var verifier = File.ReadAllText(Path.Combine(root, "eng", "verify.sh"));

        Assert.Contains("unit-api-allocation:", workflow, StringComparison.Ordinal);
        Assert.Contains("analyzer-pack:", workflow, StringComparison.Ordinal);
        Assert.Contains("maccatalyst-nativeaot:", workflow, StringComparison.Ordinal);
        Assert.Contains("android-full-trim:", workflow, StringComparison.Ordinal);
        Assert.Contains("ios-linked:", workflow, StringComparison.Ordinal);
        Assert.Contains("maccatalyst-platform-tests:", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/verify.sh contracts", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/verify.sh packages", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/verify.sh native-maccatalyst", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/verify.sh native-android", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/verify.sh native-ios", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/run-maui-platform-tests.sh maccatalyst", workflow, StringComparison.Ordinal);
        Assert.Contains("run-ios-platform-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("run-android-platform-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("run-android-api28-platform-tests", workflow, StringComparison.Ordinal);
        Assert.Contains("reactivecircus/android-emulator-runner", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: macos-26", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("runs-on: macos-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("global-json-file: global.json", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "DEVELOPER_DIR: /Applications/Xcode_26.6.app/Contents/Developer",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("actions/checkout@v7", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/setup-dotnet@v5", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v7", workflow, StringComparison.Ordinal);
        Assert.Contains("-warnnotaserror:IL2104,IL3053", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicPreviewReleasePublishesOnlyPreviouslyValidatedArtifacts()
    {
        var root = RepositoryRoot();
        var confidenceWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release-confidence.yml"));
        var releaseWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "public-preview-release.yml"));

        Assert.Contains("release_tag:", confidenceWorkflow, StringComparison.Ordinal);
        Assert.Contains("packages-{0}", confidenceWorkflow, StringComparison.Ordinal);
        Assert.Contains("v0.1.0-preview.*", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("Require the exact current main commit", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("packages-$GITHUB_REF_NAME", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("select(.head_sha == \\\"$GITHUB_SHA\\\"", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains(".conclusion == \\\"success\\\"", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("gh release create", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("--prerelease", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet ", releaseWorkflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nuget push", releaseWorkflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet pack", releaseWorkflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageMetadataIsConfiguredForReleaseValidation()
    {
        var root = RepositoryRoot();
        var buildProps = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var coreProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav", "AdamE.AppNav.csproj"));
        var mauiProject = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav.Maui", "AdamE.AppNav.Maui.csproj"));
        var coreApi = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav", "PublicAPI.Shipped.txt"));
        var mauiApi = File.ReadAllText(Path.Combine(root, "src", "AdamE.AppNav.Maui", "PublicAPI.Shipped.txt"));

        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<RepositoryUrl>https://github.com/AdamEssenmacher/AdamE.AppNav.git</RepositoryUrl>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", buildProps, StringComparison.Ordinal);
        Assert.Contains("<IncludeSymbols>true</IncludeSymbols>", buildProps, StringComparison.Ordinal);
        Assert.Contains("Microsoft.SourceLink.GitHub", buildProps, StringComparison.Ordinal);
        Assert.Contains("<AppNavDefaultVersion>0.1.0-preview.local</AppNavDefaultVersion>", buildProps, StringComparison.Ordinal);
        Assert.Contains("Stable AppNav packages require AppNavStableRelease=true", buildProps, StringComparison.Ordinal);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", coreProject, StringComparison.Ordinal);
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", mauiProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.CodeAnalysis.PublicApiAnalyzers\" Version=\"5.6.0", coreProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.CodeAnalysis.PublicApiAnalyzers\" Version=\"5.6.0", mauiProject, StringComparison.Ordinal);
        Assert.Contains("AdamE.AppNav.Navigation.IRouterNavigator", coreApi, StringComparison.Ordinal);
        Assert.Contains("AdamE.AppNav.Maui.AppLinks.MauiExternalNavigationOptions", mauiApi, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalJsonPinsTheValidatedSdkAndWorkloadSet()
    {
        var globalJson = File.ReadAllText(Path.Combine(RepositoryRoot(), "global.json"));

        Assert.Contains("\"version\": \"10.0.400\"", globalJson, StringComparison.Ordinal);
        Assert.Contains("\"rollForward\": \"latestFeature\"", globalJson, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"10.0.302.1\"", globalJson, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(RepositoryRoot(), "eng", "sdk", "net9", "global.json")));
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

    private static IEnumerable<string> DocumentationFiles(string root)
    {
        yield return Path.Combine(root, "README.md");

        foreach (string document in Directory.EnumerateFiles(
                     Path.Combine(root, "docs"),
                     "*.md",
                     SearchOption.AllDirectories))
            yield return document;

        foreach (string sampleReadme in Directory.EnumerateFiles(
                     Path.Combine(root, "samples"),
                     "README.md",
                     SearchOption.AllDirectories))
            yield return sampleReadme;
    }

    private static bool IsExternalLink(string target)
    {
        return target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    private static string HeadingAnchor(string heading)
    {
        string withoutMarkup = heading.Replace("`", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        string withoutPunctuation = Regex.Replace(withoutMarkup, @"[^\p{L}\p{N}\s-]", string.Empty);
        return Regex.Replace(withoutPunctuation.Trim(), @"\s+", "-");
    }

    [GeneratedRegex(@"\[[^\]]+\]\((?<target>[^)]+)\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"^#{1,6}\s+(?<heading>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"APPNAV\d{3}")]
    private static partial Regex DiagnosticIdPattern();

    private static string ReadRegion(string source, string regionName)
    {
        string startMarker = $"// #region {regionName}";
        string endMarker = $"// #endregion {regionName}";
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Region '{regionName}' start marker was not found.");
        Assert.True(end > start, $"Region '{regionName}' end marker was not found.");

        string body = source[(start + startMarker.Length)..end].Trim('\r', '\n');
        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int indentation = lines
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.TakeWhile(char.IsWhiteSpace).Count())
            .DefaultIfEmpty(0)
            .Min();

        return string.Join(
            Environment.NewLine,
            lines.Select(line => line.Length >= indentation ? line[indentation..] : line));
    }
}
