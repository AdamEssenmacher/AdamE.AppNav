using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Policies;

public sealed record NavigationAccessDecision
{
    private NavigationAccessDecision(
        bool isAllowed,
        RouterNavigationRequest? redirectRequest,
        bool deferOriginalRequest)
    {
        if (!isAllowed && redirectRequest is null)
            throw new ArgumentException("Denied navigation decisions must provide a redirect request.",
                nameof(redirectRequest));

        if (isAllowed && (redirectRequest is not null || deferOriginalRequest))
            throw new ArgumentException("Allowed navigation decisions cannot include redirect or defer behavior.");

        IsAllowed = isAllowed;
        RedirectRequest = redirectRequest;
        DeferOriginalRequest = deferOriginalRequest;
    }

    public bool IsAllowed { get; }

    public RouterNavigationRequest? RedirectRequest { get; }

    public bool DeferOriginalRequest { get; }

    public static NavigationAccessDecision Allow()
    {
        return new NavigationAccessDecision(true, null, false);
    }

    public static NavigationAccessDecision Redirect(RouterNavigationRequest redirectRequest)
    {
        ArgumentNullException.ThrowIfNull(redirectRequest);
        return new NavigationAccessDecision(false, redirectRequest, false);
    }

    public static NavigationAccessDecision DeferAndRedirect(RouterNavigationRequest redirectRequest)
    {
        ArgumentNullException.ThrowIfNull(redirectRequest);
        return new NavigationAccessDecision(false, redirectRequest, true);
    }
}
