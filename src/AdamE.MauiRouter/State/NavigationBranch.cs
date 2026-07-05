using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.State;

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
    private readonly string _id = NavigationIdentity.RequiredId(Id, nameof(Id));
    private readonly string _title = NavigationIdentity.RequiredText(Title, nameof(Title));
    private readonly NavigationNode _content = NavigationIdentity.Required(Content, nameof(Content));

    /// <summary>
    /// Gets the stable structural identifier used to select and update the branch.
    /// </summary>
    public string Id
    {
        get => _id;
        init => _id = NavigationIdentity.RequiredId(value, nameof(Id));
    }

    /// <summary>
    /// Gets the display title a presentation layer may use for branch selection UI.
    /// </summary>
    public string Title
    {
        get => _title;
        init => _title = NavigationIdentity.RequiredText(value, nameof(Title));
    }

    /// <summary>
    /// Gets the independent navigation tree owned by the branch.
    /// </summary>
    public NavigationNode Content
    {
        get => _content;
        init => _content = NavigationIdentity.Required(value, nameof(Content));
    }
}
