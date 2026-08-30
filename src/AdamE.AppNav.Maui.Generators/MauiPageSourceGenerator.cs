using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AdamE.AppNav.Maui.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class MauiPageSourceGenerator : IIncrementalGenerator
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
        MauiPageSymbols symbols = MauiPageSymbols.Create(compilation);
        if (symbols.AppRoute is null || symbols.MauiRoutePageAttribute is null)
            return;

        List<PageModel> pages = new();
        foreach (INamedTypeSymbol type in SymbolWalker.GetAllTypes(compilation.GlobalNamespace))
        {
            if (!SymbolFacts.IsDeclaredInSource(type))
                continue;

            AttributeData? pageAttribute = SymbolFacts.GetAttribute(type, MauiPageSymbols.MauiRoutePageAttributeName);
            if (pageAttribute is null)
                continue;

            PageModel? model = PageModelFactory.TryCreate(type, pageAttribute, symbols, context.ReportDiagnostic);
            if (model is not null)
                pages.Add(model);
        }

        ValidatePageMappings(pages, context.ReportDiagnostic);
        if (pages.Count == 0)
            return;

        string rootNamespace = NamespaceFacts.GetRootNamespace(compilation, options);
        context.AddSource(
            "AppNavMauiPages.g.cs",
            SourceText.From(MauiPageSourceEmitter.Emit(rootNamespace, pages), Encoding.UTF8));
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
                    MauiPageDiagnostics.DuplicatePageRoute,
                    right.Location,
                    right.RouteType.ToDisplayString(),
                    left.PageType.ToDisplayString(),
                    right.PageType.ToDisplayString()));
            }
        }
    }
}

internal sealed class MauiPageSymbols
{
    public const string AppRouteName = "AdamE.AppNav.AppRoute";
    public const string RouteAttributeName = "AdamE.AppNav.Routing.AppNavRouteAttribute";
    public const string MauiRoutePageAttributeName = "AdamE.AppNav.Maui.MauiRoutePageAttribute";
    public const string MauiPageName = "Microsoft.Maui.Controls.Page";
    public const string ActivatorUtilitiesConstructorAttributeName =
        "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute";

    private MauiPageSymbols(
        INamedTypeSymbol? appRoute,
        INamedTypeSymbol? mauiPage,
        INamedTypeSymbol? mauiRoutePageAttribute)
    {
        AppRoute = appRoute;
        MauiPage = mauiPage;
        MauiRoutePageAttribute = mauiRoutePageAttribute;
    }

    public INamedTypeSymbol? AppRoute { get; }

    public INamedTypeSymbol? MauiPage { get; }

    public INamedTypeSymbol? MauiRoutePageAttribute { get; }

    public static MauiPageSymbols Create(Compilation compilation)
    {
        return new MauiPageSymbols(
            compilation.GetTypeByMetadataName(AppRouteName),
            compilation.GetTypeByMetadataName(MauiPageName),
            compilation.GetTypeByMetadataName(MauiRoutePageAttributeName));
    }
}

internal static class NamespaceFacts
{
    public static string GetRootNamespace(
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
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return false;
        }

        return true;
    }

    public static bool IsDeclaredInSource(ISymbol symbol)
    {
        return symbol.Locations.Any(static location => location.IsInSource);
    }

    public static bool IsAssignableTo(ITypeSymbol source, ITypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(source, target))
            return true;

        if (target.SpecialType == SpecialType.System_Object)
            return true;

        for (ITypeSymbol? current = source.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
                return true;
        }

        return source.AllInterfaces.Any(interfaceType => SymbolEqualityComparer.Default.Equals(interfaceType, target));
    }

    public static string TypeName(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}

internal static class PageModelFactory
{
    public static PageModel? TryCreate(
        INamedTypeSymbol pageType,
        AttributeData attribute,
        MauiPageSymbols symbols,
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
            SymbolFacts.GetAttribute(routeType, MauiPageSymbols.RouteAttributeName) is null)
        {
            reportDiagnostic(Diagnostic.Create(
                MauiPageDiagnostics.InvalidPageRoute,
                location,
                pageType.ToDisplayString(),
                routeType?.ToDisplayString() ?? string.Empty));
            return null;
        }

