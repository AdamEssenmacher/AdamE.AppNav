using System.Runtime.CompilerServices;
using AdamE.AppNav.Leases;
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

    /// <summary>Releases every page this factory built that has not been released, at presenter shutdown.</summary>
    ValueTask ReleaseOutstandingPagesAsync() => ValueTask.CompletedTask;
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
/// <see cref="OnPageReleasedAsync"/> runs when the page leaves the native tree. With scoped pages, the hook comes from
/// its route entry's scope, which the entry's <see cref="RouteEntryLease"/> disposes later, once core has retired the
/// entry and every page built for it has been released.
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
    private readonly RouteEntryLeaseRegistry _leases;
    // The DI scope of each entry occurrence whose lease this factory opened, owned and disposed by that lease.
    private readonly ConditionalWeakTable<RouteEntryLease, IServiceProvider> _entryServices = new();
    // Pages built and not yet released, so shutdown can detach every artifact even if the presenter lost one.
    private readonly HashSet<Page> _outstandingPages = new(ReferenceEqualityComparer.Instance);

    public MauiRoutePageFactory(
        IServiceProvider serviceProvider,
        MauiRoutePresentationOptions options,
        RouteEntryLeaseRegistry leases)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    }

    public async ValueTask<Page> CreatePageAsync(
        RouteEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        RouteEntryLease lease = _leases.GetOrOpen(entry.Id, out bool opened);
        if (opened)
            OpenEntryServices(lease);

        if (!lease.TryAttach(out RouteEntryLeaseAttachment? attachment))
        {
            // A retired entry that is still committed stays until the application navigates; it is never rebuilt.
            if (lease.IsCommitted)
                return CreatePlaceholderPage();

            throw new OperationCanceledException(
                $"The lease of route entry '{entry.Id}' retired before the entry was presented.",
                lease.RetiredToken);
        }

        Page? page = null;
        IAsyncDisposable? pageScope = null;
        try
        {
            IServiceProvider services = ServicesFor(lease, out pageScope);
            if (_options.Pages.TryCreatePage(services, entry, out page))
            {
                if (GetPageHandle(page) is not null)
                {
                    // Never release the live page this resolution returned again.
                    page = null;
                    throw new AppNavigationConfigurationException(
                        $"The page factory for route type '{entry.Route.GetType().FullName}' returned a page that is " +
                        "already presented. Route pages must be new instances; register them as transient.");
                }

                var handle = new PageHandle(
                    lease,
                    attachment,
                    pageScope,
                    services.GetServices<IMauiRoutePageLifecycleHook>().ToArray());
                SetPageHandle(page, handle);
                attachment = null;
                pageScope = null;
                Track(page);
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
                await CleanupFailedCreationAsync(page, attachment, pageScope).ConfigureAwait(false);
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

        // A presentation page can share its owner's view model, so it holds the owner's lease too.
        RouteEntryLeaseAttachment? attachment = null;
        PageHandle? ownerHandle = GetPageHandle(ownerRoutePage);
        if (ownerHandle is { IsPlaceholder: true })
        {
            throw new InvalidOperationException(
                "A retired route entry is shown as a placeholder and cannot present pages until a navigation removes it.");
        }

        RouteEntryLease? ownerLease = ownerHandle?.Lease;
        if (ownerLease is not null && !ownerLease.TryAttach(out attachment))
        {
            throw new InvalidOperationException(
                "The owning route entry has retired, so it cannot present new pages until a navigation removes it.");
        }

        IAsyncDisposable? scope = null;
        Page? page = null;
        try
        {
            IServiceProvider services = _serviceProvider;
            if (_options.UseScopedPages)
            {
                AsyncServiceScope asyncScope = _serviceProvider.CreateAsyncScope();
                scope = asyncScope;
                services = asyncScope.ServiceProvider;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (services.GetRequiredService(pageType) is not Page resolvedPage)
            {
                throw new AppNavigationConfigurationException(
                    $"Registered presentation page service '{pageType.FullName}' did not resolve to a MAUI Page.");
            }

            if (GetPageHandle(resolvedPage) is not null)
            {
                throw new AppNavigationConfigurationException(
                    $"Presentation page service '{pageType.FullName}' returned a page that is already presented. " +
                    "Register presentation pages as transient.");
            }

            page = resolvedPage;
            SetPageHandle(page, new PageHandle(ownerLease, attachment, scope, []));
            attachment = null;
            scope = null;
            Track(page);

            if (inheritBindingContext)
                page.BindingContext = ownerRoutePage.BindingContext;

            return page;
        }
        catch (Exception creationException)
        {
            try
            {
                await CleanupFailedCreationAsync(page, attachment, scope).ConfigureAwait(false);
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

    // This factory opened the lease inside the presentation, so it is the opener: it attaches the entry's scope and
    // closes the lease. Core abandons the lease if the presentation fails.
    private void OpenEntryServices(RouteEntryLease lease)
    {
        try
        {
            if (_options.UseScopedPages)
            {
                AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
                lease.Own(scope);
                lock (_entryServices)
                {
                    _entryServices.Add(lease, scope.ServiceProvider);
                }

                // Owned last, so released first: the released scope is no longer reachable through its lease.
                lease.Own(() =>
                {
                    lock (_entryServices)
                    {
                        _entryServices.Remove(lease);
                    }
                });
            }
        }
        finally
        {
            lease.Close();
        }
    }

    private IServiceProvider ServicesFor(RouteEntryLease lease, out IAsyncDisposable? pageScope)
    {
        pageScope = null;
        if (!_options.UseScopedPages)
            return _serviceProvider;

        lock (_entryServices)
        {
            if (_entryServices.TryGetValue(lease, out IServiceProvider? services))
                return services;
        }

        // The application opened this lease and owns what the entry borrows, so the page keeps a scope of its own.
        AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        pageScope = scope;
        return scope.ServiceProvider;
    }

    public async ValueTask ReleaseOutstandingPagesAsync()
    {
        Page[] pages;
        lock (_outstandingPages)
        {
            pages = _outstandingPages.ToArray();
        }

        var failures = new List<Exception>();
        foreach (Page page in pages)
        {
            try
            {
                await ReleaseCoreAsync(page);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more outstanding pages could not be released.", failures);
    }

    private void Track(Page page)
    {
        lock (_outstandingPages)
        {
            _outstandingPages.Add(page);
        }
    }

    private static ContentPage CreatePlaceholderPage()
    {
        var page = new ContentPage();
        SetPageHandle(page, new PageHandle(null, null, null, [], isPlaceholder: true));
        return page;
    }

    private async ValueTask CleanupFailedCreationAsync(
        Page? page,
        RouteEntryLeaseAttachment? unattachedLease,
        IAsyncDisposable? unattachedScope)
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

        unattachedLease?.Detach();
        if (failures.Count > 0)
            throw new AggregateException("Page creation cleanup failed.", failures);
    }

    private async ValueTask ReleaseCoreAsync(Page page)
    {
        PageHandle? handle = GetPageHandle(page);
        if (handle is null)
        {
            page.BindingContext = null;
            return;
        }

        if (!handle.TryClaimRelease(out PageReleaseResources resources))
            return;

        lock (_outstandingPages)
        {
            _outstandingPages.Remove(page);
        }

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

        // The page has left the native tree whatever its cleanup did, so it no longer holds the entry's lease.
        resources.Attachment?.Detach();
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
        private RouteEntryLeaseAttachment? _attachment;
        private IAsyncDisposable? _scope;
        private IReadOnlyList<IMauiRoutePageLifecycleHook> _hooks;
        private bool _released;

        public PageHandle(
            RouteEntryLease? lease,
            RouteEntryLeaseAttachment? attachment,
            IAsyncDisposable? scope,
            IReadOnlyList<IMauiRoutePageLifecycleHook> hooks,
            bool isPlaceholder = false)
        {
            IsPlaceholder = isPlaceholder;
            Lease = lease;
            _attachment = attachment;
            _scope = scope;
            _hooks = hooks;
        }

        // The route entry's lease for route pages, the owner's for presentation pages, none for placeholders.
        public RouteEntryLease? Lease { get; }

        public bool IsPlaceholder { get; }

        public IReadOnlyList<IMauiRoutePageLifecycleHook> GetActiveHooks()
        {
            lock (_gate)
            {
                if (_released)
                    throw new InvalidOperationException("The route page is no longer active.");

                return _hooks;
            }
        }

        public bool TryClaimRelease(out PageReleaseResources resources)
        {
            lock (_gate)
            {
                if (_released)
                {
                    resources = default;
                    return false;
                }

                _released = true;
                resources = new PageReleaseResources(_attachment, _scope, _hooks);
                _attachment = null;
                _scope = null;
                _hooks = [];
                return true;
            }
        }
    }

    private readonly record struct PageReleaseResources(
        RouteEntryLeaseAttachment? Attachment,
        IAsyncDisposable? Scope,
        IReadOnlyList<IMauiRoutePageLifecycleHook> Hooks);
}
