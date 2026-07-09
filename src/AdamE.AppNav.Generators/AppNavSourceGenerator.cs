using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AdamE.AppNav.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class AppNavSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<(Compilation Compilation, AnalyzerConfigOptionsProvider Options)> input =
            context.CompilationProvider.Combine(context.AnalyzerConfigOptionsProvider);

        context.RegisterSourceOutput(input, static (productionContext, value) =>
            Execute(productionContext, value.Compilation, value.Options));
    }

    private static void Execute(
        SourceProductionContext context,
        Compilation compilation,
        AnalyzerConfigOptionsProvider options)
    {
        var symbols = AppNavSymbols.Create(compilation);
        if (symbols.AppRoute is null)
            return;

        var sawAppNavDeclaration = false;
        List<RouteModel> routes = new();
        foreach (INamedTypeSymbol type in SymbolWalker.GetAllTypes(compilation.GlobalNamespace))
        {
            if (!SymbolFacts.IsDeclaredInSource(type))
                continue;

            AttributeData? routeAttribute = SymbolFacts.GetAttribute(type, AppNavSymbols.RouteAttributeName);
            if (routeAttribute is null)
                continue;

            sawAppNavDeclaration = true;
            if (type.IsAbstract ||
                SymbolFacts.ContainsTypeParameters(type) ||
                !SymbolFacts.IsAccessibleFromGeneratedCode(type) ||
                !SymbolFacts.InheritsFrom(type, symbols.AppRoute))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.InvalidRouteType,
                    SymbolFacts.GetLocation(type, routeAttribute),
                    type.ToDisplayString()));
                continue;
            }

            RouteModel? model = RouteModelFactory.TryCreate(type, routeAttribute, symbols, context.ReportDiagnostic);
            if (model is not null)
                routes.Add(model);
        }

        ValidateRouteTable(routes, context.ReportDiagnostic);

        List<PageModel> pages = new();
        foreach (INamedTypeSymbol type in SymbolWalker.GetAllTypes(compilation.GlobalNamespace))
        {
            if (!SymbolFacts.IsDeclaredInSource(type))
                continue;

            AttributeData? pageAttribute = SymbolFacts.GetAttribute(type, AppNavSymbols.MauiRoutePageAttributeName);
            if (pageAttribute is null)
                continue;

            sawAppNavDeclaration = true;
            PageModel? model = PageModelFactory.TryCreate(
                type,
                pageAttribute,
                symbols,
                context.ReportDiagnostic);
            if (model is not null)
                pages.Add(model);
        }

        ValidatePageMappings(pages, context.ReportDiagnostic);

        if (routes.Count == 0 && pages.Count == 0 && !sawAppNavDeclaration)
            return;

        string rootNamespace = GetRootNamespace(compilation, options);
        string source = AppNavSourceEmitter.Emit(rootNamespace, routes, pages);
        context.AddSource("AppNavGenerated.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static void ValidateRouteTable(
        IReadOnlyList<RouteModel> routes,
        Action<Diagnostic> reportDiagnostic)
    {
        for (var i = 0; i < routes.Count; i++)
        {
            for (int j = i + 1; j < routes.Count; j++)
            {
                RouteModel left = routes[i];
                RouteModel right = routes[j];

                if (StringComparer.OrdinalIgnoreCase.Equals(left.Template.Value, right.Template.Value))
                {
                    reportDiagnostic(Diagnostic.Create(
                        AppNavDiagnostics.DuplicateRouteTemplate,
                        right.Location,
                        right.Template.Value,
                        left.RouteType.ToDisplayString(),
                        right.RouteType.ToDisplayString()));
                    continue;
                }

                if (left.Template.ComparePrecedence(right.Template) == 0 &&
                    left.Template.CanOverlap(right.Template))
                {
                    reportDiagnostic(Diagnostic.Create(
                        AppNavDiagnostics.AmbiguousRouteTemplate,
                        right.Location,
                        left.Template.Value,
                        right.Template.Value));
                }
            }
        }
    }

    private static void ValidatePageMappings(
        IReadOnlyList<PageModel> pages,
        Action<Diagnostic> reportDiagnostic)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            for (int j = i + 1; j < pages.Count; j++)
            {
                PageModel left = pages[i];
                PageModel right = pages[j];
                if (!SymbolEqualityComparer.Default.Equals(left.RouteType, right.RouteType))
                    continue;

                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.DuplicatePageRoute,
                    right.Location,
                    right.RouteType.ToDisplayString(),
                    left.PageType.ToDisplayString(),
                    right.PageType.ToDisplayString()));
            }
        }
    }

    private static string GetRootNamespace(
        Compilation compilation,
        AnalyzerConfigOptionsProvider options)
    {
        if (options.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? rootNamespace) &&
            IsValidNamespace(rootNamespace))
            return rootNamespace!;

        string fallback = compilation.AssemblyName ?? "AppNavGeneratedAssembly";
        var builder = new StringBuilder(fallback.Length);
        var nextMustStart = true;
        foreach (char ch in fallback)
        {
            if (ch == '.')
            {
                if (builder.Length > 0 && builder[builder.Length - 1] != '.')
                {
                    builder.Append('.');
                    nextMustStart = true;
                }

                continue;
            }

            if (SyntaxFacts.IsIdentifierPartCharacter(ch) &&
                (!nextMustStart || SyntaxFacts.IsIdentifierStartCharacter(ch)))
            {
                builder.Append(ch);
                nextMustStart = false;
                continue;
            }

            if (nextMustStart)
            {
                builder.Append('_');
                nextMustStart = false;
            }
        }

        string candidate = builder.ToString().Trim('.');
        return IsValidNamespace(candidate) ? candidate : "AppNavGeneratedAssembly";
    }

    private static bool IsValidNamespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (string part in value.Split('.'))
        {
            if (part.Length == 0 || !SyntaxFacts.IsValidIdentifier(part))
                return false;
        }

        return true;
    }
}

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AppNavFluentRegistrationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            AppNavDiagnostics.InvalidRouteTemplate,
            AppNavDiagnostics.MissingPathMember,
            AppNavDiagnostics.InvalidQueryProperty,
            AppNavDiagnostics.DuplicateQueryName,
            AppNavDiagnostics.PathQueryOverlap,
            AppNavDiagnostics.NoUsableConstructor,
            AppNavDiagnostics.AmbiguousConstructor,
            AppNavDiagnostics.UnsafeQueryConstructorParameter,
            AppNavDiagnostics.UnsupportedRouteValueType,
            AppNavDiagnostics.DuplicateRoutePropertyName);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not InvocationExpressionSyntax invocation)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;

        if (method.Name != "MapRoute" ||
            method.TypeArguments.Length != 1 ||
            method.ContainingType?.ToDisplayString() != "AdamE.AppNav.Routing.RouteTableBuilder")
            return;

        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        Optional<object?> constant = context.SemanticModel.GetConstantValue(
            invocation.ArgumentList.Arguments[0].Expression,
            context.CancellationToken);
        if (!constant.HasValue || constant.Value is not string template)
            return;

        var symbols = AppNavSymbols.Create(context.Compilation);
        RouteModelFactory.ValidateFluentRoute(
            method.TypeArguments[0] as INamedTypeSymbol,
            template,
            invocation.GetLocation(),
            symbols,
            context.ReportDiagnostic);
    }
}

internal sealed class AppNavSymbols
{
    public const string AppRouteName = "AdamE.AppNav.AppRoute";
    public const string RouteAttributeName = "AdamE.AppNav.Routing.AppNavRouteAttribute";
    public const string QueryAttributeName = "AdamE.AppNav.Routing.AppNavQueryAttribute";
    public const string QueryMetadataAttributeName = "AdamE.AppNav.Routing.AppNavQueryMetadataAttribute";
    public const string RouteMetadataKeyName = "AdamE.AppNav.Routing.RouteMetadataKey`1";
    public const string MauiRoutePageAttributeName = "AdamE.AppNav.Maui.MauiRoutePageAttribute";
    public const string MauiPageName = "Microsoft.Maui.Controls.Page";
    public const string ActivatorUtilitiesConstructorAttributeName =
        "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute";

    private AppNavSymbols(
        INamedTypeSymbol? appRoute,
        INamedTypeSymbol? routeMetadataKey,
        INamedTypeSymbol? mauiPage,
        INamedTypeSymbol? mauiRoutePageAttribute)
    {
        AppRoute = appRoute;
        RouteMetadataKey = routeMetadataKey;
        MauiPage = mauiPage;
        MauiRoutePageAttribute = mauiRoutePageAttribute;
    }

    public INamedTypeSymbol? AppRoute { get; }

    public INamedTypeSymbol? RouteMetadataKey { get; }

    public INamedTypeSymbol? MauiPage { get; }

    public INamedTypeSymbol? MauiRoutePageAttribute { get; }

