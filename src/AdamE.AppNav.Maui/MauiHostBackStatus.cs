namespace AdamE.AppNav.Maui;

/// <summary>
/// Identifies the result of an opt-in MAUI host-back dispatch.
/// </summary>
public enum MauiHostBackStatus
{
    Unhandled = 0,
    PresentationPagePopped = 1,
    Completed = 2,
    Canceled = 3
}
