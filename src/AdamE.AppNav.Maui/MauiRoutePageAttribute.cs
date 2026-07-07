namespace AdamE.AppNav.Maui;

/// <summary>
/// Declares a MAUI page mapping for source-generated AppNav page registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MauiRoutePageAttribute : Attribute
{
    public MauiRoutePageAttribute(Type routeType)
    {
        RouteType = routeType ?? throw new ArgumentNullException(nameof(routeType));
    }

    public Type RouteType { get; }

    /// <summary>
    /// Gets or sets whether the generated page module should resolve the page itself from services.
    /// </summary>
    public bool FromServices { get; set; }
}
