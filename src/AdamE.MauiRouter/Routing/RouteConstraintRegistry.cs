using System.Globalization;

namespace AdamE.MauiRouter.Routing;

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

        if (_constraints.TryGetValue(name, out var existing))
        {
            var message = existing.IsBuiltIn
                ? $"Route constraint '{name}' is built in and cannot be redefined."
                : $"Route constraint '{name}' is already registered.";
            throw new ArgumentException(message, nameof(name));
        }

        var disjointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (disjointWith is not null)
        {
            foreach (var disjointName in disjointWith)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(disjointName);
                disjointNames.Add(disjointName);
            }
        }

        var constraints = new Dictionary<string, RouteConstraint>(_constraints, StringComparer.OrdinalIgnoreCase)
        {
            [name] = new RouteConstraint(name, matches, IsBuiltIn: false, disjointNames)
        };

        return new RouteConstraintRegistry(constraints);
    }

    public bool Satisfies(string value, string? constraintName)
    {
        if (constraintName is null)
        {
            return true;
        }

        return _constraints.TryGetValue(constraintName, out var constraint) &&
               constraint.Matches(value);
    }

    public bool CanOverlap(string? leftName, RouteConstraintRegistry rightRegistry, string? rightName)
    {
        if (leftName is null || rightName is null)
        {
            return true;
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(leftName, rightName))
        {
            return true;
        }

        var left = Get(leftName);
        var right = rightRegistry.Get(rightName);

        if (left.DisjointWith.Contains(rightName) || right.DisjointWith.Contains(leftName))
        {
            return false;
        }

        if (left.IsBuiltIn && right.IsBuiltIn)
        {
            return BuiltInConstraintsCanOverlap(leftName, rightName);
        }

        return true;
    }

    private RouteConstraint Get(string name)
    {
        if (_constraints.TryGetValue(name, out var constraint))
        {
            return constraint;
        }

        throw new InvalidOperationException($"Route constraint '{name}' is not registered.");
    }

    private static IReadOnlyDictionary<string, RouteConstraint> CreateBuiltInConstraints()
    {
        return new Dictionary<string, RouteConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            ["int"] = BuiltInConstraint("int", value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)),
            ["long"] = BuiltInConstraint("long", value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)),
            ["guid"] = BuiltInConstraint("guid", value => Guid.TryParse(value, out _)),
            ["bool"] = BuiltInConstraint("bool", value => bool.TryParse(value, out _)),
            ["decimal"] = BuiltInConstraint("decimal", value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)),
            ["alpha"] = BuiltInConstraint("alpha", value => value.Length > 0 && value.All(char.IsLetter))
        };
    }

    private static RouteConstraint BuiltInConstraint(string name, Func<string, bool> matches)
    {
        return new RouteConstraint(
            name,
            matches,
            IsBuiltIn: true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static bool BuiltInConstraintsCanOverlap(string left, string right)
    {
        if (IsNumericConstraint(left) && IsNumericConstraint(right))
        {
            return true;
        }

        if (PairEquals(left, right, "bool", "alpha"))
        {
            return true;
        }

        if (PairEquals(left, right, "alpha", "guid"))
        {
            return true;
        }

        return false;
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
        string Name,
        Func<string, bool> Matches,
        bool IsBuiltIn,
        IReadOnlySet<string> DisjointWith);
}
