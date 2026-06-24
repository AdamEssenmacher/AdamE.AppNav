using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace AdamE.MauiRouter.Routing;

public sealed class ConventionRouteBuilder<TRoute>
    where TRoute : AppRoute
{
    private readonly List<ConventionQueryBinding> _queryBindings = new();
    private readonly List<ConventionMetadataQueryBinding> _metadataQueryBindings = new();
    private readonly HashSet<string> _queryNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _propertyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _metadataNames = new(StringComparer.Ordinal);

    internal IReadOnlyList<ConventionQueryBinding> QueryBindings => _queryBindings;

    internal IReadOnlyList<ConventionMetadataQueryBinding> MetadataQueryBindings => _metadataQueryBindings;

    public ConventionRouteBuilder<TRoute> Query<TValue>(
        Expression<Func<TRoute, TValue>> member,
        bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(member);

        var property = GetProperty(member);
        return AddQuery(property, ConventionQueryName.Infer(property), omitWhenNull);
    }

    public ConventionRouteBuilder<TRoute> Query<TValue>(
        Expression<Func<TRoute, TValue>> member,
        string name,
        bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var property = GetProperty(member);
        return AddQuery(property, name, omitWhenNull);
    }

    public ConventionRouteBuilder<TRoute> QueryMetadata<TValue>(
        RouteMetadataKey<TValue> key,
        bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_metadataNames.Add(key.Name))
        {
            throw new InvalidOperationException(
                $"Metadata query binding for metadata key '{key.Name}' is already registered for route type '{typeof(TRoute).FullName}'.");
        }

        if (!_queryNames.Add(key.Name))
        {
            throw new InvalidOperationException(
                $"Query binding for query parameter '{key.Name}' is already registered for route type '{typeof(TRoute).FullName}'.");
        }

        _metadataQueryBindings.Add(new ConventionMetadataQueryBinding(key.Name, key.Name, typeof(TValue), omitWhenNull));
        return this;
    }

    private ConventionRouteBuilder<TRoute> AddQuery(
        PropertyInfo property,
        string name,
        bool omitWhenNull)
    {
        if (!_propertyNames.Add(property.Name))
        {
            throw new InvalidOperationException(
                $"Query binding for route member '{typeof(TRoute).FullName}.{property.Name}' is already registered.");
        }

        if (!_queryNames.Add(name))
        {
            throw new InvalidOperationException(
                $"Query binding for query parameter '{name}' is already registered for route type '{typeof(TRoute).FullName}'.");
        }

        _queryBindings.Add(new ConventionQueryBinding(property, name, omitWhenNull));
        return this;
    }

    private static PropertyInfo GetProperty<TValue>(Expression<Func<TRoute, TValue>> member)
    {
        Expression body = member.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression } ||
            property.GetMethod is not { IsPublic: true })
        {
            throw new InvalidOperationException(
                $"Query bindings for route type '{typeof(TRoute).FullName}' must select a public route property directly.");
        }

        if (property.DeclaringType is null || !property.DeclaringType.IsAssignableFrom(typeof(TRoute)))
        {
            throw new InvalidOperationException(
                $"Query binding member '{property.Name}' does not belong to route type '{typeof(TRoute).FullName}'.");
        }

        return property;
    }
}

internal sealed record ConventionQueryBinding(
    PropertyInfo Property,
    string QueryName,
    bool OmitWhenNull);

internal sealed record ConventionMetadataQueryBinding(
    string MetadataName,
    string QueryName,
    Type ValueType,
    bool OmitWhenNull);

internal static class ConventionQueryName
{
    public static string Infer(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }
}
