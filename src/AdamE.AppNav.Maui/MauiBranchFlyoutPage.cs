using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal sealed record MauiFlyoutBranchPresentation(
    string Id,
    string Title,
    Page Page,
    ImageSource? IconImageSource);

internal sealed class MauiFlyoutBranchSelectedEventArgs(string branchId) : EventArgs
{
    public string BranchId { get; } = branchId;
}

internal sealed class MauiBranchFlyoutPage : FlyoutPage
{
    private readonly VerticalStackLayout _menuItems = new() { Spacing = 0 };
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private IReadOnlyList<MauiFlyoutBranchPresentation> _branches = [];

    public MauiBranchFlyoutPage(MauiFlyoutBranchHostOptions options)
    {
        ApplyOptions(options);
        Flyout = new ContentPage
        {
            Title = options.MenuTitle,
            Content = new ScrollView { Content = _menuItems }
        };
    }

    public event EventHandler<MauiFlyoutBranchSelectedEventArgs>? BranchSelected;

    public IReadOnlyList<MauiFlyoutBranchPresentation> Branches => _branches;

    public string? SelectedBranchId { get; private set; }

    public void ApplyOptions(MauiFlyoutBranchHostOptions options)
    {
        FlyoutLayoutBehavior = options.LayoutBehavior;
        IsGestureEnabled = options.IsGestureEnabled;
        if (Flyout is not null)
            Flyout.Title = options.MenuTitle;
    }

    public Page? FindBranchPage(string branchId) =>
        _branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, branchId))?.Page;

    public void SetBranches(IReadOnlyList<MauiFlyoutBranchPresentation> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        _branches = branches.ToArray();
        RebuildMenu();
    }

    public void SetSelectedBranch(string? branchId)
    {
        SelectedBranchId = branchId;
        foreach ((string id, Button button) in _buttons)
            button.FontAttributes = StringComparer.Ordinal.Equals(id, branchId)
                ? FontAttributes.Bold
                : FontAttributes.None;
    }

    internal void RequestBranchSelection(string branchId) =>
        BranchSelected?.Invoke(this, new MauiFlyoutBranchSelectedEventArgs(branchId));

    private void RebuildMenu()
    {
        foreach (Button button in _buttons.Values)
            button.Clicked -= OnBranchButtonClicked;
        _buttons.Clear();
        _menuItems.Children.Clear();

        foreach (MauiFlyoutBranchPresentation branch in _branches)
        {
            var button = new Button
            {
                Text = branch.Title,
                ImageSource = branch.IconImageSource,
                CommandParameter = branch.Id,
                HorizontalOptions = LayoutOptions.Fill
            };
            AutomationProperties.SetName(button, branch.Title);
            button.Clicked += OnBranchButtonClicked;
            _buttons.Add(branch.Id, button);
            _menuItems.Children.Add(button);
        }

        if (SelectedBranchId is not null)
            SetSelectedBranch(SelectedBranchId);
    }

    private void OnBranchButtonClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string branchId })
            RequestBranchSelection(branchId);
    }
}
