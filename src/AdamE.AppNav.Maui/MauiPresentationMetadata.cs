using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal static class MauiPresentationMetadata
{
    private static readonly BindableProperty HostIdProperty =
        BindableProperty.CreateAttached("RouterHostId", typeof(string), typeof(MauiPresentationMetadata), null);

    private static readonly BindableProperty BranchIdProperty =
        BindableProperty.CreateAttached("RouterBranchId", typeof(string), typeof(MauiPresentationMetadata), null);

    private static readonly BindableProperty RouteEntryIdProperty =
        BindableProperty.CreateAttached("RouterRouteEntryId", typeof(string), typeof(MauiPresentationMetadata), null);

    private static readonly BindableProperty ModalIdProperty =
        BindableProperty.CreateAttached("RouterModalId", typeof(string), typeof(MauiPresentationMetadata), null);

    private static readonly BindableProperty PresentationOwnerRouteEntryIdProperty =
        BindableProperty.CreateAttached(
            "RouterPresentationOwnerRouteEntryId",
            typeof(string),
            typeof(MauiPresentationMetadata),
            null);

    private static readonly BindableProperty PresentationPageKeyProperty =
        BindableProperty.CreateAttached(
            "RouterPresentationPageKey",
            typeof(string),
            typeof(MauiPresentationMetadata),
            null);

    public static void SetHostId(BindableObject bindableObject, string? id)
    {
        bindableObject.SetValue(HostIdProperty, id);
    }

    public static string? GetHostId(BindableObject? bindableObject)
    {
        return bindableObject?.GetValue(HostIdProperty) as string;
    }

    public static void SetBranchId(BindableObject bindableObject, string? id)
    {
        bindableObject.SetValue(BranchIdProperty, id);
    }

    public static string? GetBranchId(BindableObject? bindableObject)
    {
        return bindableObject?.GetValue(BranchIdProperty) as string;
    }

    public static void SetRouteEntryId(BindableObject bindableObject, string? id)
    {
        bindableObject.SetValue(RouteEntryIdProperty, id);
    }

    public static string? GetRouteEntryId(BindableObject? bindableObject)
    {
        return bindableObject?.GetValue(RouteEntryIdProperty) as string;
    }

    public static void SetModalId(BindableObject bindableObject, string? id)
    {
        bindableObject.SetValue(ModalIdProperty, id);
    }

    public static string? GetModalId(BindableObject? bindableObject)
    {
        return bindableObject?.GetValue(ModalIdProperty) as string;
    }

    public static void SetPresentationOwnerRouteEntryId(BindableObject bindableObject, string? id)
    {
        bindableObject.SetValue(PresentationOwnerRouteEntryIdProperty, id);
    }

    public static string? GetPresentationOwnerRouteEntryId(BindableObject? bindableObject)
    {
        return bindableObject?.GetValue(PresentationOwnerRouteEntryIdProperty) as string;
    }

    public static void SetPresentationPageKey(BindableObject bindableObject, string? key)
    {
        bindableObject.SetValue(PresentationPageKeyProperty, key);
    }

    public static string? GetPresentationPageKey(BindableObject? bindableObject)
    {
        return bindableObject?.GetValue(PresentationPageKeyProperty) as string;
    }
}
