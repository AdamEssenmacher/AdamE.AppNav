using AdamE.AppNav.Navigation;
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
    ValueTask<Page> CreatePageAsync(RouteEntry entry, CancellationToken cancellationToken = default);

    ValueTask<Page> CreatePresentationPageAsync(
        Type pageType,
        Page ownerRoutePage,
        bool inheritBindingContext,
        CancellationToken cancellationToken = default);

    ValueTask UpdatePageAsync(
        Page page,
        RouteEntry entry,
        MauiRoutePageUpdateContext context,
        CancellationToken cancellationToken = default);

    ValueTask ReleasePageAsync(Page page);

    ValueTask ReleasePresentationPageAsync(Page page);

    MauiPageAbandonment? CaptureAbandonment(Page page);
}

internal sealed class MauiPageAbandonment : IAsyncDisposable
{
    private IAsyncDisposable? _scope;

    public MauiPageAbandonment(IAsyncDisposable? scope, string pageTypeName)
    {
        _scope = scope;
        PageTypeName = pageTypeName;
    }

    public string PageTypeName { get; }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable? scope = Interlocked.Exchange(ref _scope, null);
        if (scope is not null)
            await scope.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Observes the creation, update, and release of router-owned MAUI route pages.
/// </summary>
/// <remarks>
/// Lifecycle callbacks run within the router operation that is presenting the page. They must not synchronously or
/// asynchronously re-enter <see cref="IRouterNavigator.NavigateAsync"/>,
/// <see cref="IRouterNavigator.BackAsync(string?, CancellationToken)"/>,
/// or <see cref="IRouterNavigator.ReconcileAsync"/> on the same navigator. Schedule follow-up navigation to run after
/// the callback and its owning router operation have completed.
/// <para>
/// <see cref="OnPageReleasedAsync"/> is invoked for normal page retirement. When the native host destroys an entire
/// page tree, AppNav cannot safely pass those invalid pages to user code; it skips the release callback and disposes
/// the captured page scope instead.
/// </para>
/// </remarks>
public interface IMauiRoutePageLifecycleHook
{
    ValueTask OnPageCreatedAsync(
        Page page,
        RouteEntry entry,
        CancellationToken cancellationToken = default);

    ValueTask OnPageUpdatedAsync(
        Page page,
        RouteEntry entry,
        MauiRoutePageUpdateContext context,
        CancellationToken cancellationToken = default);

    ValueTask OnPageReleasedAsync(Page page, CancellationToken cancellationToken = default);
}

internal sealed class MauiRoutePageFactory : IMauiRoutePageFactory
{
    private static readonly BindableProperty PageHandleProperty =
        BindableProperty.CreateAttached(
            "RouterPageHandle",
            typeof(PageHandle),
            typeof(MauiRoutePageFactory),
            null);

    private readonly IServiceProvider _serviceProvider;
    private readonly MauiRoutePresentationOptions _options;

    public MauiRoutePageFactory(IServiceProvider serviceProvider, MauiRoutePresentationOptions options)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<Page> CreatePageAsync(
        RouteEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        IAsyncDisposable? scope = null;
        IServiceProvider services = _serviceProvider;
        if (_options.UseScopedPages)
        {
            AsyncServiceScope asyncScope = _serviceProvider.CreateAsyncScope();
            scope = asyncScope;
            services = asyncScope.ServiceProvider;
        }

        Page? page = null;
        try
        {
            if (_options.Pages.TryCreatePage(services, entry, out page))
            {
                var handle = new PageHandle(
                    scope,
                    services.GetServices<IMauiRoutePageLifecycleHook>().ToArray());
                SetPageHandle(page, handle);
                scope = null;
                foreach (IMauiRoutePageLifecycleHook hook in handle.GetActiveHooks())
                    await hook.OnPageCreatedAsync(page, entry, cancellationToken);

                return page;
            }

            throw new AppNavigationConfigurationException(
                $"No MAUI page factory is registered for route type '{entry.Route.GetType().FullName}'.");
        }
        catch (Exception creationException)
        {
            try
            {
                await CleanupFailedCreationAsync(page, scope).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(creationException, cleanupException);
            }

            throw;
        }
    }

