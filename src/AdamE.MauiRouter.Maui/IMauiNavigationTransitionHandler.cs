using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Maui;

internal interface IMauiNavigationTransitionHandler<TTransition>
    where TTransition : NavigationTransition
{
    ValueTask ApplyAsync(
        MauiNavigationTransitionContext<TTransition> context,
        CancellationToken cancellationToken = default);
}
