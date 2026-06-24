namespace AdamE.MauiRouter.Testing;

public sealed class NavigationAssertionException : Exception
{
    public NavigationAssertionException(string message)
        : base(message)
    {
    }
}
