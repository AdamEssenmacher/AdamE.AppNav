namespace AdamE.MauiRouter.Maui;

internal sealed class MauiRoutePresentationOptions
{
    public MauiRoutePageRegistry Pages { get; } = new();

    public MauiNavigationTransitionRegistry Transitions { get; } = new();

    public bool UseScopedPages { get; set; } = true;
}
