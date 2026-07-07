using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Maui.AppLinks;

public sealed class AppNavAppLinkOptions
{
    private Func<RouterNavigationRequest, bool> _shouldDispatch = static _ => true;

    public Func<RouterNavigationRequest, bool> ShouldDispatch
    {
        get => _shouldDispatch;
        set => _shouldDispatch = value ?? throw new ArgumentNullException(nameof(value));
    }
}
