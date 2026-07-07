namespace AdamE.MauiRouter.Maui.Tests;

public sealed class NavigationAssertionException : Exception
{
    public NavigationAssertionException(string message)
        : base(message)
    {
    }
}
