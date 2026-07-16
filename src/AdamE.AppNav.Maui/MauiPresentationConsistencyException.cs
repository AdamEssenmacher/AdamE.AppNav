namespace AdamE.AppNav.Maui;

/// <summary>
/// Indicates that MAUI presentation failed and neither rollback nor full-state recovery could
/// establish a known native state.
/// </summary>
public sealed class MauiPresentationConsistencyException : Exception
{
    /// <summary>
    /// Initializes a presentation consistency failure.
    /// </summary>
    /// <param name="message">A stable description of the consistency failure.</param>
    /// <param name="innerException">The presentation, rollback, and recovery failures.</param>
    public MauiPresentationConsistencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
