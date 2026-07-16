using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class RouteTableTests
{
    [Fact]
    public void AddModuleAddsRouteDefinitionsAndReturnsBuilder()
    {
        var module = new ModuleRoutes();
        RouteTableBuilder? returned = null;

        var table = RouteTable.Create(routes =>
        {
            returned = routes.AddModule(module);
        });

        var result = table.Match(new Uri("/modules/northwind", UriKind.Relative));

        Assert.NotNull(returned);
        Assert.Equal(new ModuleRoute("northwind"), result.Route);
    }

    [Fact]
    public void MatchParsesTypedPathAndQueryValues()
    {
        var table = TestRoutes.CreateTable();

        var result = table.Match(new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring"));

        Assert.True(result.IsSuccess);
        var route = Assert.IsType<TestRoutes.ProductDetailRoute>(result.Route);
        Assert.Equal("northwind", route.StoreId);
        Assert.Equal(123, route.ProductId);
        Assert.Equal("blue", route.Variant);
        Assert.Equal("spring", route.Promo);
    }

    [Fact]
    public void MatchIgnoresFragmentForRelativeUriWithoutQuery()
    {
        var table = TestRoutes.CreateTable();

        var result = table.Match(new Uri("/stores/northwind#details", UriKind.Relative));

        Assert.True(result.IsSuccess);
        Assert.Equal(new TestRoutes.StoreRoute("northwind"), result.Route);
    }

    [Fact]
    public void MatchIgnoresFragmentForRelativeUriWithQuery()
    {
        var table = TestRoutes.CreateTable();

        var result = table.Match(new Uri("/stores/northwind/products/123?variant=blue&promo=spring#details", UriKind.Relative));

        Assert.True(result.IsSuccess);
        var route = Assert.IsType<TestRoutes.ProductDetailRoute>(result.Route);
        Assert.Equal("northwind", route.StoreId);
        Assert.Equal(123, route.ProductId);
        Assert.Equal("blue", route.Variant);
        Assert.Equal("spring", route.Promo);
    }

    [Fact]
    public void FormatRoundTripsTypedRoute()
    {
        var table = TestRoutes.CreateTable();
        var route = new TestRoutes.ProductDetailRoute("northwind", 123, "blue", "spring");

        var formatted = table.Format(route);
        var matched = table.Match(new Uri(formatted, UriKind.Relative));

        Assert.Equal("/stores/northwind/products/123?variant=blue&promo=spring", formatted);
        Assert.Equal(route, matched.Route);
    }

    [Fact]
    public void FormatPrefersMostSpecificFormatterForDerivedRoute()
    {
        var table = RouteTable.Create(routes => routes
            .Map("/base", _ => new BasePolymorphicRoute())
            .Map("/derived", _ => new DerivedPolymorphicRoute()));

        Assert.Equal("/derived", table.Format(new DerivedPolymorphicRoute()));
    }

    [Fact]
    public void FormatFallsBackToBaseFormatterWhenDerivedFormatterIsMissing()
    {
        var table = RouteTable.Create(routes => routes
            .Map("/base", _ => new BasePolymorphicRoute()));

        Assert.Equal("/base", table.Format(new DerivedPolymorphicRoute()));
    }

    [Fact]
    public void MatchFailureDoesNotCreateHostState()
    {
        var table = TestRoutes.CreateTable();

        var result = table.Match(new Uri("https://example.com/app?tab=catalog&id=123"));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Route);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "route.not_matched");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Data.ContainsKey("path"));
    }

    [Fact]
    public void ConstrainedPathParamsRejectInvalidValues()
    {
        var table = TestRoutes.CreateTable();

        var result = table.Match(new Uri("https://example.com/stores/northwind/products/not-a-number"));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "route.not_matched");
    }

    [Theory]
    [InlineData("/values/42", "/values/{value:int}")]
    [InlineData("/values/9223372036854775807", "/values/{value:long}")]
    [InlineData("/values/6f9619ff-8b86-d011-b42d-00cf4fc964ff", "/values/{value:guid}")]
    [InlineData("/values/true", "/values/{value:bool}")]
    [InlineData("/values/19.95", "/values/{value:decimal}")]
    [InlineData("/values/Northwind", "/values/{value:alpha}")]
    public void BuiltInConstraintsAcceptMatchingValues(string path, string template)
    {
        var table = RouteTable.Create(routes => routes.Map(
            template,
            match => new ValueRoute(match.Path("value")),
            format => format.PathParam("value", route => route.Value)));

        var route = Assert.IsType<ValueRoute>(table.Match(new Uri(path, UriKind.Relative)).Route);

        Assert.Equal(path["/values/".Length..], route.Value);
        Assert.Equal(path, table.Format(route));
    }

    [Fact]
    public void OptionalTrailingPathParamsCanBeOmittedOrPresent()
    {
        var table = RouteTable.Create(routes => routes.Map(
            "/stores/{storeId}/catalog/{category?}",
            match => new CatalogCategoryRoute(match.Path("storeId"), match.PathOptional("category")),
            format => format
                .PathParam("storeId", route => route.StoreId)
                .PathParam("category", route => route.Category)));

        var withoutCategory = table.Match(new Uri("/stores/northwind/catalog", UriKind.Relative));
        var withCategory = table.Match(new Uri("/stores/northwind/catalog/widgets", UriKind.Relative));

        Assert.Equal(new CatalogCategoryRoute("northwind", null), withoutCategory.Route);
        Assert.Equal(new CatalogCategoryRoute("northwind", "widgets"), withCategory.Route);
        Assert.Equal("/stores/northwind/catalog", table.Format(new CatalogCategoryRoute("northwind", null)));
        Assert.Equal("/stores/northwind/catalog/widgets", table.Format(new CatalogCategoryRoute("northwind", "widgets")));
    }

    [Fact]
    public void ConstrainedOptionalPathParamsCanBeOmittedOrPresent()
    {
        var table = RouteTable.Create(routes => routes.Map(
            "/products/{productId:int?}",
            match => new OptionalProductRoute(match.PathOptional<int>("productId")),
            format => format.PathParam("productId", route => route.ProductId)));

        Assert.Equal(new OptionalProductRoute(null), table.Match(new Uri("/products", UriKind.Relative)).Route);
        Assert.Equal(new OptionalProductRoute(123), table.Match(new Uri("/products/123", UriKind.Relative)).Route);
        Assert.False(table.Match(new Uri("/products/not-a-number", UriKind.Relative)).IsSuccess);
        Assert.Equal("/products", table.Format(new OptionalProductRoute(null)));
        Assert.Equal("/products/123", table.Format(new OptionalProductRoute(123)));
    }

    [Fact]
    public void ConstrainedOptionalRoutesBeatUnconstrainedRequiredRoutes()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/products/{slug}",
                match => new ProductSlugRoute(match.Path("slug")),
                format => format.PathParam("slug", route => route.Slug))
            .Map(
                "/products/{productId:int?}",
                match => new OptionalProductRoute(match.PathOptional<int>("productId")),
                format => format.PathParam("productId", route => route.ProductId)));

        Assert.Equal(new OptionalProductRoute(123), table.Match(new Uri("/products/123", UriKind.Relative)).Route);
        Assert.Equal(new ProductSlugRoute("abc"), table.Match(new Uri("/products/abc", UriKind.Relative)).Route);
        Assert.Equal(new OptionalProductRoute(null), table.Match(new Uri("/products", UriKind.Relative)).Route);
    }

    [Fact]
    public void CatchAllPathParamsCaptureAndFormatRemainingPath()
    {
        var table = RouteTable.Create(routes => routes.Map(
            "/docs/{*path}",
            match => new DocsRoute(match.Path("path")),
            format => format.PathParam("path", route => route.Path)));

        var result = table.Match(new Uri("/docs/guides/install", UriKind.Relative));

        Assert.Equal(new DocsRoute("guides/install"), result.Route);
        Assert.Equal("/docs/guides/install", table.Format(new DocsRoute("guides/install")));
    }

    [Fact]
    public void RepeatedQueryParamsCanBeReadAndFormatted()
    {
        var table = RouteTable.Create(routes => routes.Map(
            "/search",
            match => new SearchRoute(match.Query("tag"), match.QueryAll("tag")),
            format => format.QueryParam("tag", route => route.Tags)));

        var result = table.Match(new Uri("/search?tag=blue&tag=green", UriKind.Relative));
        var route = Assert.IsType<SearchRoute>(result.Route);

        Assert.Equal("green", route.LastTag);
        Assert.Equal(new[] { "blue", "green" }, route.Tags);
        Assert.Equal("/search?tag=blue&tag=green", table.Format(route));
    }

    [Fact]
    public void ConventionRouteMapsPathOnlyRecordRoute()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>("/stores/{storeId}"));

        var result = table.Match(new Uri("/stores/northwind", UriKind.Relative));

        Assert.Equal(new TestRoutes.StoreRoute("northwind"), result.Route);
        Assert.Equal("/stores/northwind", table.Format(new TestRoutes.StoreRoute("northwind")));
    }

    [Fact]
    public void ConventionRouteMapsMultiPathRecordRoute()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<ConventionProductRoute>(
            "/stores/{storeId}/products/{productId:int}"));

        var result = table.Match(new Uri("/stores/northwind/products/123", UriKind.Relative));

        Assert.Equal(new ConventionProductRoute("northwind", 123), result.Route);
        Assert.Equal("/stores/northwind/products/123", table.Format(new ConventionProductRoute("northwind", 123)));
    }

    [Fact]
    public void ConventionRouteMapsOptionalQueryRoute()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<ConventionProductRoute>(
            "/stores/{storeId}/products/{productId:int}",
            route => route.Query(product => product.Variant, "variant")));

        var withQuery = table.Match(new Uri("/stores/northwind/products/123?variant=blue", UriKind.Relative));
        var withoutQuery = table.Match(new Uri("/stores/northwind/products/123", UriKind.Relative));

        Assert.Equal(new ConventionProductRoute("northwind", 123, "blue"), withQuery.Route);
        Assert.Equal(new ConventionProductRoute("northwind", 123, null), withoutQuery.Route);
        Assert.Equal(
            "/stores/northwind/products/123?variant=blue",
            table.Format(new ConventionProductRoute("northwind", 123, "blue")));
        Assert.Equal(
            "/stores/northwind/products/123",
            table.Format(new ConventionProductRoute("northwind", 123, null)));
    }

    [Fact]
    public void ConventionRouteMapsDefaultedQueryRoute()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<ConventionDefaultedQueryRoute>(
            "/stores/{storeId}",
            route => route.Query(value => value.Page)));

        var withQuery = table.Match(new Uri("/stores/northwind?page=7", UriKind.Relative));
        var withoutQuery = table.Match(new Uri("/stores/northwind", UriKind.Relative));

        Assert.Equal(new ConventionDefaultedQueryRoute("northwind", 7), withQuery.Route);
        Assert.Equal(new ConventionDefaultedQueryRoute("northwind"), withoutQuery.Route);
        Assert.Equal(
            "/stores/northwind?page=7",
            table.Format(new ConventionDefaultedQueryRoute("northwind", 7)));
        Assert.Equal(
            "/stores/northwind?page=1",
            table.Format(new ConventionDefaultedQueryRoute("northwind")));
    }

    [Fact]
    public void ConventionRouteMapsAndFormatsEverySupportedRepeatedQueryCollection()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<ConventionCollectionQueryRoute>(
            "/collections",
            route => route
                .Query(value => value.ArrayValues, "array")
                .Query(value => value.EnumerableValues, "enumerable")
                .Query(value => value.ReadOnlyCollectionValues, "readOnlyCollection")
                .Query(value => value.ReadOnlyListValues, "readOnlyList")
                .Query(value => value.CollectionValues, "collection")
                .Query(value => value.ListInterfaceValues, "listInterface")
                .Query(value => value.ListValues, "list")));
        var uri = new Uri(
            "/collections?array=1&array=2&enumerable=3&enumerable=4" +
            "&readOnlyCollection=5&readOnlyCollection=6&readOnlyList=7&readOnlyList=8" +
            "&collection=9&collection=10&listInterface=11&listInterface=12&list=13&list=14",
            UriKind.Relative);

        var route = Assert.IsType<ConventionCollectionQueryRoute>(table.Match(uri).Route);

        Assert.Equal([1, 2], Assert.IsType<int[]>(route.ArrayValues));
        Assert.Equal([3, 4], route.EnumerableValues);
        Assert.Equal([5, 6], route.ReadOnlyCollectionValues);
        Assert.Equal([7, 8], route.ReadOnlyListValues);
        Assert.Equal([9, 10], route.CollectionValues);
        Assert.Equal([11, 12], route.ListInterfaceValues);
        Assert.Equal([13, 14], route.ListValues);
        Assert.IsType<int[]>(route.ReadOnlyListValues);
        Assert.IsType<List<int>>(route.ListValues);
        Assert.Equal(uri.OriginalString, table.Format(route));
    }

    [Fact]
    public void ConventionRepeatedQueryUsesElementCodec()
    {
        var table = RouteTable.Create(routes => routes
            .AddValueCodec<SlugValue>(static value => new SlugValue(value), static value => value.Value)
            .MapRoute<ConventionCustomCollectionRoute>(
                "/collections/custom",
                route => route.Query(value => value.Values, "value")));

        var route = Assert.IsType<ConventionCustomCollectionRoute>(table.Match(
            new Uri("/collections/custom?value=one&value=two", UriKind.Relative)).Route);

        Assert.Equal([new SlugValue("one"), new SlugValue("two")], route.Values);
        Assert.Equal("/collections/custom?value=one&value=two", table.Format(route));
    }

    [Fact]
    public void ConventionRepeatedQueryRejectsNullableValueTypeElements()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ConventionNullableCollectionRoute>(
                "/collections/nullable",
                route => route.Query(value => value.Values, "value"))));

        Assert.Contains("nullable value-type element", exception.Message);
    }

    [Fact]
    public void ConventionRepeatedQueryRejectsNestedCollections()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ConventionNestedCollectionRoute>(
                "/collections/nested",
                route => route.Query(value => value.Values, "value"))));

        Assert.Contains("nested collection element", exception.Message);
    }

    [Fact]
    public void ConventionRepeatedQueryRejectsUnsupportedCollectionShapes()
    {
        var matrixException = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ConventionMatrixCollectionRoute>(
                "/collections/matrix",
                route => route.Query(value => value.Values, "value"))));
        var setException = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ConventionSetCollectionRoute>(
                "/collections/set",
                route => route.Query(value => value.Values, "value"))));
        var legacyException = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ConventionLegacyCollectionRoute>(
                "/collections/legacy",
                route => route.Query(value => value.Values, "value"))));

        Assert.Contains("unsupported query collection type", matrixException.Message);
        Assert.Contains("unsupported query collection type", setException.Message);
        Assert.Contains("unsupported query collection type", legacyException.Message);
    }

    [Fact]
    public void ConventionRouteInfersCamelCaseQueryNames()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<ConventionQueryRoute>(
            "/stores/{storeId}",
            route => route
                .Query(value => value.MissionId)
                .Query(value => value.CoverImageDraftId)));

        var result = table.Match(new Uri("/stores/northwind?missionId=mission-123&coverImageDraftId=draft-456", UriKind.Relative));

        Assert.Equal(
            new ConventionQueryRoute("northwind", "mission-123", "draft-456"),
            result.Route);
        Assert.Equal(
            "/stores/northwind?missionId=mission-123&coverImageDraftId=draft-456",
            table.Format(new ConventionQueryRoute("northwind", "mission-123", "draft-456")));
    }

    [Fact]
    public void ConventionRouteInfersAcronymQueryNamesUsingJsonCamelCase()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<ConventionAcronymQueryRoute>(
            "/values/{value}",
            route => route.Query(value => value.QRValue)));

        var result = table.Match(new Uri("/values/one?qrValue=abc", UriKind.Relative));

        Assert.Equal(new ConventionAcronymQueryRoute("one", "abc"), result.Route);
        Assert.Equal(
            "/values/one?qrValue=abc",
            table.Format(new ConventionAcronymQueryRoute("one", "abc")));
    }

    [Fact]
    public void ConventionRouteMapsMetadataQueryValues()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route
                .QueryMetadata(MissionIdMetadata)
                .QueryMetadata(RankMetadata)));

        var result = table.Match(new Uri("/stores/northwind?missionId=mission-1&rank=42", UriKind.Relative));

        Assert.Equal(new TestRoutes.StoreRoute("northwind"), result.Route);
        Assert.Equal("mission-1", result.Metadata[MissionIdMetadata.Name]);
        Assert.Equal(42, result.Metadata[RankMetadata.Name]);
        Assert.Equal(
            "/stores/northwind?missionId=mission-1&rank=42",
            table.Format(
                new TestRoutes.StoreRoute("northwind"),
                new Dictionary<string, object?>
                {
                    [MissionIdMetadata.Name] = "mission-1",
                    [RankMetadata.Name] = 42
                }));
    }

    [Fact]
    public void FormatAppRouteRequestHonorsRequestMetadata()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route.QueryMetadata(MissionIdMetadata)));
        var request = AppRouteRequest
            .For(new TestRoutes.StoreRoute("northwind"))
            .WithMetadata(MissionIdMetadata, "mission-1");

        Assert.Equal("/stores/northwind?missionId=mission-1", table.Format(request));
    }

    [Fact]
    public void FormatUriAppRouteRequestHonorsRequestMetadata()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route.QueryMetadata(MissionIdMetadata)));
        var request = AppRouteRequest
            .For(new TestRoutes.StoreRoute("northwind"))
            .WithMetadata(MissionIdMetadata, "mission-1");

        Assert.Equal(
            "https://example.com/stores/northwind?missionId=mission-1",
            table.FormatUri(request, new Uri("https://example.com")).ToString());
    }

    [Fact]
    public void AppRouteRequestRoundTripsSampleStyleMetadataQuery()
    {
        var table = RouteTable.Create(routes => routes.Map(
            "/stores/{storeId}/products/{productId:int}",
            match =>
            {
                match.QueryMetadata(CampaignMetadata);
                return new TestRoutes.ProductDetailRoute(
                    match.Path("storeId"),
                    match.Path<int>("productId"),
                    match.Query("variant"),
                    match.Query("promo"));
            },
            format => format
                .PathParam("storeId", route => route.StoreId)
                .PathParam("productId", route => route.ProductId)
                .QueryParam("variant", route => route.Variant)
                .QueryParam("promo", route => route.Promo)
                .QueryMetadata(CampaignMetadata)));
        var request = AppRouteRequest
            .For(new TestRoutes.ProductDetailRoute("northwind", 123, "blue", "spring"))
            .WithMetadata(CampaignMetadata, "spring-launch");

        var formatted = table.Format(request);
        var matched = table.Match(new Uri(formatted, UriKind.Relative));

        Assert.Equal(
            "/stores/northwind/products/123?variant=blue&promo=spring&campaign=spring-launch",
            formatted);
        Assert.Equal(request.Route, matched.Route);
        Assert.Equal("spring-launch", matched.Metadata[CampaignMetadata.Name]);
    }

    [Fact]
    public void ConventionRouteOmitsNullMetadataQueryValues()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route.QueryMetadata(MissionIdMetadata)));

        var result = table.Match(new Uri("/stores/northwind", UriKind.Relative));

        Assert.Empty(result.Metadata);
        Assert.Equal(
            "/stores/northwind",
            table.Format(
                new TestRoutes.StoreRoute("northwind"),
                new Dictionary<string, object?> { [MissionIdMetadata.Name] = null }));
    }

    [Fact]
    public void ExplicitRouteMapsMetadataQueryValues()
    {
        var table = RouteTable.Create(routes => routes.Map(
            "/stores/{storeId}",
            match =>
            {
                match.QueryMetadata(MissionIdMetadata);
                return new TestRoutes.StoreRoute(match.Path("storeId"));
            },
            format => format
                .PathParam("storeId", route => route.StoreId)
                .QueryMetadata(MissionIdMetadata)));

        var result = table.Match(new Uri("/stores/northwind?missionId=mission-1", UriKind.Relative));

        Assert.Equal(new TestRoutes.StoreRoute("northwind"), result.Route);
        Assert.Equal("mission-1", result.Metadata[MissionIdMetadata.Name]);
        Assert.Equal(
            "/stores/northwind?missionId=mission-1",
            table.Format(
                new TestRoutes.StoreRoute("northwind"),
                new Dictionary<string, object?> { [MissionIdMetadata.Name] = "mission-1" }));
    }

    [Fact]
    public void ConventionRouteExplicitQueryNameOverridesInferredName()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<ConventionProductRoute>(
            "/stores/{storeId}/products/{productId:int}",
            route => route.Query(product => product.Variant, "sku")));

        var result = table.Match(new Uri("/stores/northwind/products/123?sku=blue", UriKind.Relative));

        Assert.Equal(new ConventionProductRoute("northwind", 123, "blue"), result.Route);
        Assert.Equal(
            "/stores/northwind/products/123?sku=blue",
            table.Format(new ConventionProductRoute("northwind", 123, "blue")));
    }

    [Fact]
    public void ConventionRouteRejectsDuplicateInferredQueryNameAtRegistration()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ConventionQueryRoute>(
                "/stores/{storeId}",
                route => route
                    .Query(value => value.CoverImageDraftId, "missionId")
                    .Query(value => value.MissionId))));
    }

    [Fact]
    public void ConventionRoutePreservesConstrainedPathBehavior()
    {
        var table = RouteTable.Create(routes => routes.MapRoute<OptionalProductRoute>("/products/{productId:int}"));

        Assert.Equal(new OptionalProductRoute(123), table.Match(new Uri("/products/123", UriKind.Relative)).Route);
        Assert.False(table.Match(new Uri("/products/not-a-number", UriKind.Relative)).IsSuccess);
        Assert.Equal("/products/123", table.Format(new OptionalProductRoute(123)));
    }

    [Fact]
    public void ConventionRouteUsesExplicitCustomValueCodecForMatchingAndFormatting()
    {
        var table = RouteTable.Create(routes => routes
            .AddValueCodec<SlugValue>(
                static value => new SlugValue(value.ToUpperInvariant()),
                static value => value.Value.ToLowerInvariant())
            .MapRoute<CustomSlugRoute>("/slugs/{slug}"));

        var match = table.Match(new Uri("/slugs/northwind", UriKind.Relative));

        Assert.Equal(new CustomSlugRoute(new SlugValue("NORTHWIND")), match.Route);
        Assert.Equal("/slugs/northwind", table.Format(match.Route!));
    }

    [Fact]
    public void ConventionRouteRejectsMissingCustomValueCodecWhenTableIsBuilt()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<CustomSlugRoute>("/slugs/{slug}")));

        Assert.Contains("requires a registered codec", exception.Message);
    }

    [Fact]
    public void ConventionRouteRejectsNonNullableOptionalPathParameterAtRegistration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<RequiredProductIdRoute>("/products/{productId:int?}")));

        Assert.Contains("path value may be absent", exception.Message);
        Assert.Contains("nullable or provide a default value", exception.Message);
    }

    [Fact]
    public void ConventionRouteRejectsMissingPathMemberAtRegistration()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<MissingPathMemberRoute>("/values/{value}")));
    }

    [Fact]
    public void ConventionRouteRejectsRouteWithNoUsableConstructorAtRegistration()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<NoUsableConstructorRoute>("/values/{value}")));
    }

    [Fact]
    public void ConventionRouteRejectsNonNullableReferenceTypeQueryConstructorParameterAtRegistration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<RequiredReferenceQueryRoute>(
                "/stores/{storeId}",
                route => route.Query(value => value.Variant, "variant"))));

        Assert.Contains("query values are always optional", exception.Message);
        Assert.Contains("nullable or provide a default value", exception.Message);
    }

    [Fact]
    public void ConventionRouteRejectsNonNullableValueTypeQueryConstructorParameterAtRegistration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<RequiredValueQueryRoute>(
                "/stores/{storeId}",
                route => route.Query(value => value.Page))));

        Assert.Contains("query values are always optional", exception.Message);
        Assert.Contains("nullable or provide a default value", exception.Message);
    }

    [Fact]
    public void ConventionRouteRejectsObliviousReferenceTypeQueryConstructorParameterAtRegistration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ObliviousQueryRoute>(
                "/stores/{storeId}",
                route => route.Query(value => value.Variant, "variant"))));

        Assert.Contains("query values are always optional", exception.Message);
        Assert.Contains("nullable or provide a default value", exception.Message);
    }

    [Fact]
    public void ConventionRouteRejectsMismatchedQueryPropertyAndConstructorTypesAtRegistration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<MismatchedCollectionQueryRoute>(
                "/search",
                route => route.Query(value => value.Values, "value"))));

        Assert.Contains("types must match", exception.Message);
    }

    [Fact]
    public void ConventionRouteRejectsUnsupportedQueryExpressionAtRegistration()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
            routes.MapRoute<ValueRoute>(
                "/values/{value}",
                route => route.Query(value => value.Value.Length, "length"))));
    }

    [Fact]
    public void LiteralRoutesWinOverGenericRoutesRegardlessOfRegistrationOrder()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/products/{productId:int}",
                match => new OptionalProductRoute(match.Path<int>("productId")),
                format => format.PathParam("productId", route => route.ProductId))
            .Map(
                "/products/new",
                _ => new NewProductRoute()));

        Assert.IsType<NewProductRoute>(table.Match(new Uri("/products/new", UriKind.Relative)).Route);
        Assert.Equal(new OptionalProductRoute(123), table.Match(new Uri("/products/123", UriKind.Relative)).Route);
    }

    [Fact]
    public void MatchDecodesLiteralSegmentsBeforeCandidateSelection()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/stores/northwind",
                _ => new TestRoutes.StoreRoute("northwind")));

        Assert.Equal(
            new TestRoutes.StoreRoute("northwind"),
            table.Match(new Uri("/stores/north%77ind", UriKind.Relative)).Route);
    }

    [Fact]
    public void MatchRetainsGenericCandidatesWhenLongerLiteralPrefixDoesNotMatch()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/products/new/details",
                _ => new NewProductRoute())
            .Map(
                "/products/{slug}",
                match => new ProductSlugRoute(match.Path("slug")),
                format => format.PathParam("slug", route => route.Slug)));

        Assert.Equal(new ProductSlugRoute("other"), table.Match(new Uri("/products/other", UriKind.Relative)).Route);
    }

    [Fact]
    public void ExactShorterRoutesWinOverOptionalTrailingRoutesRegardlessOfRegistrationOrder()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/stores/{storeId}/{section?}",
                match => new StoreSectionRoute(match.Path("storeId"), match.PathOptional("section")),
                format => format
                    .PathParam("storeId", route => route.StoreId)
                    .PathParam("section", route => route.Section))
            .Map(
                "/stores/{storeId}",
                match => new TestRoutes.StoreRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId)));

        Assert.Equal(new TestRoutes.StoreRoute("northwind"), table.Match(new Uri("/stores/northwind", UriKind.Relative)).Route);
        Assert.Equal(
            new StoreSectionRoute("northwind", "catalog"),
            table.Match(new Uri("/stores/northwind/catalog", UriKind.Relative)).Route);
    }

    [Fact]
    public void ExactShorterRoutesWinOverCatchAllRoutesRegardlessOfRegistrationOrder()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/docs/{*path}",
                match => new DocsRoute(match.Path("path")),
                format => format.PathParam("path", route => route.Path))
            .Map(
                "/docs",
                _ => new DocsIndexRoute()));

        Assert.IsType<DocsIndexRoute>(table.Match(new Uri("/docs", UriKind.Relative)).Route);
        Assert.Equal(new DocsRoute("guides/install"), table.Match(new Uri("/docs/guides/install", UriKind.Relative)).Route);
    }

    [Fact]
    public void RequiredParameterRoutesCanCoexistWithCatchAllRoutes()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/docs/{*path}",
                match => new DocsRoute(match.Path("path")),
                format => format.PathParam("path", route => route.Path))
            .Map(
                "/docs/{section}",
                match => new ValueRoute(match.Path("section")),
                format => format.PathParam("section", route => route.Value)));

        Assert.Equal(new ValueRoute("guide"), table.Match(new Uri("/docs/guide", UriKind.Relative)).Route);
        Assert.Equal(new DocsRoute("guides/install"), table.Match(new Uri("/docs/guides/install", UriKind.Relative)).Route);
    }

    [Fact]
    public void DuplicateTemplatesAreRejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .Map(
                "/stores/{storeId}",
                match => new TestRoutes.StoreRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}",
                match => new TestRoutes.CatalogRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))));

        Assert.Equal("Route template '/stores/{storeId}' is registered more than once.", exception.Message);
    }

    [Fact]
    public void MultipleFluentTemplatesForSameExactRouteTypeAreRejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .Map(
                "/stores/{storeId}",
                match => new TestRoutes.StoreRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/legacy-stores/{storeId}",
                match => new TestRoutes.StoreRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))));

        Assert.Equal(
            $"Route type '{typeof(TestRoutes.StoreRoute).FullName}' is registered with multiple canonical templates: " +
            "'/legacy-stores/{storeId}', '/stores/{storeId}'. Register one template per exact route type and normalize " +
            "aliases with INavigationRequestTransformer.",
            exception.Message);
    }

    [Fact]
    public void FluentAndConventionTemplatesForSameExactRouteTypeAreRejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .Map(
                "/aliases/{value}",
                match => new ValueRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))
            .MapRoute<ValueRoute>("/values/{value}")));

        Assert.Contains($"Route type '{typeof(ValueRoute).FullName}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'/aliases/{value}', '/values/{value}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleCompositionRejectsMultipleTemplatesForSameExactRouteType()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .AddModule(new ModuleRoutes())
            .MapRoute<ModuleRoute>("/legacy-modules/{value}")));

        Assert.Contains($"Route type '{typeof(ModuleRoute).FullName}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'/legacy-modules/{value}', '/modules/{value}'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateRouteTypeMessageIsIndependentOfRegistrationOrder(bool reverse)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes =>
        {
            if (reverse)
            {
                routes.MapRoute<ValueRoute>("/z/{value}");
                routes.MapRoute<ValueRoute>("/a/{value}");
            }
            else
            {
                routes.MapRoute<ValueRoute>("/a/{value}");
                routes.MapRoute<ValueRoute>("/z/{value}");
            }
        }));

        Assert.Contains("'/a/{value}', '/z/{value}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctBaseAndDerivedRouteTypesMayUseDifferentTemplates()
    {
        RouteTable table = RouteTable.Create(routes => routes
            .Map("/base", _ => new BasePolymorphicRoute())
            .Map("/derived", _ => new DerivedPolymorphicRoute()));

        Assert.IsType<BasePolymorphicRoute>(table.Match(new Uri("/base", UriKind.Relative)).Route);
        Assert.IsType<DerivedPolymorphicRoute>(table.Match(new Uri("/derived", UriKind.Relative)).Route);
    }

    [Fact]
    public void MissingPathFormattersAreRejectedAtRegistration()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .Map(
                "/stores/{storeId}",
                match => new TestRoutes.StoreRoute(match.Path("storeId")))));
    }

    [Fact]
    public void AmbiguousOverlappingTemplatesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .Map(
                "/products/{id}",
                match => new DocsRoute(match.Path("id")),
                format => format.PathParam("id", route => route.Path))
            .Map(
                "/products/{slug}",
                match => new DocsRoute(match.Path("slug")),
                format => format.PathParam("slug", route => route.Path))));
    }

    [Theory]
    [InlineData("int", "long")]
    [InlineData("int", "decimal")]
    [InlineData("long", "decimal")]
    [InlineData("bool", "alpha")]
    [InlineData("alpha", "guid")]
    public void OverlappingConstrainedTemplatesAreRejected(string leftConstraint, string rightConstraint)
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .Map(
                $"/values/{{value:{leftConstraint}}}",
                match => new LeftConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))
            .Map(
                $"/values/{{value:{rightConstraint}}}",
                match => new RightConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))));
    }

    [Fact]
    public void OverlappingConstrainedRequiredAndOptionalTemplatesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .Map(
                "/{id:int}",
                match => new RequiredProductIdRoute(match.Path<int>("id")),
                format => format.PathParam("id", route => route.ProductId))
            .Map(
                "/{id:int?}",
                match => new OptionalProductRoute(match.PathOptional<int>("id")),
                format => format.PathParam("id", route => route.ProductId))));
    }

    [Fact]
    public void DisjointConstrainedTemplatesCanCoexist()
    {
        var table = RouteTable.Create(routes => routes
            .Map(
                "/values/{value:int}",
                match => new LeftConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))
            .Map(
                "/values/{value:alpha}",
                match => new RightConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value)));

        Assert.Equal(new LeftConstrainedRoute("123"), table.Match(new Uri("/values/123", UriKind.Relative)).Route);
        Assert.Equal(new RightConstrainedRoute("northwind"), table.Match(new Uri("/values/northwind", UriKind.Relative)).Route);
    }

    [Fact]
    public void CustomConstraintMatchesValidValuesAndRejectsInvalidValues()
    {
        var table = RouteTable.Create(routes => routes
            .AddConstraint("slug", IsSlug)
            .Map(
                "/stores/{storeId:slug}",
                match => new SlugRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.Value)));

        Assert.Equal(new SlugRoute("northwind-1"), table.Match(new Uri("/stores/northwind-1", UriKind.Relative)).Route);
        Assert.False(table.Match(new Uri("/stores/Northwind", UriKind.Relative)).IsSuccess);
        Assert.Equal("/stores/northwind-1", table.Format(new SlugRoute("northwind-1")));
    }

    [Fact]
    public void CustomConstraintRejectsInvalidFormattedValues()
    {
        var table = RouteTable.Create(routes => routes
            .AddConstraint("slug", IsSlug)
            .Map(
                "/stores/{storeId:slug}",
                match => new SlugRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.Value)));

        Assert.Throws<InvalidOperationException>(() => table.Format(new SlugRoute("Northwind")));
    }

    [Fact]
    public void OptionalCustomConstrainedPathParamsCanBeOmittedOrPresent()
    {
        var table = RouteTable.Create(routes => routes
            .AddConstraint("culture", IsCultureCode)
            .Map(
                "/catalog/{culture:culture?}",
                match => new CultureRoute(match.PathOptional("culture")),
                format => format.PathParam("culture", route => route.Culture)));

        Assert.Equal(new CultureRoute(null), table.Match(new Uri("/catalog", UriKind.Relative)).Route);
        Assert.Equal(new CultureRoute("en-US"), table.Match(new Uri("/catalog/en-US", UriKind.Relative)).Route);
        Assert.False(table.Match(new Uri("/catalog/english", UriKind.Relative)).IsSuccess);
        Assert.Equal("/catalog", table.Format(new CultureRoute(null)));
        Assert.Equal("/catalog/en-US", table.Format(new CultureRoute("en-US")));
    }

    [Fact]
    public void CustomConstraintsAreBuilderScoped()
    {
        var table = RouteTable.Create(routes => routes
            .AddConstraint("slug", IsSlug)
            .Map(
                "/stores/{storeId:slug}",
                match => new SlugRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.Value)));

        Assert.Equal(new SlugRoute("northwind"), table.Match(new Uri("/stores/northwind", UriKind.Relative)).Route);
        Assert.Throws<ArgumentException>(() => RouteTemplate.Parse("/stores/{storeId:slug}"));
        Assert.Throws<ArgumentException>(() => RouteTable.Create(routes => routes.Map(
            "/stores/{storeId:slug}",
            match => new SlugRoute(match.Path("storeId")),
            format => format.PathParam("storeId", route => route.Value))));
    }

    [Fact]
    public void BuiltInConstraintsCannotBeRedefined()
    {
        Assert.Throws<ArgumentException>(() => RouteTable.Create(routes => routes.AddConstraint("int", _ => true)));
    }

    [Fact]
    public void OverlappingCustomConstraintsAreRejectedUnlessDeclaredDisjoint()
    {
        Assert.Throws<InvalidOperationException>(() => RouteTable.Create(routes => routes
            .AddConstraint("slug", IsSlug)
            .AddConstraint("sku", IsSku)
            .Map(
                "/values/{value:slug}",
                match => new LeftConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))
            .Map(
                "/values/{value:sku}",
                match => new RightConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))));

        var table = RouteTable.Create(routes => routes
            .AddConstraint("digits", value => value.Length > 0 && value.All(char.IsDigit), disjointWith: new[] { "letters" })
            .AddConstraint("letters", value => value.Length > 0 && value.All(char.IsLetter))
            .Map(
                "/values/{value:digits}",
                match => new LeftConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))
            .Map(
                "/values/{value:letters}",
                match => new RightConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value)));

        Assert.Equal(new LeftConstrainedRoute("123"), table.Match(new Uri("/values/123", UriKind.Relative)).Route);
        Assert.Equal(new RightConstrainedRoute("northwind"), table.Match(new Uri("/values/northwind", UriKind.Relative)).Route);
    }

    [Fact]
    public void CustomConstraintsDeclaredDisjointFromBuiltInsCanCoexist()
    {
        var table = RouteTable.Create(routes => routes
            .AddConstraint("slug", value => value.Length > 0 && value.All(ch => char.IsLower(ch) || ch == '-'), disjointWith: new[] { "int" })
            .Map(
                "/values/{value:int}",
                match => new LeftConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value))
            .Map(
                "/values/{value:slug}",
                match => new RightConstrainedRoute(match.Path("value")),
                format => format.PathParam("value", route => route.Value)));

        Assert.Equal(new LeftConstrainedRoute("123"), table.Match(new Uri("/values/123", UriKind.Relative)).Route);
        Assert.Equal(new RightConstrainedRoute("northwind"), table.Match(new Uri("/values/northwind", UriKind.Relative)).Route);
    }

    [Theory]
    [InlineData("/docs/{*path}/edit")]
    [InlineData("/catalog/{category?}/products")]
    [InlineData("/stores/{id:regex}")]
    [InlineData("/stores/{id}/{id}")]
    public void InvalidTemplatesAreRejected(string template)
    {
        Assert.ThrowsAny<Exception>(() => RouteTable.Create(routes => routes.Map(
            template,
            _ => new NewProductRoute(),
            format => format.PathParam("id", _ => "value").PathParam("path", _ => "value").PathParam("category", _ => "value"))));
    }

    private sealed record CatalogCategoryRoute(string StoreId, string? Category) : AppRoute;

    private sealed record StoreSectionRoute(string StoreId, string? Section) : AppRoute;

    private sealed record OptionalProductRoute(int? ProductId) : AppRoute;

    private sealed record RequiredProductIdRoute(int ProductId) : AppRoute;

    private sealed record ProductSlugRoute(string Slug) : AppRoute;

    private readonly record struct SlugValue(string Value);

    private sealed record CustomSlugRoute(SlugValue Slug) : AppRoute;

    private sealed record DocsRoute(string Path) : AppRoute;

    private sealed record DocsIndexRoute : AppRoute;

    private sealed record SearchRoute(string? LastTag, IReadOnlyList<string> Tags) : AppRoute;

    private sealed record ValueRoute(string Value) : AppRoute;

    private record BasePolymorphicRoute : AppRoute;

    private sealed record DerivedPolymorphicRoute : BasePolymorphicRoute;

    private sealed record ConventionProductRoute(string StoreId, int ProductId, string? Variant = null) : AppRoute;

    private sealed record ConventionDefaultedQueryRoute(string StoreId, int Page = 1) : AppRoute;

    private sealed record ConventionCollectionQueryRoute(
        int[]? ArrayValues = null,
        IEnumerable<int>? EnumerableValues = null,
        IReadOnlyCollection<int>? ReadOnlyCollectionValues = null,
        IReadOnlyList<int>? ReadOnlyListValues = null,
        ICollection<int>? CollectionValues = null,
        IList<int>? ListInterfaceValues = null,
        List<int>? ListValues = null) : AppRoute;

    private sealed record ConventionCustomCollectionRoute(
        IReadOnlyList<SlugValue>? Values = null) : AppRoute;

    private sealed record ConventionNullableCollectionRoute(
        IReadOnlyList<int?>? Values = null) : AppRoute;

    private sealed record ConventionNestedCollectionRoute(
        IReadOnlyList<string[]>? Values = null) : AppRoute;

    private sealed record ConventionMatrixCollectionRoute(
        int[,]? Values = null) : AppRoute;

    private sealed record ConventionSetCollectionRoute(
        HashSet<string>? Values = null) : AppRoute;

    private sealed record ConventionLegacyCollectionRoute(
        System.Collections.ArrayList? Values = null) : AppRoute;

    private sealed record MismatchedCollectionQueryRoute : AppRoute
    {
        public MismatchedCollectionQueryRoute(List<int>? values = null)
        {
            Values = values;
        }

        public IReadOnlyList<int>? Values { get; }
    }

    private sealed record ConventionQueryRoute(
        string StoreId,
        string? MissionId = null,
        string? CoverImageDraftId = null) : AppRoute;

    private sealed record ConventionAcronymQueryRoute(string Value, string? QRValue = null) : AppRoute;

    private sealed record RequiredReferenceQueryRoute(string StoreId, string Variant) : AppRoute;

    private sealed record RequiredValueQueryRoute(string StoreId, int Page) : AppRoute;

