using AdamE.AppNav.History;
using AdamE.AppNav.Internal;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class StateImmutabilityTests
{
    [Fact]
    public void NavigationStateSnapshotsWindowCollections()
    {
        var windows = new List<WindowNode>
        {
            new("main", Stack("main-stack", "home"))
        };

        var state = new NavigationState(windows, "main");
        windows.Add(new WindowNode("secondary"));

        Assert.Single(state.Windows);
        Assert.Equal("main", state.Windows[0].Id);
    }

    [Fact]
    public void NavigationNodesSnapshotChildCollections()
    {
        var entries = new List<RouteEntry>
        {
            Entry("catalog")
        };
        var stack = new StackNode("catalog-stack", entries);
        entries.Add(Entry("product"));

        var branches = new List<NavigationBranch>
        {
            new("catalog", "Catalog", stack)
        };
        var branchHost = new BranchHostNode("branchHost", branches, "catalog", "catalog");
        var secondaryBranchHost = new BranchHostNode("secondary-branchHost", branches, "catalog", "catalog");
        branches.Add(new NavigationBranch("cart", "Cart", Stack("cart-stack", "cart")));

        var modals = new List<ModalNode>
        {
            new("promo-modal", Entry("promo"))
        };
        var window = new WindowNode("main", branchHost, modals);
        modals.Add(new ModalNode("cart-modal", Entry("cart-modal")));

        Assert.Single(stack.Entries);
        Assert.Single(branchHost.Branches);
        Assert.Single(secondaryBranchHost.Branches);
        Assert.Single(window.Modals);
    }

    [Fact]
    public void WithExpressionsSnapshotAssignedCollections()
    {
        var replacementWindows = new List<WindowNode>
        {
            new("replacement")
        };
        var state = NavigationState.Empty with { Windows = replacementWindows };
        replacementWindows.Clear();

        var replacementEntries = new List<RouteEntry>
        {
            Entry("replacement")
        };
        var stack = Stack("stack", "original") with { Entries = replacementEntries };
        replacementEntries.Clear();

        var replacementModals = new List<ModalNode>
        {
            new("modal", Entry("modal"))
        };
        var window = new WindowNode("main") with { Modals = replacementModals };
        replacementModals.Clear();

        Assert.Single(state.Windows);
        Assert.Single(stack.Entries);
        Assert.Single(window.Modals);
    }

    [Fact]
    public void MetadataDictionariesAreSnapshotted()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["source"] = "original"
        };
        var request = RouterNavigationRequest.FromRoute(new TestRoute("home"), NavigationRequestSource.Test, metadata: metadata);
        var routeEntry = new RouteEntry("entry", new TestRoute("entry"), Metadata: metadata);
        metadata["source"] = "mutated";
        metadata["extra"] = true;

        Assert.Equal("original", request.Metadata["source"]);
        Assert.False(request.Metadata.ContainsKey("extra"));
        Assert.Equal("original", routeEntry.Metadata!["source"]);
        Assert.False(routeEntry.Metadata!.ContainsKey("extra"));
    }

    [Fact]
    public void RouteDiagnosticSnapshotsDataDictionary()
    {
        var data = new Dictionary<string, object?>
        {
            ["path"] = "/original"
        };

        var diagnostic = new RouteDiagnostic("route.test", "Test diagnostic.", data);
        data["path"] = "/mutated";
        data["extra"] = true;

        Assert.Equal("/original", diagnostic.Data["path"]);
        Assert.False(diagnostic.Data.ContainsKey("extra"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, object?>)diagnostic.Data)["new"] = "value");
    }

    [Fact]
    public void EmptyMetadataDictionariesReuseCanonicalSnapshot()
    {
        IReadOnlyDictionary<string, object?> canonical = CollectionSnapshot.MetadataDictionary(null);
        IReadOnlyDictionary<string, object?> emptySnapshot =
            CollectionSnapshot.MetadataDictionary(new Dictionary<string, object?>());
        var diagnostic = new RouteDiagnostic("route.test", "Test diagnostic.");

        Assert.Same(canonical, emptySnapshot);
        Assert.Same(canonical, diagnostic.Data);
        Assert.Same(canonical, RouteMatchResult.EmptyMetadata);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, object?>)canonical)["new"] = "value");
    }

    [Fact]
    public void NavigationHistorySnapshotsEntries()
    {
        var entries = new List<NavigationHistoryEntry>
        {
            HistoryEntry("first")
        };
        var history = new NavigationHistory(entries, 0);
        entries.Add(HistoryEntry("second"));

        Assert.Single(history.Entries);
        Assert.Equal("first", ((TestRoute)history.Current!.Route).Value);
    }

    [Fact]
    public void NavigationHistoryRejectsInvalidCurrentIndex()
    {
        var entries = new[]
        {
            HistoryEntry("first")
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationHistory(Array.Empty<NavigationHistoryEntry>(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationHistory(entries, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationHistory(entries, entries.Length));
    }

    private static StackNode Stack(string id, params string[] routeValues)
    {
        return new StackNode(id, routeValues.Select(Entry).ToArray());
    }

    private static RouteEntry Entry(string value)
    {
        return new RouteEntry(value, new TestRoute(value));
    }

    private static NavigationHistoryEntry HistoryEntry(string value)
    {
        var route = new TestRoute(value);
        var state = new NavigationState(new[] { new WindowNode("main", Stack("stack", value)) }, "main");
        return new NavigationHistoryEntry(
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            state);
    }

    private sealed record TestRoute(string Value) : AppRoute;
}
