using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

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

    Page CreatePresentationPage(Type pageType, Page ownerRoutePage, bool inheritBindingContext);

    void UpdatePage(Page page, RouteEntry entry, MauiRoutePageUpdateContext context);

    void ReleasePage(Page page);

    void ReleasePresentationPage(Page page);
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

    private static readonly BindableProperty PresentationPageServiceScopeProperty =
        BindableProperty.CreateAttached(
            "RouterPresentationPageServiceScope",
            typeof(IServiceScope),
            typeof(MauiRoutePageFactory),
            null);

    private static readonly BindableProperty InheritedBindingContextProperty =
        BindableProperty.CreateAttached(
            "RouterInheritedBindingContext",
            typeof(bool),
            typeof(MauiRoutePageFactory),
            false);

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

    public Page CreatePresentationPage(Type pageType, Page ownerRoutePage, bool inheritBindingContext)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        ArgumentNullException.ThrowIfNull(ownerRoutePage);

        if (!typeof(Page).IsAssignableFrom(pageType))
        {
            throw new ArgumentException(
                $"Presentation page type '{pageType.FullName}' must derive from '{typeof(Page).FullName}'.",
                nameof(pageType));
        }

        IServiceScope? scope = null;
        var services = _serviceProvider;

        if (_options.UseScopedPages)
        {
            scope = _serviceProvider.CreateScope();
            services = scope.ServiceProvider;
        }

        try
        {
            if (services.GetRequiredService(pageType) is not Page page)
            {
                throw new InvalidOperationException(
                    $"Registered presentation page service '{pageType.FullName}' did not resolve to a MAUI Page.");
            }

            if (scope is not null)
            {
                SetPresentationPageServiceScope(page, scope);
            }

            if (inheritBindingContext)
            {
                page.BindingContext = ownerRoutePage.BindingContext;
                SetInheritedBindingContext(page, true);
            }

            return page;
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

    public void ReleasePresentationPage(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (GetInheritedBindingContext(page))
        {
            page.BindingContext = null;
            SetInheritedBindingContext(page, false);
        }

        if (GetPresentationPageServiceScope(page) is not { } scope)
        {
            return;
        }

        SetPresentationPageServiceScope(page, null);
        scope.Dispose();
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

    private static void SetPresentationPageServiceScope(BindableObject bindableObject, IServiceScope? scope)
    {
        bindableObject.SetValue(PresentationPageServiceScopeProperty, scope);
    }

    private static IServiceScope? GetPresentationPageServiceScope(BindableObject bindableObject)
    {
        return bindableObject.GetValue(PresentationPageServiceScopeProperty) as IServiceScope;
    }

    private static void SetInheritedBindingContext(BindableObject bindableObject, bool value)
    {
        bindableObject.SetValue(InheritedBindingContextProperty, value);
    }

    private static bool GetInheritedBindingContext(BindableObject bindableObject)
    {
        return bindableObject.GetValue(InheritedBindingContextProperty) is true;
    }
}
