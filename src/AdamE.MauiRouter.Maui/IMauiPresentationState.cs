using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

/// <summary>
/// Exposes the MAUI presentation state currently owned by the router presenter.
/// </summary>
public interface IMauiPresentationState
{
    /// <summary>
    /// Raised when the router-owned root page attached to the active window changes.
    /// </summary>
    event EventHandler<Page?>? RootPageChanged;

    /// <summary>
    /// Gets the currently attached MAUI window, if one is attached.
    /// </summary>
    Window? AttachedWindow { get; }

    /// <summary>
    /// Gets the router window id for the attached window, if one is attached.
    /// </summary>
    string? AttachedWindowId { get; }

    /// <summary>
    /// Gets the router-owned root page tree currently attached to the active window.
    /// </summary>
    Page? RootPage { get; }

    /// <summary>
    /// Gets the currently interactive leaf page after modal and container traversal.
    /// </summary>
    Page? GetTopPresentedPage();

    /// <summary>
    /// Gets whether the page is still present in the MAUI modal stack.
    /// </summary>
    bool IsModalPresented(Page page);
}
