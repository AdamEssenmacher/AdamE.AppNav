using System.Reflection;
using System.Collections.Immutable;
using AdamE.AppNav;
using AdamE.AppNav.Routing;
using AdamE.AppNav.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AdamE.AppNav.Generators.Tests;

public static class ReferencedMetadataKeys
{
    public static RouteMetadataKey<string> Campaign { get; } = new("campaign");

    public static RouteMetadataKey<string> CampaignAlias { get; } = new("CAMPAIGN");
}

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
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));

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
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));

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
        Assert.Contains("match.ConvertValue<", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/pages/999999999999999999999", UriKind.Relative));

        Assert.False(match.IsSuccess);
        Assert.Contains(match.Diagnostics, static diagnostic => diagnostic.Code == "route.value.invalid");
    }

    [Fact]
    public void GeneratedRouteTableRequiresAndUsesExplicitCustomValueCodec()
    {
        const string source = """
            using System;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public readonly record struct Slug(string Value);

            [AppNavRoute("/slugs/{slug}")]
            public sealed record SlugRoute(Slug Slug) : AppRoute;

            public static partial class AppNavGenerated
            {
                public static RouteTable CreateConfiguredRouteTable()
                {
                    return CreateRouteTable(routes => routes.AddValueCodec<Slug>(
                        value => value == "bad"
                            ? throw new FormatException("Invalid slug.")
                            : new Slug(value),
                        value => value.Value));
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("routes.RequireValueCodec<global::Commerce.Sample.Slug>()", result.GeneratedSource);
        Assert.Contains("match.ConvertValue<global::Commerce.Sample.Slug>", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var missingCodec = Assert.Throws<TargetInvocationException>(
            () => generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));
        Assert.IsType<InvalidOperationException>(missingCodec.InnerException);

        var table = Assert.IsType<RouteTable>(
            generatedType.GetMethod("CreateConfiguredRouteTable")!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/slugs/bad", UriKind.Relative));

        Assert.False(match.IsSuccess);
        Assert.Contains(match.Diagnostics, static diagnostic => diagnostic.Code == "route.value.invalid");

        RouteMatchResult success = table.Match(new Uri("/slugs/good", UriKind.Relative));
        Assert.True(success.IsSuccess);
        Assert.Equal("/slugs/good", table.Format(success.Route!));
    }

    [Fact]
    public void OptionalPathWithNonNullableConstructorParameterReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/products/{productId:int?}")]
            public sealed record ProductRoute(int ProductId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV014");
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
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/search?tag=blue&tag=green", UriKind.Relative));

        Assert.True(match.IsSuccess);
        object? tagsValue = match.Route!.GetType().GetProperty("Tags")!.GetValue(match.Route);
        var tags = Assert.IsAssignableFrom<IEnumerable<string>>(tagsValue);
        Assert.Equal(["blue", "green"], tags);
        Assert.Equal("/search?tag=blue&tag=green", table.Format(match.Route));
    }

    [Fact]
    public void GeneratedRouteTableSupportsAllRepeatedQueryCollectionShapes()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/collections")]
            [AppNavQuery(nameof(ArrayValues), Name = "array")]
            [AppNavQuery(nameof(EnumerableValues), Name = "enumerable")]
            [AppNavQuery(nameof(ReadOnlyCollectionValues), Name = "readOnlyCollection")]
            [AppNavQuery(nameof(ReadOnlyListValues), Name = "readOnlyList")]
            [AppNavQuery(nameof(CollectionValues), Name = "collection")]
            [AppNavQuery(nameof(ListInterfaceValues), Name = "listInterface")]
            [AppNavQuery(nameof(ListValues), Name = "list")]
            public sealed record CollectionRoute(
                int[]? ArrayValues = null,
                IEnumerable<int>? EnumerableValues = null,
                IReadOnlyCollection<int>? ReadOnlyCollectionValues = null,
                IReadOnlyList<int>? ReadOnlyListValues = null,
                ICollection<int>? CollectionValues = null,
                IList<int>? ListInterfaceValues = null,
                List<int>? ListValues = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assembly assembly = Emit(result.Compilation);
        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));
        var uri = new Uri(
            "/collections?array=1&array=2&enumerable=3&enumerable=4" +
            "&readOnlyCollection=5&readOnlyCollection=6&readOnlyList=7&readOnlyList=8" +
            "&collection=9&collection=10&listInterface=11&listInterface=12&list=13&list=14",
            UriKind.Relative);

        AppRoute route = table.Match(uri).Route!;

        Assert.Equal([1, 2], Values("ArrayValues"));
        Assert.Equal([3, 4], Values("EnumerableValues"));
        Assert.Equal([5, 6], Values("ReadOnlyCollectionValues"));
        Assert.Equal([7, 8], Values("ReadOnlyListValues"));
        Assert.Equal([9, 10], Values("CollectionValues"));
        Assert.Equal([11, 12], Values("ListInterfaceValues"));
        Assert.Equal([13, 14], Values("ListValues"));
        Assert.Equal(uri.OriginalString, table.Format(route));
        return;

        int[] Values(string propertyName)
        {
            object? value = route.GetType().GetProperty(propertyName)!.GetValue(route);
            return Assert.IsAssignableFrom<IEnumerable<int>>(value).ToArray();
        }
    }

    [Fact]
    public void GeneratedRepeatedQueryUsesElementCodec()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public readonly record struct Slug(string Value);

            [AppNavRoute("/search")]
            [AppNavQuery(nameof(Slugs), Name = "slug")]
            public sealed record SearchRoute(IReadOnlyList<Slug>? Slugs = null) : AppRoute;

            public static partial class AppNavGenerated
            {
                public static RouteTable CreateConfiguredRouteTable()
                {
                    return CreateRouteTable(routes => routes.AddValueCodec<Slug>(
                        value => new Slug(value),
                        value => value.Value));
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("routes.RequireValueCodec<global::Commerce.Sample.Slug>()", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);
        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(
            generatedType.GetMethod("CreateConfiguredRouteTable")!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri("/search?slug=one&slug=two", UriKind.Relative));

        Assert.True(match.IsSuccess);
        object? values = match.Route!.GetType().GetProperty("Slugs")!.GetValue(match.Route);
        Assert.Equal(["one", "two"], Assert.IsAssignableFrom<System.Collections.IEnumerable>(values)
            .Cast<object>()
            .Select(static value => (string)value.GetType().GetProperty("Value")!.GetValue(value)!)
            .ToArray());
        Assert.Equal("/search?slug=one&slug=two", table.Format(match.Route));
    }

    [Fact]
    public void GeneratedRouteTableFormatsEveryValueShapeWithItsDeclaredCodecType()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public interface IRouteValue
            {
                string Value { get; }
            }

            public sealed record RouteValue(string Value) : IRouteValue;

            public static class Metadata
            {
                public static RouteMetadataKey<IRouteValue> Context { get; } = new("context");
            }

            [AppNavRoute("/declared/{id}")]
            [AppNavQuery(nameof(Filter), Name = "filter")]
            [AppNavQuery(nameof(Tags), Name = "tag")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Context))]
            public sealed record DeclaredCodecRoute(
                IRouteValue Id,
                IRouteValue? Filter = null,
                IReadOnlyList<IRouteValue>? Tags = null) : AppRoute;

            public static partial class AppNavGenerated
            {
                public static RouteTable CreateConfiguredRouteTable()
                {
                    return CreateRouteTable(routes => routes.AddValueCodec<IRouteValue>(
                        value => new RouteValue(value.ToUpperInvariant()),
                        value => value.Value.ToLowerInvariant()));
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);
        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(
            "format.PathParam<global::Commerce.Sample.IRouteValue>",
            result.GeneratedSource);
        Assert.Contains(
            "format.QueryParam<global::Commerce.Sample.IRouteValue",
            result.GeneratedSource);
        Assert.Contains(
            "format.QueryParam<global::System.Collections.Generic.IReadOnlyList<global::Commerce.Sample.IRouteValue>",
            result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);
        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(
            generatedType.GetMethod("CreateConfiguredRouteTable")!.Invoke(null, null));

        RouteMatchResult match = table.Match(new Uri(
            "/declared/ONE?filter=TWO&tag=THREE&tag=FOUR&context=FIVE",
            UriKind.Relative));

        Assert.True(match.IsSuccess);
        object route = match.Route!;
        Assert.Equal("ONE", Value(route.GetType().GetProperty("Id")!.GetValue(route)!));
        Assert.Equal("TWO", Value(route.GetType().GetProperty("Filter")!.GetValue(route)!));
        Assert.Equal(
            ["THREE", "FOUR"],
            Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                    route.GetType().GetProperty("Tags")!.GetValue(route))
                .Cast<object>()
                .Select(Value));
        Assert.Equal("FIVE", Value(match.Metadata["context"]!));
        Assert.Equal(
            "/declared/one?filter=two&tag=three&tag=four&context=five",
            table.Format(
                match.Route!,
                new Dictionary<string, object?> { ["context"] = match.Metadata["context"] }));

        RouteMatchResult withoutOptionalValues = table.Match(new Uri("/declared/ONE", UriKind.Relative));
        Assert.True(withoutOptionalValues.IsSuccess);
        Assert.Equal("/declared/one", table.Format(withoutOptionalValues.Route!));
        return;

        static string Value(object value)
        {
            return Assert.IsType<string>(value.GetType().GetProperty("Value")!.GetValue(value));
        }
    }

    [Fact]
    public void GeneratedRouteTableRejectsNullableValueTypeCollectionElements()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/search")]
            [AppNavQuery(nameof(Values), Name = "value")]
            public sealed record SearchRoute(IReadOnlyList<int?>? Values = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV015");
    }

    [Fact]
    public void GeneratedRouteTableRejectsUnsupportedAndNestedQueryCollections()
    {
        const string source = """
            #nullable enable
            using System.Collections;
            using System.Collections.Generic;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/nested")]
            [AppNavQuery(nameof(Values), Name = "value")]
            public sealed record NestedRoute(IReadOnlyList<string[]>? Values = null) : AppRoute;

            [AppNavRoute("/matrix")]
            [AppNavQuery(nameof(Values), Name = "value")]
            public sealed record MatrixRoute(int[,]? Values = null) : AppRoute;

            [AppNavRoute("/set")]
            [AppNavQuery(nameof(Values), Name = "value")]
            public sealed record SetRoute(HashSet<string>? Values = null) : AppRoute;

            [AppNavRoute("/legacy")]
            [AppNavQuery(nameof(Values), Name = "value")]
            public sealed record LegacyRoute(ArrayList? Values = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Equal(
            4,
            result.GeneratorDiagnostics
                .Where(static diagnostic => diagnostic.Id == "APPNAV015")
                .Select(static diagnostic => diagnostic.GetMessage())
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void GeneratedRouteTableRejectsMismatchedQueryPropertyAndConstructorTypes()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/search")]
            [AppNavQuery(nameof(Values), Name = "value")]
            public sealed record SearchRoute : AppRoute
            {
                public IReadOnlyList<int>? Values { get; }

                public SearchRoute(List<int>? values = null)
                {
                    Values = values;
                }
            }
            """;

        GeneratorResult result = RunGenerator(source);

        Diagnostic diagnostic = result.GeneratorDiagnostics.First(static item => item.Id == "APPNAV016");
        Assert.Contains("types must match", diagnostic.GetMessage());
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

    [Theory]
    [InlineData("int", "guid")]
    [InlineData("guid", "int")]
    [InlineData("long", "guid")]
    [InlineData("guid", "long")]
    [InlineData("decimal", "guid")]
    [InlineData("guid", "decimal")]
    public void NumericAndGuidRouteConstraintsReportAmbiguity(string leftConstraint, string rightConstraint)
    {
        string source = CreateConstrainedRoutePairSource(leftConstraint, rightConstraint);

        GeneratorResult result = RunGenerator(source);
        string leftTemplate = $"/values/{{value:{leftConstraint}}}";
        string rightTemplate = $"/values/{{value:{rightConstraint}}}";
        string expectedMessage = $"Route templates '{leftTemplate}' and '{rightTemplate}' can match the same URI path";
        Diagnostic? diagnostic = result.GeneratorDiagnostics.FirstOrDefault(
            item => item.Id == "APPNAV010" &&
                    item.Severity == DiagnosticSeverity.Error &&
                    StringComparer.Ordinal.Equals(item.GetMessage(), expectedMessage));

        Assert.NotNull(diagnostic);
        Assert.Contains(
            rightTemplate,
            diagnostic!.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan),
            StringComparison.Ordinal);
        Assert.DoesNotContain(result.GeneratorDiagnostics, static item => item.Id == "APPNAV009");
    }

    [Theory]
    [InlineData("int", "alpha")]
    [InlineData("alpha", "int")]
    [InlineData("decimal", "bool")]
    [InlineData("bool", "decimal")]
    [InlineData("guid", "bool")]
    [InlineData("bool", "guid")]
    public void DisjointRouteConstraintsDoNotReportAmbiguity(string leftConstraint, string rightConstraint)
    {
        string source = CreateConstrainedRoutePairSource(leftConstraint, rightConstraint);

        GeneratorResult result = RunGenerator(source);

        Assert.DoesNotContain(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV010");
        AssertCompileClean(result.Compilation);
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
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));
        RouteDefinition definition = Assert.Single(table.Definitions);
        Assert.Equal("LocalRoute", definition.RouteType.Name);
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
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));

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
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));

        var match = table.Match(new Uri("/stores/northwind", UriKind.Relative));

        Assert.True(match.IsSuccess);
        Assert.True(match.Metadata.ContainsKey("page"));
        Assert.Null(match.Metadata["page"]);
    }

    [Fact]
    public void GeneratedRouteTableUsesReferencedMetadataKeyNames()
    {
        MetadataReference metadataReference = EmitReference(
            "Commerce.Contracts",
            """
            using AdamE.AppNav.Routing;

            namespace Commerce.Contracts;

            public static class Metadata
            {
                public static RouteMetadataKey<string> Campaign { get; } = new("campaign");
            }
            """);

        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;
            using Commerce.Contracts;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Campaign))]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source, [metadataReference]);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("global::Commerce.Contracts.Metadata.Campaign.Name", result.GeneratedSource);
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void GeneratedRouteTableUsesLocalMetadataKeyNamesThatAreNotCompileTimeConstants()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            internal static class Metadata
            {
                internal static RouteMetadataKey<string> Campaign { get; } = CreateCampaignKey();

                private static RouteMetadataKey<string> CreateCampaignKey() => new("campaign");
            }

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata), nameof(Metadata.Campaign))]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("global::Commerce.Sample.Metadata.Campaign.Name", result.GeneratedSource);
        Assembly assembly = Emit(result.Compilation);

        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");
        var table = Assert.IsType<RouteTable>(generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));
        var match = table.Match(new Uri("/stores/northwind?campaign=spring-launch", UriKind.Relative));

        Assert.True(match.IsSuccess);
        Assert.Equal("spring-launch", match.Metadata["campaign"]);
    }

    [Fact]
    public void ReferencedMetadataKeyNameConflictingWithRouteQueryFailsAtRegistration()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Generators.Tests;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQuery(nameof(Campaign), Name = "CAMPAIGN")]
            [AppNavQueryMetadata(typeof(ReferencedMetadataKeys), nameof(ReferencedMetadataKeys.Campaign))]
            public sealed record StoreRoute(string StoreId, string? Campaign = null) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assembly assembly = Emit(result.Compilation);
        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");

        var exception = Assert.Throws<TargetInvocationException>(
            () => generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));
        var duplicate = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("query parameter 'campaign'", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateReferencedMetadataKeyNamesFailAtRegistration()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Generators.Tests;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(ReferencedMetadataKeys), nameof(ReferencedMetadataKeys.Campaign))]
            [AppNavQueryMetadata(typeof(ReferencedMetadataKeys), nameof(ReferencedMetadataKeys.CampaignAlias))]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assembly assembly = Emit(result.Compilation);
        Type generatedType = assembly.GetRequiredType("Commerce.Sample.AppNavGenerated");

        var exception = Assert.Throws<TargetInvocationException>(
            () => generatedType.GetMethod("CreateRouteTable", Type.EmptyTypes)!.Invoke(null, null));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
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
    public void InaccessibleReferencedMetadataMemberReportsDiagnostic()
    {
        MetadataReference metadataReference = EmitReference(
            "Commerce.Contracts",
            """
            using AdamE.AppNav.Routing;

            namespace Commerce.Contracts;

            public static class Metadata
            {
                internal static RouteMetadataKey<string> Campaign { get; } = new("campaign");
            }
            """);

        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;
            using Commerce.Contracts;

            namespace Commerce.Sample;

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata), "Campaign")]
            public sealed record StoreRoute(string StoreId) : AppRoute;
            """;

        GeneratorResult result = RunGenerator(source, [metadataReference]);

        Assert.Contains(result.GeneratorDiagnostics, static diagnostic => diagnostic.Id == "APPNAV003");
        AssertCompileClean(result.Compilation);
    }

    [Fact]
    public void OpenGenericMetadataContainerReportsDiagnostic()
    {
        const string source = """
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            public static class Metadata<T>
            {
                public static RouteMetadataKey<string> Campaign { get; } = new("campaign");
            }

            [AppNavRoute("/stores/{storeId}")]
            [AppNavQueryMetadata(typeof(Metadata<>), nameof(Metadata<object>.Campaign))]
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

    private static string CreateConstrainedRoutePairSource(string leftConstraint, string rightConstraint)
    {
        return $$"""
            using AdamE.AppNav;
            using AdamE.AppNav.Routing;

            namespace Commerce.Sample;

            [AppNavRoute("/values/{value:{{leftConstraint}}}")]
            public sealed record LeftValueRoute(string Value) : AppRoute;

            [AppNavRoute("/values/{value:{{rightConstraint}}}")]
            public sealed record RightValueRoute(string Value) : AppRoute;
            """;
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
