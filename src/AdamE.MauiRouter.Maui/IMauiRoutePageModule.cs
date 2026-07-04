namespace AdamE.MauiRouter.Maui;

/// <summary>
/// Groups related MAUI route-page registrations behind a reusable module.
/// </summary>
/// <remarks>
/// Page modules let applications organize route-to-page mappings by feature or
/// domain area while still using the normal <see cref="MauiRoutePageRegistry"/> API.
/// </remarks>
public interface IMauiRoutePageModule
{
    /// <summary>
    /// Adds this module's page mappings to the supplied page registry.
    /// </summary>
    /// <param name="pages">The page registry receiving the module's registrations.</param>
    void MapPages(MauiRoutePageRegistry pages);
}