    public async ValueTask<Page> CreatePresentationPageAsync(
        Type pageType,
        Page ownerRoutePage,
        bool inheritBindingContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        ArgumentNullException.ThrowIfNull(ownerRoutePage);

        if (!typeof(Page).IsAssignableFrom(pageType))
        {
            throw new ArgumentException(
                $"Presentation page type '{pageType.FullName}' must derive from '{typeof(Page).FullName}'.",
                nameof(pageType));
        }

        IAsyncDisposable? scope = null;
        IServiceProvider services = _serviceProvider;
        if (_options.UseScopedPages)
        {
            AsyncServiceScope asyncScope = _serviceProvider.CreateAsyncScope();
            scope = asyncScope;
            services = asyncScope.ServiceProvider;
        }

        Page? page = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (services.GetRequiredService(pageType) is not Page resolvedPage)
            {
                throw new AppNavigationConfigurationException(
                    $"Registered presentation page service '{pageType.FullName}' did not resolve to a MAUI Page.");
            }

            page = resolvedPage;
            SetPageHandle(page, new PageHandle(scope, []));
            scope = null;

            if (inheritBindingContext)
                page.BindingContext = ownerRoutePage.BindingContext;

            return page;
        }
        catch (Exception creationException)
        {
            try
            {
                await CleanupFailedCreationAsync(page, scope).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(creationException, cleanupException);
            }

            throw;
        }
    }

    public async ValueTask UpdatePageAsync(
        Page page,
        RouteEntry entry,
        MauiRoutePageUpdateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(entry);

        PageHandle handle = GetPageHandle(page) ??
            throw new InvalidOperationException("The route page is not owned by this page factory.");
        foreach (IMauiRoutePageLifecycleHook hook in handle.GetActiveHooks())
            await hook.OnPageUpdatedAsync(page, entry, context, cancellationToken);
    }

    public ValueTask ReleasePageAsync(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return ReleaseCoreAsync(page);
    }

    public ValueTask ReleasePresentationPageAsync(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return ReleaseCoreAsync(page);
    }

    public MauiPageAbandonment? CaptureAbandonment(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        PageHandle? handle = GetPageHandle(page);
        return handle?.TryClaimAbandonment(
            page.GetType().FullName ?? page.GetType().Name);
    }

    private static async ValueTask CleanupFailedCreationAsync(Page? page, IAsyncDisposable? unattachedScope)
    {
        var failures = new List<Exception>();
        if (page is not null)
        {
            try
            {
                await ReleaseCoreAsync(page).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (unattachedScope is not null)
        {
            try
            {
                await unattachedScope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("Page creation cleanup failed.", failures);
    }

    private static async ValueTask ReleaseCoreAsync(Page page)
    {
        PageHandle? handle = GetPageHandle(page);
        if (handle is null)
        {
            page.BindingContext = null;
            return;
        }

        if (!handle.TryClaimRelease(out PageReleaseResources resources))
            return;

        var failures = new List<Exception>();
        try
        {
            SetPageHandle(page, null);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        foreach (IMauiRoutePageLifecycleHook hook in resources.Hooks)
        {
            try
            {
                await hook.OnPageReleasedAsync(page, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        try
        {
            page.BindingContext = null;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (resources.Scope is not null)
        {
            try
            {
                await resources.Scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more page cleanup operations failed.", failures);
    }

    private static void SetPageHandle(BindableObject page, PageHandle? handle)
    {
        page.SetValue(PageHandleProperty, handle);
    }

    private static PageHandle? GetPageHandle(BindableObject page)
    {
        return page.GetValue(PageHandleProperty) as PageHandle;
    }

    private sealed class PageHandle
    {
        private readonly Lock _gate = new();
        private IAsyncDisposable? _scope;
        private IReadOnlyList<IMauiRoutePageLifecycleHook> _hooks;
        private PageOwnershipState _state;

        public PageHandle(
            IAsyncDisposable? scope,
            IReadOnlyList<IMauiRoutePageLifecycleHook> hooks)
        {
            _scope = scope;
            _hooks = hooks;
        }

        public IReadOnlyList<IMauiRoutePageLifecycleHook> GetActiveHooks()
        {
            lock (_gate)
            {
                if (_state != PageOwnershipState.Active)
                    throw new InvalidOperationException("The route page is no longer active.");

                return _hooks;
            }
        }

        public bool TryClaimRelease(out PageReleaseResources resources)
        {
            lock (_gate)
            {
                if (_state != PageOwnershipState.Active)
                {
                    resources = default;
                    return false;
                }

                _state = PageOwnershipState.Released;
                resources = TakeResources();
                return true;
            }
        }

        public MauiPageAbandonment? TryClaimAbandonment(string pageTypeName)
        {
            lock (_gate)
            {
                if (_state != PageOwnershipState.Active)
                    return null;

                _state = PageOwnershipState.Abandoned;
                PageReleaseResources resources = TakeResources();
                return new MauiPageAbandonment(resources.Scope, pageTypeName);
            }
        }

        private PageReleaseResources TakeResources()
        {
            var resources = new PageReleaseResources(_scope, _hooks);
            _scope = null;
            _hooks = [];
            return resources;
        }
    }

    private readonly record struct PageReleaseResources(
        IAsyncDisposable? Scope,
        IReadOnlyList<IMauiRoutePageLifecycleHook> Hooks);

    private enum PageOwnershipState
    {
        Active,
        Released,
        Abandoned
    }
}
