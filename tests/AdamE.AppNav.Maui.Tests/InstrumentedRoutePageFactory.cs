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

    public IReadOnlyList<Page> CreatedPages => _createdPages.ToArray();

    public IReadOnlyList<Page> ReleasedPages => _releasedPages.ToArray();

    private readonly List<Page> _createdPages = new();
    private readonly List<Page> _releasedPages = new();

    public InstrumentedRoutePageFactory(
        Func<RouteEntry, Page>? createPage = null,
        Action<Page, RouteEntry, MauiRoutePageUpdateContext>? updatePage = null)
    {
        _createPage = createPage;
        _updatePage = updatePage;
    }

    public Page CreatePage(RouteEntry entry)
    {
        var page = _createPage?.Invoke(entry) ??
                   new ContentPage
                   {
                       Title = entry.Id,
                       Content = new Label { Text = entry.Id }
                   };

        _createdPages.Add(page);
        return page;
    }

    public void UpdatePage(Page page, RouteEntry entry, MauiRoutePageUpdateContext context)
    {
        _updateCounts.TryGetValue(page, out var count);
        _updateCounts[page] = count + 1;
        _lastUpdatedEntries[page] = entry;
        _lastUpdateContexts[page] = context;
        _updatePage?.Invoke(page, entry, context);
    }

    public void ReleasePage(Page page)
    {
        _releasedPages.Add(page);
        _releaseCounts.TryGetValue(page, out var count);
        _releaseCounts[page] = count + 1;
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
