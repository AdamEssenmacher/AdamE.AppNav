namespace AdamE.MauiRouter.Maui;

/// <summary>
/// Describes how the MAUI presenter should materialize a platform-neutral branch host.
/// </summary>
public enum MauiBranchHostPresentation
{
    /// <summary>
    /// Materialize the branch host as a <see cref="Microsoft.Maui.Controls.TabbedPage"/>.
    /// </summary>
    Tabs,

    /// <summary>
    /// Materialize the branch host as a <see cref="Microsoft.Maui.Controls.FlyoutPage"/>.
    /// </summary>
    Flyout
}
