using AdamE.AppNav.Internal;

namespace AdamE.AppNav.State;

public sealed record NavigationState
{
    public static NavigationState Empty { get; } = new([]);

    public NavigationState(IReadOnlyList<WindowNode> Windows, string? ActiveWindowId = null)
    {
        this.Windows = Windows;
        this.ActiveWindowId = ActiveWindowId;
    }

    public IReadOnlyList<WindowNode> Windows
    {
        get;
        init
        {
            IReadOnlyList<WindowNode> windows = NavigationIdentity.RequiredList(value, nameof(Windows));
            NavigationIdentity.EnsureUniqueIds(
                windows,
                static window => window.Id,
                nameof(Windows),
                "window id",
                "Navigation state windows");
            field = windows;
        }
    } = CollectionSnapshot.List<WindowNode>(null);

    public string? ActiveWindowId
    {
        get;
        init => field = NavigationIdentity.OptionalId(value, nameof(ActiveWindowId));
    }

    /// <summary>
    /// Gets the explicitly active window, or the first window when no active window id is set.
    /// </summary>
    /// <remarks>
    /// A non-null <see cref="ActiveWindowId"/> never falls back to a different window.
    /// Navigation-state validation requires it to identify an existing window.
    /// </remarks>
    public WindowNode? ActiveWindow => ActiveWindowId is null
        ? (Windows.Count > 0 ? Windows[0] : null)
        : FindWindow(ActiveWindowId);

    public WindowNode? FindWindow(string? id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? null
            : Windows.FirstOrDefault(window => StringComparer.Ordinal.Equals(window.Id, id));
    }

    /// <summary>
    /// Replaces the window with the same identifier, or appends the supplied window when no match exists.
    /// </summary>
    /// <remarks>
    /// This operation preserves <see cref="ActiveWindowId"/>. Callers that intend to activate the supplied
    /// window must set <see cref="ActiveWindowId"/> explicitly.
    /// </remarks>
    public NavigationState ReplaceWindow(WindowNode window)
    {
        ArgumentNullException.ThrowIfNull(window);

        WindowNode[] windows = Windows.ToArray();
        for (var i = 0; i < windows.Length; i++)
            if (StringComparer.Ordinal.Equals(windows[i].Id, window.Id))
            {
                windows[i] = window;
                return this with { Windows = windows };
            }

        return this with { Windows = windows.Concat([window]).ToArray() };
    }
}
