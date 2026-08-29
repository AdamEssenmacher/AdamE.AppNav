namespace AdamE.AppNav.Navigation;

/// <summary>
/// Identifies a deterministic AppNav configuration or topology failure that cannot succeed on retry.
/// </summary>
public sealed class AppNavigationConfigurationException : InvalidOperationException
{
    public AppNavigationConfigurationException(string message)
        : base(message)
    {
    }

    public AppNavigationConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
