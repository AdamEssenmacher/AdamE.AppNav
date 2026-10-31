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

    /// <summary>
    /// Gets or sets how long the runtime's disposal waits for route-entry leases before it writes a warning naming
    /// what holds them. The default is five seconds; <see cref="Timeout.InfiniteTimeSpan"/> turns the warning off.
    /// </summary>
    public TimeSpan EntryLeaseShutdownWarningDelay { get; set; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (MaxRedirects < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRedirects),
                MaxRedirects,
                "MaxRedirects cannot be negative.");
        }

        if (EntryLeaseShutdownWarningDelay != Timeout.InfiniteTimeSpan &&
            (EntryLeaseShutdownWarningDelay <= TimeSpan.Zero ||
             EntryLeaseShutdownWarningDelay.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(EntryLeaseShutdownWarningDelay),
                EntryLeaseShutdownWarningDelay,
                "EntryLeaseShutdownWarningDelay must be positive and at most about 49 days, or " +
                "Timeout.InfiniteTimeSpan to turn the warning off.");
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
