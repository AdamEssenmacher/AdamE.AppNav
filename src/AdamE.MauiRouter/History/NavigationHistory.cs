using AdamE.MauiRouter.Internal;
using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.History;

public sealed record NavigationHistory
{
    public static NavigationHistory Empty { get; } = new(Array.Empty<NavigationHistoryEntry>(), -1);

    private IReadOnlyList<NavigationHistoryEntry> _entries = CollectionSnapshot.List<NavigationHistoryEntry>(null);

    public NavigationHistory(IReadOnlyList<NavigationHistoryEntry> Entries, int CurrentIndex)
    {
        this.Entries = Entries;
        this.CurrentIndex = CurrentIndex;
    }

    public IReadOnlyList<NavigationHistoryEntry> Entries
    {
        get => _entries;
        init => _entries = CollectionSnapshot.List(value);
    }

    public int CurrentIndex { get; init; }

    public NavigationHistoryEntry? Current =>
        CurrentIndex >= 0 && CurrentIndex < Entries.Count ? Entries[CurrentIndex] : null;

    public void Deconstruct(out IReadOnlyList<NavigationHistoryEntry> Entries, out int CurrentIndex)
    {
        Entries = this.Entries;
        CurrentIndex = this.CurrentIndex;
    }

    public NavigationHistory Push(NavigationHistoryEntry entry, int? maxEntries = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var retainedCount = CurrentIndex + 1;
        var entries = new NavigationHistoryEntry[retainedCount + 1];
        for (var i = 0; i < retainedCount; i++)
        {
            entries[i] = Entries[i];
        }

        entries[^1] = entry;

        if (maxEntries is null || maxEntries.Value >= entries.Length)
        {
            return new NavigationHistory(entries, entries.Length - 1);
        }

        if (maxEntries.Value <= 0)
        {
            return Empty;
        }

        var trimmed = entries
            .Skip(entries.Length - maxEntries.Value)
            .ToArray();

        return new NavigationHistory(trimmed, trimmed.Length - 1);
    }
}

public sealed record NavigationHistoryEntry(
    string Id,
    RouterNavigationRequest Request,
    AppRoute Route,
    State.NavigationState State,
    string? Reason,
    DateTimeOffset Timestamp);
