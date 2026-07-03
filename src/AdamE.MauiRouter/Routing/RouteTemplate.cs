namespace AdamE.MauiRouter.Routing;

public sealed class RouteTemplate
{
    private readonly IReadOnlyList<TemplateSegment> _segments;
    private readonly Dictionary<string, TemplateSegment> _parameters;
    private readonly RouteConstraintRegistry _constraints;

    private RouteTemplate(
        string value,
        IReadOnlyList<TemplateSegment> segments,
        RouteConstraintRegistry constraints)
    {
        Value = value;
        _segments = segments;
        _constraints = constraints;
        _parameters = segments
            .Where(segment => segment.ParameterName is not null)
            .ToDictionary(segment => segment.ParameterName!, StringComparer.OrdinalIgnoreCase);
        ParameterNames = _parameters.Keys.ToArray();
        PrecedenceKey = string.Join(".", segments.Select(segment => segment.Precedence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public string Value { get; }

    public IReadOnlyList<string> ParameterNames { get; }

    internal IReadOnlyList<TemplateSegment> Segments => _segments;

    internal string PrecedenceKey { get; }

    internal int MinimumSegmentCount =>
        _segments.Count(segment => !segment.IsOptional && !segment.IsCatchAll);

    internal int? MaximumSegmentCount =>
        _segments.Any(segment => segment.IsCatchAll)
            ? null
            : _segments.Count;

    public static RouteTemplate Parse(string value)
    {
        return Parse(value, RouteConstraintRegistry.BuiltIn);
    }

    internal static RouteTemplate Parse(string value, RouteConstraintRegistry constraints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(constraints);

        if (!value.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Route templates must start with '/'.", nameof(value));
        }

        var segments = SplitPath(value)
            .Select(segment => ParseSegment(segment, constraints))
            .ToArray();

        ValidateSegments(value, segments);
        return new RouteTemplate(value, segments, constraints);
    }

    public IReadOnlyDictionary<string, string>? Match(string path)
    {
        var pathSegments = SplitPath(path).ToArray();
        if (pathSegments.Length < MinimumSegmentCount)
        {
            return null;
        }

        if (MaximumSegmentCount is { } max && pathSegments.Length > max)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathIndex = 0;

        foreach (var templateSegment in _segments)
        {
            if (templateSegment.IsCatchAll)
            {
                var remaining = pathSegments
                    .Skip(pathIndex)
                    .Select(Uri.UnescapeDataString)
                    .ToArray();
                values[templateSegment.ParameterName!] = string.Join("/", remaining);
                pathIndex = pathSegments.Length;
                break;
            }

            if (pathIndex >= pathSegments.Length)
            {
                if (templateSegment.IsOptional)
                {
                    continue;
                }

                return null;
            }

            var pathSegment = Uri.UnescapeDataString(pathSegments[pathIndex]);

            if (templateSegment.ParameterName is not null)
            {
                if (!_constraints.Satisfies(pathSegment, templateSegment.Constraint))
                {
                    return null;
                }

                values[templateSegment.ParameterName] = pathSegment;
                pathIndex++;
                continue;
            }

            if (!StringComparer.Ordinal.Equals(templateSegment.Literal, pathSegment))
            {
                return null;
            }

            pathIndex++;
        }

        return pathIndex == pathSegments.Length ? values : null;
    }

    public string Format(IReadOnlyDictionary<string, string> pathValues)
    {
        ArgumentNullException.ThrowIfNull(pathValues);

        var segments = new List<string>(_segments.Count);
        foreach (var segment in _segments)
        {
            if (segment.ParameterName is null)
            {
                segments.Add(segment.Literal!);
                continue;
            }

            if (!pathValues.TryGetValue(segment.ParameterName, out var value) ||
                string.IsNullOrEmpty(value))
            {
                if (segment.IsOptional)
                {
                    continue;
                }

                if (segment.IsCatchAll)
                {
                    continue;
                }

                throw new InvalidOperationException($"No value was supplied for path parameter '{segment.ParameterName}'.");
            }

            if (!segment.IsCatchAll && !_constraints.Satisfies(value, segment.Constraint))
            {
                throw new InvalidOperationException(
                    $"Path parameter '{segment.ParameterName}' value '{value}' does not satisfy constraint '{segment.Constraint}'.");
            }

            if (segment.IsCatchAll)
            {
                segments.AddRange(value
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));
                continue;
            }

            segments.Add(Uri.EscapeDataString(value));
        }

        return "/" + string.Join("/", segments);
    }

    internal bool IsOptionalParameter(string name)
    {
        return _parameters.TryGetValue(name, out var segment) && segment.IsOptional;
    }

    internal bool IsCatchAllParameter(string name)
    {
        return _parameters.TryGetValue(name, out var segment) && segment.IsCatchAll;
    }

    internal bool CanOverlap(RouteTemplate other)
    {
        var min = Math.Max(MinimumSegmentCount, other.MinimumSegmentCount);
        var max = MaximumSegmentCount is null || other.MaximumSegmentCount is null
            ? Math.Max(_segments.Count, other._segments.Count)
            : Math.Min(MaximumSegmentCount.Value, other.MaximumSegmentCount.Value);

        if (min > max)
        {
            return false;
        }

        for (var i = 0; i < min; i++)
        {
            var left = SegmentAt(i);
            var right = other.SegmentAt(i);
            if (left is null || right is null)
            {
                continue;
            }

            if (!SegmentsCanOverlap(left, other, right))
            {
                return false;
            }
        }

        return true;
    }

    internal int ComparePrecedence(RouteTemplate other)
    {
        var count = Math.Max(_segments.Count, other._segments.Count);
        for (var i = 0; i < count; i++)
        {
            var left = i < _segments.Count ? _segments[i] : null;
            var right = i < other._segments.Count ? other._segments[i] : null;
            var comparison = CompareSegmentPrecedence(left, right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _segments.Count.CompareTo(other._segments.Count);
    }

    private static int CompareSegmentPrecedence(TemplateSegment? left, TemplateSegment? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return right!.IsOptional || right.IsCatchAll ? -1 : 1;
        }

        if (right is null)
        {
            return left.IsOptional || left.IsCatchAll ? 1 : -1;
        }

        return right.Precedence.CompareTo(left.Precedence);
    }

    private TemplateSegment? SegmentAt(int index)
    {
        if (index < _segments.Count)
        {
            return _segments[index];
        }

        return _segments.Count > 0 && _segments[^1].IsCatchAll
            ? _segments[^1]
            : null;
    }

    private static TemplateSegment ParseSegment(string segment, RouteConstraintRegistry constraints)
    {
        if (segment.StartsWith("{", StringComparison.Ordinal) &&
            segment.EndsWith("}", StringComparison.Ordinal) &&
            segment.Length > 2)
        {
            var body = segment[1..^1];
            if (body.StartsWith("*", StringComparison.Ordinal))
            {
                var catchAllName = body[1..];
                ArgumentException.ThrowIfNullOrWhiteSpace(catchAllName);
                return TemplateSegment.CatchAll(catchAllName);
            }

            var separator = body.IndexOf(':');
            var name = separator < 0 ? body : body[..separator];
            var constraint = separator < 0 ? null : body[(separator + 1)..];
            var optional = false;

            if (constraint is not null && constraint.EndsWith("?", StringComparison.Ordinal))
            {
                optional = true;
                constraint = constraint[..^1];
            }
            else if (name.EndsWith("?", StringComparison.Ordinal))
            {
                optional = true;
                name = name[..^1];
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (constraint is not null && !constraints.Contains(constraint))
            {
                throw new ArgumentException($"Route constraint '{constraint}' is not supported.");
            }

            return TemplateSegment.Parameter(name, constraint, optional);
        }

        if (segment.Contains('{', StringComparison.Ordinal) || segment.Contains('}', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Route template segment '{segment}' is invalid.");
        }

        return TemplateSegment.ForLiteral(Uri.UnescapeDataString(segment));
    }

    private static void ValidateSegments(string template, IReadOnlyList<TemplateSegment> segments)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionalStarted = false;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            if (segment.ParameterName is not null && !seenNames.Add(segment.ParameterName))
            {
                throw new ArgumentException($"Route template '{template}' contains duplicate parameter '{segment.ParameterName}'.");
            }

            if (segment.IsCatchAll && i != segments.Count - 1)
            {
                throw new ArgumentException($"Route template '{template}' has a catch-all segment that is not final.");
            }

            if (optionalStarted && !segment.IsOptional && !segment.IsCatchAll)
            {
                throw new ArgumentException($"Route template '{template}' has a non-optional segment after an optional segment.");
            }

            if (segment.IsOptional)
            {
                optionalStarted = true;
            }
        }
    }

    private bool SegmentsCanOverlap(
        TemplateSegment left,
        RouteTemplate rightTemplate,
        TemplateSegment right)
    {
        if (left.IsCatchAll || right.IsCatchAll)
        {
            return true;
        }

        if (left.Literal is not null && right.Literal is not null)
        {
            return StringComparer.Ordinal.Equals(left.Literal, right.Literal);
        }

        if (left.Literal is not null && right.ParameterName is not null)
        {
            return rightTemplate._constraints.Satisfies(left.Literal, right.Constraint);
        }

        if (right.Literal is not null && left.ParameterName is not null)
        {
            return _constraints.Satisfies(right.Literal, left.Constraint);
        }

        return _constraints.CanOverlap(left.Constraint, rightTemplate._constraints, right.Constraint);
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        return path.Trim('/').Length == 0
            ? Enumerable.Empty<string>()
            : path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    internal sealed record TemplateSegment(
        string? Literal,
        string? ParameterName,
        string? Constraint,
        bool IsOptional,
        bool IsCatchAll)
    {
        public int Precedence =>
            Literal is not null ? 5 :
            Constraint is not null ? 4 :
            IsCatchAll ? 1 :
            !IsOptional ? 3 :
            2;

        public static TemplateSegment ForLiteral(string value) => new(value, null, null, false, false);

        public static TemplateSegment Parameter(string name, string? constraint, bool optional) =>
            new(null, name, constraint, optional, false);

        public static TemplateSegment CatchAll(string name) => new(null, name, null, false, true);
    }
}
