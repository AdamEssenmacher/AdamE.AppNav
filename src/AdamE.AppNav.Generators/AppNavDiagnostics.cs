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
        "Route value type requires an explicit codec",
        "Route '{0}' uses value type '{1}' for '{2}', which requires registration with RouteTableBuilder.AddValueCodec");

    public static readonly DiagnosticDescriptor InvalidRouteType = Error(
        "APPNAV012",
        "Route declaration type is invalid",
        "Type '{0}' has AppNavRouteAttribute but must be a concrete, non-generic, accessible AppRoute");

    public static readonly DiagnosticDescriptor DuplicateRoutePropertyName = Error(
        "APPNAV013",
        "Route exposes duplicate public properties",
        "Route '{0}' exposes multiple public readable properties named '{1}' ignoring case");

    public static readonly DiagnosticDescriptor UnsafeOptionalPathConstructorParameter = Error(
        "APPNAV014",
        "Optional path constructor parameter is not missing-safe",
        "Optional path binding '{0}' on route '{1}' targets constructor parameter '{2}', but the path value may be absent; make the parameter nullable or provide a default value");

    public static readonly DiagnosticDescriptor InvalidQueryCollection = Error(
        "APPNAV015",
        "Repeated query collection type is invalid",
        "Route '{0}' uses invalid repeated-query collection type '{1}' for '{2}': {3}");

    public static readonly DiagnosticDescriptor QueryConstructorTypeMismatch = Error(
        "APPNAV016",
        "Query property and constructor parameter types differ",
        "Convention query binding '{0}' on route '{1}' uses property type '{2}', but constructor parameter '{3}' uses '{4}'; the types must match");

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
