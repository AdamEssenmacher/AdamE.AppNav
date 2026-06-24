using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Persistence;

public sealed class NavigationRestoreOptions
{
    public string? Reason { get; set; }

    public bool SaveAfterRestore { get; set; } = true;
}

public sealed record NavigationRestoreResult(
    bool Accepted,
    NavigationState? State,
    NavigationHistory? History,
    string? RejectionReason,
    IReadOnlyList<RouteDiagnostic> Diagnostics,
    bool Presented)
{
    public static NavigationRestoreResult AcceptedResult(
        NavigationState state,
        NavigationHistory history,
        bool presented,
        IReadOnlyList<RouteDiagnostic>? diagnostics = null)
    {
        return new NavigationRestoreResult(
            true,
            state,
            history,
            null,
            diagnostics ?? Array.Empty<RouteDiagnostic>(),
            presented);
    }

    public static NavigationRestoreResult Rejected(
        string reason,
        IReadOnlyList<RouteDiagnostic>? diagnostics = null)
    {
        return new NavigationRestoreResult(
            false,
            null,
            null,
            reason,
            diagnostics ?? Array.Empty<RouteDiagnostic>(),
            Presented: false);
    }
}

public interface INavigationRestorePolicy
{
    ValueTask<NavigationRestoreDecision> EvaluateAsync(
        NavigationRestoreContext context,
        CancellationToken cancellationToken = default);
}

public sealed record NavigationRestoreContext(
    NavigationSnapshot Snapshot,
    NavigationState RestoredState,
    NavigationHistory RestoredHistory,
    NavigationState CurrentState,
    string OperationId);

public sealed record NavigationRestoreDecision(bool Accepted, string? Reason)
{
    public static NavigationRestoreDecision Accept(string? reason = null)
    {
        return new NavigationRestoreDecision(true, reason);
    }

    public static NavigationRestoreDecision Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new NavigationRestoreDecision(false, reason);
    }
}
