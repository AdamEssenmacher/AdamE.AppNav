using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Navigates native MAUI pages that are presentation details of the current logical route.
/// </summary>
/// <remarks>
/// Route-owned pages participate in native platform back navigation but do not create route
/// entries or logical navigation history. The current route must be hosted by a router-owned
/// <see cref="NavigationPage"/>.
/// </remarks>
public interface IMauiRoutePresentationNavigator
{
    /// <summary>
    /// Pushes a page owned by the current logical route.
    /// </summary>
    /// <typeparam name="TPage">The registered MAUI page type to resolve and push.</typeparam>
    /// <param name="key">A nonblank key that uniquely identifies the page within the owning route segment.</param>
    /// <param name="options">Optional page creation and presentation behavior.</param>
    /// <param name="cancellationToken">A token that can cancel before the native operation begins.</param>
    /// <returns>A value task that completes when the native page has been pushed.</returns>
    ValueTask PushAsync<TPage>(
        string key,
        MauiRoutePresentationPageOptions? options = null,
        CancellationToken cancellationToken = default)
        where TPage : Page;

    /// <summary>
    /// Pops the top page when it is owned by the current logical route.
    /// </summary>
    /// <param name="animated">Whether to use the platform-native pop animation.</param>
    /// <param name="cancellationToken">A token that can cancel before the native operation begins.</param>
    /// <returns>
    /// <see langword="true"/> when a route-owned page was popped; otherwise,
    /// <see langword="false"/> when the logical route page is already on top.
    /// </returns>
    ValueTask<bool> PopAsync(
        bool animated = true,
        CancellationToken cancellationToken = default);
}
