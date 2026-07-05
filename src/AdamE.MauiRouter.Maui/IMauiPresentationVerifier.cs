using AdamE.MauiRouter.State;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal interface IMauiPresentationVerifier
{
    MauiPresentationVerificationMismatch? Verify(MauiPresentationVerificationContext context);
}

internal sealed record MauiPresentationVerificationContext(
    NavigationState TargetState,
    Page? CurrentPage,
    Window? AttachedWindow,
    MauiRoutePresentationOptions PresentationOptions);

internal sealed record MauiPresentationVerificationMismatch(
    string Path,
    string Expected,
    string Actual);