#nullable disable
    private sealed record ObliviousQueryRoute(string StoreId, string Variant) : AppRoute;
#nullable restore

    private sealed record MissingPathMemberRoute(string Id) : AppRoute;

    private sealed record SlugRoute(string Value) : AppRoute;

    private sealed record CultureRoute(string? Culture) : AppRoute;

    private sealed record LeftConstrainedRoute(string Value) : AppRoute;

    private sealed record RightConstrainedRoute(string Value) : AppRoute;

    private sealed record NewProductRoute : AppRoute;

    private sealed record NoUsableConstructorRoute(string Value, string Extra) : AppRoute;

    private sealed record ModuleRoute(string Value) : AppRoute;

    private sealed class ModuleRoutes : IRouteTableModule
    {
        public void MapRoutes(RouteTableBuilder routes)
        {
            routes.MapRoute<ModuleRoute>("/modules/{value}");
        }
    }

    private static readonly RouteMetadataKey<string> MissionIdMetadata = new("missionId");

    private static readonly RouteMetadataKey<string> CampaignMetadata = new("campaign");

    private static readonly RouteMetadataKey<int> RankMetadata = new("rank");

    private static bool IsSlug(string value)
    {
        return value.Length is > 0 and <= 80 &&
               value.All(ch => char.IsLower(ch) || char.IsDigit(ch) || ch == '-');
    }

    private static bool IsSku(string value)
    {
        return value.Length > 0 &&
               value.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '-');
    }

    private static bool IsCultureCode(string value)
    {
        return value.Length == 5 &&
               char.IsLower(value[0]) &&
               char.IsLower(value[1]) &&
               value[2] == '-' &&
               char.IsUpper(value[3]) &&
               char.IsUpper(value[4]);
    }
}
