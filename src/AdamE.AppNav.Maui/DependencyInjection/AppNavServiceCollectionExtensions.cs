using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Maui.Requests;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Planning;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace AdamE.AppNav.Maui.DependencyInjection;

public static class AppNavServiceCollectionExtensions
{
    /// <summary>
    /// Configures AppNav diagnostic data handling. Safe mode remains the default when this method is not called.
    /// </summary>
    public static IServiceCollection AddAppNavDiagnostics(
        this IServiceCollection services,
        Action<NavigationDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
            services.AddSingleton<INavigationDiagnosticsOptionsContributor>(
                new DelegateNavigationDiagnosticsOptionsContributor(configure));

        return services.AddAppNavDiagnosticsServices();
    }

#pragma warning disable RS0026 // Advanced-planner and standard-model registration are intentional preview overloads.
    public static IServiceCollection AddAppNav<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TPlanner>(
        this IServiceCollection services,
        RouteTable routes,
        Action<MauiRoutePageRegistry>? configurePages = null)
        where TPlanner : class, IAppNavigationPlanner
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(routes);
        EnsureRouterNavigatorIsAvailable(services);

        services.AddAppNavCoreServices(routes);
        if (configurePages is not null)
        {
            services.AddAppNavPages(configurePages);
        }

        services.AddSingleton<IAppNavigationPlanner, TPlanner>();
        services.AddAppNavRuntime();

        return services;
    }

    /// <summary>
    /// Registers AppNav with the standard disposition planner backed by a topology model.
    /// </summary>
    public static IServiceCollection AddAppNav<TRoute>(
        this IServiceCollection services,
        RouteTable routes,
        INavigationModel<TRoute> model,
        Action<MauiRoutePageRegistry>? configurePages = null)
        where TRoute : AppRoute
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(model);
        EnsureRouterNavigatorIsAvailable(services);

        services.AddAppNavCoreServices(routes);
        if (configurePages is not null)
        {
            services.AddAppNavPages(configurePages);
        }

        services.AddSingleton(model);
        services.AddSingleton<IAppNavigationPlanner>(new NavigationModelPlanner<TRoute>(model));
        services.AddAppNavRuntime();

        return services;
    }
#pragma warning restore RS0026

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
        services.AddAppNavDiagnosticsServices();
        services.TryAddSingleton<IBackNavigator>(provider =>
            new DefaultBackNavigator(diagnostics: provider.GetRequiredService<NavigationDiagnostics>()));
        services.AddSingleton(routes);

        return services;
    }

    private static IServiceCollection AddAppNavRuntime(this IServiceCollection services)
    {
        services.AddAppNavBoundaryServices();
        services.AddAppNavPresentationServices();
        services.TryAddSingleton<IAppNavRuntime>(provider =>
        {
            var options = new RouterNavigatorFactoryOptions
            {
                Diagnostics = provider.GetRequiredService<NavigationDiagnostics>(),
                BackNavigator = provider.GetRequiredService<IBackNavigator>(),
                LoggerFactory = provider.GetService<ILoggerFactory>(),
                RequestTransformers = provider.GetServices<INavigationRequestTransformer>().ToArray(),
                RequestPolicies = provider.GetServices<INavigationRequestPolicy>().ToArray()
            };

            MauiNavigationPresenter presenter = provider.GetRequiredService<MauiNavigationPresenter>();
            IRouterNavigator navigator = RouterNavigatorFactory.Create(
                provider.GetRequiredService<RouteTable>(),
                provider.GetRequiredService<IAppNavigationPlanner>(),
                presenter,
                options);
            return new AppNavRuntime(navigator, presenter);
        });
        services.AddSingleton<IRouterNavigator>(provider => provider.GetRequiredService<IAppNavRuntime>());
        services.TryAddSingleton<IMauiWindowAttachment>(provider =>
            (IMauiWindowAttachment)provider.GetRequiredService<IAppNavRuntime>());

        return services;
    }

    private static void EnsureRouterNavigatorIsAvailable(IServiceCollection services)
    {
        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(IRouterNavigator) &&
                !descriptor.IsKeyedService))
        {
            throw new AppNavigationConfigurationException(
                $"{nameof(AddAppNav)} owns the unkeyed {nameof(IRouterNavigator)} registration so the navigator and " +
                "MAUI presenter cannot diverge. Remove the existing registration, or compose a complete " +
                $"navigator and presenter with {nameof(RouterNavigatorFactory)} instead of calling {nameof(AddAppNav)}.");
        }
    }

    private static IServiceCollection AddAppNavBoundaryServices(this IServiceCollection services)
    {
        services.AddAppNavDiagnosticsServices();
        services.TryAddSingleton<MauiExternalNavigationOptions>();
        services.TryAddSingleton<MauiExternalNavigationDispatcher>();
        services.TryAddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        return services;
    }

    private static IServiceCollection AddAppNavDiagnosticsServices(this IServiceCollection services)
    {
        services.TryAddSingleton(provider =>
        {
            var options = new NavigationDiagnosticsOptions();
            foreach (INavigationDiagnosticsOptionsContributor contributor in
                     provider.GetServices<INavigationDiagnosticsOptionsContributor>())
            {
                contributor.Contribute(options);
            }

            return new NavigationDiagnostics(
                provider.GetService<ILoggerFactory>()?.CreateLogger("AdamE.AppNav.Diagnostics"),
                options,
                provider.GetService<INavigationDiagnosticRedactor>());
        });
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
        services.AddSingleton<IMauiPresentationState>(provider =>
        {
            _ = provider.GetRequiredService<IAppNavRuntime>();
            return provider.GetRequiredService<MauiNavigationPresenter>();
        });
        services.AddSingleton<IMauiRoutePresentationNavigator>(provider =>
        {
            _ = provider.GetRequiredService<IAppNavRuntime>();
            return provider.GetRequiredService<MauiNavigationPresenter>();
        });

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

    private interface IMauiRoutePageContributor
    {
        void Contribute(MauiRoutePageRegistry registry);
    }

    private interface INavigationDiagnosticsOptionsContributor
    {
        void Contribute(NavigationDiagnosticsOptions options);
    }

    private sealed class DelegateNavigationDiagnosticsOptionsContributor(
        Action<NavigationDiagnosticsOptions> configure)
        : INavigationDiagnosticsOptionsContributor
    {
        public void Contribute(NavigationDiagnosticsOptions options) => configure(options);
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
