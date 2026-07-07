using System.Globalization;

namespace AdamE.AppNav.Routing;

internal sealed class RouteConstraintRegistry
{
    private readonly IReadOnlyDictionary<string, RouteConstraint> _constraints;

    private RouteConstraintRegistry(IReadOnlyDictionary<string, RouteConstraint> constraints)
    {
        _constraints = constraints;
    }

    public static RouteConstraintRegistry BuiltIn { get; } = new(CreateBuiltInConstraints());

    public bool Contains(string name)
    {
        return _constraints.ContainsKey(name);
    }

    public RouteConstraintRegistry AddCustom(
        string name,
        Func<string, bool> matches,
        IEnumerable<string>? disjointWith)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(matches);

        if (_constraints.TryGetValue(name, out RouteConstraint? existing))
        {
            string message = existing.IsBuiltIn
                ? $"Route constraint '{name}' is built in and cannot be redefined."
                : $"Route constraint '{name}' is already registered.";
            throw new ArgumentException(message, nameof(name));
        }

        var disjointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (disjointWith is not null)
            foreach (string disjointName in disjointWith)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(disjointName);
                disjointNames.Add(disjointName);
            }

        var constraints = new Dictionary<string, RouteConstraint>(_constraints, StringComparer.OrdinalIgnoreCase)
        {
            [name] = new(matches, false, disjointNames)
        };

        return new RouteConstraintRegistry(constraints);
    }

    public bool Satisfies(string value, string? constraintName)
    {
        if (constraintName is null)
            return true;

        return _constraints.TryGetValue(constraintName, out RouteConstraint? constraint) &&
               constraint.Matches(value);
    }

    public bool CanOverlap(string? leftName, RouteConstraintRegistry rightRegistry, string? rightName)
    {
        if (leftName is null || rightName is null)
            return true;

        if (StringComparer.OrdinalIgnoreCase.Equals(leftName, rightName))
            return true;

        RouteConstraint left = Get(leftName);
        RouteConstraint right = rightRegistry.Get(rightName);

        if (left.DisjointWith.Contains(rightName) || right.DisjointWith.Contains(leftName))
            return false;

        if (left.IsBuiltIn && right.IsBuiltIn)
            return BuiltInConstraintsCanOverlap(leftName, rightName);

        return true;
    }

    private RouteConstraint Get(string name)
    {
        return _constraints.TryGetValue(name, out RouteConstraint? constraint)
            ? constraint
            : throw new InvalidOperationException($"Route constraint '{name}' is not registered.");
    }

    private static Dictionary<string, RouteConstraint> CreateBuiltInConstraints()
    {
        return new Dictionary<string, RouteConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            ["int"] = BuiltInConstraint(value =>
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)),
            ["long"] = BuiltInConstraint(value =>
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)),
            ["guid"] = BuiltInConstraint(value => Guid.TryParse(value, out _)),
            ["bool"] = BuiltInConstraint(value => bool.TryParse(value, out _)),
            ["decimal"] = BuiltInConstraint(value =>
                decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)),
            ["alpha"] = BuiltInConstraint(value => value.Length > 0 && value.All(char.IsLetter))
        };
    }

    private static RouteConstraint BuiltInConstraint(Func<string, bool> matches)
    {
        return new RouteConstraint(
            matches,
            true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static bool BuiltInConstraintsCanOverlap(string left, string right)
    {
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

    private sealed record RouteConstraint(
        Func<string, bool> Matches,
        bool IsBuiltIn,
        IReadOnlySet<string> DisjointWith);
}
