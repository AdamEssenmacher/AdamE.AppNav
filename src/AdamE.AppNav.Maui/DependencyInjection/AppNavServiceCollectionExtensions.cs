using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Maui.Requests;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Maui.DependencyInjection;

public static class AppNavServiceCollectionExtensions
{
    public static IServiceCollection AddAppNav<TPlanner>(
        this IServiceCollection services,
        RouteTable routes,
        Action<MauiRoutePageRegistry>? configurePages = null)
        where TPlanner : class, IAppNavigationPlanner
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(routes);

        services.AddAppNavCoreServices(routes);
        if (configurePages is not null)
        {
            services.AddAppNavPages(configurePages);
        }

        services.AddSingleton<IAppNavigationPlanner, TPlanner>();
        services.AddAppNavRuntime();

        return services;
    }

    public static IServiceCollection AddAppNavPages(
        this IServiceCollection services,
        Action<MauiRoutePageRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<IMauiRoutePageContributor>(
            new DelegateMauiRoutePageContributor(configure));

        return services;
    }

    public static IServiceCollection AddAppNavFileDeferredNavigationRequests(
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

    public static IServiceCollection AddAppNavStartup(
        this IServiceCollection services,
        Action<AppNavStartupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AppNavStartupOptions();
        configure?.Invoke(options);

        services.AddAppNavBoundaryServices();
        services.AddSingleton(options);
        services.TryAddSingleton<IAppNavStartupService, AppNavStartupService>();

        return services;
    }

    private static IServiceCollection AddAppNavCoreServices(
        this IServiceCollection services,
        RouteTable routes)
    {
        services.TryAddSingleton(provider =>
            new NavigationDiagnostics(provider.GetService<ILoggerFactory>()?.CreateLogger("AdamE.AppNav.Diagnostics")));
        services.TryAddSingleton<IBackNavigator>(provider =>
            new DefaultBackNavigator(diagnostics: provider.GetRequiredService<NavigationDiagnostics>()));
        services.AddSingleton(routes);

        return services;
    }

    private static IServiceCollection AddAppNavRuntime(this IServiceCollection services)
    {
        services.AddAppNavBoundaryServices();
        services.AddAppNavPresentationServices();
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
        services.TryAddSingleton<IAppNavRuntime>(provider =>
            new AppNavRuntime(
                provider.GetRequiredService<CoreRouterNavigator>().Navigator,
                provider.GetRequiredService<MauiNavigationPresenter>()));
        services.TryAddSingleton<IRouterNavigator>(provider => provider.GetRequiredService<IAppNavRuntime>());
        services.TryAddSingleton<IMauiWindowAttachment>(provider =>
            (IMauiWindowAttachment)provider.GetRequiredService<IAppNavRuntime>());

        return services;
    }

    private static IServiceCollection AddAppNavBoundaryServices(this IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
            new NavigationDiagnostics(provider.GetService<ILoggerFactory>()?.CreateLogger("AdamE.AppNav.Diagnostics")));
        services.TryAddSingleton<MauiExternalNavigationDispatcher>();
        services.TryAddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        return services;
    }

    private static IServiceCollection AddAppNavPresentationServices(this IServiceCollection services)
    {
        services.TryAddSingleton(CreatePresentationOptions);
        services.AddSingleton<IMauiRoutePageFactory, MauiRoutePageFactory>();
        services.AddSingleton(provider => new MauiNavigationPresenter(
            provider.GetRequiredService<IMauiRoutePageFactory>(),
            provider.GetService<MauiExternalNavigationDispatcher>(),
            provider.GetService<NavigationDiagnostics>(),
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
