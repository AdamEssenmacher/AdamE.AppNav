using AdamE.AppNav.Internal;

namespace AdamE.AppNav.State;

/// <summary>
/// Describes one named branch of a branch-host navigation node.
/// </summary>
/// <param name="Id">The stable structural identifier used to select and update the branch.</param>
/// <param name="Title">The display title a presentation layer may use for branch selection UI.</param>
/// <param name="Content">The independent navigation tree owned by the branch.</param>
public sealed record NavigationBranch(
    string Id,
    string Title,
    NavigationNode Content)
{
    /// <summary>
    /// Gets the stable structural identifier used to select and update the branch.
    /// </summary>
    public string Id
    {
        get;
        init => field = NavigationIdentity.RequiredId(value, nameof(Id));
    } = NavigationIdentity.RequiredId(Id, nameof(Id));

    /// <summary>
    /// Gets the display title a presentation layer may use for branch selection UI.
    /// </summary>
    public string Title
    {
        get;
        init => field = NavigationIdentity.RequiredText(value, nameof(Title));
    } = NavigationIdentity.RequiredText(Title, nameof(Title));

    /// <summary>
    /// Gets the independent navigation tree owned by the branch.
    /// </summary>
    public NavigationNode Content
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(Content));
    } = NavigationIdentity.Required(Content, nameof(Content));
}
