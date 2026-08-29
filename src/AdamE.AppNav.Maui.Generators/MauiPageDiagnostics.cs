using Microsoft.CodeAnalysis;

namespace AdamE.AppNav.Maui.Generators;

internal static class MauiPageDiagnostics
{
    public static readonly DiagnosticDescriptor InvalidPageRoute = Error(
        "APPNAV020",
        "MAUI page route mapping is invalid",
        "Page '{0}' maps to route '{1}', but that route is not an attributed AppNav route");

    public static readonly DiagnosticDescriptor InvalidPageType = Error(
        "APPNAV021",
        "MAUI page mapping must target a Page",
        "Type '{0}' has MauiRoutePageAttribute but does not derive from Microsoft.Maui.Controls.Page");

    public static readonly DiagnosticDescriptor AmbiguousPageConstructor = Error(
        "APPNAV022",
        "MAUI page constructor is ambiguous",
        "Page '{0}' has multiple public constructors; mark one with ActivatorUtilitiesConstructorAttribute");

    public static readonly DiagnosticDescriptor MissingPageRouteParameter = Error(
        "APPNAV023",
        "MAUI page constructor does not accept the mapped route",
        "Page '{0}' must expose a selected public constructor with a parameter assignable from route '{1}'");

    public static readonly DiagnosticDescriptor DuplicatePageRoute = Error(
        "APPNAV024",
        "MAUI page route is mapped more than once",
        "Route '{0}' is mapped to both page '{1}' and page '{2}'");

    public static readonly DiagnosticDescriptor InvalidPageModelType = Error(
        "APPNAV025",
        "MAUI page model type is invalid",
        "Page '{0}' uses page model type '{1}', but page model types must be non-generic and accessible from generated code");

    private static DiagnosticDescriptor Error(string id, string title, string message)
    {
        return new DiagnosticDescriptor(
            id,
            title,
            message,
            "AppNav",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
