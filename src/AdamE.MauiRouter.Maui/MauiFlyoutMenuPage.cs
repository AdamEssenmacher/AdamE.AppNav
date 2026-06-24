using AdamE.MauiRouter.State;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal sealed class MauiFlyoutMenuPage : ContentPage
{
    private readonly IReadOnlyList<NavigationBranch> _branches;

    public MauiFlyoutMenuPage(IReadOnlyList<NavigationBranch> branches, string selectedItemId)
    {
        _branches = branches;
        Title = "Navigation";

        var collectionView = new CollectionView
        {
            ItemsSource = branches,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label
                {
                    Margin = new Thickness(20, 14),
                    FontSize = 17
                };
                label.SetBinding(Label.TextProperty, nameof(NavigationBranch.Title));
                return label;
            })
        };

        collectionView.SelectedItem = branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, selectedItemId));
        collectionView.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is NavigationBranch branch)
            {
                SelectedItemChanged?.Invoke(this, branch.Id);
            }
        };

        Content = collectionView;
    }

    public event EventHandler<string>? SelectedItemChanged;

    public NavigationBranch? FindBranch(string id)
    {
        return _branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, id));
    }
}
