using System.Diagnostics;

namespace AdamE.MauiRouter.Diagnostics;

public static class NavigationActivitySources
{
    public const string DefaultName = "AdamE.MauiRouter";

    public static ActivitySource Default { get; } = new(DefaultName);
}
