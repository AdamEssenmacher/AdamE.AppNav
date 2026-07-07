using System.Collections.ObjectModel;

namespace AdamE.AppNav.Routing;

public sealed record RouteDiagnostic(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Data = null)
{
    public IReadOnlyDictionary<string, object?> Data { get; } = SnapshotData(Data);

    private static IReadOnlyDictionary<string, object?> SnapshotData(IReadOnlyDictionary<string, object?>? data)
    {
        return data is null || data.Count == 0
            ? EmptyData.Value
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(data, StringComparer.Ordinal));
    }

    private static class EmptyData
    {
        public static readonly IReadOnlyDictionary<string, object?> Value =
            new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}
