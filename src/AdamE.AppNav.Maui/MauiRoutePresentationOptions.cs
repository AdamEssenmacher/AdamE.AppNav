namespace AdamE.AppNav.Maui;

internal sealed class MauiRoutePresentationOptions
{
    public MauiRoutePageRegistry Pages { get; } = new();

    public Dictionary<string, MauiFlyoutBranchHostOptions> FlyoutBranchHosts { get; } =
        new(StringComparer.Ordinal);

    public bool UseScopedPages { get; set; } = true;

    public bool TryGetFlyout(string branchHostId, out MauiFlyoutBranchHostOptions options) =>
        FlyoutBranchHosts.TryGetValue(branchHostId, out options!);
}
