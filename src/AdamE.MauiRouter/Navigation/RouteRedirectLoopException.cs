using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Navigation;

public sealed class RouteRedirectLoopException : Exception
{
    public RouteRedirectLoopException(
        RouterNavigationRequest initialRequest,
        RouterNavigationRequest lastRequest,
        IReadOnlyList<RouterNavigationRequest> redirects,
        string message)
        : base(message)
    {
        InitialRequest = initialRequest;
        LastRequest = lastRequest;
        Redirects = redirects.ToArray();
    }

    public RouterNavigationRequest InitialRequest { get; }

    public RouterNavigationRequest LastRequest { get; }

    public IReadOnlyList<RouterNavigationRequest> Redirects { get; }
}
