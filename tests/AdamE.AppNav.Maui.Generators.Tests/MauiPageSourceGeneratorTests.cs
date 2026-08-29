using System.Collections.Immutable;
using AdamE.AppNav;
using AdamE.AppNav.Generators;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Maui.Generators;
using AdamE.AppNav.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Generators.Tests;

public sealed class MauiPageSourceGeneratorTests
{
    [Fact]
    public void RouteAndMauiGeneratorsEmitIndependentComposingSources()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route) { }
            }
            """;

        GeneratorResult result = RunGenerators(source, includeRouteGenerator: true);

        Assert.Empty(Errors(result));
        Assert.Equal(["AppNavRoutes.g.cs", "AppNavMauiPages.g.cs"], result.HintNames);
        Assert.Contains("RouteTableModule", result.GeneratedSources["AppNavRoutes.g.cs"]);
        Assert.DoesNotContain("MauiPageModule", result.GeneratedSources["AppNavRoutes.g.cs"]);
        Assert.Contains("MauiPageModule", result.GeneratedSources["AppNavMauiPages.g.cs"]);
        Assert.DoesNotContain("CreateRouteTable", result.GeneratedSources["AppNavMauiPages.g.cs"]);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedModuleUsesRouteServicesAndPreservesDefaults()
    {
        const string source = """
            using System.Threading;
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Navigation;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            public enum BigValue : ulong { Max = ulong.MaxValue }

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(
                    StoreRoute route,
                    IRouterNavigator navigator,
                    string title = "Details",
                    CancellationToken cancellationToken = default,
                    BigValue value = BigValue.Max,
                    double scale = double.NaN) { }
            }
            """;

        GeneratorResult result = RunGenerators(source);
        string generated = Assert.Single(result.GeneratedSources).Value;

        Assert.Empty(Errors(result));
        Assert.Contains("GetRequiredService<global::AdamE.AppNav.Navigation.IRouterNavigator>(services)", generated);
        Assert.Contains("\"Details\", default(global::System.Threading.CancellationToken)", generated);
        Assert.Contains("(global::Commerce.Sample.BigValue)18446744073709551615UL", generated);
        Assert.Contains("global::System.Double.NaN", generated);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedModuleBindsOnlyBestRouteConstructorParameter()
    {
        const string source = """
            #nullable enable
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route, object? bindingContext = null) { }
            }
            """;

        GeneratorResult result = RunGenerators(source);
        string generated = Assert.Single(result.GeneratedSources).Value;

        Assert.Empty(Errors(result));
        Assert.Contains("new global::Commerce.Sample.StorePage(route, null)", generated);
        Assert.DoesNotContain("StorePage(route, route)", generated);
    }

    [Fact]
    public void InvalidPageRouteReportsAppNav020()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route) { }
            }
            """;

        AssertDiagnostic(source, "APPNAV020");
    }

    [Fact]
    public void InvalidPageTypeReportsAppNav021()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage
            {
                public StorePage(StoreRoute route) { }
            }
            """;

        AssertDiagnostic(source, "APPNAV021");
    }

    [Fact]
    public void AmbiguousPageConstructorReportsAppNav022()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route) { }
                public StorePage(StoreRoute route, string title) { }
            }
            """;

        AssertDiagnostic(source, "APPNAV022");
    }

    [Fact]
    public void MissingRouteConstructorParameterReportsAppNav023()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(string title) { }
            }
            """;

        AssertDiagnostic(source, "APPNAV023");
    }

    [Fact]
    public void DuplicatePageMappingReportsAppNav024()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route) { }
            }

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class OtherStorePage : ContentPage
            {
                public OtherStorePage(StoreRoute route) { }
            }
            """;

        AssertDiagnostic(source, "APPNAV024");
    }

    [Fact]
    public void InvalidPageModelTypeReportsAppNav025()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            public sealed class StorePageModel<T> { }

            [MauiRoutePage(typeof(StoreRoute), PageModelType = typeof(StorePageModel<>))]
            public sealed class StorePage : ContentPage { }
            """;

        AssertDiagnostic(source, "APPNAV025");
    }

    [Fact]
    public void ReferencedAttributedRouteCanBeMapped()
    {
        MetadataReference routeReference = EmitReference(
            "Commerce.Contracts",
            """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Contracts;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """);

        const string source = """
            using AdamE.AppNav.Maui;
            using Commerce.Contracts;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route) { }
            }
            """;

        GeneratorResult result = RunGenerators(source, [routeReference]);

        Assert.Empty(Errors(result));
        Assert.Contains("pages.MapPage<global::Commerce.Contracts.StoreRoute>", Assert.Single(result.GeneratedSources).Value);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void PageModelUsesServiceResolvedPageAndSupportsXamlPartialBase()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute), PageModelType = typeof(StorePageModel))]
            public partial class StorePage { }

            public sealed class StorePageModel { }
            """;

        GeneratorResult result = RunGenerators(source);
        string generated = Assert.Single(result.GeneratedSources).Value;

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "APPNAV021");
        Assert.Contains("GetRequiredService<global::Commerce.Sample.StorePage>(services)", generated);
        Assert.Contains("GetRequiredService<global::Commerce.Sample.StorePageModel>(services)", generated);
        AssertCompileCleanWithAdditionalSource(
            result.Compilation,
            """
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            public partial class StorePage : ContentPage { }
            """);
    }

    private static void AssertDiagnostic(string source, string id)
    {
        GeneratorResult result = RunGenerators(source);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == id);
    }

    private static IEnumerable<Diagnostic> Errors(GeneratorResult result)
    {
        return result.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static GeneratorResult RunGenerators(
        string source,
        IReadOnlyList<MetadataReference>? additionalReferences = null,
        bool includeRouteGenerator = false)
    {
        CSharpCompilation compilation = CreateCompilation("Commerce.Sample", source, additionalReferences);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var generatedSources = new Dictionary<string, string>(StringComparer.Ordinal);

        if (includeRouteGenerator)
            compilation = RunGenerator(new AppNavSourceGenerator(), compilation, diagnostics, generatedSources);

        compilation = RunGenerator(new MauiPageSourceGenerator(), compilation, diagnostics, generatedSources);
        diagnostics.AddRange(compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Id.StartsWith("APPNAV", StringComparison.Ordinal)));

        return new GeneratorResult(
            compilation,
            diagnostics.ToImmutable(),
            generatedSources,
            generatedSources.Keys.ToArray());
    }

    private static CSharpCompilation RunGenerator(
        IIncrementalGenerator generator,
        CSharpCompilation compilation,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        IDictionary<string, string> generatedSources)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out ImmutableArray<Diagnostic> generatorDiagnostics);
        GeneratorDriverRunResult runResult = driver.GetRunResult();
        diagnostics.AddRange(runResult.Diagnostics);
        diagnostics.AddRange(runResult.Results.SelectMany(static result => result.Diagnostics));
        diagnostics.AddRange(generatorDiagnostics);

        foreach (GeneratedSourceResult generatedSource in runResult.Results.SelectMany(static result => result.GeneratedSources))
            generatedSources.Add(generatedSource.HintName, generatedSource.SourceText.ToString());

        return (CSharpCompilation)outputCompilation;
    }

    private static MetadataReference EmitReference(string assemblyName, string source)
    {
        CSharpCompilation compilation = CreateCompilation(assemblyName, source);
        using var stream = new MemoryStream();
        EmitResult result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        string source,
        IReadOnlyList<MetadataReference>? additionalReferences = null)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        MetadataReference[] references = additionalReferences is null
            ? References().ToArray()
            : References().Concat(additionalReferences).ToArray();

        return CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }

    private static IReadOnlyList<MetadataReference> References()
    {
        MetadataReference[] explicitReferences =
        [
            MetadataReference.CreateFromFile(typeof(AppRoute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(RouteTable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(MauiRoutePageAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Page).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ServiceProviderServiceExtensions).Assembly.Location)
        ];

        return explicitReferences
            .Concat(AppDomain.CurrentDomain.GetAssemblies()
                .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
                .Select(static assembly => MetadataReference.CreateFromFile(assembly.Location)))
            .DistinctBy(static reference => reference.Display)
            .ToArray();
    }

    private static void AssertCompileClean(Compilation compilation)
    {
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    private static void AssertCompileCleanWithAdditionalSource(Compilation compilation, string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        AssertCompileClean(compilation.AddSyntaxTrees(syntaxTree));
    }

    private sealed record GeneratorResult(
        CSharpCompilation Compilation,
        ImmutableArray<Diagnostic> Diagnostics,
        IReadOnlyDictionary<string, string> GeneratedSources,
        IReadOnlyList<string> HintNames);
}
