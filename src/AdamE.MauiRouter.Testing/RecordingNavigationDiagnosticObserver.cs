using AdamE.MauiRouter.Diagnostics;

namespace AdamE.MauiRouter.Testing;

public sealed class RecordingNavigationDiagnosticObserver : INavigationDiagnosticObserver
{
    private readonly List<NavigationDiagnosticEvent> _events = new();

    public IReadOnlyList<NavigationDiagnosticEvent> Events => _events.ToArray();

    public void OnNavigationDiagnosticEvent(NavigationDiagnosticEvent diagnosticEvent)
    {
        _events.Add(diagnosticEvent);
    }

    public IReadOnlyList<NavigationDiagnosticEvent> EventsOfKind(params NavigationDiagnosticEventKind[] kinds)
    {
        var set = new HashSet<NavigationDiagnosticEventKind>(kinds);
        return _events
            .Where(diagnosticEvent => set.Contains(diagnosticEvent.Kind))
            .ToArray();
    }

    public NavigationDiagnosticEvent Single(NavigationDiagnosticEventKind kind)
    {
        var matches = EventsOfKind(kind);
        if (matches.Count != 1)
        {
            throw new NavigationAssertionException(
                $"Expected exactly one navigation diagnostic event of kind '{kind}', but found {matches.Count}.");
        }

        return matches[0];
    }

    public bool Contains(NavigationDiagnosticEventKind kind)
    {
        return _events.Any(diagnosticEvent => diagnosticEvent.Kind == kind);
    }

    public void Clear()
    {
        _events.Clear();
    }
}
