using AdamE.MauiRouter.Routing;

namespace AdamE.MauiRouter;

public sealed class RouteStateRegistryBuilder
{
    private readonly Dictionary<string, RouteStateRegistration> _registrations = new(StringComparer.Ordinal);

    public RouteStateRegistryBuilder Canonical<TValue>(RouteMetadataKey<TValue> key)
    {
        return Register(key, RouteStateLifetime.Canonical);
    }

    public RouteStateRegistryBuilder Restorable<TValue>(RouteMetadataKey<TValue> key)
    {
        return Register(key, RouteStateLifetime.Restorable);
    }

    public RouteStateRegistryBuilder Ephemeral<TValue>(RouteMetadataKey<TValue> key)
    {
        return Register(key, RouteStateLifetime.Ephemeral);
    }

    internal RouteStateRegistry Build()
    {
        return new RouteStateRegistry(_registrations);
    }

    private RouteStateRegistryBuilder Register<TValue>(
        RouteMetadataKey<TValue> key,
        RouteStateLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_registrations.TryAdd(key.Name, new RouteStateRegistration(key.Name, typeof(TValue), lifetime)))
        {
            throw new InvalidOperationException(
                $"Route state key '{key.Name}' is already registered.");
        }

        return this;
    }
}
