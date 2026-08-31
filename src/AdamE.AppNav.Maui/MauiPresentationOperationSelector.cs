using AdamE.AppNav.Plans;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Maui;

internal sealed record MauiPresentationOperationCandidate(
    MauiPresentationOperationKind Kind,
    string HostId,
    RouteEntry? SourceEntry,
    RouteEntry? TargetEntry);

internal sealed class MauiPresentationOperationScope(
    IMauiPresentationOperationPolicy policy,
    NavigationPlan plan,
    AdamE.AppNav.Presentation.NavigationPresentationContext presentationContext,
    MauiPresentationOperationCandidate candidate)
{
    private bool _resolved;

    public bool ResolveAnimated(
        MauiPresentationOperationKind kind,
        string hostId,
        bool nativeOperationIsSingular)
    {
        if (_resolved || !nativeOperationIsSingular || candidate.Kind != kind ||
            !StringComparer.Ordinal.Equals(candidate.HostId, hostId))
        {
            return false;
        }

        _resolved = true;
        MauiPresentationOperationOptions options = policy.Resolve(
            new MauiPresentationOperationContext(
                plan,
                presentationContext,
                candidate.Kind,
                candidate.SourceEntry,
                candidate.TargetEntry)) ??
            throw new InvalidOperationException(
                $"{nameof(IMauiPresentationOperationPolicy)}.{nameof(IMauiPresentationOperationPolicy.Resolve)} " +
                "returned null.");

        return options.Motion switch
        {
            MauiPresentationMotion.Automatic or MauiPresentationMotion.PlatformDefault => true,
            MauiPresentationMotion.Suppressed => false,
            _ => throw new InvalidOperationException(
                $"Unsupported {nameof(MauiPresentationMotion)} value '{options.Motion}'.")
        };
    }
}

internal static class MauiPresentationOperationSelector
{
    public static MauiPresentationOperationCandidate? Select(
        NavigationState currentState,
        NavigationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Kind == NavigationPlanKind.Reconcile)
            return null;

        WindowNode? currentWindow = currentState.ActiveWindow;
        WindowNode? targetWindow = plan.TargetState.ActiveWindow;
        if (currentWindow is null || targetWindow is null ||
            !StringComparer.Ordinal.Equals(currentWindow.Id, targetWindow.Id))
        {
            return null;
        }

