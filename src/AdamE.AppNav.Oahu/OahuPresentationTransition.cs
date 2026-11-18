using AdamE.AppNav.State;

namespace AdamE.AppNav.Oahu;

/// <summary>Visual context for one native presentation. Empty strings denote missing source or destination information.</summary>
public sealed record OahuPresentationTransition(
    string Operation, string SourceEntryId, string DestinationEntryId, string SourceKey,
    string SourceScreen, string DestinationScreen)
{
    internal static readonly OahuPresentationTransition Initial = new("initial", "", "", "", "", "");

    internal static OahuPresentationTransition Describe(OahuNavigationTarget? from, OahuNavigationTarget to,
        string? operation, string sourceKey, Func<RouteEntry, string>? screenName)
    {
        var before = from?.VisibleEntries.LastOrDefault();
        var after = to.VisibleEntries.LastOrDefault();
        var kind = operation ?? Classify(from, to);
        return new(kind, before?.Id ?? "", after?.Id ?? "", sourceKey,
            before is null ? "" : screenName?.Invoke(before) ?? "",
            after is null ? "" : screenName?.Invoke(after) ?? "");
    }

    private static string Classify(OahuNavigationTarget? from, OahuNavigationTarget to)
    {
        if (from is null || from.VisibleEntries.Count == 0) return "initial";
        if (from.WindowId != to.WindowId || from.RootId != to.RootId || from.IsBranchHost != to.IsBranchHost)
            return "replace";
        if (from.Branch != to.Branch) return "branch";
        if (from.ModalId != to.ModalId)
        {
            if (from.ModalId is null) return "modalPresent";
            if (to.ModalId is null) return "modalDismiss";
            return "replace";
        }
        var before = from.VisibleEntries;
        var after = to.VisibleEntries;
        if (after.Count == before.Count + 1 && after.Take(before.Count).SequenceEqual(before)) return "push";
        if (after.Count > 0 && after.Count < before.Count && before.Take(after.Count).SequenceEqual(after)) return "pop";
        return "replace";
    }
}
