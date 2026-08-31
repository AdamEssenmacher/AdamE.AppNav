using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal interface IMauiNativeNavigationOperations
{
    Task PushAsync(NavigationPage navigationPage, Page page, bool animated);

    Task<Page?> PopAsync(NavigationPage navigationPage, bool animated);

    Task PushModalAsync(Page host, Page page, bool animated);

    Task<Page?> PopModalAsync(Page host, bool animated);

    void InsertTab(TabbedPage tabbedPage, int index, Page page);

    void RemoveTab(TabbedPage tabbedPage, Page page);

    void SetCurrentTab(TabbedPage tabbedPage, Page? page);

    void SetFlyoutDetail(FlyoutPage flyoutPage, Page page);

    void SetFlyoutPresented(FlyoutPage flyoutPage, bool isPresented);

    void SetFlyoutBranches(
        MauiBranchFlyoutPage flyoutPage,
        IReadOnlyList<MauiFlyoutBranchPresentation> branches);

    void SetSelectedFlyoutBranch(MauiBranchFlyoutPage flyoutPage, string branchId);

    void SetWindowPage(Window window, Page? page);
}

internal sealed class MauiNativeNavigationOperations : IMauiNativeNavigationOperations
{
    public static MauiNativeNavigationOperations Instance { get; } = new();

    public Task PushAsync(NavigationPage navigationPage, Page page, bool animated) =>
        navigationPage.Navigation.PushAsync(page, animated);

    public Task<Page?> PopAsync(NavigationPage navigationPage, bool animated) =>
        navigationPage.Navigation.PopAsync(animated);

    public Task PushModalAsync(Page host, Page page, bool animated) =>
        host.Navigation.PushModalAsync(page, animated);

    public Task<Page?> PopModalAsync(Page host, bool animated) =>
        host.Navigation.PopModalAsync(animated);

    public void InsertTab(TabbedPage tabbedPage, int index, Page page) =>
        tabbedPage.Children.Insert(index, page);

    public void RemoveTab(TabbedPage tabbedPage, Page page) =>
        tabbedPage.Children.Remove(page);

    public void SetCurrentTab(TabbedPage tabbedPage, Page? page) =>
        tabbedPage.CurrentPage = page;

    public void SetFlyoutDetail(FlyoutPage flyoutPage, Page page) =>
        flyoutPage.Detail = page;

    public void SetFlyoutPresented(FlyoutPage flyoutPage, bool isPresented) =>
        flyoutPage.IsPresented = isPresented;

    public void SetFlyoutBranches(
        MauiBranchFlyoutPage flyoutPage,
        IReadOnlyList<MauiFlyoutBranchPresentation> branches) =>
        flyoutPage.SetBranches(branches);

    public void SetSelectedFlyoutBranch(MauiBranchFlyoutPage flyoutPage, string branchId) =>
        flyoutPage.SetSelectedBranch(branchId);

    public void SetWindowPage(Window window, Page? page) =>
        window.Page = page;
}

internal sealed class GuardedMauiNativeNavigationOperations(
    IMauiNativeNavigationOperations inner,
    Func<Page, bool> canMutatePage,
    Func<Window, bool> canMutateWindow) : IMauiNativeNavigationOperations
{
    public Task PushAsync(NavigationPage navigationPage, Page page, bool animated)
    {
        Guard(navigationPage);
        Guard(page);
        return inner.PushAsync(navigationPage, page, animated);
    }

    public Task<Page?> PopAsync(NavigationPage navigationPage, bool animated)
    {
        Guard(navigationPage);
        return inner.PopAsync(navigationPage, animated);
    }

    public Task PushModalAsync(Page host, Page page, bool animated)
    {
        Guard(host);
        Guard(page);
        return inner.PushModalAsync(host, page, animated);
    }

    public Task<Page?> PopModalAsync(Page host, bool animated)
    {
        Guard(host);
        return inner.PopModalAsync(host, animated);
    }

    public void InsertTab(TabbedPage tabbedPage, int index, Page page)
    {
        Guard(tabbedPage);
        Guard(page);
        inner.InsertTab(tabbedPage, index, page);
    }

    public void RemoveTab(TabbedPage tabbedPage, Page page)
    {
        Guard(tabbedPage);
        Guard(page);
        inner.RemoveTab(tabbedPage, page);
    }

    public void SetCurrentTab(TabbedPage tabbedPage, Page? page)
    {
        Guard(tabbedPage);
        if (page is not null)
            Guard(page);
        inner.SetCurrentTab(tabbedPage, page);
    }

    public void SetFlyoutDetail(FlyoutPage flyoutPage, Page page)
    {
        Guard(flyoutPage);
        Guard(page);
        inner.SetFlyoutDetail(flyoutPage, page);
    }

    public void SetFlyoutPresented(FlyoutPage flyoutPage, bool isPresented)
    {
        Guard(flyoutPage);
        inner.SetFlyoutPresented(flyoutPage, isPresented);
    }

    public void SetFlyoutBranches(
        MauiBranchFlyoutPage flyoutPage,
        IReadOnlyList<MauiFlyoutBranchPresentation> branches)
    {
        Guard(flyoutPage);
        foreach (MauiFlyoutBranchPresentation branch in branches)
            Guard(branch.Page);
        inner.SetFlyoutBranches(flyoutPage, branches);
    }

    public void SetSelectedFlyoutBranch(MauiBranchFlyoutPage flyoutPage, string branchId)
    {
        Guard(flyoutPage);
        inner.SetSelectedFlyoutBranch(flyoutPage, branchId);
    }

    public void SetWindowPage(Window window, Page? page)
    {
        if (!canMutateWindow(window))
            throw new MauiNativeTreeInvalidatedException();
        inner.SetWindowPage(window, page);
    }

    private void Guard(Page page)
    {
        if (!canMutatePage(page))
            throw new MauiNativeTreeInvalidatedException();
    }
}
