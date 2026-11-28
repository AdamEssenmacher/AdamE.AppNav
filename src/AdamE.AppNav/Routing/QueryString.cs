using System.Runtime.InteropServices;

namespace AdamE.AppNav.Routing;

internal static class QueryString
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyQueryValueLists =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return EmptyQueryValueLists;

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string rawName = separator < 0 ? pair : pair[..separator];
            string rawValue = separator < 0 ? string.Empty : pair[(separator + 1)..];

            string name = Uri.UnescapeDataString(rawName.Replace("+", "%20", StringComparison.Ordinal));
            string value = Uri.UnescapeDataString(rawValue.Replace("+", "%20", StringComparison.Ordinal));

            ref IReadOnlyList<string>? values = ref CollectionsMarshal.GetValueRefOrAddDefault(result, name, out _);
            values ??= new List<string>();
            ((List<string>)values).Add(value);
        }

        // Replace the builders in place so QueryAll continues to expose fixed-length arrays.
        foreach ((string name, IReadOnlyList<string> values) in result)
            CollectionsMarshal.GetValueRefOrNullRef(result, name) = ((List<string>)values).ToArray();

        return result;
    }
}
