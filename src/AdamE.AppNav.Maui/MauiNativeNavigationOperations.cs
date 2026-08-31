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
    Func<Page, bool> isInvalidPage,
    Func<Window, bool> isInvalidWindow) : IMauiNativeNavigationOperations
{
    public Task PushAsync(NavigationPage navigationPage, Page page, bool animated)
    {
        ThrowIfInvalid(navigationPage);
        return inner.PushAsync(navigationPage, page, animated);
    }

    public Task<Page?> PopAsync(NavigationPage navigationPage, bool animated)
    {
        ThrowIfInvalid(navigationPage);
        return inner.PopAsync(navigationPage, animated);
    }

    public Task PushModalAsync(Page host, Page page, bool animated)
    {
        ThrowIfInvalid(host);
        return inner.PushModalAsync(host, page, animated);
    }

    public Task<Page?> PopModalAsync(Page host, bool animated)
    {
        ThrowIfInvalid(host);
        return inner.PopModalAsync(host, animated);
    }

    public void InsertTab(TabbedPage tabbedPage, int index, Page page)
    {
        ThrowIfInvalid(tabbedPage);
        inner.InsertTab(tabbedPage, index, page);
    }

    public void RemoveTab(TabbedPage tabbedPage, Page page)
    {
        ThrowIfInvalid(tabbedPage);
        inner.RemoveTab(tabbedPage, page);
    }

    public void SetCurrentTab(TabbedPage tabbedPage, Page? page)
    {
        ThrowIfInvalid(tabbedPage);
        inner.SetCurrentTab(tabbedPage, page);
    }

    public void SetFlyoutDetail(FlyoutPage flyoutPage, Page page)
    {
        ThrowIfInvalid(flyoutPage);
        inner.SetFlyoutDetail(flyoutPage, page);
    }

    public void SetFlyoutPresented(FlyoutPage flyoutPage, bool isPresented)
    {
        ThrowIfInvalid(flyoutPage);
        inner.SetFlyoutPresented(flyoutPage, isPresented);
    }

    public void SetFlyoutBranches(
        MauiBranchFlyoutPage flyoutPage,
        IReadOnlyList<MauiFlyoutBranchPresentation> branches)
    {
        ThrowIfInvalid(flyoutPage);
        inner.SetFlyoutBranches(flyoutPage, branches);
    }

    public void SetSelectedFlyoutBranch(MauiBranchFlyoutPage flyoutPage, string branchId)
    {
        ThrowIfInvalid(flyoutPage);
        inner.SetSelectedFlyoutBranch(flyoutPage, branchId);
    }

    public void SetWindowPage(Window window, Page? page)
    {
        if (isInvalidWindow(window))
            throw new MauiNativeTreeInvalidatedException();

        inner.SetWindowPage(window, page);
    }

    private void ThrowIfInvalid(Page page)
    {
        if (isInvalidPage(page))
            throw new MauiNativeTreeInvalidatedException();
    }
}

internal sealed class MauiNativeTreeInvalidatedException : OperationCanceledException
{
    public MauiNativeTreeInvalidatedException()
        : base("Native MAUI navigation was canceled because its window was destroyed.")
    {
    }
}
