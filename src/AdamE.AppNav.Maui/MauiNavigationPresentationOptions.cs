using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Configures how host-neutral navigation topology is presented by the MAUI adapter.
/// </summary>
public sealed class MauiNavigationPresentationOptions
{
    private readonly Dictionary<string, MauiFlyoutBranchHostOptions> _flyoutBranchHosts =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Maps a root branch host to a native <see cref="FlyoutPage"/>.
    /// </summary>
    /// <param name="branchHostId">The logical <c>BranchHostNode</c> identifier.</param>
    /// <param name="menuTitle">The localized title displayed by the built-in flyout menu.</param>
    /// <param name="layoutBehavior">The native flyout layout behavior.</param>
    /// <param name="isGestureEnabled">Whether the platform flyout gesture is enabled.</param>
    /// <returns>The same options instance for chaining.</returns>
    public MauiNavigationPresentationOptions MapFlyoutBranchHost(
        string branchHostId,
        string menuTitle,
        FlyoutLayoutBehavior layoutBehavior = FlyoutLayoutBehavior.Default,
        bool isGestureEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchHostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(menuTitle);
        if (!Enum.IsDefined(layoutBehavior))
            throw new ArgumentOutOfRangeException(nameof(layoutBehavior));
        if (!_flyoutBranchHosts.TryAdd(
                branchHostId,
                new MauiFlyoutBranchHostOptions(menuTitle, layoutBehavior, isGestureEnabled)))
        {
            throw new InvalidOperationException(
                $"A MAUI flyout presentation is already mapped for branch-host id '{branchHostId}'.");
        }

        return this;
    }

    internal IReadOnlyDictionary<string, MauiFlyoutBranchHostOptions> FlyoutBranchHosts =>
        _flyoutBranchHosts;
}

internal sealed record MauiFlyoutBranchHostOptions(
    string MenuTitle,
    FlyoutLayoutBehavior LayoutBehavior,
    bool IsGestureEnabled);
