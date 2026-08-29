using AdamE.AppNav.Internal;

namespace AdamE.AppNav.State;

/// <summary>
/// Identifies a platform-neutral node in the logical navigation tree.
/// </summary>
public abstract record NavigationNode
{
    private protected NavigationNode(string id)
    {
        Id = id;
    }

    // Non-sealed records are required by C# to expose a protected copy constructor.
    // This private-protected abstract member closes that otherwise-derivable path:
    // only node records in this assembly can provide the required override.
    private protected abstract void SealNodeType();

    /// <summary>
    /// Gets the stable structural identifier of the node within its owning navigation surface.
    /// </summary>
    public string Id
    {
        get;
        init => field = NavigationIdentity.RequiredId(value, nameof(Id));
    } = null!;
}
