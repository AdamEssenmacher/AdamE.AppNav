using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.State;

public sealed record NavigationState
{
    public static NavigationState Empty { get; } = new(Array.Empty<WindowNode>());

    private IReadOnlyList<WindowNode> _windows = CollectionSnapshot.List<WindowNode>(null);
    private string? _activeWindowId;

    public NavigationState(IReadOnlyList<WindowNode> Windows, string? ActiveWindowId = null)
    {
        this.Windows = Windows;
        this.ActiveWindowId = ActiveWindowId;
    }

    public IReadOnlyList<WindowNode> Windows
    {
        get => _windows;
        init
        {
            var windows = NavigationIdentity.RequiredList(value, nameof(Windows));
            NavigationIdentity.EnsureUniqueIds(
                windows,
                static window => window.Id,
                nameof(Windows),
                "window id",
                "Navigation state windows");
            _windows = windows;
        }
    }

    public string? ActiveWindowId
    {
        get => _activeWindowId;
        init => _activeWindowId = NavigationIdentity.OptionalId(value, nameof(ActiveWindowId));
    }

    public WindowNode? ActiveWindow => FindWindow(ActiveWindowId) ?? Windows.FirstOrDefault();

    public void Deconstruct(out IReadOnlyList<WindowNode> Windows, out string? ActiveWindowId)
    {
        Windows = this.Windows;
        ActiveWindowId = this.ActiveWindowId;
    }

    public WindowNode? FindWindow(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Windows.FirstOrDefault();
        }

        return Windows.FirstOrDefault(window => StringComparer.Ordinal.Equals(window.Id, id));
    }

    public NavigationState ReplaceWindow(WindowNode window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var windows = Windows.ToArray();
        for (var i = 0; i < windows.Length; i++)
        {
            if (StringComparer.Ordinal.Equals(windows[i].Id, window.Id))
            {
                windows[i] = window;
                return this with { Windows = windows, ActiveWindowId = ActiveWindowId ?? window.Id };
            }
        }

        return this with
        {
            Windows = windows.Concat(new[] { window }).ToArray(),
            ActiveWindowId = ActiveWindowId ?? window.Id
        };
    }
}