    public static AppNavSymbols Create(Compilation compilation)
    {
        return new AppNavSymbols(
            compilation.GetTypeByMetadataName(AppRouteName),
            compilation.GetTypeByMetadataName(RouteMetadataKeyName),
            compilation.GetTypeByMetadataName(MauiPageName),
            compilation.GetTypeByMetadataName(MauiRoutePageAttributeName));
    }
}

internal static class SymbolWalker
{
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol root)
    {
        foreach (INamespaceSymbol namespaceSymbol in root.GetNamespaceMembers())
        foreach (INamedTypeSymbol type in GetAllTypesInNamespace(namespaceSymbol))
            yield return type;

        foreach (INamedTypeSymbol type in root.GetTypeMembers())
        foreach (INamedTypeSymbol nestedOrSelf in GetAllTypes(type))
            yield return nestedOrSelf;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypesInNamespace(INamespaceSymbol namespaceSymbol)
    {
        foreach (INamespaceSymbol child in namespaceSymbol.GetNamespaceMembers())
        foreach (INamedTypeSymbol type in GetAllTypesInNamespace(child))
            yield return type;

        foreach (INamedTypeSymbol type in namespaceSymbol.GetTypeMembers())
        foreach (INamedTypeSymbol nestedOrSelf in GetAllTypes(type))
            yield return nestedOrSelf;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamedTypeSymbol type)
    {
        yield return type;

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        foreach (INamedTypeSymbol nestedOrSelf in GetAllTypes(nested))
            yield return nestedOrSelf;
    }
}

internal static class SymbolFacts
{
    public static bool InheritsFrom(ITypeSymbol? type, INamedTypeSymbol baseType)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }

        return false;
    }

    public static AttributeData? GetAttribute(ISymbol symbol, string metadataName)
    {
        return symbol.GetAttributes()
            .FirstOrDefault(attribute => IsAttribute(attribute, metadataName));
    }

    public static IEnumerable<AttributeData> GetAttributes(ISymbol symbol, string metadataName)
    {
        return symbol.GetAttributes()
            .Where(attribute => IsAttribute(attribute, metadataName));
    }

    private static bool IsAttribute(AttributeData attribute, string metadataName)
    {
        INamedTypeSymbol? attributeClass = attribute.AttributeClass;
        if (attributeClass is null)
            return false;

        return attributeClass.ToDisplayString() == metadataName ||
               attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::" + metadataName;
    }

    public static Location? GetLocation(ISymbol symbol, AttributeData? attribute = null)
    {
        SyntaxReference? syntaxReference = attribute?.ApplicationSyntaxReference;
        return syntaxReference?.GetSyntax().GetLocation() ?? symbol.Locations.FirstOrDefault();
    }

    public static bool IsDeclaredInSource(ISymbol symbol)
    {
        return symbol.Locations.Any(static location => location.IsInSource);
    }

    public static bool ContainsTypeParameters(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0)
                return true;
        }

        return false;
    }

    public static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (!IsAccessibleFromGeneratedCode(current.DeclaredAccessibility))
                return false;
        }

        return true;
    }

    private static bool IsAccessibleFromGeneratedCode(Accessibility accessibility)
    {
        return accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
    }

    public static string TypeName(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    public static string MetadataTypeName(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    public static string Identifier(string name)
    {
        return SyntaxFacts.IsValidIdentifier(name) &&
               SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None &&
               SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None
            ? name
            : "@" + name;
    }

    public static bool IsAssignableTo(ITypeSymbol source, ITypeSymbol target)
    {
        for (ITypeSymbol? current = source; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        }

        return source.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, target));
    }
}

