using AdamE.MauiRouter.Plans;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.MauiRouter.Maui;

internal sealed class MauiNavigationTransitionRegistry
{
    private readonly Dictionary<Type, Func<IServiceProvider, object>> _factories = new();

    public NavigationTransition? DefaultTransition { get; set; } = new NoNavigationTransition();

    public MauiNavigationTransitionRegistry Map<TTransition, THandler>()
        where TTransition : NavigationTransition
        where THandler : class, IMauiNavigationTransitionHandler<TTransition>
    {
        _factories[typeof(TTransition)] = services => ActivatorUtilities.CreateInstance<THandler>(services);
        return this;
    }

    public MauiNavigationTransitionRegistry Map<TTransition>(
        Func<IServiceProvider, IMauiNavigationTransitionHandler<TTransition>> factory)
        where TTransition : NavigationTransition
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[typeof(TTransition)] = services => factory(services);
        return this;
    }

    internal bool TryCreateHandler(
        IServiceProvider services,
        Type transitionType,
        out object handler)
    {
        var factory = _factories.FirstOrDefault(candidate => candidate.Key.IsAssignableFrom(transitionType)).Value;
        if (factory is null)
        {
            handler = null!;
            return false;
        }

        handler = factory(services);
        return true;
    }
}
