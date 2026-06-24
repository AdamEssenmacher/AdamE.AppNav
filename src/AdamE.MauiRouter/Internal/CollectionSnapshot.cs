using System.Collections.ObjectModel;

namespace AdamE.MauiRouter.Internal;

internal static class CollectionSnapshot
{
    public static IReadOnlyList<T> List<T>(IEnumerable<T>? source)
    {
        return source is null
            ? Array.AsReadOnly(Array.Empty<T>())
            : Array.AsReadOnly(source.ToArray());
    }

    public static IReadOnlyDictionary<string, object?> Dictionary(
        IReadOnlyDictionary<string, object?>? source)
    {
        return source is null
            ? EmptyDictionary.Value
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(source, StringComparer.Ordinal));
    }

    public static IReadOnlyDictionary<string, object?>? NullableDictionary(
        IReadOnlyDictionary<string, object?>? source)
    {
        return source is null ? null : Dictionary(source);
    }

    private static class EmptyDictionary
    {
        public static readonly IReadOnlyDictionary<string, object?> Value =
            new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}
