using System.Collections.ObjectModel;

namespace AdamE.AppNav.Internal;

/// <summary>
/// Creates defensive snapshots for collections accepted by immutable router model types.
/// </summary>
internal static class CollectionSnapshot
{
    /// <summary>
    /// Copies a sequence into a read-only list so later source collection mutations cannot
    /// affect the receiving router model.
    /// </summary>
    public static IReadOnlyList<T> List<T>(IEnumerable<T>? source)
    {
        return source is null
            ? Array.AsReadOnly(Array.Empty<T>())
            : Array.AsReadOnly(source.ToArray());
    }

    /// <summary>
    /// Copies route metadata into an ordinal string-keyed read-only dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> MetadataDictionary(
        IReadOnlyDictionary<string, object?>? source)
    {
        return source is null
            ? EmptyMetadataDictionary.Value
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(source, StringComparer.Ordinal));
    }

    /// <summary>
    /// Copies optional route metadata while preserving <see langword="null"/> when metadata
    /// absence is semantically different from an empty dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? NullableMetadataDictionary(
        IReadOnlyDictionary<string, object?>? source)
    {
        return source is null ? null : MetadataDictionary(source);
    }

    private static class EmptyMetadataDictionary
    {
        public static readonly IReadOnlyDictionary<string, object?> Value =
            new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}
