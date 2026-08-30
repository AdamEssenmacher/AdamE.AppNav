using AdamE.AppNav.Navigation;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Configures the router navigator owned by the MAUI AppNav runtime.
/// </summary>
public sealed class AppNavNavigatorOptions
{
    /// <summary>
    /// Gets or sets the factory used to select a fallback route when URI route matching reports an unmatched route.
    /// </summary>
    public Func<NavigationFallbackContext, AppRoute?>? FallbackRouteFactory { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of request-target redirects allowed during one navigation operation.
    /// </summary>
    public int MaxRedirects { get; set; } = 16;

    /// <summary>
    /// Gets or sets the maximum number of entries retained in logical navigation history.
    /// </summary>
    public int MaxHistoryEntries { get; set; } = 128;

    internal void Validate()
    {
        if (MaxRedirects < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRedirects),
                MaxRedirects,
                "MaxRedirects cannot be negative.");
        }

        if (MaxHistoryEntries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxHistoryEntries),
                MaxHistoryEntries,
                "MaxHistoryEntries cannot be negative.");
        }
    }
}
