namespace AdamE.AppNav.Maui;

internal sealed class MauiRoutePresentationOptions
{
    public MauiRoutePageRegistry Pages { get; } = new();

    public Dictionary<string, MauiBranchHostRegistration> BranchHosts { get; } =
        new(StringComparer.Ordinal);

    public bool UseScopedPages { get; set; } = true;

    public bool TryGetBranchHost(string branchHostId, out MauiBranchHostRegistration registration) =>
        BranchHosts.TryGetValue(branchHostId, out registration!);
}
