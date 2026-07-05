using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Back;

/// <summary>
/// Provides the router state and operation metadata needed to create a back-navigation plan.
/// </summary>
/// <remarks>
/// Back navigators use this context to inspect the current logical navigation tree, determine
/// which window should handle the back request, and emit diagnostics that share the router's
/// operation correlation identifier.
/// </remarks>
public sealed record BackNavigationContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BackNavigationContext"/> class.
    /// </summary>
    /// <param name="state">The current router state before back navigation is applied.</param>
    /// <param name="requestedWindowId">
    /// The caller-supplied window identifier, or <see langword="null"/> to use the active window.
    /// Blank values are treated the same as <see langword="null"/>.
    /// </param>
    /// <param name="operationId">The diagnostics correlation identifier for the back operation.</param>
    public BackNavigationContext(
        NavigationState state,
        string? requestedWindowId,
        string operationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        State = state;
        RequestedWindowId = requestedWindowId;
        OperationId = operationId;
        UsesActiveWindow = string.IsNullOrWhiteSpace(requestedWindowId);
        Window = UsesActiveWindow
            ? state.ActiveWindow
            : state.FindWindow(requestedWindowId);
        ResolvedWindowId = Window?.Id;
    }

    /// <summary>
    /// Gets the current router state before back navigation is applied.
    /// </summary>
    public NavigationState State { get; }

    /// <summary>
    /// Gets the caller-supplied window identifier, or <see langword="null"/> when no window was requested.
    /// </summary>
    public string? RequestedWindowId { get; }

    /// <summary>
    /// Gets the diagnostics correlation identifier for the back operation.
    /// </summary>
    public string OperationId { get; }

    /// <summary>
    /// Gets the window that should handle back navigation if one could be resolved.
    /// </summary>
    /// <remarks>
    /// Null or blank requested window ids resolve through <see cref="NavigationState.ActiveWindow"/>.
    /// Explicit requested window ids resolve through <see cref="NavigationState.FindWindow"/>.
    /// </remarks>
    public WindowNode? Window { get; }

    /// <summary>
    /// Gets the identifier of the resolved window, or <see langword="null"/> when no window could be resolved.
    /// </summary>
    public string? ResolvedWindowId { get; }

    /// <summary>
    /// Gets a value indicating whether the request should use the router's active window.
    /// </summary>
    public bool UsesActiveWindow { get; }
}