internal static class RouteModelFactory
{
    public static RouteModel? TryCreate(
        INamedTypeSymbol routeType,
        AttributeData attribute,
        AppNavSymbols symbols,
        Action<Diagnostic> reportDiagnostic)
    {
        string? template = attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : null;
        Location? location = SymbolFacts.GetLocation(routeType, attribute);

        if (!ParsedRouteTemplate.TryParse(template, out ParsedRouteTemplate? parsedTemplate, out string? parseError))
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.InvalidRouteTemplate,
                location,
                routeType.ToDisplayString(),
                parseError ?? "Template was not supplied."));
            return null;
        }

        return TryCreate(routeType, parsedTemplate!, location, symbols, reportDiagnostic);
    }

    public static void ValidateFluentRoute(
        INamedTypeSymbol? routeType,
        string template,
        Location? location,
        AppNavSymbols symbols,
        Action<Diagnostic> reportDiagnostic)
    {
        if (routeType is null)
            return;

        if (!ParsedRouteTemplate.TryParse(
                template,
                out ParsedRouteTemplate? parsedTemplate,
                out string? parseError,
                allowCustomConstraints: true))
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.InvalidRouteTemplate,
                location,
                routeType.ToDisplayString(),
                parseError ?? "Template was not supplied."));
            return;
        }

        var hasPropertyErrors = false;
        Dictionary<string, IPropertySymbol> properties = GetPublicProperties(
            routeType,
            location,
            reportDiagnostic,
            ref hasPropertyErrors);
        if (hasPropertyErrors)
            return;

        foreach (TemplateParameter parameter in parsedTemplate!.Parameters)
        {
            if (!properties.TryGetValue(parameter.Name, out IPropertySymbol? property))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.MissingPathMember,
                    location,
                    routeType.ToDisplayString(),
                    parameter.Name,
                    parsedTemplate.Value));
                continue;
            }

            ReportUnsupportedValueType(routeType, property.Type, parameter.Name, location, reportDiagnostic);
        }
    }

    private static RouteModel? TryCreate(
        INamedTypeSymbol routeType,
        ParsedRouteTemplate template,
        Location? location,
        AppNavSymbols symbols,
        Action<Diagnostic> reportDiagnostic,
        bool includeAttributeQueries = true)
    {
        var hasErrors = false;
        Dictionary<string, IPropertySymbol> properties = GetPublicProperties(
            routeType,
            location,
            reportDiagnostic,
            ref hasErrors);
        var pathProperties = new Dictionary<string, IPropertySymbol>(StringComparer.OrdinalIgnoreCase);

        foreach (TemplateParameter parameter in template.Parameters)
        {
            if (!properties.TryGetValue(parameter.Name, out IPropertySymbol? property))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.MissingPathMember,
                    location,
                    routeType.ToDisplayString(),
                    parameter.Name,
                    template.Value));
                hasErrors = true;
                continue;
            }

            pathProperties[parameter.Name] = property;
            ReportUnsupportedValueType(routeType, property.Type, parameter.Name, location, reportDiagnostic);
        }

        IReadOnlyList<QueryBinding> queryBindings = includeAttributeQueries
            ? BuildQueryBindings(routeType, properties, pathProperties, location, reportDiagnostic, ref hasErrors)
            : Array.Empty<QueryBinding>();

        IReadOnlyList<MetadataQueryBinding> metadataBindings = includeAttributeQueries
            ? BuildMetadataBindings(routeType, symbols, queryBindings, reportDiagnostic, ref hasErrors)
            : Array.Empty<MetadataQueryBinding>();

        IMethodSymbol? constructor = SelectConstructor(routeType, template, queryBindings, location, reportDiagnostic, ref hasErrors);
        if (constructor is not null)
            ValidateQueryConstructorParameters(routeType, constructor, queryBindings, location, reportDiagnostic, ref hasErrors);

        if (hasErrors || constructor is null)
            return null;

        return new RouteModel(
            routeType,
            template,
            pathProperties,
            queryBindings,
            metadataBindings,
            constructor,
            BindConstructorParameters(constructor, template, queryBindings),
            location);
    }

    private static Dictionary<string, IPropertySymbol> GetPublicProperties(
        INamedTypeSymbol routeType,
        Location? fallbackLocation,
        Action<Diagnostic> reportDiagnostic,
        ref bool hasErrors)
    {
        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.OrdinalIgnoreCase);
        var duplicateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (INamedTypeSymbol? current = routeType; current is not null; current = current.BaseType)
        {
            foreach (IPropertySymbol property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic ||
                    property.Parameters.Length != 0 ||
                    property.DeclaredAccessibility != Accessibility.Public ||
                    property.GetMethod?.DeclaredAccessibility != Accessibility.Public)
                    continue;

                if (properties.TryGetValue(property.Name, out IPropertySymbol? existingProperty))
                {
                    if (IsHiddenBaseProperty(existingProperty, property))
                        continue;

                    if (duplicateNames.Add(property.Name))
                    {
                        reportDiagnostic(Diagnostic.Create(
                            AppNavDiagnostics.DuplicateRoutePropertyName,
                            property.Locations.FirstOrDefault(static candidate => candidate.IsInSource) ??
                            fallbackLocation,
                            routeType.ToDisplayString(),
                            property.Name));
                    }

                    hasErrors = true;
                    continue;
                }

                properties.Add(property.Name, property);
            }
        }

        return properties;
    }

    private static bool IsHiddenBaseProperty(IPropertySymbol derivedProperty, IPropertySymbol baseProperty)
    {
        if (!StringComparer.Ordinal.Equals(derivedProperty.Name, baseProperty.Name) ||
            !SymbolFacts.InheritsFrom(derivedProperty.ContainingType, baseProperty.ContainingType))
            return false;

        for (IPropertySymbol? overridden = derivedProperty.OverriddenProperty;
             overridden is not null;
             overridden = overridden.OverriddenProperty)
        {
            if (SymbolEqualityComparer.Default.Equals(overridden, baseProperty))
                return true;
        }

        return true;
    }

    private static IReadOnlyList<QueryBinding> BuildQueryBindings(
        INamedTypeSymbol routeType,
        IReadOnlyDictionary<string, IPropertySymbol> properties,
        IReadOnlyDictionary<string, IPropertySymbol> pathProperties,
        Location? fallbackLocation,
        Action<Diagnostic> reportDiagnostic,
        ref bool hasErrors)
    {
        var bindings = new List<QueryBinding>();
        var queryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AttributeData attribute in SymbolFacts.GetAttributes(routeType, AppNavSymbols.QueryAttributeName))
        {
            Location? location = SymbolFacts.GetLocation(routeType, attribute) ?? fallbackLocation;
            string? propertyName = attribute.ConstructorArguments.Length == 1
                ? attribute.ConstructorArguments[0].Value as string
                : null;
            if (string.IsNullOrWhiteSpace(propertyName) ||
                !properties.TryGetValue(propertyName!, out IPropertySymbol? property))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.InvalidQueryProperty,
                    location,
                    routeType.ToDisplayString(),
                    propertyName ?? string.Empty));
                hasErrors = true;
                continue;
            }

            if (pathProperties.ContainsKey(property.Name))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.PathQueryOverlap,
                    location,
                    routeType.ToDisplayString(),
                    property.Name));
                hasErrors = true;
                continue;
            }

            if (!propertyNames.Add(property.Name))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.InvalidQueryProperty,
                    location,
                    routeType.ToDisplayString(),
                    property.Name));
                hasErrors = true;
                continue;
            }

            string queryName = GetNamedString(attribute, "Name");
            if (string.IsNullOrWhiteSpace(queryName))
                queryName = JsonCamelCase.ConvertName(property.Name);

            if (!queryNames.Add(queryName))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.DuplicateQueryName,
                    location,
                    routeType.ToDisplayString(),
                    queryName));
                hasErrors = true;
                continue;
            }

            bool omitWhenNull = GetNamedBool(attribute, "OmitWhenNull", defaultValue: true);
            ReportUnsupportedValueType(routeType, property.Type, queryName, location, reportDiagnostic,
                allowQueryCollections: true);
            bindings.Add(new QueryBinding(property, queryName, omitWhenNull));
        }

        return bindings;
    }

    private static IReadOnlyList<MetadataQueryBinding> BuildMetadataBindings(
        INamedTypeSymbol routeType,
        AppNavSymbols symbols,
        IReadOnlyList<QueryBinding> queryBindings,
        Action<Diagnostic> reportDiagnostic,
        ref bool hasErrors)
    {
        var bindings = new List<MetadataQueryBinding>();
        var members = new HashSet<string>(StringComparer.Ordinal);
        var queryNames = new HashSet<string>(
            queryBindings.Select(static binding => binding.QueryName),
            StringComparer.OrdinalIgnoreCase);
        var metadataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AttributeData attribute in SymbolFacts.GetAttributes(routeType, AppNavSymbols.QueryMetadataAttributeName))
        {
            Location? location = SymbolFacts.GetLocation(routeType, attribute);
            INamedTypeSymbol? declaringType = attribute.ConstructorArguments.Length >= 1
                ? attribute.ConstructorArguments[0].Value as INamedTypeSymbol
                : null;
            string? memberName = attribute.ConstructorArguments.Length >= 2
                ? attribute.ConstructorArguments[1].Value as string
                : null;

            ISymbol? member = declaringType?.GetMembers(memberName ?? string.Empty)
                .FirstOrDefault(candidate => candidate is IPropertySymbol or IFieldSymbol);
            ITypeSymbol? memberType = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => null
            };

            if (declaringType is null ||
                string.IsNullOrWhiteSpace(memberName) ||
                member is null ||
                symbols.RouteMetadataKey is null ||
                memberType is not INamedTypeSymbol namedMemberType ||
                !SymbolEqualityComparer.Default.Equals(namedMemberType.ConstructedFrom, symbols.RouteMetadataKey) ||
                !IsStaticAccessibleMember(member) ||
                !TryGetRouteMetadataKeyName(member, out string? queryName))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.InvalidQueryProperty,
                    location,
                    routeType.ToDisplayString(),
                    memberName ?? string.Empty));
                hasErrors = true;
                continue;
            }

            string memberKey = declaringType.ToDisplayString() + "." + memberName;
            if (!members.Add(memberKey) ||
                !metadataNames.Add(queryName!) ||
                !queryNames.Add(queryName!))
            {
                reportDiagnostic(Diagnostic.Create(
                    AppNavDiagnostics.DuplicateQueryName,
                    location,
                    routeType.ToDisplayString(),
                    queryName!));
                hasErrors = true;
                continue;
            }

            bool omitWhenNull = GetNamedBool(attribute, "OmitWhenNull", defaultValue: true);
            ReportUnsupportedValueType(routeType, namedMemberType.TypeArguments[0], queryName!, location, reportDiagnostic);
            bindings.Add(new MetadataQueryBinding(
                declaringType,
                memberName!,
                queryName!,
                namedMemberType.TypeArguments[0],
                omitWhenNull));
        }

        return bindings;
    }

    private static bool IsStaticAccessibleMember(ISymbol member)
    {
        if (!member.IsStatic)
            return false;

        for (INamedTypeSymbol? containingType = member.ContainingType;
             containingType is not null;
             containingType = containingType.ContainingType)
        {
            if (!IsAccessibleFromGeneratedCode(containingType.DeclaredAccessibility))
                return false;
        }

        Accessibility accessibility = member switch
        {
            IPropertySymbol property => property.GetMethod?.DeclaredAccessibility ?? property.DeclaredAccessibility,
            _ => member.DeclaredAccessibility
        };

        return IsAccessibleFromGeneratedCode(accessibility);
    }

    private static bool IsAccessibleFromGeneratedCode(Accessibility accessibility)
    {
        return accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
    }

    private static bool TryGetRouteMetadataKeyName(ISymbol member, out string? name)
    {
        foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
        {
            ExpressionSyntax? initializer = reference.GetSyntax() switch
            {
                VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                PropertyDeclarationSyntax property => property.Initializer?.Value ?? property.ExpressionBody?.Expression,
                _ => null
            };

            if (initializer is not null && TryGetRouteMetadataKeyName(initializer, out name))
                return true;
        }

        name = null;
        return false;
    }

    private static bool TryGetRouteMetadataKeyName(ExpressionSyntax expression, out string? name)
    {
        SeparatedSyntaxList<ArgumentSyntax>? arguments = expression switch
        {
            ObjectCreationExpressionSyntax objectCreation => objectCreation.ArgumentList?.Arguments,
            ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation.ArgumentList?.Arguments,
            _ => null
        };

        if (arguments is { Count: > 0 } &&
            arguments.Value[0].Expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            literal.Token.ValueText is { Length: > 0 } value)
        {
            name = value;
            return true;
        }

        name = null;
        return false;
    }

    private static IMethodSymbol? SelectConstructor(
        INamedTypeSymbol routeType,
        ParsedRouteTemplate template,
        IReadOnlyList<QueryBinding> queryBindings,
        Location? location,
        Action<Diagnostic> reportDiagnostic,
        ref bool hasErrors)
    {
        var requiredNames = new HashSet<string>(template.Parameters.Select(parameter => parameter.Name), StringComparer.OrdinalIgnoreCase);
        foreach (QueryBinding queryBinding in queryBindings)
            requiredNames.Add(queryBinding.Property.Name);

        IMethodSymbol[] candidates = routeType.Constructors
            .Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public)
            .Where(constructor => IsUsableConstructor(constructor, requiredNames))
            .OrderByDescending(constructor => constructor.Parameters.Length)
            .ToArray();

        if (candidates.Length == 0)
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.NoUsableConstructor,
                location,
                routeType.ToDisplayString(),
                template.Value));
            hasErrors = true;
            return null;
        }

        if (candidates.Length > 1 &&
            candidates[0].Parameters.Length == candidates[1].Parameters.Length)
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.AmbiguousConstructor,
                location,
                routeType.ToDisplayString(),
                template.Value));
            hasErrors = true;
            return null;
        }

        return candidates[0];
    }

    private static bool IsUsableConstructor(IMethodSymbol constructor, ISet<string> requiredNames)
    {
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IParameterSymbol parameter in constructor.Parameters)
        {
            if (string.IsNullOrEmpty(parameter.Name) || !parameterNames.Add(parameter.Name))
                return false;
        }

        if (requiredNames.Any(requiredName => !parameterNames.Contains(requiredName)))
            return false;

        return constructor.Parameters.All(parameter =>
            requiredNames.Contains(parameter.Name) ||
            parameter.HasExplicitDefaultValue ||
            parameter.IsOptional);
    }

    private static void ValidateQueryConstructorParameters(
        INamedTypeSymbol routeType,
        IMethodSymbol constructor,
        IReadOnlyList<QueryBinding> queryBindings,
        Location? location,
        Action<Diagnostic> reportDiagnostic,
        ref bool hasErrors)
    {
        Dictionary<string, QueryBinding> queryByProperty = queryBindings.ToDictionary(
            binding => binding.Property.Name,
            StringComparer.OrdinalIgnoreCase);

        foreach (IParameterSymbol parameter in constructor.Parameters)
        {
            if (!queryByProperty.TryGetValue(parameter.Name, out QueryBinding? queryBinding) ||
                IsMissingSafeQueryParameter(parameter))
                continue;

            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.UnsafeQueryConstructorParameter,
                location,
                queryBinding.QueryName,
                routeType.ToDisplayString(),
                parameter.Name));
            hasErrors = true;
        }
    }

    private static bool IsMissingSafeQueryParameter(IParameterSymbol parameter)
    {
        if (parameter.HasExplicitDefaultValue || parameter.IsOptional)
            return true;

        if (parameter.Type.IsValueType)
            return parameter.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        return parameter.NullableAnnotation == NullableAnnotation.Annotated;
    }

    private static IReadOnlyList<ConstructorArgument> BindConstructorParameters(
        IMethodSymbol constructor,
        ParsedRouteTemplate template,
        IReadOnlyList<QueryBinding> queryBindings)
    {
        var pathNames = new HashSet<string>(template.Parameters.Select(parameter => parameter.Name), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, QueryBinding> queryByProperty = queryBindings.ToDictionary(
            binding => binding.Property.Name,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TemplateParameter> pathByName = template.Parameters.ToDictionary(
            parameter => parameter.Name,
            StringComparer.OrdinalIgnoreCase);

        return constructor.Parameters
            .Select(parameter =>
            {
                if (pathNames.Contains(parameter.Name))
                    return ConstructorArgument.Path(parameter, pathByName[parameter.Name]);

                if (queryByProperty.TryGetValue(parameter.Name, out QueryBinding? query))
                    return ConstructorArgument.Query(parameter, query);

                return ConstructorArgument.Default(parameter);
            })
            .ToArray();
    }

    private static void ReportUnsupportedValueType(
        INamedTypeSymbol routeType,
        ITypeSymbol type,
        string name,
        Location? location,
        Action<Diagnostic> reportDiagnostic,
        bool allowQueryCollections = false)
    {
        if (RouteValueSourceEmitter.IsDirectlySupported(type) ||
            (allowQueryCollections && RouteValueSourceEmitter.IsDirectlySupportedQueryCollection(type)))
            return;

        reportDiagnostic(Diagnostic.Create(
            AppNavDiagnostics.UnsupportedRouteValueType,
            location,
            routeType.ToDisplayString(),
            type.ToDisplayString(),
            name));
    }

    private static string GetNamedString(AttributeData attribute, string name)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is string value)
                return value;
        }

        return string.Empty;
    }

    private static bool GetNamedBool(AttributeData attribute, string name, bool defaultValue)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is bool value)
                return value;
        }

        return defaultValue;
    }
}

