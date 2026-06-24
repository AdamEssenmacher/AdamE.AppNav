using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Maui.Persistence;
using AdamE.MauiRouter.Maui.Requests;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Persistence;
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

    internal static IServiceCollection AddMauiRouter(
        this IServiceCollection services,
        RouteTable routes,
        Action<MauiRouterPlannerOptions> configurePlanners,
        Action<MauiRoutePageRegistry>? configurePages = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(configurePlanners);

        var plannerOptions = new MauiRouterPlannerOptions();
        configurePlanners(plannerOptions);
        if (plannerOptions.Registrations.Count == 0)
        {
            throw new InvalidOperationException("At least one typed app route planner must be registered.");
        }

        services.AddMauiRouterCoreServices(routes);
        if (configurePages is not null)
        {
            services.AddMauiRouterPages(configurePages);
        }

        foreach (var registration in plannerOptions.Registrations)
        {
            services.AddSingleton(
                typeof(IAppRoutePlanner<>).MakeGenericType(registration.RouteType),
                registration.PlannerType);
            services.AddSingleton(
                typeof(IAppRoutePlannerRegistration),
                typeof(AppRoutePlannerRegistration<>).MakeGenericType(registration.RouteType));
        }

        services.AddSingleton<IAppNavigationPlanner, TypedAppNavigationPlanner>();
        services.AddMauiRouterRuntime();

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

    public static IServiceCollection AddMauiRouterFileNavigationPersistence(
        this IServiceCollection services,
        Action<NavigationPersistenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<INavigationStateStore, MauiFileNavigationStateStore>();
        services.AddSingleton(provider =>
        {
            var options = new NavigationPersistenceOptions
            {
                Store = provider.GetRequiredService<INavigationStateStore>()
            };
            configure?.Invoke(options);
            options.Store ??= provider.GetRequiredService<INavigationStateStore>();
            return options;
        });

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
            var options = new RouterNavigatorOptions
            {
                Diagnostics = provider.GetRequiredService<NavigationDiagnostics>(),
                BackNavigator = provider.GetRequiredService<IBackNavigator>(),
                Persistence = provider.GetService<NavigationPersistenceOptions>(),
                LoggerFactory = provider.GetService<ILoggerFactory>(),
                RequestPolicies = provider.GetServices<INavigationRequestPolicy>().ToArray(),
                PlanPolicies = provider.GetServices<INavigationPlanPolicy>().ToArray()
            };

            return new RouterNavigator(
                provider.GetRequiredService<RouteTable>(),
                provider.GetRequiredService<IAppNavigationPlanner>(),
                provider.GetRequiredService<MauiNavigationPresenter>(),
                options);
        });
        services.TryAddSingleton<IMauiRouterRuntime>(provider =>
            new MauiRouterRuntime(
                provider.GetRequiredService<RouterNavigator>(),
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
        services.AddSingleton<MauiNavigationPresenter>();
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
