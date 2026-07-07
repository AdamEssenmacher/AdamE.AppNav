namespace AdamE.AppNav.Routing;

/// <summary>
/// Declares a route template for source-generated AppNav route-table registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AppNavRouteAttribute : Attribute
{
    public AppNavRouteAttribute(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        Template = template;
    }

    public string Template { get; }
}
