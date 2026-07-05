using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Maui.Requests;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Maui.DependencyInjection;

public static class MauiRouterServiceCollectionExtensions
{
    public static IServiceCollection AddMauiRouter<TPlanner>(
        this IServiceCollection services,
        RouteTable routes,
        Action<MauiRoutePageRegistry>? configurePages = null)
        where TPlanner : class, IAppNavigationPlanner
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(routes);

        services.AddMauiRouterCoreServices(routes);
        if (configurePages is not null)
        {
            services.AddMauiRouterPages(configurePages);
        }

        services.AddSingleton<IAppNavigationPlanner, TPlanner>();
        services.AddMauiRouterRuntime();

        return services;
    }

    public static IServiceCollection AddMauiRouterPages(
        this IServiceCollection services,
        Action<MauiRoutePageRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<IMauiRoutePageContributor>(
            new DelegateMauiRoutePageContributor(configure));

        return services;
    }

    public static IServiceCollection AddMauiRouterFileDeferredNavigationRequests(
        this IServiceCollection services,
        Action<MauiFileDeferredNavigationRequestStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider =>
        {
            var options = new MauiFileDeferredNavigationRequestStoreOptions();
            configure?.Invoke(options);
            return options;
        });
        services.TryAddSingleton<MauiFileDeferredNavigationRequestStore>();
        services.TryAddSingleton<IDeferredNavigationRequestStore>(provider =>
            provider.GetRequiredService<MauiFileDeferredNavigationRequestStore>());
        services.TryAddSingleton<IDeferredNavigationRequestReplayer, DeferredNavigationRequestReplayer>();

        return services;
    }

    public static IServiceCollection AddMauiRouterStartup(
        this IServiceCollection services,
        Action<MauiRouterStartupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MauiRouterStartupOptions();
        configure?.Invoke(options);

        services.AddMauiRouterBoundaryServices();
        services.AddSingleton(options);
        services.TryAddSingleton<IMauiRouterStartupService, MauiRouterStartupService>();

        return services;
    }

    private static IServiceCollection AddMauiRouterCoreServices(
        this IServiceCollection services,
        RouteTable routes)
    {
        services.TryAddSingleton(provider =>
            new NavigationDiagnostics(provider.GetService<ILoggerFactory>()?.CreateLogger("AdamE.MauiRouter.Diagnostics")));
        services.TryAddSingleton<IBackNavigator>(provider =>
            new DefaultBackNavigator(diagnostics: provider.GetRequiredService<NavigationDiagnostics>()));
        services.AddSingleton(routes);

        return services;
    }

    private static IServiceCollection AddMauiRouterRuntime(this IServiceCollection services)
    {
        services.AddMauiRouterBoundaryServices();
        services.AddMauiRouterPresentationServices();
        services.TryAddSingleton(provider =>
        {
            var options = new RouterNavigatorFactoryOptions
            {
                Diagnostics = provider.GetRequiredService<NavigationDiagnostics>(),
                BackNavigator = provider.GetRequiredService<IBackNavigator>(),
                LoggerFactory = provider.GetService<ILoggerFactory>(),
                RequestPolicies = provider.GetServices<INavigationRequestPolicy>().ToArray()
            };

            return new CoreRouterNavigator(
                RouterNavigatorFactory.Create(
                    provider.GetRequiredService<RouteTable>(),
                    provider.GetRequiredService<IAppNavigationPlanner>(),
                    provider.GetRequiredService<MauiNavigationPresenter>(),
                    options));
        });
        services.TryAddSingleton<IMauiRouterRuntime>(provider =>
            new MauiRouterRuntime(
                provider.GetRequiredService<CoreRouterNavigator>().Navigator,
                provider.GetRequiredService<MauiNavigationPresenter>()));
        services.TryAddSingleton<IRouterNavigator>(provider => provider.GetRequiredService<IMauiRouterRuntime>());
        services.TryAddSingleton<IMauiWindowAttachment>(provider =>
            (IMauiWindowAttachment)provider.GetRequiredService<IMauiRouterRuntime>());

        return services;
    }

    private static IServiceCollection AddMauiRouterBoundaryServices(this IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
            new NavigationDiagnostics(provider.GetService<ILoggerFactory>()?.CreateLogger("AdamE.MauiRouter.Diagnostics")));
        services.TryAddSingleton<MauiExternalNavigationDispatcher>();
        services.TryAddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        return services;
    }

    private static IServiceCollection AddMauiRouterPresentationServices(this IServiceCollection services)
    {
        services.TryAddSingleton(CreatePresentationOptions);
        services.AddSingleton<IMauiRoutePageFactory, MauiRoutePageFactory>();
        services.AddSingleton<MauiNavigationTransitionService>();
        services.AddSingleton(provider => new MauiNavigationPresenter(
            provider.GetRequiredService<IMauiRoutePageFactory>(),
            provider.GetService<MauiExternalNavigationDispatcher>(),
            provider.GetService<NavigationDiagnostics>(),
            provider.GetService<MauiNavigationTransitionService>(),
            provider.GetRequiredService<MauiRoutePresentationOptions>()));
        services.AddSingleton<IMauiPresentationState>(provider => provider.GetRequiredService<MauiNavigationPresenter>());

        return services;
    }

    private static MauiRoutePresentationOptions CreatePresentationOptions(IServiceProvider provider)
    {
        var options = new MauiRoutePresentationOptions();
        foreach (var contributor in provider.GetServices<IMauiRoutePageContributor>())
        {
            contributor.Contribute(options.Pages);
        }

        return options;
    }

    private sealed class CoreRouterNavigator(IRouterNavigator navigator)
    {
        public IRouterNavigator Navigator { get; } = navigator ?? throw new ArgumentNullException(nameof(navigator));
    }

    private interface IMauiRoutePageContributor
    {
        void Contribute(MauiRoutePageRegistry registry);
    }

    private sealed class DelegateMauiRoutePageContributor(Action<MauiRoutePageRegistry> configure)
        : IMauiRoutePageContributor
    {
        public void Contribute(MauiRoutePageRegistry registry)
        {
            configure(registry);
        }
    }
}
