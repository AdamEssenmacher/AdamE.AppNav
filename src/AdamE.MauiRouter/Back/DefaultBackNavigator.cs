using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Back;

public sealed class DefaultBackNavigator(
    BackNavigationOptions? options = null,
    NavigationDiagnostics? diagnostics = null)
    : IBackNavigator
{
    private readonly BackNavigationOptions _options = options ?? BackNavigationOptions.Default;
    private readonly NavigationDiagnostics _diagnostics = diagnostics ?? NavigationDiagnostics.None;

    public NavigationPlan? CreateBackPlan(NavigationState state, string? windowId = null)
    {
        return CreateBackPlan(state, windowId, Guid.NewGuid().ToString("N"));
    }

    internal NavigationPlan? CreateBackPlan(
        NavigationState state,
        string? windowId,
        string operationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        WindowNode? window = ResolveWindow(state, windowId);
        if (window is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, "No active window was available for back navigation.");
            return null;
        }

        if (window.Modals.Count > 0)
        {
            ModalNode topModal = window.Modals[^1];
            if (topModal.Content is not null &&
                TryBack(topModal.Content) is { } modalBackResult)
            {
                var updatedModals = window.Modals.ToArray();
                updatedModals[^1] = topModal with { Content = modalBackResult.Node };
                _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, modalBackResult.Reason);
                return new NavigationPlan(
                    state.ReplaceWindow(window with { Modals = updatedModals }),
                    NavigationPlanKind.Back,
                    modalBackResult.Reason);
            }

            WindowNode updatedWindow = window with { Modals = RemoveLast(window.Modals) };
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, "Back navigation will dismiss the top modal.");
            return new NavigationPlan(state.ReplaceWindow(updatedWindow), NavigationPlanKind.Back, "Dismiss modal");
        }

        if (window.Root is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, "The active window has no root node.");
            return null;
        }

        NodeBackResult? backResult = TryBack(window.Root);
        if (backResult is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, "No host accepted back navigation.");
            return null;
        }

        _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, backResult.Reason);
        return new NavigationPlan(
            state.ReplaceWindow(window with { Root = backResult.Node }),
            NavigationPlanKind.Back,
            backResult.Reason);
    }

    private NodeBackResult? TryBack(NavigationNode node)
    {
        return node switch
        {
            StackNode stack => TryBackStack(stack),
            TabsNode tabs => TryBackTabs(tabs),
            FlyoutNode flyout => TryBackFlyout(flyout),
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
        {
            return null;
        }

        return new NodeBackResult(
            stack with { Entries = RemoveLast(stack.Entries) },
            "Back navigation will pop the selected stack.");
    }

    private NodeBackResult? TryBackTabs(TabsNode tabs)
    {
        NavigationBranch? selectedBranch = tabs.SelectedBranch;
        if (selectedBranch is not null)
        {
            NodeBackResult? childBack = TryBack(selectedBranch.Content);
            if (childBack is not null)
            {
                return new NodeBackResult(
                    tabs.ReplaceBranch(selectedBranch with { Content = childBack.Node }),
                    childBack.Reason);
            }
        }

        if (_options.ReturnToDefaultTabBeforeLeaving &&
            !string.IsNullOrWhiteSpace(tabs.DefaultTabId) &&
            !StringComparer.Ordinal.Equals(tabs.SelectedTabId, tabs.DefaultTabId) &&
            tabs.Branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, tabs.DefaultTabId)))
        {
            return new NodeBackResult(
                tabs with { SelectedTabId = tabs.DefaultTabId },
                "Back navigation will return to the default tab.");
        }

        return null;
    }

    private NodeBackResult? TryBackFlyout(FlyoutNode flyout)
    {
        NavigationBranch? selectedBranch = flyout.SelectedBranch;
        if (selectedBranch is not null)
        {
            NodeBackResult? childBack = TryBack(selectedBranch.Content);
            if (childBack is not null)
            {
                return new NodeBackResult(
                    flyout.ReplaceBranch(selectedBranch with { Content = childBack.Node }),
                    childBack.Reason);
            }
        }

        if (_options.ReturnToDefaultFlyoutItemBeforeLeaving &&
            !string.IsNullOrWhiteSpace(flyout.DefaultItemId) &&
            !StringComparer.Ordinal.Equals(flyout.SelectedItemId, flyout.DefaultItemId) &&
            flyout.Branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, flyout.DefaultItemId)))
        {
            return new NodeBackResult(
                flyout with { SelectedItemId = flyout.DefaultItemId },
                "Back navigation will return to the default flyout item.");
        }

        return null;
    }

    private static WindowNode? ResolveWindow(NavigationState state, string? windowId)
    {
        return string.IsNullOrWhiteSpace(windowId)
            ? state.ActiveWindow
            : state.FindWindow(windowId);
    }

    private static IReadOnlyList<T> RemoveLast<T>(IReadOnlyList<T> source)
    {
        if (source.Count <= 1)
        {
            return [];
        }

        var result = new T[source.Count - 1];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = source[i];
        }

        return result;
    }

    private sealed record NodeBackResult(NavigationNode Node, string Reason);
}
