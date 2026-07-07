namespace AdamE.AppNav.Routing;

/// <summary>
/// Groups related route-table registrations behind a reusable module.
/// </summary>
/// <remarks>
/// Route table modules let applications organize URI-to-route mappings by feature
/// or domain area while still using the normal <see cref="RouteTableBuilder"/> API.
/// </remarks>
public interface IRouteTableModule
{
    /// <summary>
    /// Adds this module's route definitions to the supplied route table builder.
    /// </summary>
    /// <param name="routes">The route table builder receiving the module's registrations.</param>
    void MapRoutes(RouteTableBuilder routes);
}