        return SelectWindowOperation(currentWindow, targetWindow);
    }

    private static MauiPresentationOperationCandidate? SelectWindowOperation(
        WindowNode current,
        WindowNode target)
    {
        var commonModalCount = CommonModalShellPrefix(current.Modals, target.Modals);
        var modalPopCount = current.Modals.Count - commonModalCount;
        var modalPushCount = target.Modals.Count - commonModalCount;

        if (modalPopCount + modalPushCount == 1 && VisibleNodeEquivalent(current.Root, target.Root))
        {
            if (!ModalPrefixEquivalent(current.Modals, target.Modals, commonModalCount))
                return null;

            if (modalPushCount == 1)
            {
                ModalNode pushed = target.Modals[^1];
                return new MauiPresentationOperationCandidate(
                    MauiPresentationOperationKind.ModalPush,
                    pushed.Id,
                    PresentedEntry(current),
                    pushed.RouteEntry);
            }

            ModalNode popped = current.Modals[^1];
            return new MauiPresentationOperationCandidate(
                MauiPresentationOperationKind.ModalPop,
                popped.Id,
                popped.RouteEntry,
                PresentedEntry(target));
        }

        if (modalPopCount != 0 || modalPushCount != 0)
            return null;

        if (current.Modals.Count > 0)
        {
            for (var index = 0; index < current.Modals.Count - 1; index++)
                if (!ModalEquivalent(current.Modals[index], target.Modals[index]))
                    return null;

            ModalNode currentTop = current.Modals[^1];
            ModalNode targetTop = target.Modals[^1];
            if (!ModalShellEquivalent(currentTop, targetTop))
                return null;

            return SelectNodeOperation(currentTop.Content, targetTop.Content);
        }

        return SelectNodeOperation(current.Root, target.Root);
    }

    private static MauiPresentationOperationCandidate? SelectNodeOperation(
        NavigationNode? current,
        NavigationNode? target)
    {
        if (current is StackNode currentStack && target is StackNode targetStack &&
            StringComparer.Ordinal.Equals(currentStack.Id, targetStack.Id))
        {
            return SelectStackOperation(currentStack, targetStack);
        }

        if (current is BranchHostNode currentHost && target is BranchHostNode targetHost &&
            BranchShellEquivalent(currentHost, targetHost))
        {
            return SelectNodeOperation(
                currentHost.SelectedBranch?.Content,
                targetHost.SelectedBranch?.Content);
        }

        if (current is ModalNode currentModal && target is ModalNode targetModal &&
            StringComparer.Ordinal.Equals(currentModal.Id, targetModal.Id) &&
            RouteEntryEquivalent(currentModal.RouteEntry, targetModal.RouteEntry))
        {
            return SelectNodeOperation(currentModal.Content, targetModal.Content);
        }

        return null;
    }

    private static MauiPresentationOperationCandidate? SelectStackOperation(
        StackNode current,
        StackNode target)
    {
        int commonCount = CommonEntryPrefix(current.Entries, target.Entries);
        int popCount = current.Entries.Count - commonCount;
        int pushCount = target.Entries.Count - commonCount;
        if (popCount + pushCount != 1)
            return null;

        if (pushCount == 1)
        {
            return new MauiPresentationOperationCandidate(
                MauiPresentationOperationKind.StackPush,
                target.Id,
                current.Top,
                target.Top);
        }

        return new MauiPresentationOperationCandidate(
            MauiPresentationOperationKind.StackPop,
            target.Id,
            current.Top,
            target.Top);
    }

    private static int CommonEntryPrefix(
        IReadOnlyList<RouteEntry> current,
        IReadOnlyList<RouteEntry> target)
    {
        int count = Math.Min(current.Count, target.Count);
        int common = 0;
        while (common < count && RouteEntryEquivalent(current[common], target[common]))
            common++;
        return common;
    }

    private static int CommonModalShellPrefix(
        IReadOnlyList<ModalNode> current,
        IReadOnlyList<ModalNode> target)
    {
        int count = Math.Min(current.Count, target.Count);
        int common = 0;
        while (common < count && ModalShellEquivalent(current[common], target[common]))
            common++;
        return common;
    }

    private static bool ModalPrefixEquivalent(
        IReadOnlyList<ModalNode> current,
        IReadOnlyList<ModalNode> target,
        int count)
    {
        if (current.Count < count || target.Count < count)
            return false;
        for (var index = 0; index < count; index++)
            if (!ModalEquivalent(current[index], target[index]))
                return false;
        return true;
    }

    private static bool ModalEquivalent(ModalNode current, ModalNode target) =>
        ModalShellEquivalent(current, target) &&
        VisibleNodeEquivalent(current.Content, target.Content);

    private static bool ModalShellEquivalent(ModalNode current, ModalNode target) =>
        StringComparer.Ordinal.Equals(current.Id, target.Id) &&
        RouteEntryEquivalent(current.RouteEntry, target.RouteEntry);

    private static bool VisibleNodeEquivalent(NavigationNode? current, NavigationNode? target)
    {
        if (current is null || target is null)
            return current is null && target is null;

        return (current, target) switch
        {
            (StackNode currentStack, StackNode targetStack) =>
                StringComparer.Ordinal.Equals(currentStack.Id, targetStack.Id) &&
                currentStack.Entries.Count == targetStack.Entries.Count &&
                CommonEntryPrefix(currentStack.Entries, targetStack.Entries) == currentStack.Entries.Count,
            (BranchHostNode currentHost, BranchHostNode targetHost) =>
                BranchShellEquivalent(currentHost, targetHost) &&
                VisibleNodeEquivalent(
                    currentHost.SelectedBranch?.Content,
                    targetHost.SelectedBranch?.Content),
            (ModalNode currentModal, ModalNode targetModal) => ModalEquivalent(currentModal, targetModal),
            _ => false
        };
    }

    private static bool BranchShellEquivalent(BranchHostNode current, BranchHostNode target)
    {
        if (!StringComparer.Ordinal.Equals(current.Id, target.Id) ||
            !StringComparer.Ordinal.Equals(current.SelectedBranchId, target.SelectedBranchId) ||
            !StringComparer.Ordinal.Equals(current.DefaultBranchId, target.DefaultBranchId) ||
            current.Branches.Count != target.Branches.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Branches.Count; index++)
        {
            NavigationBranch currentBranch = current.Branches[index];
            NavigationBranch targetBranch = target.Branches[index];
            if (!StringComparer.Ordinal.Equals(currentBranch.Id, targetBranch.Id) ||
                !StringComparer.Ordinal.Equals(currentBranch.Title, targetBranch.Title))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RouteEntryEquivalent(RouteEntry current, RouteEntry target)
    {
        if (!StringComparer.Ordinal.Equals(current.Id, target.Id) ||
            !Equals(current.Route, target.Route))
        {
            return false;
        }

        return MetadataEquivalent(current.Metadata, target.Metadata);
    }

    private static bool MetadataEquivalent(
        IReadOnlyDictionary<string, object?>? current,
        IReadOnlyDictionary<string, object?>? target)
    {
        if (current is null || target is null)
            return current is null && target is null;
        if (current.Count != target.Count)
            return false;

        foreach ((string key, object? value) in current)
            if (!target.TryGetValue(key, out object? targetValue) || !Equals(value, targetValue))
                return false;
        return true;
    }

    private static RouteEntry? PresentedEntry(WindowNode window)
    {
        if (window.Modals.Count > 0)
        {
            ModalNode modal = window.Modals[^1];
            return PresentedEntry(modal.Content) ?? modal.RouteEntry;
        }

        return PresentedEntry(window.Root);
    }

    private static RouteEntry? PresentedEntry(NavigationNode? node) => node switch
    {
        StackNode stack => stack.Top,
        BranchHostNode host => PresentedEntry(host.SelectedBranch?.Content),
        ModalNode modal => PresentedEntry(modal.Content) ?? modal.RouteEntry,
        _ => null
    };
}
