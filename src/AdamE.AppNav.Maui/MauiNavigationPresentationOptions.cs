using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Configures how host-neutral navigation topology is presented by the MAUI adapter.
/// </summary>
public sealed class MauiNavigationPresentationOptions
{
    private readonly Dictionary<string, MauiBranchHostRegistration> _branchHosts =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Maps a branch host to a factory that owns its MAUI presentation surface.
    /// </summary>
    /// <param name="branchHostId">The logical <c>BranchHostNode</c> identifier.</param>
    /// <param name="factory">The factory used to create the host presentation.</param>
    /// <returns>The same options instance for chaining.</returns>
    public MauiNavigationPresentationOptions MapBranchHost(
        string branchHostId,
        IMauiBranchHostFactory factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchHostId);
        ArgumentNullException.ThrowIfNull(factory);
        if (factory.SupportedPlacements == MauiBranchHostPlacement.None ||
            (factory.SupportedPlacements & ~MauiBranchHostPlacement.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(factory), "The branch-host factory declares no valid placement capabilities.");
        if (!_branchHosts.TryAdd(branchHostId, new MauiBranchHostRegistration(factory)))
        {
            throw new InvalidOperationException(
                $"A MAUI branch-host presentation is already mapped for branch-host id '{branchHostId}'.");
        }

        return this;
    }

    internal IReadOnlyDictionary<string, MauiBranchHostRegistration> BranchHosts => _branchHosts;

    internal bool TryGetBranchHost(string branchHostId, out MauiBranchHostRegistration registration) =>
        _branchHosts.TryGetValue(branchHostId, out registration!);
}

internal sealed record MauiFlyoutBranchHostOptions(
    string MenuTitle,
    FlyoutLayoutBehavior LayoutBehavior,
    bool IsGestureEnabled);

internal sealed record MauiBranchHostRegistration(IMauiBranchHostFactory Factory);
