using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Maui.AppLinks;

public sealed class MauiRouterAppLinkOptions
{
    private Func<RouterNavigationRequest, bool> _shouldDispatch = static _ => true;

    public Func<RouterNavigationRequest, bool> ShouldDispatch
    {
        get => _shouldDispatch;
        set => _shouldDispatch = value ?? throw new ArgumentNullException(nameof(value));
    }
}
