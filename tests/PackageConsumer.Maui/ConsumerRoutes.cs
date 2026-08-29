using AdamE.AppNav;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Routing;
using Microsoft.Maui.Controls;

namespace PackageConsumer.Maui;

[AppNavRoute("/details/{id:int}")]
public sealed record DetailRoute(int Id) : AppRoute;

[MauiRoutePage(typeof(DetailRoute))]
public sealed class DetailPage : ContentPage
{
    public DetailPage(DetailRoute route)
    {
        Route = route;
    }

    public DetailRoute Route { get; }
}

internal static class GeneratedSurfaceProbe
{
    public static IRouteTableModule Routes => AppNavGenerated.RouteTableModule;

    public static IMauiRoutePageModule Pages => AppNavGenerated.MauiPageModule;
}
