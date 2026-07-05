using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Back;

/// <summary>
/// Provides MauiRouter's default host-aware logical back-navigation behavior.
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
                "No active window was available for back navigation.");

            return null;
        }

        if (window.Modals.Count > 0)
        {
            ModalNode topModal = window.Modals[^1];
            if (topModal.Content is not null && TryBack(topModal.Content) is { } modalBackResult)
            {
                ModalNode[] updatedModals = window.Modals.ToArray();
                updatedModals[^1] = topModal with { Content = modalBackResult.Node };
                _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                    modalBackResult.Reason);

                return new NavigationPlan(
                    ReplaceWindow(context, window with { Modals = updatedModals }),
                    NavigationPlanKind.Back,
                    modalBackResult.Reason);
            }

            WindowNode updatedWindow = window with { Modals = RemoveLast(window.Modals) };
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                "Back navigation will dismiss the top modal.");

            return new NavigationPlan(ReplaceWindow(context, updatedWindow), NavigationPlanKind.Back, "Dismiss modal");
        }

        if (window.Root is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                "The active window has no root node.");

            return null;
        }

        NodeBackResult? backResult = TryBack(window.Root);
        if (backResult is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId,
                "No host accepted back navigation.");

            return null;
        }

        NodeBackResult acceptedBackResult = backResult.Value;
        _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, context.OperationId, acceptedBackResult.Reason);
        return new NavigationPlan(
            ReplaceWindow(context, window with { Root = acceptedBackResult.Node }),
            NavigationPlanKind.Back,
            acceptedBackResult.Reason);
    }

    private NodeBackResult? TryBack(NavigationNode node)
    {
        return node switch
        {
            StackNode stack => TryBackStack(stack),
            BranchHostNode branchHost => TryBackBranchHost(branchHost),
            // Window-level modal dismissal is handled from WindowNode.Modals. A nested
            // modal node can only delegate back navigation into its content.
            ModalNode { Content: not null } modal => TryBack(modal.Content) is { } result
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

    private NodeBackResult? TryBackBranchHost(BranchHostNode branchHost)
    {
        NavigationBranch? selectedBranch = branchHost.SelectedBranch;
        if (selectedBranch is not null)
            if (TryBack(selectedBranch.Content) is { } childBack)
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

    private static NavigationState ReplaceWindow(BackNavigationContext context, WindowNode window)
    {
        NavigationState state = context.State.ReplaceWindow(window);
        return context.UsesActiveWindow && !string.IsNullOrWhiteSpace(context.ResolvedWindowId)
            ? state with { ActiveWindowId = context.ResolvedWindowId }
            : state;
    }

    private static IReadOnlyList<T> RemoveLast<T>(IReadOnlyList<T> source)
    {
        if (source.Count <= 1)
            return [];

        var result = new T[source.Count - 1];
        for (var i = 0; i < result.Length; i++) result[i] = source[i];

        return result;
    }

    private readonly record struct NodeBackResult(NavigationNode Node, string Reason);
}
