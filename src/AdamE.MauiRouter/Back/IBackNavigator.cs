using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Back;

public interface IBackNavigator
{
    NavigationPlan? CreateBackPlan(NavigationState state, string? windowId = null);
}
