using AdamE.AppNav.Navigation;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Describes the terminal result of an opt-in MAUI host-back dispatch.
/// </summary>
public readonly record struct MauiHostBackResult
{
    private MauiHostBackResult(MauiHostBackStatus status, NavigationResult? navigationResult)
    {
        if (status == MauiHostBackStatus.Completed && navigationResult is null)
            throw new ArgumentNullException(nameof(navigationResult));
        if (status != MauiHostBackStatus.Completed && navigationResult is not null)
            throw new ArgumentException("Only a completed host-back result can carry a navigation result.", nameof(navigationResult));

        Status = status;
        NavigationResult = navigationResult;
    }

    public MauiHostBackStatus Status { get; }

    public NavigationResult? NavigationResult { get; }

    public static MauiHostBackResult PresentationPagePopped { get; } =
        new(MauiHostBackStatus.PresentationPagePopped, null);

    public static MauiHostBackResult Canceled { get; } = new(MauiHostBackStatus.Canceled, null);

    public static MauiHostBackResult Unhandled { get; } = new(MauiHostBackStatus.Unhandled, null);

    public static MauiHostBackResult CompletedBy(NavigationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new MauiHostBackResult(MauiHostBackStatus.Completed, result);
    }
}
