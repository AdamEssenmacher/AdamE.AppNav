namespace AdamE.AppNav.Back;

/// <summary>
/// Asynchronously decides whether a candidate logical back plan may be presented.
/// </summary>
/// <remarks>
/// Policies run in registration order inside the router operation. They may await external work,
/// but must not re-enter the same router navigator.
/// </remarks>
public interface IBackNavigationPolicy
{
    ValueTask<BackNavigationPolicyDecision> EvaluateAsync(
        BackNavigationPolicyContext context,
        CancellationToken cancellationToken = default);
}
