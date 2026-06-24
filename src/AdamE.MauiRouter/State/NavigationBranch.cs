namespace AdamE.MauiRouter.State;

public sealed record NavigationBranch(
    string Id,
    string Title,
    NavigationNode Content);
