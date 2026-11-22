using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Plans;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Back;

/// <summary>
/// Provides AppNav's default host-aware logical back-navigation behavior.
/// </summary>
/// <param name="options">
/// Optional fallback behavior for branch hosts, or <see langword="null"/> to use the defaults.
/// </param>
/// <param name="diagnostics">
/// Optional diagnostics pipeline used to report back-planning decisions, or <see langword="null"/> to suppress diagnostics.
/// </param>
/// <remarks>
/// The default navigator first gives modal content and selected child hosts a chance to go back,
/// then falls back to modal dismissal, stack popping, and configured default-branch selection.
/// A <see cref="SplitNode"/> goes back as its presenter shows it, which
/// <see cref="BackNavigationContext.SplitLayout"/> says, and every evaluation records that layout as
/// <see cref="NavigationDiagnosticDataKeys.SplitLayout"/>. Collapsed (the default), Back goes back
/// inside the secondary column while <see cref="SplitNode.CompactColumn"/> is <see cref="SplitColumn.Secondary"/>,
/// then returns to the primary column, and otherwise goes back inside the primary column. Side by side, Back goes
/// back inside the secondary column, then inside the primary column, and never only changes
/// <see cref="SplitNode.CompactColumn"/>. In both layouts, Back inside the primary column, including a branch host's
/// default-branch fallback there, clears the secondary column and shows the primary column.
/// </remarks>
public sealed class DefaultBackNavigator(
    BackNavigationOptions? options = null,
    NavigationDiagnostics? diagnostics = null)
    : IBackNavigator
{
    private readonly BackNavigationOptions _options = options ?? BackNavigationOptions.Default;
    private readonly NavigationDiagnostics _diagnostics = diagnostics ?? NavigationDiagnostics.None;

    /// <summary>
    /// Creates a back-navigation plan for a state and optional window id.
    /// </summary>
    /// <param name="state">The current router state before back navigation is applied.</param>
    /// <param name="windowId">
    /// The window to navigate within, or <see langword="null"/> to use the active window.
    /// Blank values are treated the same as <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A back-navigation plan, or <see langword="null"/> when the current state cannot handle back navigation.
    /// </returns>
    /// <remarks>
    /// This overload plans every <see cref="SplitNode"/> as <see cref="SplitLayout.Collapsed"/>. To plan for another
    /// layout, pass a <see cref="BackNavigationContext"/> with its <see cref="BackNavigationContext.SplitLayout"/> set.
    /// </remarks>
    public NavigationPlan? CreateBackPlan(NavigationState state, string? windowId = null)
    {
        return CreateBackPlan(new BackNavigationContext(state, windowId, Guid.NewGuid().ToString("N")));
    }

    /// <inheritdoc />
    public NavigationPlan? CreateBackPlan(BackNavigationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        WindowNode? window = context.Window;
        if (window is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                "No active window was available for back navigation.", LayoutData(context));

            return null;
        }

        if (window.Modals.Count > 0)
        {
            ModalNode topModal = window.Modals[^1];
            if (topModal.Content is not null &&
                TryBack(topModal.Content, context.SplitLayout == SplitLayout.SideBySide) is { } modalBackResult)
            {
                ModalNode[] updatedModals = window.Modals.ToArray();
                updatedModals[^1] = topModal with { Content = modalBackResult.Node };
                _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                modalBackResult.Reason, LayoutData(context));

                return new NavigationPlan(
                    ReplaceWindow(context, window with { Modals = updatedModals }),
                    NavigationPlanKind.Back,
                    modalBackResult.Reason);
            }

            WindowNode updatedWindow = window with { Modals = RemoveLast(window.Modals) };
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                "Back navigation will dismiss the top modal.", LayoutData(context));

            return new NavigationPlan(ReplaceWindow(context, updatedWindow), NavigationPlanKind.Back, "Dismiss modal");
        }

        if (window.Root is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                "The active window has no root node.", LayoutData(context));

            return null;
        }

        NodeBackResult? backResult = TryBack(window.Root, context.SplitLayout == SplitLayout.SideBySide);
        if (backResult is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                "No host accepted back navigation.", LayoutData(context));

            return null;
        }

        NodeBackResult acceptedBackResult = backResult.Value;
        _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
            acceptedBackResult.Reason, LayoutData(context));
        return new NavigationPlan(
            ReplaceWindow(context, window with { Root = acceptedBackResult.Node }),
            NavigationPlanKind.Back,
            acceptedBackResult.Reason);
    }

    private NodeBackResult? TryBack(NavigationNode node, bool splitsSideBySide)
    {
        return node switch
        {
            StackNode stack => TryBackStack(stack),
            BranchHostNode branchHost => TryBackBranchHost(branchHost, splitsSideBySide),
            SplitNode split => TryBackSplit(split, splitsSideBySide),
            // Window-level modal dismissal is handled from WindowNode.Modals. A nested
            // modal node can only delegate back navigation into its content.
            ModalNode { Content: not null } modal => TryBack(modal.Content, splitsSideBySide) is { } result
                ? result with { Node = modal with { Content = result.Node } }
                : null,
            _ => null
        };
    }

    private static NodeBackResult? TryBackStack(StackNode stack)
    {
        if (stack.Entries.Count <= 1)
            return null;

        return new NodeBackResult(stack with { Entries = RemoveLast(stack.Entries) },
            "Back navigation will pop the selected stack.");
    }

    private NodeBackResult? TryBackBranchHost(BranchHostNode branchHost, bool splitsSideBySide)
    {
        NavigationBranch? selectedBranch = branchHost.SelectedBranch;
        if (selectedBranch is not null)
            if (TryBack(selectedBranch.Content, splitsSideBySide) is { } childBack)
                return childBack with
                {
                    Node = branchHost.ReplaceBranch(selectedBranch with { Content = childBack.Node })
                };

        if (_options.ReturnToDefaultBranchBeforeLeaving &&
            !string.IsNullOrWhiteSpace(branchHost.DefaultBranchId) &&
            !StringComparer.Ordinal.Equals(branchHost.SelectedBranchId, branchHost.DefaultBranchId) &&
            branchHost.Branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, branchHost.DefaultBranchId)))
            return new NodeBackResult(
                branchHost with { SelectedBranchId = branchHost.DefaultBranchId },
                "Back navigation will return to the default branch.");

        return null;
    }

    // Collapsed (the default), a split goes back inside its secondary column while that column is the one shown, then
    // returns to the primary column. Side by side, both columns are shown, so it goes back inside the secondary column
    // and then the primary one, and never plans a Back that would only change CompactColumn. Either way, Back in the
    // primary column leaves the context the secondary column belonged to, so the secondary column is cleared.
    private NodeBackResult? TryBackSplit(SplitNode split, bool sideBySide)
    {
        bool secondaryFirst = sideBySide || split.CompactColumn == SplitColumn.Secondary;
        if (secondaryFirst && split.Secondary is { } secondary)
        {
            if (TryBack(secondary, sideBySide) is { } secondaryBack)
                return secondaryBack with { Node = split with { Secondary = secondaryBack.Node } };

            if (!sideBySide)
                return new NodeBackResult(
                    split with { CompactColumn = SplitColumn.Primary },
                    "Back navigation will return to the primary column.");
        }

        return TryBack(split.Primary, sideBySide) is { } primaryBack
            ? primaryBack with
            {
                Node = split with
                {
                    Primary = primaryBack.Node,
                    CompactColumn = SplitColumn.Primary,
                    Secondary = null
                }
            }
            : null;
    }

    private static NavigationState ReplaceWindow(BackNavigationContext context, WindowNode window)
    {
        NavigationState state = context.State.ReplaceWindow(window);
        return context.UsesActiveWindow && !string.IsNullOrWhiteSpace(context.ResolvedWindowId)
            ? state with { ActiveWindowId = context.ResolvedWindowId }
            : state;
    }

    private static T[] RemoveLast<T>(IReadOnlyList<T> source)
    {
        if (source.Count <= 1)
            return [];

        var result = new T[source.Count - 1];
        for (var i = 0; i < result.Length; i++) result[i] = source[i];

        return result;
    }

    // Shared, so recording the layout on every BackEvaluated allocates nothing.
    private static readonly IReadOnlyDictionary<string, object?> CollapsedData =
        new Dictionary<string, object?> { [NavigationDiagnosticDataKeys.SplitLayout] = nameof(SplitLayout.Collapsed) };

    private static readonly IReadOnlyDictionary<string, object?> SideBySideData =
        new Dictionary<string, object?> { [NavigationDiagnosticDataKeys.SplitLayout] = nameof(SplitLayout.SideBySide) };

    private static IReadOnlyDictionary<string, object?> LayoutData(BackNavigationContext context) =>
        context.SplitLayout == SplitLayout.SideBySide ? SideBySideData : CollapsedData;

    private readonly record struct NodeBackResult(NavigationNode Node, string Reason);
}
