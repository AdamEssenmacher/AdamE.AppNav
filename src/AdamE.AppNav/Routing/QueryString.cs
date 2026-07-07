namespace AdamE.AppNav.Routing;

internal static class QueryString
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string rawName = separator < 0 ? pair : pair[..separator];
            string rawValue = separator < 0 ? string.Empty : pair[(separator + 1)..];

            string name = Uri.UnescapeDataString(rawName.Replace("+", "%20", StringComparison.Ordinal));
            string value = Uri.UnescapeDataString(rawValue.Replace("+", "%20", StringComparison.Ordinal));

            if (!result.TryGetValue(name, out List<string>? values))
            {
                values = [];
                result[name] = values;
            }

            values.Add(value);
        }

        return result.ToDictionary(
            pair => pair.Key, IReadOnlyList<string> (pair) => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
