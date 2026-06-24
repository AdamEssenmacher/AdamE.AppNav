namespace AdamE.MauiRouter.Routing;

public sealed record RouteDiagnostic(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Data = null)
{
    public IReadOnlyDictionary<string, object?> Data { get; init; } =
        Data ?? EmptyData.Value;

    private static class EmptyData
    {
        public static readonly IReadOnlyDictionary<string, object?> Value = new Dictionary<string, object?>();
    }
}