internal static class PageModelFactory
{
    public static PageModel? TryCreate(
        INamedTypeSymbol pageType,
        AttributeData attribute,
        AppNavSymbols symbols,
        Action<Diagnostic> reportDiagnostic)
    {
        Location? location = SymbolFacts.GetLocation(pageType, attribute);
        INamedTypeSymbol? routeType = attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as INamedTypeSymbol
            : null;

        if (routeType is null ||
            symbols.AppRoute is null ||
            !SymbolFacts.InheritsFrom(routeType, symbols.AppRoute) ||
            routeType.IsAbstract ||
            SymbolFacts.ContainsTypeParameters(routeType) ||
            !SymbolFacts.IsAccessibleFromGeneratedCode(routeType) ||
            SymbolFacts.GetAttribute(routeType, AppNavSymbols.RouteAttributeName) is null)
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.InvalidPageRoute,
                location,
                pageType.ToDisplayString(),
                routeType?.ToDisplayString() ?? string.Empty));
            return null;
        }

        if (SymbolFacts.ContainsTypeParameters(pageType) ||
            !SymbolFacts.IsAccessibleFromGeneratedCode(pageType))
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.InvalidPageType,
                location,
                pageType.ToDisplayString()));
            return null;
        }

        if (symbols.MauiPage is not null &&
            !SymbolFacts.IsAssignableTo(pageType, symbols.MauiPage) &&
            !CanDeferPageTypeValidation(pageType))
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.InvalidPageType,
                location,
                pageType.ToDisplayString()));
            return null;
        }

        bool fromServices = false;
        INamedTypeSymbol? pageModelType = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == "FromServices" && argument.Value.Value is bool value)
            {
                fromServices = value;
                continue;
            }

            if (argument.Key == "PageModelType")
                pageModelType = argument.Value.Value as INamedTypeSymbol;
        }

        if (pageModelType is not null &&
            (SymbolFacts.ContainsTypeParameters(pageModelType) ||
             !SymbolFacts.IsAccessibleFromGeneratedCode(pageModelType)))
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.InvalidPageModelType,
                location,
                pageType.ToDisplayString(),
                pageModelType.ToDisplayString()));
            return null;
        }

        IMethodSymbol? constructor = null;
        IParameterSymbol? routeParameter = null;
        if (!fromServices && pageModelType is null)
        {
            constructor = SelectPageConstructor(pageType, routeType, location, reportDiagnostic);
            if (constructor is null)
                return null;

            routeParameter = SelectPageRouteParameter(constructor, routeType);
        }

        return new PageModel(pageType, routeType, fromServices, pageModelType, constructor, routeParameter, location);
    }

    private static bool CanDeferPageTypeValidation(INamedTypeSymbol pageType)
    {
        if (pageType.TypeKind != TypeKind.Class)
            return false;

        if (pageType.BaseType is { SpecialType: not SpecialType.System_Object })
            return false;

        return pageType.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .Any(static declaration =>
                declaration.BaseList is null &&
                declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
    }

    private static IMethodSymbol? SelectPageConstructor(
        INamedTypeSymbol pageType,
        INamedTypeSymbol routeType,
        Location? location,
        Action<Diagnostic> reportDiagnostic)
    {
        IMethodSymbol[] constructors = pageType.Constructors
            .Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public)
            .ToArray();

        IMethodSymbol[] markedConstructors = constructors
            .Where(constructor => SymbolFacts.GetAttribute(
                constructor,
                AppNavSymbols.ActivatorUtilitiesConstructorAttributeName) is not null)
            .ToArray();

        IMethodSymbol? selected = markedConstructors.Length switch
        {
            1 => markedConstructors[0],
            > 1 => null,
            _ => constructors.Length == 1 ? constructors[0] : null
        };

        if (selected is null)
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.AmbiguousPageConstructor,
                location,
                pageType.ToDisplayString()));
            return null;
        }

        if (SelectPageRouteParameter(selected, routeType) is null)
        {
            reportDiagnostic(Diagnostic.Create(
                AppNavDiagnostics.MissingPageRouteParameter,
                location,
                pageType.ToDisplayString(),
                routeType.ToDisplayString()));
            return null;
        }

        return selected;
    }

    private static IParameterSymbol? SelectPageRouteParameter(IMethodSymbol constructor, INamedTypeSymbol routeType)
    {
        return constructor.Parameters
            .Where(parameter => SymbolFacts.IsAssignableTo(routeType, parameter.Type))
            .OrderBy(parameter => RouteParameterScore(routeType, parameter.Type))
            .FirstOrDefault();
    }

    private static int RouteParameterScore(INamedTypeSymbol routeType, ITypeSymbol parameterType)
    {
        if (SymbolEqualityComparer.Default.Equals(routeType, parameterType))
            return 0;

        var distance = 1;
        for (INamedTypeSymbol? current = routeType.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, parameterType))
                return distance;

            distance++;
        }

        if (parameterType.SpecialType == SpecialType.System_Object)
            return 1000;

        return 500;
    }
}

internal sealed class RouteModel
{
    public RouteModel(
        INamedTypeSymbol routeType,
        ParsedRouteTemplate template,
        IReadOnlyDictionary<string, IPropertySymbol> pathProperties,
        IReadOnlyList<QueryBinding> queryBindings,
        IReadOnlyList<MetadataQueryBinding> metadataQueryBindings,
        IMethodSymbol constructor,
        IReadOnlyList<ConstructorArgument> constructorArguments,
        Location? location)
    {
        RouteType = routeType;
        Template = template;
        PathProperties = pathProperties;
        QueryBindings = queryBindings;
        MetadataQueryBindings = metadataQueryBindings;
        Constructor = constructor;
        ConstructorArguments = constructorArguments;
        Location = location;
    }

