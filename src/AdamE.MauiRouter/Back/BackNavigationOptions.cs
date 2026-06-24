namespace AdamE.MauiRouter.Back;

public sealed record BackNavigationOptions
{
    public static BackNavigationOptions Default { get; } = new();

    public bool ReturnToDefaultTabBeforeLeaving { get; init; } = true;

    public bool ReturnToDefaultFlyoutItemBeforeLeaving { get; init; } = true;
}
