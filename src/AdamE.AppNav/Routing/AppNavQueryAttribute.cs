namespace AdamE.AppNav.Routing;

/// <summary>
/// Declares a route property that should be bound to a query parameter by source-generated route registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class AppNavQueryAttribute : Attribute
{
    public AppNavQueryAttribute(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        PropertyName = propertyName;
    }

    public string PropertyName { get; }

    /// <summary>
    /// Gets or sets the query parameter name. When omitted, AppNav infers the camel-case property name.
    /// </summary>
    public string? Name { get; set; }

    public bool OmitWhenNull { get; set; } = true;
}
