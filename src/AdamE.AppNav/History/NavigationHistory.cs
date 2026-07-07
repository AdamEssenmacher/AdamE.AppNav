using AdamE.AppNav.Internal;
using AdamE.AppNav.Requests;
using JetBrains.Annotations;

namespace AdamE.AppNav.History;

/// <summary>
/// Represents an immutable history of completed router navigations.
/// </summary>
/// <remarks>
/// A history has a current position. Pushing a new entry retains entries up to that
/// position, discards any entries after it, and appends the new entry.
/// </remarks>
public sealed class NavigationHistory
{
    /// <summary>
    /// Gets an empty navigation history.
    /// </summary>
    public static NavigationHistory Empty { get; } = new([], -1);

    /// <summary>
    /// Initializes a navigation history from a snapshot of entries and the current entry index.
    /// </summary>
    /// <param name="entries">The history entries, ordered from oldest to newest.</param>
    /// <param name="currentIndex">
    /// The index of the current entry, or -1 when <paramref name="entries"/> is empty.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="currentIndex"/> is not -1 for an empty history, or does not identify
    /// an entry in a non-empty history.
    /// </exception>
    internal NavigationHistory(IReadOnlyList<NavigationHistoryEntry> entries, int currentIndex)
    {
        Entries = entries;
        int entryCount = Entries.Count;
        bool hasEntries = entryCount > 0;
        if ((hasEntries && (currentIndex < 0 || currentIndex >= entryCount)) ||
            (!hasEntries && currentIndex != -1))
            throw new ArgumentOutOfRangeException(
                nameof(currentIndex),
                currentIndex,
                "CurrentIndex must be -1 for empty history, or a valid entry index for non-empty history.");

        CurrentIndex = currentIndex;
    }

    /// <summary>
    /// Gets the history entries in oldest-to-newest order.
    /// </summary>
    /// <remarks>
    /// The collection is snapshotted when the history is created, so later mutations to the
    /// source collection do not affect this instance.
    /// </remarks>
    public IReadOnlyList<NavigationHistoryEntry> Entries
    {
        get;
        private init => field = CollectionSnapshot.List(value);
    }

    /// <summary>
    /// Gets the index of <see cref="Current"/> within <see cref="Entries"/>, or -1 when the
    /// history is empty.
    /// </summary>
    private int CurrentIndex { get; }

    /// <summary>
    /// Gets the current history entry, or <see langword="null"/> when the history is empty.
    /// </summary>
    public NavigationHistoryEntry? Current =>
        CurrentIndex >= 0 && CurrentIndex < Entries.Count ? Entries[CurrentIndex] : null;

    /// <summary>
    /// Returns a new history with the specified entry appended after the current entry.
    /// </summary>
    /// <param name="entry">The history entry to append.</param>
    /// <param name="maxEntries">
    /// Optional maximum number of newest entries to retain. A value less than or equal to zero
    /// returns <see cref="Empty"/>.
    /// </param>
    /// <returns>A new history containing the appended entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Entries after <see cref="Current"/> are discarded before <paramref name="entry"/> is appended.
    /// </remarks>
    internal NavigationHistory Push(NavigationHistoryEntry entry, int? maxEntries = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Appending from the middle follows browser-style history behavior: stale forward
        // entries are discarded before the new current entry is added.
        int retainedCount = CurrentIndex + 1;
        var entries = new NavigationHistoryEntry[retainedCount + 1];
        for (var i = 0; i < retainedCount; i++)
            entries[i] = Entries[i];

        entries[^1] = entry;

        if (maxEntries is null || maxEntries.Value >= entries.Length)
            return new NavigationHistory(entries, entries.Length - 1);

        if (maxEntries.Value <= 0)
            return Empty;

        NavigationHistoryEntry[] trimmed = entries
            .Skip(entries.Length - maxEntries.Value)
            .ToArray();

        return new NavigationHistory(trimmed, trimmed.Length - 1);
    }
}

/// <summary>
/// Represents a single completed navigation stored in <see cref="NavigationHistory"/>.
/// </summary>
/// <param name="Request">The navigation request that produced the entry.</param>
/// <param name="Route">The route resolved for the request.</param>
/// <param name="State">The router state captured after the navigation completed.</param>
public sealed record NavigationHistoryEntry(
    [UsedImplicitly] RouterNavigationRequest Request,
    AppRoute Route,
    State.NavigationState State);
