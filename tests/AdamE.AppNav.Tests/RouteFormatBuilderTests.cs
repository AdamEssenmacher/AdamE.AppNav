using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class RouteFormatBuilderTests
{
    [Theory]
    [InlineData("value")]
    [InlineData("VALUE")]
    public void PathParamRejectsDuplicateNamesAndKeepsTheFirstFormatter(string duplicateName)
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        builder.PathParam("value", static route => route.PathValue);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            builder.PathParam(duplicateName, static route => route.SecondaryValue));

        Assert.Equal(
            $"Path formatter for path parameter '{duplicateName}' is already registered for route type " +
            $"'{typeof(FormatterRoute).FullName}'.",
            exception.Message);
        Assert.Equal("/first", Format(builder, "/{value}"));
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("FILTER")]
    public void QueryParamRejectsDuplicateNamesAndKeepsTheFirstFormatter(string duplicateName)
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        builder.QueryParam("filter", static route => route.PrimaryValue);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            builder.QueryParam(duplicateName, static route => route.SecondaryValue));

        AssertQueryDuplicate(exception, duplicateName);
        Assert.Equal("/values?filter=first", Format(builder));
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("FILTER")]
    public void QueryMetadataRejectsAQueryParamNameAndKeepsTheQueryFormatter(string duplicateName)
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        builder.QueryParam("filter", static route => route.PrimaryValue);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            builder.QueryMetadata(new RouteMetadataKey<string>(duplicateName)));

        AssertQueryDuplicate(exception, duplicateName);
        Assert.Equal("/values?filter=first", Format(builder));
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("FILTER")]
    public void QueryParamRejectsAMetadataQueryNameAndKeepsTheMetadataFormatter(string duplicateName)
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        var metadataKey = new RouteMetadataKey<string>("filter");
        builder.QueryMetadata(metadataKey);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            builder.QueryParam(duplicateName, static route => route.SecondaryValue));

        AssertQueryDuplicate(exception, duplicateName);
        Assert.Equal(
            "/values?filter=metadata-first",
            Format(builder, metadata: new Dictionary<string, object?> { [metadataKey.Name] = "metadata-first" }));
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("FILTER")]
    public void QueryMetadataRejectsDuplicateNamesAndKeepsTheFirstFormatter(string duplicateName)
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        var metadataKey = new RouteMetadataKey<string>("filter");
        builder.QueryMetadata(metadataKey);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            builder.QueryMetadata(new RouteMetadataKey<string>(duplicateName)));

        AssertQueryDuplicate(exception, duplicateName);
        Assert.Equal(
            "/values?filter=metadata-first",
            Format(builder, metadata: new Dictionary<string, object?> { [metadataKey.Name] = "metadata-first" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PathAndQueryFormatterNamesRemainIndependent(bool pathFirst)
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        if (pathFirst)
        {
            builder.PathParam("value", static route => route.PathValue);
            builder.QueryParam("VALUE", static route => route.PrimaryValue);
        }
        else
        {
            builder.QueryParam("VALUE", static route => route.PrimaryValue);
            builder.PathParam("value", static route => route.PathValue);
        }

        Assert.Equal("/first?VALUE=first", Format(builder, "/{value}"));
    }

    [Fact]
    public void ACollectionQueryFormatterCanEmitRepeatedValues()
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        builder.QueryParam("tag", static route => route.Tags);

        Assert.Equal("/values?tag=blue&tag=green", Format(builder));
    }

    [Fact]
    public void PathParamValidatesArgumentsBeforeCheckingForADuplicate()
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        builder.PathParam("value", static route => route.PathValue);
        Func<FormatterRoute, object?> nullValue = null!;

        ArgumentException nameException = Assert.Throws<ArgumentException>(() =>
            builder.PathParam(" ", nullValue));
        ArgumentNullException valueException = Assert.Throws<ArgumentNullException>(() =>
            builder.PathParam("VALUE", nullValue));

        Assert.Equal("name", nameException.ParamName);
        Assert.Equal("value", valueException.ParamName);
    }

    [Fact]
    public void QueryParamValidatesArgumentsBeforeCheckingForADuplicate()
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        builder.QueryParam("filter", static route => route.PrimaryValue);
        Func<FormatterRoute, object?> nullValue = null!;

        ArgumentException nameException = Assert.Throws<ArgumentException>(() =>
            builder.QueryParam(" ", nullValue));
        ArgumentNullException valueException = Assert.Throws<ArgumentNullException>(() =>
            builder.QueryParam("FILTER", nullValue));

        Assert.Equal("name", nameException.ParamName);
        Assert.Equal("value", valueException.ParamName);
    }

    [Fact]
    public void QueryMetadataValidatesItsKeyBeforeCheckingForADuplicate()
    {
        var builder = new RouteFormatBuilder<FormatterRoute>();
        builder.QueryParam("filter", static route => route.PrimaryValue);

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            builder.QueryMetadata<string>(null!));

        Assert.Equal("key", exception.ParamName);
    }

    private static void AssertQueryDuplicate(InvalidOperationException exception, string duplicateName)
    {
        Assert.Equal(
            $"Query binding for query parameter '{duplicateName}' is already registered for route type " +
            $"'{typeof(FormatterRoute).FullName}'.",
            exception.Message);
    }

    private static string Format(
        RouteFormatBuilder<FormatterRoute> builder,
        string template = "/values",
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return builder.Format(
            new FormatterRoute("first", "first", "second", ["blue", "green"]),
            RouteTemplate.Parse(template),
            new RouteValueCodecCollection().Build(),
            metadata);
    }

    private sealed record FormatterRoute(
        string PathValue,
        string PrimaryValue,
        string SecondaryValue,
        IReadOnlyList<string> Tags) : AppRoute;
}
