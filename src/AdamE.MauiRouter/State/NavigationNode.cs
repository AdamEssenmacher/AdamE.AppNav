using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.State;

/// <summary>
/// Identifies a platform-neutral node in the logical navigation tree.
/// </summary>
/// <param name="Id">The stable structural identifier of the node within its owning navigation surface.</param>
public abstract record NavigationNode(string Id)
{
    /// <summary>
    /// Gets the stable structural identifier of the node within its owning navigation surface.
    /// </summary>
    public string Id
    {
        get;
        init => field = NavigationIdentity.RequiredId(value, nameof(Id));
    } = NavigationIdentity.RequiredId(Id, nameof(Id));
}
