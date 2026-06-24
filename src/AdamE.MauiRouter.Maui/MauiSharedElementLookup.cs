using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

internal static class MauiSharedElementLookup
{
    public static VisualElement? Find(Element? root, string id)
    {
        if (root is null || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (root is VisualElement visual &&
            StringComparer.Ordinal.Equals(MauiRouterTransition.GetSharedElementId(visual), id))
        {
            return visual;
        }

        foreach (var child in Children(root))
        {
            if (Find(child, id) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<Element> Children(Element element)
    {
        switch (element)
        {
            case ContentPage contentPage when contentPage.Content is not null:
                yield return contentPage.Content;
                break;
            case ContentView contentView when contentView.Content is not null:
                yield return contentView.Content;
                break;
            case ScrollView scrollView when scrollView.Content is not null:
                yield return scrollView.Content;
                break;
            case Border border when border.Content is not null:
                yield return border.Content;
                break;
            case Layout layout:
                foreach (var child in layout.Children.OfType<Element>())
                {
                    yield return child;
                }

                break;
            case FlyoutPage flyout:
                if (flyout.Flyout is not null)
                {
                    yield return flyout.Flyout;
                }

                if (flyout.Detail is not null)
                {
                    yield return flyout.Detail;
                }

                break;
            case TabbedPage tabbed:
                foreach (var child in tabbed.Children)
                {
                    yield return child;
                }

                break;
            case NavigationPage navigation:
                foreach (var child in navigation.Navigation.NavigationStack)
                {
                    yield return child;
                }

                break;
        }
    }
}
