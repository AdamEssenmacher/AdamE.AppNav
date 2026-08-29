using AdamE.AppNav;
using AdamE.AppNav.Planning;

namespace GettingStarted.Sample;

public static class GettingStartedNavigationModel
{
    // #region getting-started-model
    public static StackNavigationModel<AppRoute> Create()
    {
        return StackNavigationModel<AppRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "main-stack");
            builder.Map<HomeRoute>(recipe => recipe
                .EntryId(_ => "home")
                .ScopeKey(_ => "main"));
            builder.Map<DetailRoute>(recipe => recipe
                .EntryId(route => $"detail-{route.ItemId}")
                .ScopeKey(_ => "main")
                .Canonical((route, metadata) =>
                [
                    new StackRouteStep<AppRoute>(new HomeRoute()),
                    new StackRouteStep<AppRoute>(route, metadata)
                ]));
        });
    }
    // #endregion getting-started-model
}
