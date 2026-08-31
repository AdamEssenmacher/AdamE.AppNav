using AdamE.AppNav.Plans;

namespace AdamE.AppNav.Back;

/// <summary>
/// Provides the request, resolved planning context, and validated candidate plan to a back policy.
/// </summary>
public sealed class BackNavigationPolicyContext
{
    public BackNavigationPolicyContext(
        BackNavigationRequest request,
        BackNavigationContext navigationContext,
        NavigationPlan candidatePlan)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        NavigationContext = navigationContext ?? throw new ArgumentNullException(nameof(navigationContext));
        CandidatePlan = candidatePlan ?? throw new ArgumentNullException(nameof(candidatePlan));
    }

    public BackNavigationRequest Request { get; }

    public BackNavigationContext NavigationContext { get; }

    public NavigationPlan CandidatePlan { get; }
}