        if (SymbolFacts.ContainsTypeParameters(pageType) ||
            !SymbolFacts.IsAccessibleFromGeneratedCode(pageType))
        {
            reportDiagnostic(Diagnostic.Create(
                MauiPageDiagnostics.InvalidPageType,
                location,
                pageType.ToDisplayString()));
            return null;
        }

        if (symbols.MauiPage is not null &&
            !SymbolFacts.IsAssignableTo(pageType, symbols.MauiPage) &&
            !CanDeferPageTypeValidation(pageType))
        {
            reportDiagnostic(Diagnostic.Create(
                MauiPageDiagnostics.InvalidPageType,
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
                MauiPageDiagnostics.InvalidPageModelType,
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
                MauiPageSymbols.ActivatorUtilitiesConstructorAttributeName) is not null)
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
                MauiPageDiagnostics.AmbiguousPageConstructor,
                location,
                pageType.ToDisplayString()));
            return null;
        }

        if (SelectPageRouteParameter(selected, routeType) is null)
        {
            reportDiagnostic(Diagnostic.Create(
                MauiPageDiagnostics.MissingPageRouteParameter,
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

        if (parameterType.SpecialType == SpecialType.System_Object)
            return 1000;

        var distance = 1;
        for (INamedTypeSymbol? current = routeType.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, parameterType))
                return distance;

            distance++;
        }

        return 500;
    }
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

internal static class ConstantEmitter
{
    public static string Emit(ITypeSymbol type, object? value)
    {
        if (value is null)
            return type.IsValueType ? "default(" + SymbolFacts.TypeName(type) + ")" : "null";

        ITypeSymbol valueType = type;
        if (valueType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            valueType = ((INamedTypeSymbol)valueType).TypeArguments[0];

        if (valueType.TypeKind == TypeKind.Enum)
        {
            string enumTypeName = SymbolFacts.TypeName(valueType);
            ITypeSymbol underlyingType = ((INamedTypeSymbol)valueType).EnumUnderlyingType!;
            if (underlyingType.SpecialType is SpecialType.System_Byte or
                SpecialType.System_UInt16 or
                SpecialType.System_UInt32 or
                SpecialType.System_UInt64)
            {
                string suffix = underlyingType.SpecialType == SpecialType.System_UInt64 ? "UL" : "U";
                return "(" + enumTypeName + ")" +
                       Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + suffix;
            }

            string literal = Convert.ToInt64(value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
            return "(" + enumTypeName + ")(" + literal + ")";
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
                return Convert.ToString(value, CultureInfo.InvariantCulture) ??
                       "default(" + SymbolFacts.TypeName(type) + ")";
        }
    }
}

internal static class MauiPageSourceEmitter
{
    public static string Emit(string rootNamespace, IReadOnlyList<PageModel> pages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.Append("namespace ").Append(rootNamespace).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("public static partial class AppNavGenerated");
        builder.AppendLine("{");
        builder.AppendLine("    public static global::AdamE.AppNav.Maui.IMauiRoutePageModule MauiPageModule { get; } = new GeneratedMauiPageModule();");
        builder.AppendLine();
        builder.AppendLine("    private sealed class GeneratedMauiPageModule : global::AdamE.AppNav.Maui.IMauiRoutePageModule");
        builder.AppendLine("    {");
        builder.AppendLine("        public void MapPages(global::AdamE.AppNav.Maui.MauiRoutePageRegistry pages)");
        builder.AppendLine("        {");

        foreach (PageModel page in pages)
            EmitPageMapping(builder, page);

        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitPageMapping(StringBuilder builder, PageModel page)
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
            return;
        }

        if (page.FromServices)
        {
            builder.AppendLine("            pages.MapPageFromServices<" + routeTypeName + ", " + pageTypeName + ">();");
            return;
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
}
