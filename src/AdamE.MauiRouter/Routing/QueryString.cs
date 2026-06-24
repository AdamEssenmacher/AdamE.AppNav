namespace AdamE.MauiRouter.Routing;

internal static class QueryString
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawName = separator < 0 ? pair : pair[..separator];
            var rawValue = separator < 0 ? string.Empty : pair[(separator + 1)..];

            var name = Uri.UnescapeDataString(rawName.Replace("+", "%20", StringComparison.Ordinal));
            var value = Uri.UnescapeDataString(rawValue.Replace("+", "%20", StringComparison.Ordinal));

            if (!result.TryGetValue(name, out var values))
            {
                values = new List<string>();
                result[name] = values;
            }

            values.Add(value);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
