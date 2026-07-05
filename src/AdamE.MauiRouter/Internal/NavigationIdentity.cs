namespace AdamE.MauiRouter.Internal;

internal static class NavigationIdentity
{
    public static string RequiredId(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Navigation identifiers cannot be null, empty, or whitespace.", paramName);
        }

        return value;
    }

    public static string? OptionalId(string? value, string paramName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Navigation identifiers cannot be empty or whitespace.", paramName);
        }

        return value;
    }

    public static string RequiredText(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Navigation text values cannot be null, empty, or whitespace.", paramName);
        }

        return value;
    }

    public static T Required<T>(T? value, string paramName)
        where T : class
    {
        return value ?? throw new ArgumentNullException(paramName);
    }

    public static IReadOnlyList<T> RequiredList<T>(IEnumerable<T>? source, string paramName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source, paramName);
        return SnapshotList(source, paramName);
    }

    public static IReadOnlyList<T> OptionalList<T>(IEnumerable<T>? source, string paramName)
        where T : class
    {
        return source is null ? CollectionSnapshot.List<T>(null) : SnapshotList(source, paramName);
    }

    public static void EnsureNotEmpty<T>(
        IReadOnlyList<T> items,
        string paramName,
        string collectionName)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException($"{collectionName} must contain at least one item.", paramName);
        }
    }

    public static void EnsureUniqueIds<T>(
        IReadOnlyList<T> items,
        Func<T, string> idSelector,
        string paramName,
        string idName,
        string collectionName)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = idSelector(item);
            if (!ids.Add(id))
            {
                throw new ArgumentException(
                    $"{collectionName} cannot contain duplicate {idName} '{id}'.",
                    paramName);
            }
        }
    }

    private static IReadOnlyList<T> SnapshotList<T>(IEnumerable<T> source, string paramName)
        where T : class
    {
        var snapshot = CollectionSnapshot.List(source);
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i] is null)
            {
                throw new ArgumentException("Navigation state collections cannot contain null items.", paramName);
            }
        }

        return snapshot;
    }
}
