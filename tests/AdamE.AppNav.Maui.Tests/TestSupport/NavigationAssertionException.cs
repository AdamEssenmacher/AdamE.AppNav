namespace AdamE.AppNav.Maui.Tests;

public sealed class NavigationAssertionException : Exception
{
    public NavigationAssertionException(string message)
        : base(message)
    {
    }
}