    public INamedTypeSymbol RouteType { get; }

    public ParsedRouteTemplate Template { get; }

    public IReadOnlyDictionary<string, IPropertySymbol> PathProperties { get; }

    public IReadOnlyList<QueryBinding> QueryBindings { get; }

    public IReadOnlyList<MetadataQueryBinding> MetadataQueryBindings { get; }

    public IMethodSymbol Constructor { get; }

    public IReadOnlyList<ConstructorArgument> ConstructorArguments { get; }

    public Location? Location { get; }
}

internal sealed class PageModel
{
    public PageModel(
        INamedTypeSymbol pageType,
        INamedTypeSymbol routeType,
        bool fromServices,
        INamedTypeSymbol? pageModelType,
        IMethodSymbol? constructor,
        IParameterSymbol? routeParameter,
        Location? location)
    {
        PageType = pageType;
        RouteType = routeType;
        FromServices = fromServices;
        PageModelType = pageModelType;
        Constructor = constructor;
        RouteParameter = routeParameter;
        Location = location;
    }

    public INamedTypeSymbol PageType { get; }

    public INamedTypeSymbol RouteType { get; }

    public bool FromServices { get; }

    public INamedTypeSymbol? PageModelType { get; }

    public IMethodSymbol? Constructor { get; }

    public IParameterSymbol? RouteParameter { get; }

    public Location? Location { get; }
}

internal sealed class QueryBinding
{
    public QueryBinding(IPropertySymbol property, string queryName, bool omitWhenNull)
    {
        Property = property;
        QueryName = queryName;
        OmitWhenNull = omitWhenNull;
    }

    public IPropertySymbol Property { get; }

    public string QueryName { get; }

    public bool OmitWhenNull { get; }
}

internal sealed class MetadataQueryBinding
{
    public MetadataQueryBinding(
        INamedTypeSymbol declaringType,
        string memberName,
        string queryName,
        ITypeSymbol valueType,
        bool omitWhenNull)
    {
        DeclaringType = declaringType;
        MemberName = memberName;
        QueryName = queryName;
        ValueType = valueType;
        OmitWhenNull = omitWhenNull;
    }

    public INamedTypeSymbol DeclaringType { get; }

    public string MemberName { get; }

    public string QueryName { get; }

    public ITypeSymbol ValueType { get; }

    public bool OmitWhenNull { get; }

    public string AccessExpression => SymbolFacts.TypeName(DeclaringType) + "." + SymbolFacts.Identifier(MemberName);
}

internal sealed class ConstructorArgument
{
    private ConstructorArgument(
        IParameterSymbol parameter,
        ConstructorArgumentKind kind,
        TemplateParameter? pathParameter,
        QueryBinding? queryBinding)
    {
        Parameter = parameter;
        Kind = kind;
        PathParameter = pathParameter;
        QueryBinding = queryBinding;
    }

    public IParameterSymbol Parameter { get; }

    public ConstructorArgumentKind Kind { get; }

    public TemplateParameter? PathParameter { get; }

    public QueryBinding? QueryBinding { get; }

    public static ConstructorArgument Path(IParameterSymbol parameter, TemplateParameter pathParameter)
    {
        return new ConstructorArgument(parameter, ConstructorArgumentKind.Path, pathParameter, null);
    }

    public static ConstructorArgument Query(IParameterSymbol parameter, QueryBinding queryBinding)
    {
        return new ConstructorArgument(parameter, ConstructorArgumentKind.Query, null, queryBinding);
    }

    public static ConstructorArgument Default(IParameterSymbol parameter)
    {
        return new ConstructorArgument(parameter, ConstructorArgumentKind.Default, null, null);
    }
}

internal enum ConstructorArgumentKind
{
    Path,
    Query,
    Default
}

internal sealed class ParsedRouteTemplate
{
    private ParsedRouteTemplate(string value, IReadOnlyList<TemplateSegment> segments)
    {
        Value = value;
        Segments = segments;
        Parameters = segments
            .Where(segment => segment.ParameterName is not null)
            .Select(segment => new TemplateParameter(
                segment.ParameterName!,
                segment.Constraint,
                segment.IsOptional,
                segment.IsCatchAll))
            .ToArray();
    }

    public string Value { get; }

    public IReadOnlyList<TemplateSegment> Segments { get; }

    public IReadOnlyList<TemplateParameter> Parameters { get; }

    private int MinimumSegmentCount => Segments.Count(segment => !segment.IsOptional && !segment.IsCatchAll);

    private int? MaximumSegmentCount => Segments.Any(segment => segment.IsCatchAll) ? (int?)null : Segments.Count;

    public static bool TryParse(
        string? value,
        out ParsedRouteTemplate? template,
        out string? error,
        bool allowCustomConstraints = false)
    {
        template = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Route templates cannot be empty.";
            return false;
        }

        if (!value!.StartsWith("/", StringComparison.Ordinal))
        {
            error = "Route templates must start with '/'.";
            return false;
        }

        var segments = new List<TemplateSegment>();
        foreach (string segmentText in SplitPath(value))
        {
            if (!TryParseSegment(segmentText, allowCustomConstraints, out TemplateSegment? segment, out error))
                return false;

            segments.Add(segment!);
        }

        if (!ValidateSegments(value, segments, out error))
            return false;

        template = new ParsedRouteTemplate(value, segments);
        return true;
    }

    public bool CanOverlap(ParsedRouteTemplate other)
    {
        int min = Math.Max(MinimumSegmentCount, other.MinimumSegmentCount);
        int max = MaximumSegmentCount is null || other.MaximumSegmentCount is null
            ? Math.Max(Segments.Count, other.Segments.Count)
            : Math.Min(MaximumSegmentCount.Value, other.MaximumSegmentCount.Value);

        if (min > max)
            return false;

        for (var i = 0; i < min; i++)
        {
            TemplateSegment? left = SegmentAt(i);
            TemplateSegment? right = other.SegmentAt(i);
            if (left is null || right is null)
                continue;

            if (!SegmentsCanOverlap(left, right))
                return false;
        }

        return true;
    }

    public int ComparePrecedence(ParsedRouteTemplate other)
    {
        int count = Math.Max(Segments.Count, other.Segments.Count);
        for (var i = 0; i < count; i++)
        {
            TemplateSegment? left = i < Segments.Count ? Segments[i] : null;
            TemplateSegment? right = i < other.Segments.Count ? other.Segments[i] : null;
            int comparison = CompareSegmentPrecedence(left, right);
            if (comparison != 0)
                return comparison;
        }

        return Segments.Count.CompareTo(other.Segments.Count);
    }

    public bool IsOptionalParameter(string name)
    {
        return Parameters.Any(parameter =>
            parameter.IsOptional && StringComparer.OrdinalIgnoreCase.Equals(parameter.Name, name));
    }

    private static bool TryParseSegment(
        string segmentText,
        bool allowCustomConstraints,
        out TemplateSegment? segment,
        out string? error)
    {
        segment = null;
        error = null;

        if (segmentText.StartsWith("{", StringComparison.Ordinal) &&
            segmentText.EndsWith("}", StringComparison.Ordinal) &&
            segmentText.Length > 2)
        {
            string body = segmentText.Substring(1, segmentText.Length - 2);
            if (body.StartsWith("*", StringComparison.Ordinal))
            {
                string catchAllName = body.Substring(1);
                if (string.IsNullOrWhiteSpace(catchAllName))
                {
                    error = "Catch-all route parameters must have a name.";
                    return false;
                }

                segment = TemplateSegment.CatchAll(catchAllName);
                return true;
            }

            int separator = body.IndexOf(':');
            string name = separator < 0 ? body : body.Substring(0, separator);
            string? constraint = separator < 0 ? null : body.Substring(separator + 1);
            var optional = false;

            if (constraint is not null && constraint.EndsWith("?", StringComparison.Ordinal))
            {
                optional = true;
                constraint = constraint.Substring(0, constraint.Length - 1);
            }
            else if (name.EndsWith("?", StringComparison.Ordinal))
            {
                optional = true;
                name = name.Substring(0, name.Length - 1);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Route parameters must have a name.";
                return false;
            }

            if (constraint is not null && !allowCustomConstraints && !BuiltInConstraints.Contains(constraint))
            {
                error = $"Route constraint '{constraint}' is not supported by source-generated routes.";
                return false;
            }

            segment = TemplateSegment.Parameter(name, constraint, optional);
            return true;
        }

        if (segmentText.IndexOf('{') >= 0 || segmentText.IndexOf('}') >= 0)
        {
            error = $"Route template segment '{segmentText}' is invalid.";
            return false;
        }

        segment = TemplateSegment.Literal(Uri.UnescapeDataString(segmentText));
        return true;
    }

    private static bool ValidateSegments(string template, IReadOnlyList<TemplateSegment> segments, out string? error)
    {
        error = null;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionalStarted = false;

        for (var i = 0; i < segments.Count; i++)
        {
            TemplateSegment segment = segments[i];
            if (segment.ParameterName is not null && !seenNames.Add(segment.ParameterName))
            {
                error = $"Route template '{template}' contains duplicate parameter '{segment.ParameterName}'.";
                return false;
            }

            if (segment.IsCatchAll && i != segments.Count - 1)
            {
                error = $"Route template '{template}' has a catch-all segment that is not final.";
                return false;
            }

            if (optionalStarted && !segment.IsOptional && !segment.IsCatchAll)
            {
                error = $"Route template '{template}' has a non-optional segment after an optional segment.";
                return false;
            }

            if (segment.IsOptional)
                optionalStarted = true;
        }

        return true;
    }

    private static bool SegmentsCanOverlap(TemplateSegment left, TemplateSegment right)
    {
        if (left.IsCatchAll || right.IsCatchAll)
            return true;

        if (left.LiteralValue is not null && right.LiteralValue is not null)
            return StringComparer.Ordinal.Equals(left.LiteralValue, right.LiteralValue);

        if (left.LiteralValue is not null && right.ParameterName is not null)
            return ConstraintSatisfiesLiteral(left.LiteralValue, right.Constraint);

        if (right.LiteralValue is not null && left.ParameterName is not null)
            return ConstraintSatisfiesLiteral(right.LiteralValue, left.Constraint);

        return BuiltInConstraints.CanOverlap(left.Constraint, right.Constraint);
    }

    private static bool ConstraintSatisfiesLiteral(string literal, string? constraint)
    {
        if (constraint is null)
            return true;

        switch (constraint.ToLowerInvariant())
        {
            case "int":
                return int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            case "long":
                return long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            case "guid":
                return Guid.TryParse(literal, out _);
            case "bool":
                return bool.TryParse(literal, out _);
            case "decimal":
                return decimal.TryParse(literal, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
            case "alpha":
                return literal.Length > 0 && literal.All(char.IsLetter);
            default:
                return true;
        }
    }

    private TemplateSegment? SegmentAt(int index)
    {
        if (index < Segments.Count)
            return Segments[index];

        return Segments.Count > 0 && Segments[Segments.Count - 1].IsCatchAll
            ? Segments[Segments.Count - 1]
            : null;
    }

    private static int CompareSegmentPrecedence(TemplateSegment? left, TemplateSegment? right)
    {
        if (left is null && right is null)
            return 0;

        if (left is null)
            return right!.IsOptional || right.IsCatchAll ? -1 : 1;

        if (right is null)
            return left.IsOptional || left.IsCatchAll ? 1 : -1;

        return right.Precedence.CompareTo(left.Precedence);
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        string trimmed = path.Trim('/');
        return trimmed.Length == 0
            ? Enumerable.Empty<string>()
            : trimmed.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
    }
}

