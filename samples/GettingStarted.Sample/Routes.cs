using AdamE.AppNav;
using AdamE.AppNav.Routing;

namespace GettingStarted.Sample;

// #region getting-started-routes
[AppNavRoute("/home")]
public sealed record HomeRoute : AppRoute;

[AppNavRoute("/details/{itemId:int}")]
public sealed record DetailRoute(int ItemId) : AppRoute;
// #endregion getting-started-routes
