using System.Reflection;
using System.Collections.Immutable;
using AdamE.AppNav;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Routing;
using AdamE.AppNav.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Generators.Tests;

public sealed class AppNavSourceGeneratorTests
{
    [Fact]
    public void GeneratedRouteTableMatchesAndFormatsAttributedConventionRoute()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public static class Metadata
            {
                public static RouteMetadataKey<string> Campaign { get; } = new("campaign");
            }

            [AppNavRoute("/stores/{storeId}/products/{productId:int}")]
            [AppNavQuery(nameof(Variant))]
            [AppNavQuery(nameof(Promo))]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Campaign))]
            public sealed record ProductDetailRoute(
                string StoreId,
                int ProductId,
                string? Variant = null,
                string? Promo = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));

        var match = table.Match(new Uri("/stores/northwind/products/123?variant=blue&promo=spring&campaign=spring-launch", UriKind.Relative));
        Assert.True(match.IsSuccess);
        Assert.Equal("ProductDetailRoute", match.Route!.GetType().Name);
        Assert.Equal("northwind", match.Route.GetType().GetProperty("StoreId")!.GetValue(match.Route));
        Assert.Equal(123, match.Route.GetType().GetProperty("ProductId")!.GetValue(match.Route));
        Assert.Equal("blue", match.Route.GetType().GetProperty("Variant")!.GetValue(match.Route));
        Assert.Equal("spring-launch", match.Metadata["campaign"]);

        Assert.Equal(
            "/stores/northwind/products/123?variant=blue&promo=spring&campaign=spring-launch",
            table.Format(
                match.Route,
                new Dictionary<string, object?> { ["campaign"] = "spring-launch" }));
    }

    [Fact]
    public void GeneratedMauiPageModuleUsesExplicitRouteAndServiceConstructorArguments()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Navigation;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route, IRouterNavigator navigator)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("public static global::AdamE.AppNav.Maui.IMauiRoutePageModule MauiPageModule", result.GeneratedSource);
        Assert.Contains("ServiceProviderServiceExtensions.GetRequiredService<global::AdamE.AppNav.Navigation.IRouterNavigator>(services)", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void MissingPathPropertyReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string Id) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV002");
    }

    [Fact]
    public void RequiredQueryConstructorParameterReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQuery(nameof(Page))]
            public sealed record StoreRoute(string StoreId, int Page) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV008");
    }

    [Fact]
    public void DuplicateTemplatesReportDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [AppNavRoute("/stores/{id}")]
            public sealed record DuplicateStoreRoute(string Id) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV010");
    }

    [Fact]
    public void PageMappedToNonGeneratedRouteReportsDiagnostic()
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
                public StorePage(StoreRoute route)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV020");
    }

    private static GeneratorResult RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Commerce.Sample",
            [syntaxTree],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        var generator = new AppNavSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out ImmutableArray<Diagnostic> generatorDiagnostics);
        GeneratorDriverRunResult runResult = driver.GetRunResult();
        string generatedSource = runResult.GeneratedTrees.SingleOrDefault()?.GetText().ToString() ?? string.Empty;

        ImmutableArray<Diagnostic> diagnostics = runResult.Diagnostics
            .AddRange(runResult.Results.SelectMany(static result => result.Diagnostics))
            .AddRange(generatorDiagnostics)
            .AddRange(outputCompilation.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Id.StartsWith("APPNAV", StringComparison.Ordinal)));

        return new GeneratorResult(
            outputCompilation,
            diagnostics,
            generatedSource);
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

    private static Assembly Emit(Compilation compilation)
    {
        AssertCompileClean(compilation);

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;
        return Assembly.Load(stream.ToArray());
    }

    private static void AssertCompileClean(Compilation compilation)
    {
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    private sealed record GeneratorResult(
        Compilation Compilation,
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        string GeneratedSource);
}

internal static class AssemblyExtensions
{
    public static Type GetRequiredType(this Assembly assembly, string name)
    {
        return assembly.GetType(name, throwOnError: true)!;
    }
}
