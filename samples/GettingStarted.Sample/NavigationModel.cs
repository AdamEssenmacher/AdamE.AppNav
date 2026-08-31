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
            // These app-defined stable IDs identify the canonical surface.
            // "main" becomes WindowNode.Id and matches startup.Start(window, "main").
            // "main-stack" becomes StackNode.Id for the NavigationPage-backed stack.
            builder.CanonicalSurface("main", "main-stack");

            // Home has one stable entry on the shared "main" stack scope.
            builder.Map<HomeRoute>(recipe => recipe
                .EntryId(_ => "home")
                .ScopeKey(_ => "main"));

            // Each item gets a distinct entry. Canonical navigation rebuilds
            // the complete Home -> Detail stack from any previous state.
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
