using AdamE.AppNav.Maui;
using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

internal sealed class InstrumentedRoutePageFactory : IMauiRoutePageFactory
{
    private readonly Func<RouteEntry, Page>? _createPage;
    private readonly Action<Page, RouteEntry, MauiRoutePageUpdateContext>? _updatePage;
    private readonly Dictionary<Page, int> _releaseCounts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Page, int> _updateCounts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Page, RouteEntry> _lastUpdatedEntries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Page, MauiRoutePageUpdateContext> _lastUpdateContexts = new(ReferenceEqualityComparer.Instance);

    public event Action<Page>? PresentationPageReleased;

    public IReadOnlyList<Page> CreatedPages => _createdPages.ToArray();

    public IReadOnlyList<Page> ReleasedPages => _releasedPages.ToArray();

    public IReadOnlyList<Page> CreatedPresentationPages => _createdPresentationPages.ToArray();

    public IReadOnlyList<Page> ReleasedPresentationPages => _releasedPresentationPages.ToArray();

    private readonly List<Page> _createdPages = new();
    private readonly List<Page> _releasedPages = new();
    private readonly List<Page> _createdPresentationPages = new();
    private readonly List<Page> _releasedPresentationPages = new();

    public InstrumentedRoutePageFactory(
        Func<RouteEntry, Page>? createPage = null,
        Action<Page, RouteEntry, MauiRoutePageUpdateContext>? updatePage = null)
    {
        _createPage = createPage;
        _updatePage = updatePage;
    }

    public ValueTask<Page> CreatePageAsync(
        RouteEntry entry,
        CancellationToken cancellationToken = default)
    {
        var page = _createPage?.Invoke(entry) ??
                   new ContentPage
                   {
                       Title = entry.Id,
                       Content = new Label { Text = entry.Id }
                   };

        _createdPages.Add(page);
        return ValueTask.FromResult(page);
    }

    public ValueTask<Page> CreatePresentationPageAsync(
        Type pageType,
        Page ownerRoutePage,
        bool inheritBindingContext,
        CancellationToken cancellationToken = default)
    {
        var page = Assert.IsAssignableFrom<Page>(Activator.CreateInstance(pageType));
        if (inheritBindingContext)
        {
            page.BindingContext = ownerRoutePage.BindingContext;
        }

        _createdPresentationPages.Add(page);
        return ValueTask.FromResult(page);
    }

    public ValueTask UpdatePageAsync(
        Page page,
        RouteEntry entry,
        MauiRoutePageUpdateContext context,
        CancellationToken cancellationToken = default)
    {
        _updateCounts.TryGetValue(page, out var count);
        _updateCounts[page] = count + 1;
        _lastUpdatedEntries[page] = entry;
        _lastUpdateContexts[page] = context;
        _updatePage?.Invoke(page, entry, context);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleasePageAsync(Page page)
    {
        _releasedPages.Add(page);
        _releaseCounts.TryGetValue(page, out var count);
        _releaseCounts[page] = count + 1;
        page.BindingContext = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleasePresentationPageAsync(Page page)
    {
        page.BindingContext = null;
        _releasedPresentationPages.Add(page);
        _releaseCounts.TryGetValue(page, out var count);
        _releaseCounts[page] = count + 1;
        PresentationPageReleased?.Invoke(page);
        return ValueTask.CompletedTask;
    }

    public int ReleaseCountFor(Page page)
    {
        return _releaseCounts.TryGetValue(page, out var count) ? count : 0;
    }

    public int UpdateCountFor(Page page)
    {
        return _updateCounts.TryGetValue(page, out var count) ? count : 0;
    }

    public RouteEntry? LastUpdatedEntryFor(Page page)
    {
        return _lastUpdatedEntries.GetValueOrDefault(page);
    }

    public MauiRoutePageUpdateContext? LastUpdateContextFor(Page page)
    {
        return _lastUpdateContexts.GetValueOrDefault(page);
    }
}
