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

    public void SetWindowPage(Window window, Page? page) =>
        window.Page = page;
}
