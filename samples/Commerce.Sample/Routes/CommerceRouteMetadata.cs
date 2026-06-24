using AdamE.MauiRouter;
using AdamE.MauiRouter.Routing;

namespace Commerce.Sample.Routes;

public static class CommerceRouteMetadata
{
    public static RouteMetadataKey<string> Campaign { get; } = new("campaign");

    public static RouteStateRegistry RouteStateRegistry { get; } = RouteStateRegistry.Create(
        static builder => builder.Canonical(Campaign));
}