internal sealed class TemplateParameter
{
    public TemplateParameter(string name, string? constraint, bool isOptional, bool isCatchAll)
    {
        Name = name;
        Constraint = constraint;
        IsOptional = isOptional;
        IsCatchAll = isCatchAll;
    }

    public string Name { get; }

    public string? Constraint { get; }

    public bool IsOptional { get; }

    public bool IsCatchAll { get; }
}

internal sealed class TemplateSegment
{
    private TemplateSegment(string? literalValue, string? parameterName, string? constraint, bool isOptional, bool isCatchAll)
    {
        LiteralValue = literalValue;
        ParameterName = parameterName;
        Constraint = constraint;
        IsOptional = isOptional;
        IsCatchAll = isCatchAll;
    }

    public string? LiteralValue { get; }

    public string? ParameterName { get; }

    public string? Constraint { get; }

    public bool IsOptional { get; }

    public bool IsCatchAll { get; }

    public int Precedence =>
        LiteralValue is not null ? 5 :
        Constraint is not null ? 4 :
        IsCatchAll ? 1 :
        !IsOptional ? 3 :
        2;

    public static TemplateSegment Literal(string value)
    {
        return new TemplateSegment(value, null, null, false, false);
    }

    public static TemplateSegment Parameter(string name, string? constraint, bool optional)
    {
        return new TemplateSegment(null, name, constraint, optional, false);
    }

    public static TemplateSegment CatchAll(string name)
    {
        return new TemplateSegment(null, name, null, false, true);
    }
}

internal static class BuiltInConstraints
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "int",
        "long",
        "guid",
        "bool",
        "decimal",
        "alpha"
    };

    public static bool Contains(string name)
    {
        return Names.Contains(name);
    }

    public static bool CanOverlap(string? left, string? right)
    {
        if (left is null || right is null)
            return true;

        if (StringComparer.OrdinalIgnoreCase.Equals(left, right))
            return true;

        if (IsNumericConstraint(left) && IsNumericConstraint(right))
            return true;

        return PairEquals(left, right, "bool", "alpha") || PairEquals(left, right, "alpha", "guid");
    }

    private static bool IsNumericConstraint(string constraint)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(constraint, "int") ||
               StringComparer.OrdinalIgnoreCase.Equals(constraint, "long") ||
               StringComparer.OrdinalIgnoreCase.Equals(constraint, "decimal");
    }

    private static bool PairEquals(string left, string right, string first, string second)
    {
        return (StringComparer.OrdinalIgnoreCase.Equals(left, first) &&
                StringComparer.OrdinalIgnoreCase.Equals(right, second)) ||
               (StringComparer.OrdinalIgnoreCase.Equals(left, second) &&
                StringComparer.OrdinalIgnoreCase.Equals(right, first));
    }
}

internal static class JsonCamelCase
{
    public static string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
            return name;

        char[] chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;

            bool hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                break;

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }
}

internal static class RouteValueSourceEmitter
{
    public static bool IsDirectlySupported(ITypeSymbol type)
    {
        ITypeSymbol valueType = UnwrapNullable(type);
        if (valueType.TypeKind == TypeKind.Enum)
            return true;

        switch (valueType.SpecialType)
        {
            case SpecialType.System_String:
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Decimal:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                return true;
        }

        return valueType.ToDisplayString() == "System.Guid";
    }

    public static bool IsDirectlySupportedQueryCollection(ITypeSymbol type)
    {
        return TryGetSupportedQueryCollection(type, out ITypeSymbol? elementType, out _) &&
               elementType is not null &&
               IsDirectlySupported(elementType);
    }

