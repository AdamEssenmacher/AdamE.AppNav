namespace AdamE.MauiRouter.Routing;

public sealed record RouteMetadataKey<TValue>
{
    public RouteMetadataKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}
