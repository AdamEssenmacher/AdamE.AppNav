using global::Oahu;

namespace AdamE.AppNav.Oahu.Hosting;

public sealed record ScreenEntry(string EntryId, long Mount);
public sealed record BranchStack(string Branch, string Title, IReadOnlyList<ScreenEntry> Entries);
public sealed record PresentationTransition(string Operation, string SourceEntryId, string DestinationEntryId,
    string SourceKey, string SourceScreen, string DestinationScreen);
public sealed record ShellTarget(
    long Revision, string SelectedBranch, bool IsBranchHost, IReadOnlyList<BranchStack> Branches,
    string ModalId, IReadOnlyList<ScreenEntry> ModalEntries, PresentationTransition Transition);
public sealed record NativeObservation(
    long Revision, string Branch, IReadOnlyList<string> Entries, string ModalId, IReadOnlyList<string> ModalEntries);

[OahuContract]
public interface INavigationShellViewModel
{
    IOahuState<ShellTarget> Target { get; }
    void Report(NativeObservation observation);
}
