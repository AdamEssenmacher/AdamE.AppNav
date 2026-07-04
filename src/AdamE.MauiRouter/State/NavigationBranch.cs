namespace AdamE.MauiRouter.State;

/// <summary>
/// Describes one named branch of a branch-host navigation node.
/// </summary>
/// <param name="Id">The stable identifier used to select and update the branch.</param>
/// <param name="Title">The display title a presentation layer may use for branch selection UI.</param>
/// <param name="Content">The independent navigation tree owned by the branch.</param>
public sealed record NavigationBranch(
    string Id,
    string Title,
    NavigationNode Content);
