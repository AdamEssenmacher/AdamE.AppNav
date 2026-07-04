using System.Diagnostics;

namespace AdamE.MauiRouter.Diagnostics;

/// <summary>
/// Provides the <see cref="ActivitySource"/> instances used by MauiRouter navigation diagnostics.
/// </summary>
public static class NavigationActivitySources
{
    /// <summary>
    /// The default activity source name used for router operations.
    /// </summary>
    public const string DefaultName = "AdamE.MauiRouter";

    /// <summary>
    /// Gets the default activity source used to create navigation activities.
    /// </summary>
    public static ActivitySource Default { get; } = new(DefaultName);
}
