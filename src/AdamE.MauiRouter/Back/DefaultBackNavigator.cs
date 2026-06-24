using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Back;

public sealed class DefaultBackNavigator : IBackNavigator
{
    private readonly BackNavigationOptions _options;
    private readonly NavigationDiagnostics _diagnostics;

    public DefaultBackNavigator(
        BackNavigationOptions? options = null,
        NavigationDiagnostics? diagnostics = null)
    {
        _options = options ?? BackNavigationOptions.Default;
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;
    }

    public NavigationPlan? CreateBackPlan(NavigationState state, string? windowId = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var operationId = Guid.NewGuid().ToString("N");
        var window = state.FindWindow(windowId ?? state.ActiveWindowId);
        if (window is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, "No active window was available for back navigation.");
            return null;
        }

        if (window.Modals.Count > 0)
        {
            var topModal = window.Modals[^1];
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

            var updatedWindow = window with { Modals = RemoveLast(window.Modals) };
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, "Back navigation will dismiss the top modal.");
            return new NavigationPlan(state.ReplaceWindow(updatedWindow), NavigationPlanKind.Back, "Dismiss modal");
        }

        if (window.Root is null)
        {
            _diagnostics.Write(NavigationDiagnosticEventKind.BackEvaluated, operationId, "The active window has no root node.");
            return null;
        }

        var backResult = TryBack(window.Root);
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
            ModalNode modal when modal.Content is not null => TryBack(modal.Content) is { } result
                ? new NodeBackResult(modal with { Content = result.Node }, result.Reason)
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
        var selectedBranch = tabs.SelectedBranch;
        if (selectedBranch is not null)
        {
            var childBack = TryBack(selectedBranch.Content);
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
        var selectedBranch = flyout.SelectedBranch;
        if (selectedBranch is not null)
        {
            var childBack = TryBack(selectedBranch.Content);
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

    private static IReadOnlyList<T> RemoveLast<T>(IReadOnlyList<T> source)
    {
        if (source.Count <= 1)
        {
            return Array.Empty<T>();
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
