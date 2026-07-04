namespace AdamE.MauiRouter.State;

/// <summary>
/// Identifies a platform-neutral node in the logical navigation tree.
/// </summary>
/// <param name="Id">The stable identifier of the node within its owning navigation surface.</param>
public abstract record NavigationNode(string Id);
