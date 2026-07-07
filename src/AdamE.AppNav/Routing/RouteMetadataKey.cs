namespace AdamE.AppNav.Routing;

public sealed record RouteMetadataKey<TValue>
{
    public RouteMetadataKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    internal static Type ValueType => typeof(TValue);
}
