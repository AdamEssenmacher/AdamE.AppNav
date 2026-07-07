using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.State;

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

    public WindowNode? ActiveWindow => FindWindow(ActiveWindowId) ?? (Windows.Count > 0 ? Windows[0] : null);

    public WindowNode? FindWindow(string? id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? null
            : Windows.FirstOrDefault(window => StringComparer.Ordinal.Equals(window.Id, id));
    }

    public NavigationState ReplaceWindow(WindowNode window)
    {
        ArgumentNullException.ThrowIfNull(window);

        WindowNode[] windows = Windows.ToArray();
        for (var i = 0; i < windows.Length; i++)
            if (StringComparer.Ordinal.Equals(windows[i].Id, window.Id))
            {
                windows[i] = window;
                return this with { Windows = windows, ActiveWindowId = ActiveWindowId ?? window.Id };
            }

        return this with
        {
            Windows = windows.Concat([window]).ToArray(),
            ActiveWindowId = ActiveWindowId ?? window.Id
        };
    }
}
