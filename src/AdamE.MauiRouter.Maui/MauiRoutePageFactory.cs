using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

public enum MauiRoutePageReuseKind
{
    NonTargetReuse,
    ExplicitTarget,
    ResurfacedTarget
}

public readonly record struct MauiRoutePageUpdateContext(MauiRoutePageReuseKind ReuseKind)
{
    public bool IsNavigationTarget =>
        ReuseKind is MauiRoutePageReuseKind.ExplicitTarget or MauiRoutePageReuseKind.ResurfacedTarget;
}

internal interface IMauiRoutePageFactory
{
    Page CreatePage(RouteEntry entry);

    void UpdatePage(Page page, RouteEntry entry, MauiRoutePageUpdateContext context);

    void ReleasePage(Page page);
}

public interface IMauiRoutePageLifecycleHook
{
    void OnPageCreated(Page page, RouteEntry entry, IServiceProvider pageServices);

    void OnPageUpdated(Page page, RouteEntry entry, MauiRoutePageUpdateContext context, IServiceProvider pageServices);

    void OnPageReleased(Page page, IServiceProvider pageServices);
}

internal sealed class MauiRoutePageFactory : IMauiRoutePageFactory
{
    private static readonly BindableProperty PageServiceScopeProperty =
        BindableProperty.CreateAttached("RouterPageServiceScope", typeof(IServiceScope), typeof(MauiRoutePageFactory), null);

    private readonly IServiceProvider _serviceProvider;
    private readonly MauiRoutePresentationOptions _options;

    public MauiRoutePageFactory(IServiceProvider serviceProvider, MauiRoutePresentationOptions options)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Page CreatePage(RouteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        IServiceScope? scope = null;
        var services = _serviceProvider;

        if (_options.UseScopedPages)
        {
            scope = _serviceProvider.CreateScope();
            services = scope.ServiceProvider;
        }

        try
        {
            if (_options.Pages.TryCreatePage(services, entry, out var page))
            {
                if (scope is not null)
                {
                    SetPageServiceScope(page, scope);
                }

                InvokePageCreated(page, entry, services);
                return page;
            }

            throw new InvalidOperationException(
                $"No MAUI page factory is registered for route type '{entry.Route.GetType().FullName}'.");
        }
        catch
        {
            scope?.Dispose();
            throw;
        }
    }

    public void UpdatePage(Page page, RouteEntry entry, MauiRoutePageUpdateContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(entry);

        var services = GetPageServices(page);
        foreach (var hook in services.GetServices<IMauiRoutePageLifecycleHook>())
        {
            hook.OnPageUpdated(page, entry, context, services);
        }
    }

    public void ReleasePage(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (GetPageServiceScope(page) is { } scope)
        {
            try
            {
                InvokePageReleased(page, scope.ServiceProvider);
            }
            finally
            {
                SetPageServiceScope(page, null);
                scope.Dispose();
            }

            return;
        }

        InvokePageReleased(page, _serviceProvider);
    }

    private void InvokePageCreated(Page page, RouteEntry entry, IServiceProvider services)
    {
        foreach (var hook in services.GetServices<IMauiRoutePageLifecycleHook>())
        {
            hook.OnPageCreated(page, entry, services);
        }
    }

    private static void InvokePageReleased(Page page, IServiceProvider services)
    {
        foreach (var hook in services.GetServices<IMauiRoutePageLifecycleHook>())
        {
            hook.OnPageReleased(page, services);
        }
    }

    private IServiceProvider GetPageServices(BindableObject page)
    {
        return GetPageServiceScope(page)?.ServiceProvider ?? _serviceProvider;
    }

    private static void SetPageServiceScope(BindableObject bindableObject, IServiceScope? scope)
    {
        bindableObject.SetValue(PageServiceScopeProperty, scope);
    }

    private static IServiceScope? GetPageServiceScope(BindableObject bindableObject)
    {
        return bindableObject.GetValue(PageServiceScopeProperty) as IServiceScope;
    }
}
