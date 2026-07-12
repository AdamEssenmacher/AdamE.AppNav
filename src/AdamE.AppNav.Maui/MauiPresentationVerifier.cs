using AdamE.AppNav.State;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal sealed class MauiPresentationVerifier : IMauiPresentationVerifier
{
    public static MauiPresentationVerifier Instance { get; } = new();

    public MauiPresentationVerificationMismatch? Verify(MauiPresentationVerificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var window = context.TargetState.ActiveWindow;
        if (window is null || (window.Root is null && window.Modals.Count == 0))
        {
            return VerifyEmptyState(context);
        }

        if (context.CurrentPage is null)
        {
            return Mismatch("$.root", DescribeWindow(window), "null");
        }

        if (context.AttachedWindow is not null &&
            !ReferenceEquals(context.AttachedWindow.Page, context.CurrentPage))
        {
            return Mismatch(
                "$.attachedWindow.Page",
                DescribePage(context.CurrentPage),
                DescribePage(context.AttachedWindow.Page));
        }

        var rootMismatch = window.Root is null
            ? VerifyRootlessModalHost(context.CurrentPage)
            : VerifyNode(window.Root, context.CurrentPage, "$.root", context.PresentationOptions);
        if (rootMismatch is not null)
        {
            return rootMismatch;
        }

        return VerifyModals(context.CurrentPage, window.Modals, "$.modals", context.PresentationOptions);
    }

    private static MauiPresentationVerificationMismatch? VerifyEmptyState(MauiPresentationVerificationContext context)
    {
        if (context.CurrentPage is not null)
        {
            return Mismatch("$.currentPage", "null", DescribePage(context.CurrentPage));
        }

        if (context.AttachedWindow?.Page is not null)
        {
            return Mismatch("$.attachedWindow.Page", "null", DescribePage(context.AttachedWindow.Page));
        }

        return null;
    }

    private static MauiPresentationVerificationMismatch? VerifyRootlessModalHost(Page page)
    {
        return page is NavigationPage or TabbedPage
            ? Mismatch("$.root", "synthetic root page", DescribePage(page))
            : null;
    }

    private static MauiPresentationVerificationMismatch? VerifyNode(
        NavigationNode node,
        Page? page,
        string path,
        MauiRoutePresentationOptions presentationOptions)
    {
        return node switch
        {
            StackNode stack => VerifyStack(stack, page, path),
            BranchHostNode branchHost => VerifyTabbedBranchHost(branchHost, page, path, presentationOptions),
            ModalNode modal => modal.Content is null
                ? VerifyRoutePage(modal.RouteEntry, page, path)
                : VerifyNode(modal.Content, page, path, presentationOptions),
            _ => Mismatch(path, node.GetType().Name, DescribePage(page))
        };
    }

    private static MauiPresentationVerificationMismatch? VerifyStack(StackNode stack, Page? page, string path)
    {
        if (stack.Entries.Count == 0)
        {
            return page is NavigationPage or TabbedPage
                ? Mismatch(path, "empty stack page", DescribePage(page))
                : null;
        }

        if (page is not NavigationPage navigationPage)
        {
            return Mismatch(path, $"NavigationPage host '{stack.Id}'", DescribePage(page));
        }

        var hostId = MauiPresentationMetadata.GetHostId(navigationPage);
        if (!StringComparer.Ordinal.Equals(hostId, stack.Id))
        {
            return Mismatch($"{path}.hostId", stack.Id, hostId ?? "null");
        }

        var navigationStack = navigationPage.Navigation.NavigationStack;
        var projection = MauiNavigationStackProjection.Create(navigationStack);
        if (projection.Error is { } error)
        {
            return Mismatch(
                $"{path}.nativeStack[{error.PageIndex}]",
                "valid route-owned page segment",
                error.Message);
        }

        if (projection.Segments.Count != stack.Entries.Count)
        {
            return Mismatch(
                $"{path}.entries.count",
                stack.Entries.Count.ToString(),
                projection.Segments.Count.ToString());
        }

        for (var i = 0; i < stack.Entries.Count; i++)
        {
            var routeEntryId = projection.Segments[i].RouteEntryId;
            if (!StringComparer.Ordinal.Equals(routeEntryId, stack.Entries[i].Id))
            {
                return Mismatch($"{path}.entries[{i}].routeEntryId", stack.Entries[i].Id, routeEntryId ?? "null");
            }
        }

        return null;
    }

    private static MauiPresentationVerificationMismatch? VerifyTabbedBranchHost(
        BranchHostNode branchHost,
        Page? page,
        string path,
        MauiRoutePresentationOptions presentationOptions)
    {
        if (page is not TabbedPage tabbedPage)
        {
            return Mismatch(path, $"TabbedPage host '{branchHost.Id}'", DescribePage(page));
        }

        var hostMismatch = VerifyHostId(tabbedPage, branchHost.Id, path);
        if (hostMismatch is not null)
        {
            return hostMismatch;
        }

        if (tabbedPage.Children.Count != branchHost.Branches.Count)
        {
            return Mismatch($"{path}.branches.count", branchHost.Branches.Count.ToString(), tabbedPage.Children.Count.ToString());
        }

        for (var i = 0; i < branchHost.Branches.Count; i++)
        {
            var branch = branchHost.Branches[i];
            var child = tabbedPage.Children[i];
            var branchId = MauiPresentationMetadata.GetBranchId(child);
            if (!StringComparer.Ordinal.Equals(branchId, branch.Id))
            {
                return Mismatch($"{path}.branches[{i}].branchId", branch.Id, branchId ?? "null");
            }

            var childMismatch = VerifyNode(branch.Content, child, $"{path}.branches[{i}].content", presentationOptions);
            if (childMismatch is not null)
            {
                return childMismatch;
            }
        }

        if (branchHost.SelectedBranch is null)
        {
            return Mismatch($"{path}.selectedBranchId", branchHost.SelectedBranchId, "missing");
        }

        var selectedBranchId = MauiPresentationMetadata.GetBranchId(tabbedPage.CurrentPage);
        return StringComparer.Ordinal.Equals(selectedBranchId, branchHost.SelectedBranchId)
            ? null
            : Mismatch($"{path}.selectedBranchId", branchHost.SelectedBranchId, selectedBranchId ?? "null");
    }

    private static MauiPresentationVerificationMismatch? VerifyModals(
        Page root,
        IReadOnlyList<ModalNode> modals,
        string path,
        MauiRoutePresentationOptions presentationOptions)
    {
        var modalStack = root.Navigation.ModalStack;
        if (modalStack.Count != modals.Count)
        {
            return Mismatch($"{path}.count", modals.Count.ToString(), modalStack.Count.ToString());
        }

        for (var i = 0; i < modals.Count; i++)
        {
            var modalPage = modalStack[i];
            var modalId = MauiPresentationMetadata.GetModalId(modalPage);
            if (!StringComparer.Ordinal.Equals(modalId, modals[i].Id))
            {
                return Mismatch($"{path}[{i}].modalId", modals[i].Id, modalId ?? "null");
            }

            var modalMismatch = modals[i].Content is null
                ? VerifyRoutePage(modals[i].RouteEntry, modalPage, $"{path}[{i}].route")
                : VerifyNode(modals[i].Content!, modalPage, $"{path}[{i}].content", presentationOptions);
            if (modalMismatch is not null)
            {
                return modalMismatch;
            }
        }

        return null;
    }

    private static MauiPresentationVerificationMismatch? VerifyRoutePage(RouteEntry routeEntry, Page? page, string path)
    {
        var routeEntryId = MauiPresentationMetadata.GetRouteEntryId(page);
        return StringComparer.Ordinal.Equals(routeEntryId, routeEntry.Id)
            ? null
            : Mismatch($"{path}.routeEntryId", routeEntry.Id, routeEntryId ?? "null");
    }

    private static MauiPresentationVerificationMismatch? VerifyHostId(Page page, string expectedHostId, string path)
    {
        var hostId = MauiPresentationMetadata.GetHostId(page);
        return StringComparer.Ordinal.Equals(hostId, expectedHostId)
            ? null
            : Mismatch($"{path}.hostId", expectedHostId, hostId ?? "null");
    }

    private static MauiPresentationVerificationMismatch Mismatch(string path, string expected, string actual)
    {
        return new MauiPresentationVerificationMismatch(path, expected, actual);
    }

    private static string DescribeWindow(WindowNode window)
    {
        return window.Root is null
            ? $"WindowNode '{window.Id}' with {window.Modals.Count} modals"
            : $"{window.Root.GetType().Name} '{window.Root.Id}'";
    }

    private static string DescribePage(Page? page)
    {
        if (page is null)
        {
            return "null";
        }

        var parts = new List<string> { page.GetType().Name };
        AddPart(parts, "host", MauiPresentationMetadata.GetHostId(page));
        AddPart(parts, "branch", MauiPresentationMetadata.GetBranchId(page));
        AddPart(parts, "routeEntry", MauiPresentationMetadata.GetRouteEntryId(page));
        AddPart(parts, "presentationOwner", MauiPresentationMetadata.GetPresentationOwnerRouteEntryId(page));
        AddPart(parts, "presentationKey", MauiPresentationMetadata.GetPresentationPageKey(page));
        AddPart(parts, "modal", MauiPresentationMetadata.GetModalId(page));
        return string.Join(" ", parts);
    }

    private static void AddPart(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}='{value}'");
        }
    }
}
