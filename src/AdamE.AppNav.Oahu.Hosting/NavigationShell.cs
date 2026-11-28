using global::Oahu;

namespace AdamE.AppNav.Oahu.Hosting;

public sealed record ScreenEntry(string EntryId, long Mount);
public sealed record BranchStack(string Branch, string Title, IReadOnlyList<ScreenEntry> Entries);
public sealed record PresentationTransition(string Operation, string SourceEntryId, string DestinationEntryId,
    string SourceKey, string SourceScreen, string DestinationScreen);

/// <summary>A split at the window root. Its primary column is the target's only branch. Empty strings mean no
/// split, or no secondary column. <paramref name="CompactColumn"/> is <c>primary</c> or <c>secondary</c>.</summary>
public sealed record SplitColumns(string SplitId, string SecondaryId, IReadOnlyList<ScreenEntry> SecondaryEntries,
    string CompactColumn);

public sealed record ShellTarget(
    long Revision, string SelectedBranch, bool IsBranchHost, IReadOnlyList<BranchStack> Branches,
    string ModalId, IReadOnlyList<ScreenEntry> ModalEntries, PresentationTransition Transition, SplitColumns Split);

/// <summary>A settled native reading. For a split, <paramref name="Branch"/> and <paramref name="Entries"/> are the
/// primary column, and <paramref name="CompactColumn"/> is <c>primary</c> or <c>secondary</c>; it is empty without
/// a split.</summary>
public sealed record NativeObservation(
    long Revision, string Branch, IReadOnlyList<string> Entries, string ModalId, IReadOnlyList<string> ModalEntries,
    IReadOnlyList<string> SecondaryEntries, string CompactColumn);

[OahuContract]
public interface INavigationShellViewModel
{
    IOahuState<ShellTarget> Target { get; }
    Task Report(NativeObservation observation);
}
