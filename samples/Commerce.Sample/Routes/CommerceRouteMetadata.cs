using AdamE.AppNav;
using AdamE.AppNav.Routing;

namespace Commerce.Sample.Routes;

public static class CommerceRouteMetadata
{
    public static RouteMetadataKey<string> Campaign { get; } = new("campaign");

    public static RouteStateRegistry RouteStateRegistry { get; } = RouteStateRegistry.Create(
        static builder => builder.Canonical(Campaign));
}
