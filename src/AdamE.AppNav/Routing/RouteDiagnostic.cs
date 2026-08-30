using AdamE.AppNav.Internal;

namespace AdamE.AppNav.Routing;

public sealed record RouteDiagnostic(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Data = null)
{
    public IReadOnlyDictionary<string, object?> Data { get; } = CollectionSnapshot.MetadataDictionary(Data);
}
