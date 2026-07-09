using Microsoft.CodeAnalysis;

namespace AdamE.AppNav.Generators;

internal static class AppNavDiagnostics
{
    public static readonly DiagnosticDescriptor InvalidRouteTemplate = Error(
        "APPNAV001",
        "Route template is invalid",
        "Route '{0}' has invalid template: {1}");

    public static readonly DiagnosticDescriptor MissingPathMember = Error(
        "APPNAV002",
        "Route path parameter has no matching public property",
        "Route '{0}' must expose a public readable property named '{1}' for template '{2}'");

    public static readonly DiagnosticDescriptor InvalidQueryProperty = Error(
        "APPNAV003",
        "Route query property is invalid",
        "Route '{0}' has invalid query property '{1}'");

    public static readonly DiagnosticDescriptor DuplicateQueryName = Error(
        "APPNAV004",
        "Route query parameter is registered more than once",
        "Route '{0}' registers query parameter '{1}' more than once");

    public static readonly DiagnosticDescriptor PathQueryOverlap = Error(
        "APPNAV005",
        "Route member is bound by both path and query",
        "Route member '{0}.{1}' is already bound by the route path");

    public static readonly DiagnosticDescriptor NoUsableConstructor = Error(
        "APPNAV006",
        "Route has no usable public constructor",
        "Route '{0}' does not expose a public constructor compatible with route template '{1}'");

    public static readonly DiagnosticDescriptor AmbiguousConstructor = Error(
        "APPNAV007",
        "Route has ambiguous public constructors",
        "Route '{0}' has multiple public constructors compatible with route template '{1}'");

    public static readonly DiagnosticDescriptor UnsafeQueryConstructorParameter = Error(
        "APPNAV008",
        "Query-bound constructor parameter is not missing-safe",
        "Convention query binding '{0}' on route '{1}' targets constructor parameter '{2}', but query values are optional; make the parameter nullable or provide a default value");

    public static readonly DiagnosticDescriptor DuplicateRouteTemplate = Error(
        "APPNAV009",
        "Route template is registered more than once",
        "Route template '{0}' is declared by both '{1}' and '{2}'");

    public static readonly DiagnosticDescriptor AmbiguousRouteTemplate = Error(
        "APPNAV010",
        "Route templates are ambiguous",
        "Route templates '{0}' and '{1}' can match the same URI path");

    public static readonly DiagnosticDescriptor UnsupportedRouteValueType = Warning(
        "APPNAV011",
        "Route value type falls back to runtime conversion",
        "Route '{0}' uses value type '{1}' for '{2}', which is not directly source-generated; runtime conversion may require trim/AOT annotations");

    public static readonly DiagnosticDescriptor InvalidRouteType = Error(
        "APPNAV012",
        "Route declaration type is invalid",
        "Type '{0}' has AppNavRouteAttribute but must be a concrete, non-generic, accessible AppRoute");

    public static readonly DiagnosticDescriptor DuplicateRoutePropertyName = Error(
        "APPNAV013",
        "Route exposes duplicate public properties",
        "Route '{0}' exposes multiple public readable properties named '{1}' ignoring case");

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

    private static DiagnosticDescriptor Warning(string id, string title, string message)
    {
        return new DiagnosticDescriptor(
            id,
            title,
            message,
            "AppNav",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
    }
}
