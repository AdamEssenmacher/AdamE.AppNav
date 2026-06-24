namespace AdamE.MauiRouter.Navigation;

public sealed record BackNavigationResult(bool Handled, NavigationResult? NavigationResult = null)
{
    public static BackNavigationResult Unhandled { get; } = new(false);

    public static BackNavigationResult HandledBy(NavigationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new BackNavigationResult(true, result);
    }
}
