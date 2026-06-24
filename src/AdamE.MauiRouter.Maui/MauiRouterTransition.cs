using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

public static class MauiRouterTransition
{
    public static readonly BindableProperty SharedElementIdProperty =
        BindableProperty.CreateAttached("SharedElementId", typeof(string), typeof(MauiRouterTransition), null);

    public static void SetSharedElementId(BindableObject bindableObject, string? value)
    {
        ArgumentNullException.ThrowIfNull(bindableObject);
        bindableObject.SetValue(SharedElementIdProperty, value);
    }

    public static string? GetSharedElementId(BindableObject bindableObject)
    {
        ArgumentNullException.ThrowIfNull(bindableObject);
        return bindableObject.GetValue(SharedElementIdProperty) as string;
    }
}
