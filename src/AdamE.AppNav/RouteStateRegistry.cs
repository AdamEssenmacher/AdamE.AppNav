using AdamE.AppNav.Internal;

namespace AdamE.AppNav;

public sealed class RouteStateRegistry
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        CollectionSnapshot.MetadataDictionary(null);

    private readonly Dictionary<string, RouteStateRegistration> _registrations;

    internal RouteStateRegistry(IReadOnlyDictionary<string, RouteStateRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _registrations = registrations.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
    }

    public static RouteStateRegistry Create(Action<RouteStateRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new RouteStateRegistryBuilder();
        configure(builder);
        return builder.Build();
    }

    public IReadOnlyDictionary<string, object?> FilterKnown(IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Filter(metadata, static (_, _) => true);
    }

    public IReadOnlyDictionary<string, object?> FilterRestorable(IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Filter(metadata, static (_, registration) => registration.Lifetime == RouteStateLifetime.Restorable);
    }

    public AppRouteRequest Canonicalize(AppRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AppRouteRequest(
            request.Route,
            Filter(request.Metadata,
                static (_, registration) => registration.Lifetime == RouteStateLifetime.Canonical));
    }

    internal bool TryGetRegistration(string name, out RouteStateRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _registrations.TryGetValue(name, out registration!);
    }

    private IReadOnlyDictionary<string, object?> Filter(
        IReadOnlyDictionary<string, object?> metadata,
        Func<string, RouteStateRegistration, bool> predicate)
    {
        if (metadata.Count == 0)
            return EmptyMetadata;

        var filtered = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in metadata)
            if (_registrations.TryGetValue(pair.Key, out RouteStateRegistration? registration) &&
                predicate(pair.Key, registration))
                filtered[pair.Key] = pair.Value;

        return filtered.Count == 0
            ? EmptyMetadata
            : filtered;
    }
}

internal sealed record RouteStateRegistration(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    Type ValueType,
    RouteStateLifetime Lifetime);
