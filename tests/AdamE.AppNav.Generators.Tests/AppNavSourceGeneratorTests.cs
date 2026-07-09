using System.Reflection;
using System.Collections.Immutable;
using AdamE.AppNav;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Routing;
using AdamE.AppNav.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
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
    public void GeneratedRouteTableReturnsFailedMatchForInvalidEnumValues()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public enum OrderStatus
            {
                Open,
                Closed
            }

            [AppNavRoute("/orders/{status}")]
            public sealed record OrderRoute(OrderStatus Status) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/orders/not-a-status", UriKind.Relative));

        Assert.False(match.IsSuccess);
        Assert.Contains(match.Diagnostics, static diagnostic => diagnostic.Code == "route.value.invalid");
    }

    [Fact]
    public void GeneratedRouteTableReturnsFailedMatchForNumericOverflows()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/pages/{page}")]
            public sealed record PageRoute(int Page) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("ParseNumber<global::System.Int32>", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/pages/999999999999999999999", UriKind.Relative));

        Assert.False(match.IsSuccess);
        Assert.Contains(match.Diagnostics, static diagnostic => diagnostic.Code == "route.value.invalid");
    }

    [Fact]
    public void GeneratedRouteTableReturnsFailedMatchForConverterArgumentFailures()
    {
        const string source = """
            using System;
            using System.ComponentModel;
            using System.Globalization;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [TypeConverter(typeof(SlugConverter))]
            public readonly struct Slug
            {
                public Slug(string value)
                {
                    Value = value;
                }

                public string Value { get; }
            }

            public sealed class SlugConverter : TypeConverter
            {
                public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
                {
                    return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
                }

                public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
                {
                    string text = (string)value;
                    if (text == "bad")
                        throw new ArgumentException("Invalid slug.", nameof(value));

                    return new Slug(text);
                }
            }

            [AppNavRoute("/slugs/{slug}")]
            public sealed record SlugRoute(Slug Slug) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("ConvertRouteValue<global::Commerce.Sample.Slug>", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/slugs/bad", UriKind.Relative));

        Assert.False(match.IsSuccess);
        Assert.Contains(match.Diagnostics, static diagnostic => diagnostic.Code == "route.value.invalid");
    }

    [Fact]
    public void GeneratedRouteTableReadsRepeatedQueryValuesForCollectionArguments()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/search")]
            [AppNavQuery(nameof(Tags), Name = "tag")]
            public sealed record SearchRoute(IReadOnlyList<string>? Tags = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("match.QueryAll(\"tag\")", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/search?tag=blue&tag=green", UriKind.Relative));

        Assert.True(match.IsSuccess);
        object? tagsValue = match.Route!.GetType().GetProperty("Tags")!.GetValue(match.Route);
        var tags = Assert.IsAssignableFrom<IEnumerable<string>>(tagsValue);
        Assert.Equal(["blue", "green"], tags);
        Assert.Equal("/search?tag=blue&tag=green", table.Format(match.Route));
    }

    [Fact]
    public void AppNavRouteOnNonAppRouteReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores")]
            public sealed record StoreRoute() : AppRoute;

            [AppNavRoute("/invalid")]
            public sealed class NotARoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV012");
    }

    [Fact]
    public void InaccessibleAppNavRouteReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public static class Routes
            {
                [AppNavRoute("/hidden")]
                private sealed record HiddenRoute() : AppRoute;
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV012");
    }

    [Fact]
    public void EscapedLiteralRoutesParticipateInAmbiguityChecks()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/files/a%20b")]
            public sealed record EncodedFileRoute() : AppRoute;

            [AppNavRoute("/files/a b")]
            public sealed record LiteralFileRoute() : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV010");
    }

    [Fact]
    public void DuplicatePublicRoutePropertiesIgnoringCaseReportDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/items/{id}")]
            public sealed class ItemRoute : AppRoute
            {
                public ItemRoute(string id)
                {
                    Id = id;
                    this.id = id;
                }

                public string Id { get; }

                public string id { get; }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV013");
    }

    [Fact]
    public void OpenGenericAppNavRouteReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/details/{id}")]
            public sealed record DetailRoute<T>(string Id) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV012");
    }

    [Fact]
    public void OverriddenBaseRoutePropertiesDoNotReportDuplicateDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public abstract record EntityRoute : AppRoute
            {
                protected EntityRoute(string id)
                {
                    Id = id;
                }

                public virtual string Id { get; }
            }

            [AppNavRoute("/items/{id}")]
            public sealed record ItemRoute : EntityRoute
            {
                public ItemRoute(string id) : base(id)
                {
                }

                public override string Id => base.Id;
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.DoesNotContain(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV013");
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedRouteTableIgnoresAttributedRoutesFromReferencedAssemblies()
    {
        MetadataReference routeReference = EmitReference(
            "Commerce.Contracts",
            """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Contracts;

            [AppNavRoute("/external")]
            public sealed record ExternalRoute() : AppRoute;
            """);

        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/local")]
            public sealed record LocalRoute() : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source, [routeReference]);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain("ExternalRoute", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);
        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));
        RouteDefinition definition = Assert.Single(table.Definitions);
        Assert.Equal("LocalRoute", definition.RouteType.Name);
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
    public void GeneratedMauiPageModulePreservesDefaultStructConstructorDefaults()
    {
        const string source = """
            using System.Threading;
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
                public StorePage(StoreRoute route, CancellationToken cancellationToken = default)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(
            "new global::Commerce.Sample.StorePage(route, default(global::System.Threading.CancellationToken))",
            result.GeneratedSource);
        Assert.DoesNotContain("new global::Commerce.Sample.StorePage(route, null)", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedMauiPageModulePreservesUnsignedEnumConstructorDefaults()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            public enum BigValue : ulong
            {
                Max = ulong.MaxValue
            }

            [AppNavRoute("/stores/{storeId}")]
            public sealed record StoreRoute(string StoreId) : AppRoute;

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class StorePage : ContentPage
            {
                public StorePage(StoreRoute route, BigValue value = BigValue.Max)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("(global::Commerce.Sample.BigValue)18446744073709551615UL", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedMauiPageModulePreservesNonFiniteFloatingPointDefaults()
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
                public StorePage(StoreRoute route, double value = double.NaN, float scale = float.PositiveInfinity)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(
            "new global::Commerce.Sample.StorePage(route, global::System.Double.NaN, global::System.Single.PositiveInfinity)",
            result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedMauiPageModuleBindsOnlyBestRouteConstructorParameter()
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
                public StorePage(StoreRoute route, object? bindingContext = null)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("new global::Commerce.Sample.StorePage(route, null)", result.GeneratedSource);
        Assert.DoesNotContain("new global::Commerce.Sample.StorePage(route, route)", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void OpenGenericMauiPageReportsDiagnostic()
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
            public sealed class StorePage<T> : ContentPage
            {
                public StorePage(StoreRoute route)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV021");
    }

    [Fact]
    public void MauiPageMappingToOpenGenericRouteReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Commerce.Sample;

            [AppNavRoute("/details")]
            public sealed record DetailRoute<T>() : AppRoute;

            [MauiRoutePage(typeof(DetailRoute<>), FromServices = true)]
            public sealed class DetailPage : ContentPage
            {
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV020");
    }

    [Fact]
    public void DuplicateMauiPageMappingsReportDiagnostic()
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
                public StorePage(StoreRoute route)
                {
                }
            }

            [MauiRoutePage(typeof(StoreRoute))]
            public sealed class OtherStorePage : ContentPage
            {
                public OtherStorePage(StoreRoute route)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV024");
    }

    [Fact]
    public void GeneratedMauiPageModulePreservesOptionalConstructorDefaults()
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
                public StorePage(StoreRoute route, string title = "Details", int tab = 1)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("new global::Commerce.Sample.StorePage(route, \"Details\", 1)", result.GeneratedSource);
        Assert.DoesNotContain("GetRequiredService<global::System.String>", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedMauiPageModuleAcceptsAttributedRouteFromReferencedAssembly()
    {
        MetadataReference routeReference = EmitReference(
            "Scavos.UI",
            """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Scavos.UI;

            [AppNavRoute("/games/{gameId}/hub")]
            public sealed record GameHubRoute(string GameId) : AppRoute;
            """,
            runGenerator: true);

        const string source = """
            using AdamE.AppNav.Maui;
            using Microsoft.Maui.Controls;
            using Scavos.UI;

            namespace Scavos.Mobile.Presentation;

            [MauiRoutePage(typeof(GameHubRoute))]
            public sealed class PlayPage : ContentPage
            {
                public PlayPage(GameHubRoute route)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source, [routeReference]);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("public static global::AdamE.AppNav.Maui.IMauiRoutePageModule MauiPageModule", result.GeneratedSource);
        Assert.Contains("pages.MapPage<global::Scavos.UI.GameHubRoute>", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void ReferencedRouteWithoutAppNavRouteAttributeReportsDiagnostic()
    {
        MetadataReference routeReference = EmitReference(
            "Scavos.UI",
            """
            using AdamE.AppNav;

            namespace Scavos.UI;

            public sealed record GameHubRoute(string GameId) : AppRoute;
            """);

        const string source = """
            using AdamE.AppNav.Maui;
            using Microsoft.Maui.Controls;
            using Scavos.UI;

            namespace Scavos.Mobile.Presentation;

            [MauiRoutePage(typeof(GameHubRoute))]
            public sealed class PlayPage : ContentPage
            {
                public PlayPage(GameHubRoute route)
                {
                }
            }
            """;

        GeneratorResult result = RunGenerator(source, [routeReference]);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV020");
    }

    [Fact]
    public void PageModelTypeUsesServiceResolvedPageAndBindingContext()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Scavos.Mobile.Presentation;

            [AppNavRoute("/games/{gameId}/hub")]
            public sealed record GameHubRoute(string GameId) : AppRoute;

            [MauiRoutePage(typeof(GameHubRoute), PageModelType = typeof(PlayPageModel))]
            public sealed class PlayPage : ContentPage
            {
                public PlayPage()
                {
                }
            }

            public sealed class PlayPageModel
            {
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("ServiceProviderServiceExtensions.GetRequiredService<global::Scavos.Mobile.Presentation.PlayPage>(services)", result.GeneratedSource);
        Assert.Contains("page.BindingContext ??= global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Scavos.Mobile.Presentation.PlayPageModel>(services);", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void InvalidPageModelTypeReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;
            using Microsoft.Maui.Controls;

            namespace Scavos.Mobile.Presentation;

            [AppNavRoute("/games/{gameId}/hub")]
            public sealed record GameHubRoute(string GameId) : AppRoute;

            public sealed class PlayPageModel<T>
            {
            }

            [MauiRoutePage(typeof(GameHubRoute), PageModelType = typeof(PlayPageModel<>))]
            public sealed class PlayPage : ContentPage
            {
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV025");
    }

    [Fact]
    public void PageModelTypeAllowsPartialPageWithBaseSuppliedByXaml()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;

            namespace Scavos.Mobile.Presentation;

            [AppNavRoute("/games/{gameId}/hub")]
            public sealed record GameHubRoute(string GameId) : AppRoute;

            [MauiRoutePage(typeof(GameHubRoute), PageModelType = typeof(HubTabPageModel))]
            public partial class HubTab
            {
            }

            public sealed class HubTabPageModel
            {
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.DoesNotContain(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV021");
        Assert.Contains("page.BindingContext ??= global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Scavos.Mobile.Presentation.HubTabPageModel>(services);", result.GeneratedSource);
        AssertCompileCleanWithAdditionalSource(
            result.Compilation,
            """
            using Microsoft.Maui.Controls;

            namespace Scavos.Mobile.Presentation;

            public partial class HubTab : ContentPage
            {
            }
            """);
    }

    [Fact]
    public void GeneratedRouteTableDoesNotMaterializeMissingValueTypeMetadata()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public static class Metadata
            {
                public static RouteMetadataKey<int> Page { get; } = new("page");
            }

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Page))]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));

        var match = table.Match(new Uri("/stores/northwind", UriKind.Relative));

        Assert.True(match.IsSuccess);
        Assert.DoesNotContain("page", match.Metadata.Keys);
    }

    [Fact]
    public void GeneratedRouteTableCanMaterializeNullMetadataWhenRequested()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public static class Metadata
            {
                public static RouteMetadataKey<int> Page { get; } = new("page");
            }

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Page), OmitWhenNull = false)]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable")!.Invoke(null, null));

        var match = table.Match(new Uri("/stores/northwind", UriKind.Relative));

        Assert.True(match.IsSuccess);
        Assert.True(match.Metadata.ContainsKey("page"));
        Assert.Null(match.Metadata["page"]);
    }

    [Fact]
    public void DuplicateRouteAndMetadataQueryNamesReportDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public static class Metadata
            {
                public static RouteMetadataKey<string> Campaign { get; } = new("campaign");
            }

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQuery(nameof(Campaign), Name = "campaign")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Campaign))]
            public sealed record StoreRoute(string StoreId, string? Campaign = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV004");
    }

    [Fact]
    public void DuplicateMetadataQueryNamesReportDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public static class Metadata
            {
                public static RouteMetadataKey<string> Campaign { get; } = new("campaign");
                public static RouteMetadataKey<string> CampaignAlias { get; } = new("campaign");
            }

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Campaign))]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.CampaignAlias))]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV004");
    }

    [Fact]
    public void InvalidMetadataMemberReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public sealed class Metadata
            {
                public RouteMetadataKey<string> Campaign { get; } = new("campaign");
            }

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Campaign))]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV003");
    }

    [Fact]
    public void GeneratedRouteTableEscapesKeywordMemberNames()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/language/{namespace}")]
            [AppNavQuery(nameof(@event))]
            public sealed record KeywordRoute(string @namespace, string? @event = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("route.@namespace", result.GeneratedSource);
        Assert.Contains("route.@event", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void NonPartialPageWithoutPageBaseReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;

            namespace Scavos.Mobile.Presentation;

            [AppNavRoute("/games/{gameId}/hub")]
            public sealed record GameHubRoute(string GameId) : AppRoute;

            [MauiRoutePage(typeof(GameHubRoute), PageModelType = typeof(HubTabPageModel))]
            public sealed class HubTab
            {
            }

            public sealed class HubTabPageModel
            {
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV021");
    }

    [Fact]
    public void PartialPageWithExplicitNonPageBaseReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Maui;
            using AdamE.AppNav.Routing;

            namespace Scavos.Mobile.Presentation;

            [AppNavRoute("/games/{gameId}/hub")]
            public sealed record GameHubRoute(string GameId) : AppRoute;

            public class HubTabBase
            {
            }

            [MauiRoutePage(typeof(GameHubRoute), PageModelType = typeof(HubTabPageModel))]
            public partial class HubTab : HubTabBase
            {
            }

            public sealed class HubTabPageModel
            {
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV021");
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

    [Fact]
    public void FluentAnalyzerReportsUnsupportedPathValueType()
    {
        const string source = """
            using System;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public sealed record EventRoute(DateTime Start) : AppRoute;

            public static class Routes
            {
                public static RouteTable Create()
                {
                    return RouteTable.Create(routes => routes.MapRoute<EventRoute>("/events/{start}"));
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunFluentAnalyzer(source);

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "APPNAV011");
    }

    [Fact]
    public void FluentAnalyzerAllowsQueryConfiguredConstructorParameters()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public sealed record SearchRoute(string StoreId, string? Term) : AppRoute;

            public static class Routes
            {
                public static RouteTable Create()
                {
                    return RouteTable.Create(routes =>
                        routes.MapRoute<SearchRoute>("/stores/{storeId}", route => route.Query(value => value.Term)));
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunFluentAnalyzer(source);

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "APPNAV006");
    }

    [Fact]
    public void FluentAnalyzerAllowsCustomConstraints()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public sealed record SlugRoute(string Slug) : AppRoute;

            public static class Routes
            {
                public static RouteTable Create()
                {
                    return RouteTable.Create(routes => routes
                        .AddConstraint("slug", value => value.Length > 0)
                        .MapRoute<SlugRoute>("/{slug:slug}"));
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = RunFluentAnalyzer(source);

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "APPNAV001");
    }

    private static GeneratorResult RunGenerator(
        string source,
        IReadOnlyList<MetadataReference>? additionalReferences = null)
    {
        CSharpCompilation compilation = CreateCompilation("Commerce.Sample", source, additionalReferences);

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

    private static ImmutableArray<Diagnostic> RunFluentAnalyzer(string source)
    {
        CSharpCompilation compilation = CreateCompilation("Commerce.Sample", source);
        var analyzer = new AppNavFluentRegistrationAnalyzer();
        return compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    private static MetadataReference EmitReference(
        string assemblyName,
        string source,
        bool runGenerator = false)
    {
        CSharpCompilation compilation = CreateCompilation(assemblyName, source);
        if (runGenerator)
        {
            var generator = new AppNavSourceGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
            driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out Compilation outputCompilation,
                out ImmutableArray<Diagnostic> generatorDiagnostics);

            Assert.Empty(generatorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            compilation = (CSharpCompilation)outputCompilation;
        }

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        string source,
        IReadOnlyList<MetadataReference>? additionalReferences = null)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
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

    private static void AssertCompileCleanWithAdditionalSource(
        Compilation compilation,
        string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

        AssertCompileClean(compilation.AddSyntaxTrees(syntaxTree));
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