    public static bool TryGetSupportedQueryCollection(
        ITypeSymbol type,
        out ITypeSymbol? elementType,
        out QueryCollectionKind kind)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            elementType = null;
            kind = QueryCollectionKind.None;
            return false;
        }

        if (type is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            kind = QueryCollectionKind.Array;
            return true;
        }

        if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            string definition = namedType.OriginalDefinition.ToDisplayString();
            if (definition is "System.Collections.Generic.IEnumerable<T>" or
                "System.Collections.Generic.IReadOnlyCollection<T>" or
                "System.Collections.Generic.IReadOnlyList<T>" or
                "System.Collections.Generic.ICollection<T>" or
                "System.Collections.Generic.IList<T>")
            {
                elementType = namedType.TypeArguments[0];
                kind = QueryCollectionKind.Array;
                return true;
            }

            if (definition == "System.Collections.Generic.List<T>")
            {
                elementType = namedType.TypeArguments[0];
                kind = QueryCollectionKind.List;
                return true;
            }
        }

        elementType = null;
        kind = QueryCollectionKind.None;
        return false;
    }

    public static string EmitParse(ITypeSymbol type, string valueExpression, string? nameExpression = null)
    {
        ITypeSymbol valueType = UnwrapNullable(type);
        bool nullable = IsNullableValueType(type);
        string parsed = EmitNonNullableParse(
            valueType,
            valueExpression,
            nameExpression ?? SymbolDisplay.FormatLiteral("value", quote: true));

        return nullable
            ? "(" + SymbolFacts.TypeName(type) + ")" + parsed
            : parsed;
    }

    public static string EmitQueryCollectionParse(
        ITypeSymbol type,
        string valuesExpression,
        string nameExpression)
    {
        if (!TryGetSupportedQueryCollection(type, out ITypeSymbol? elementType, out QueryCollectionKind kind) ||
            elementType is null)
            return EmitParse(type, valuesExpression, nameExpression);

        string select = "global::System.Linq.Enumerable.Select(" + valuesExpression +
                        ", static appNavItem => " + EmitParse(elementType, "appNavItem", nameExpression) + ")";
        return kind switch
        {
            QueryCollectionKind.List => "new global::System.Collections.Generic.List<" +
                                        SymbolFacts.TypeName(elementType) + ">(" + select + ")",
            _ => "global::System.Linq.Enumerable.ToArray(" + select + ")"
        };
    }

    public static string EmitDefault(IParameterSymbol parameter)
    {
        if (parameter.HasExplicitDefaultValue)
            return ConstantEmitter.Emit(parameter.Type, parameter.ExplicitDefaultValue);

        return "default(" + SymbolFacts.TypeName(parameter.Type) + ")";
    }

    private static string EmitNonNullableParse(ITypeSymbol type, string valueExpression, string nameExpression)
    {
        if (type.TypeKind == TypeKind.Enum)
            return "ParseEnum<" + SymbolFacts.TypeName(type) + ">(" + valueExpression + ", " + nameExpression + ")";

        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return valueExpression;
            case SpecialType.System_Boolean:
                return "global::System.Boolean.Parse(" + valueExpression + ")";
            case SpecialType.System_Byte:
                return EmitNumericParse("global::System.Byte", valueExpression, nameExpression,
                    "appNavValue => global::System.Byte.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_SByte:
                return EmitNumericParse("global::System.SByte", valueExpression, nameExpression,
                    "appNavValue => global::System.SByte.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_Int16:
                return EmitNumericParse("global::System.Int16", valueExpression, nameExpression,
                    "appNavValue => global::System.Int16.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_UInt16:
                return EmitNumericParse("global::System.UInt16", valueExpression, nameExpression,
                    "appNavValue => global::System.UInt16.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_Int32:
                return EmitNumericParse("global::System.Int32", valueExpression, nameExpression,
                    "appNavValue => global::System.Int32.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_UInt32:
                return EmitNumericParse("global::System.UInt32", valueExpression, nameExpression,
                    "appNavValue => global::System.UInt32.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_Int64:
                return EmitNumericParse("global::System.Int64", valueExpression, nameExpression,
                    "appNavValue => global::System.Int64.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_UInt64:
                return EmitNumericParse("global::System.UInt64", valueExpression, nameExpression,
                    "appNavValue => global::System.UInt64.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_Decimal:
                return EmitNumericParse("global::System.Decimal", valueExpression, nameExpression,
                    "appNavValue => global::System.Decimal.Parse(appNavValue, global::System.Globalization.NumberStyles.Number, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_Single:
                return EmitNumericParse("global::System.Single", valueExpression, nameExpression,
                    "appNavValue => global::System.Single.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
            case SpecialType.System_Double:
                return EmitNumericParse("global::System.Double", valueExpression, nameExpression,
                    "appNavValue => global::System.Double.Parse(appNavValue, global::System.Globalization.CultureInfo.InvariantCulture)");
        }

        if (type.ToDisplayString() == "System.Guid")
            return "global::System.Guid.Parse(" + valueExpression + ")";

        string typeName = SymbolFacts.TypeName(type);
        return "ConvertRouteValue<" + typeName + ">(" + valueExpression + ", " + nameExpression + ")";
    }

    private static string EmitNumericParse(
        string typeName,
        string valueExpression,
        string nameExpression,
        string parseExpression)
    {
        return "ParseNumber<" + typeName + ">(" + valueExpression + ", " + nameExpression +
               ", static " + parseExpression + ")";
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return named.TypeArguments[0];

        return type;
    }

    private static bool IsNullableValueType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol named &&
               named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    public enum QueryCollectionKind
    {
        None,
        Array,
        List
    }
}

internal static class ConstantEmitter
{
    public static string Emit(ITypeSymbol type, object? value)
    {
        if (value is null)
            return type.IsValueType ? "default(" + SymbolFacts.TypeName(type) + ")" : "null";

        ITypeSymbol valueType = type is INamedTypeSymbol named &&
                                named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? named.TypeArguments[0]
            : type;

        if (valueType.TypeKind == TypeKind.Enum)
        {
            string enumTypeName = SymbolFacts.TypeName(valueType);
            if (valueType is INamedTypeSymbol enumType &&
                enumType.EnumUnderlyingType?.SpecialType is SpecialType.System_UInt64)
            {
                return "(" + enumTypeName + ")" +
                       Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) +
                       "UL";
            }

            return "(" + enumTypeName + ")" +
                   Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        switch (value)
        {
            case string text:
                return SymbolDisplay.FormatLiteral(text, quote: true);
            case char ch:
                return SymbolDisplay.FormatLiteral(ch, quote: true);
            case bool boolean:
                return boolean ? "true" : "false";
            case float single:
                if (float.IsNaN(single))
                    return "global::System.Single.NaN";
                if (float.IsPositiveInfinity(single))
                    return "global::System.Single.PositiveInfinity";
                if (float.IsNegativeInfinity(single))
                    return "global::System.Single.NegativeInfinity";

                return single.ToString("R", CultureInfo.InvariantCulture) + "f";
            case double dbl:
                if (double.IsNaN(dbl))
                    return "global::System.Double.NaN";
                if (double.IsPositiveInfinity(dbl))
                    return "global::System.Double.PositiveInfinity";
                if (double.IsNegativeInfinity(dbl))
                    return "global::System.Double.NegativeInfinity";

                return dbl.ToString("R", CultureInfo.InvariantCulture) + "d";
            case decimal dec:
                return dec.ToString(CultureInfo.InvariantCulture) + "m";
            case long integer:
                return integer.ToString(CultureInfo.InvariantCulture) + "L";
            case ulong integer:
                return integer.ToString(CultureInfo.InvariantCulture) + "UL";
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default(" + SymbolFacts.TypeName(type) + ")";
        }
    }
}

internal static class AppNavSourceEmitter
{
    public static string Emit(string rootNamespace, IReadOnlyList<RouteModel> routes, IReadOnlyList<PageModel> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.Append("namespace ").Append(rootNamespace).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("[global::System.CodeDom.Compiler.GeneratedCode(\"AdamE.AppNav.Generators\", \"1.0.0.0\")]");
        builder.AppendLine("public static partial class AppNavGenerated");
        builder.AppendLine("{");
        builder.AppendLine("    public static global::AdamE.AppNav.Routing.RouteTable CreateRouteTable()");
        builder.AppendLine("    {");
        builder.AppendLine("        return global::AdamE.AppNav.Routing.RouteTable.Create(static routes =>");
        builder.AppendLine("        {");
        builder.AppendLine("            routes.AddModule(RouteTableModule);");
        builder.AppendLine("        });");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    public static global::AdamE.AppNav.Routing.IRouteTableModule RouteTableModule { get; } = new GeneratedRouteTableModule();");

        if (pages.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("    public static global::AdamE.AppNav.Maui.IMauiRoutePageModule MauiPageModule { get; } = new GeneratedMauiPageModule();");
        }

        builder.AppendLine();
        builder.AppendLine("    private static TEnum ParseEnum<TEnum>(string value, string name)");
        builder.AppendLine("        where TEnum : struct, global::System.Enum");
        builder.AppendLine("    {");
        builder.AppendLine("        if (global::System.Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))");
        builder.AppendLine("            return result;");
        builder.AppendLine();
        builder.AppendLine("        throw new global::System.FormatException(\"Route value '\" + name + \"' could not be converted to \" + typeof(TEnum).Name + \".\");");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static TValue ParseNumber<TValue>(string value, string name, global::System.Func<string, TValue> parse)");
        builder.AppendLine("    {");
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine("            return parse(value);");
        builder.AppendLine("        }");
        builder.AppendLine("        catch (global::System.OverflowException ex)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new global::System.FormatException(\"Route value '\" + name + \"' could not be converted to \" + typeof(TValue).Name + \".\", ex);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static TValue ConvertRouteValue<TValue>(string value, string name)");
        builder.AppendLine("    {");
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        builder.AppendLine("            return (TValue)global::System.ComponentModel.TypeDescriptor.GetConverter(typeof(TValue)).ConvertFrom(null, global::System.Globalization.CultureInfo.InvariantCulture, value)!;");
        builder.AppendLine("        }");
        builder.AppendLine("        catch (global::System.ArgumentException ex)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new global::System.FormatException(\"Route value '\" + name + \"' could not be converted to \" + typeof(TValue).Name + \".\", ex);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");

        EmitRouteModule(builder, routes);

        if (pages.Count > 0)
            EmitPageModule(builder, pages);

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitRouteModule(StringBuilder builder, IReadOnlyList<RouteModel> routes)
    {
        builder.AppendLine();
        builder.AppendLine("    private sealed class GeneratedRouteTableModule : global::AdamE.AppNav.Routing.IRouteTableModule");
        builder.AppendLine("    {");
        builder.AppendLine("        public void MapRoutes(global::AdamE.AppNav.Routing.RouteTableBuilder routes)");
        builder.AppendLine("        {");

        foreach (RouteModel route in routes)
            EmitRouteMapping(builder, route);

        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static void EmitRouteMapping(StringBuilder builder, RouteModel route)
    {
        string routeTypeName = SymbolFacts.TypeName(route.RouteType);
        builder.AppendLine("            routes.Map<" + routeTypeName + ">(");
        builder.AppendLine("                " + SymbolDisplay.FormatLiteral(route.Template.Value, quote: true) + ",");
        builder.AppendLine("                static match =>");
        builder.AppendLine("                {");

        for (var i = 0; i < route.ConstructorArguments.Count; i++)
        {
            ConstructorArgument argument = route.ConstructorArguments[i];
            switch (argument.Kind)
            {
                case ConstructorArgumentKind.Path:
                {
                    TemplateParameter parameter = argument.PathParameter!;
                    if (parameter.IsOptional || parameter.IsCatchAll)
                    {
                        builder.AppendLine("                    string? appNavValue" + i + " = match.PathValues.TryGetValue(" +
                                           SymbolDisplay.FormatLiteral(parameter.Name, quote: true) +
                                           ", out string? appNavPathValue" + i + ") ? appNavPathValue" + i + " : null;");
                    }
                    else
                    {
                        builder.AppendLine("                    string? appNavValue" + i + " = match.Path(" +
                                           SymbolDisplay.FormatLiteral(parameter.Name, quote: true) + ");");
                    }

                    break;
                }
                case ConstructorArgumentKind.Query:
                    if (RouteValueSourceEmitter.TryGetSupportedQueryCollection(
                            argument.Parameter.Type,
                            out _,
                            out _))
                    {
                        builder.AppendLine("                    global::System.Collections.Generic.IReadOnlyList<string>? appNavValue" +
                                           i + " = match.QueryValueLists.ContainsKey(" +
                                           SymbolDisplay.FormatLiteral(argument.QueryBinding!.QueryName, quote: true) +
                                           ") ? match.QueryAll(" +
                                           SymbolDisplay.FormatLiteral(argument.QueryBinding.QueryName, quote: true) +
                                           ") : null;");
                    }
                    else
                    {
                        builder.AppendLine("                    string? appNavValue" + i + " = match.Query(" +
                                           SymbolDisplay.FormatLiteral(argument.QueryBinding!.QueryName, quote: true) + ");");
                    }

                    break;
            }
        }

        for (var i = 0; i < route.MetadataQueryBindings.Count; i++)
        {
            MetadataQueryBinding metadataBinding = route.MetadataQueryBindings[i];
            string valueName = "appNavMetadataValue" + i;
            builder.AppendLine("                    if (match.QueryValues.TryGetValue(" +
                               SymbolDisplay.FormatLiteral(metadataBinding.QueryName, quote: true) +
                               ", out string? " + valueName + "))");
            builder.AppendLine("                    {");
            builder.AppendLine("                        match.AddMetadata(" + metadataBinding.AccessExpression + ", " +
                               RouteValueSourceEmitter.EmitParse(
                                   metadataBinding.ValueType,
                                   valueName,
                                   SymbolDisplay.FormatLiteral(metadataBinding.QueryName, quote: true)) +
                               ", omitWhenNull: " + BoolLiteral(metadataBinding.OmitWhenNull) + ");");
            builder.AppendLine("                    }");
            if (!metadataBinding.OmitWhenNull)
            {
                builder.AppendLine("                    else");
                builder.AppendLine("                    {");
                builder.AppendLine("                        match.AddMetadata(" +
                                   SymbolDisplay.FormatLiteral(metadataBinding.QueryName, quote: true) +
                                   ", null, omitWhenNull: false);");
                builder.AppendLine("                    }");
            }
        }

        string arguments = string.Join(", ", route.ConstructorArguments.Select(EmitConstructorArgument));
        builder.AppendLine("                    return new " + routeTypeName + "(" + arguments + ");");
        builder.AppendLine("                },");
        builder.AppendLine("                static format =>");
        builder.AppendLine("                {");

        foreach (TemplateParameter parameter in route.Template.Parameters)
        {
            IPropertySymbol property = route.PathProperties[parameter.Name];
            builder.AppendLine("                    format.PathParam(" +
                               SymbolDisplay.FormatLiteral(parameter.Name, quote: true) +
                               ", static route => route." + SymbolFacts.Identifier(property.Name) + ");");
        }

        foreach (QueryBinding queryBinding in route.QueryBindings)
        {
            builder.AppendLine("                    format.QueryParam(" +
                               SymbolDisplay.FormatLiteral(queryBinding.QueryName, quote: true) +
                               ", static route => route." + SymbolFacts.Identifier(queryBinding.Property.Name) +
                               ", omitWhenNull: " + BoolLiteral(queryBinding.OmitWhenNull) + ");");
        }

        foreach (MetadataQueryBinding metadataBinding in route.MetadataQueryBindings)
            builder.AppendLine("                    format.QueryMetadata(" + metadataBinding.AccessExpression +
                               ", omitWhenNull: " + BoolLiteral(metadataBinding.OmitWhenNull) + ");");

        builder.AppendLine("                });");
    }

    private static string EmitConstructorArgument(ConstructorArgument argument, int index)
    {
        switch (argument.Kind)
        {
            case ConstructorArgumentKind.Path:
            case ConstructorArgumentKind.Query:
            {
                string variable = "appNavValue" + index;
                if (argument.Kind == ConstructorArgumentKind.Path &&
                    argument.PathParameter is { IsOptional: false, IsCatchAll: false })
                    return RouteValueSourceEmitter.EmitParse(
                        argument.Parameter.Type,
                        variable + "!",
                        SymbolDisplay.FormatLiteral(argument.PathParameter.Name, quote: true));

                string defaultValue = RouteValueSourceEmitter.EmitDefault(argument.Parameter);
                if (argument.Kind == ConstructorArgumentKind.Query &&
                    RouteValueSourceEmitter.TryGetSupportedQueryCollection(argument.Parameter.Type, out _, out _))
                {
                    return variable + " is null ? " + defaultValue + " : " +
                           RouteValueSourceEmitter.EmitQueryCollectionParse(
                               argument.Parameter.Type,
                               variable,
                               SymbolDisplay.FormatLiteral(argument.QueryBinding!.QueryName, quote: true));
                }

                string valueName = argument.Kind == ConstructorArgumentKind.Query
                    ? argument.QueryBinding!.QueryName
                    : argument.PathParameter!.Name;
                return variable + " is null ? " + defaultValue + " : " +
                       RouteValueSourceEmitter.EmitParse(
                           argument.Parameter.Type,
                           variable,
                           SymbolDisplay.FormatLiteral(valueName, quote: true));
            }
            default:
                return RouteValueSourceEmitter.EmitDefault(argument.Parameter);
        }
    }

    private static void EmitPageModule(StringBuilder builder, IReadOnlyList<PageModel> pages)
    {
        builder.AppendLine();
        builder.AppendLine("    private sealed class GeneratedMauiPageModule : global::AdamE.AppNav.Maui.IMauiRoutePageModule");
        builder.AppendLine("    {");
        builder.AppendLine("        public void MapPages(global::AdamE.AppNav.Maui.MauiRoutePageRegistry pages)");
        builder.AppendLine("        {");

        foreach (PageModel page in pages)
        {
            string routeTypeName = SymbolFacts.TypeName(page.RouteType);
            string pageTypeName = SymbolFacts.TypeName(page.PageType);
            if (page.PageModelType is not null)
            {
                string pageModelTypeName = SymbolFacts.TypeName(page.PageModelType);
                builder.AppendLine("            pages.MapPage<" + routeTypeName + ">(static (services, route) =>");
                builder.AppendLine("            {");
                builder.AppendLine("                var page = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<" +
                                   pageTypeName + ">(services);");
                builder.AppendLine("                page.BindingContext ??= global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<" +
                                   pageModelTypeName + ">(services);");
                builder.AppendLine("                return page;");
                builder.AppendLine("            });");
                continue;
            }

            if (page.FromServices)
            {
                builder.AppendLine("            pages.MapPageFromServices<" + routeTypeName + ", " + pageTypeName + ">();");
                continue;
            }

            string arguments = string.Join(", ", page.Constructor!.Parameters.Select(parameter =>
            {
                if (SymbolEqualityComparer.Default.Equals(parameter, page.RouteParameter))
                    return "route";

                if (parameter.Type.ToDisplayString() == "System.IServiceProvider")
                    return "services";

                if (parameter.HasExplicitDefaultValue)
                    return ConstantEmitter.Emit(parameter.Type, parameter.ExplicitDefaultValue);

                if (parameter.IsOptional)
                    return "default(" + SymbolFacts.TypeName(parameter.Type) + ")";

                return "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<" +
                       SymbolFacts.TypeName(parameter.Type) + ">(services)";
            }));

            builder.AppendLine("            pages.MapPage<" + routeTypeName + ">(static (services, route) =>");
            builder.AppendLine("                new " + pageTypeName + "(" + arguments + "));");
        }

        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static string BoolLiteral(bool value)
    {
        return value ? "true" : "false";
    }
}
