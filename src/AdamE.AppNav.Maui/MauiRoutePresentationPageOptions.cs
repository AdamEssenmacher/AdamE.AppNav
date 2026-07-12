namespace AdamE.AppNav.Maui;

/// <summary>
/// Configures creation and native presentation of a route-owned MAUI page.
/// </summary>
public sealed record MauiRoutePresentationPageOptions
{
    /// <summary>
    /// Gets whether the native push uses the platform transition animation.
    /// </summary>
    public bool Animated { get; init; } = true;

    /// <summary>
    /// Gets whether the page receives the owning route page's binding context.
    /// </summary>
    /// <remarks>
    /// Disable this when the page establishes an independent binding context. An inherited
    /// binding context is cleared when the route-owned page is released.
    /// </remarks>
    public bool InheritBindingContext { get; init; } = true;
}
