namespace AdamE.AppNav.Routing;

public sealed class RouteTemplate
{
    private static readonly IReadOnlyDictionary<string, string> EmptyPathValues =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private readonly TemplateSegment[] _segments;
    private readonly Dictionary<string, TemplateSegment> _parameters;
    private readonly RouteConstraintRegistry _constraints;
    private readonly string[] _literalPrefix;
    private readonly int _minimumSegmentCount;
    private readonly int? _maximumSegmentCount;

    private RouteTemplate(
        string value,
        TemplateSegment[] segments,
        RouteConstraintRegistry constraints)
    {
        Value = value;
        _segments = segments;
        _constraints = constraints;
        _parameters = segments
            .Where(segment => segment.ParameterName is not null)
            .ToDictionary(segment => segment.ParameterName!, StringComparer.OrdinalIgnoreCase);
        ParameterNames = _parameters.Keys.ToArray();
        _literalPrefix = CreateLiteralPrefix(segments);
        _minimumSegmentCount = segments.Count(segment => segment is { IsOptional: false, IsCatchAll: false });
        _maximumSegmentCount = segments.Any(segment => segment.IsCatchAll)
            ? null
            : segments.Length;
    }

    public string Value { get; }

    public IReadOnlyList<string> ParameterNames { get; }

    private int MinimumSegmentCount =>
        _minimumSegmentCount;

    private int? MaximumSegmentCount =>
        _maximumSegmentCount;

    internal IReadOnlyList<string> LiteralPrefix =>
        _literalPrefix;

    public static RouteTemplate Parse(string value)
    {
        return Parse(value, RouteConstraintRegistry.BuiltIn);
    }

    internal static RouteTemplate Parse(string value, RouteConstraintRegistry constraints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(constraints);

        if (!value.StartsWith('/'))
            throw new ArgumentException("Route templates must start with '/'.", nameof(value));

        TemplateSegment[] segments = SplitPath(value)
            .Select(segment => ParseSegment(segment, constraints))
            .ToArray();

        ValidateSegments(value, segments);
        return new RouteTemplate(value, segments, constraints);
    }

    public IReadOnlyDictionary<string, string>? Match(string path)
    {
        return Match(SplitPathForMatch(path));
    }

    internal IReadOnlyDictionary<string, string>? Match(IReadOnlyList<string> pathSegments)
    {
        if (pathSegments.Count < MinimumSegmentCount)
            return null;

        if (MaximumSegmentCount is { } max && pathSegments.Count > max)
            return null;

        Dictionary<string, string>? values = null;
        var pathIndex = 0;

        foreach (TemplateSegment templateSegment in _segments)
        {
            if (templateSegment.IsCatchAll)
            {
                values ??= new Dictionary<string, string>(_parameters.Count, StringComparer.OrdinalIgnoreCase);
                values[templateSegment.ParameterName!] = JoinRemainingSegments(pathSegments, pathIndex);
                pathIndex = pathSegments.Count;
                break;
            }

            if (pathIndex >= pathSegments.Count)
            {
                if (templateSegment.IsOptional)
                    continue;

                return null;
            }

            string pathSegment = pathSegments[pathIndex];

            if (templateSegment.ParameterName is not null)
            {
                if (!_constraints.Satisfies(pathSegment, templateSegment.Constraint))
                    return null;

                values ??= new Dictionary<string, string>(_parameters.Count, StringComparer.OrdinalIgnoreCase);
                values[templateSegment.ParameterName] = pathSegment;
                pathIndex++;
                continue;
            }

            if (!StringComparer.Ordinal.Equals(templateSegment.Literal, pathSegment))
                return null;

            pathIndex++;
        }

        return pathIndex == pathSegments.Count ? values ?? EmptyPathValues : null;
    }

    public string Format(IReadOnlyDictionary<string, string> pathValues)
    {
        ArgumentNullException.ThrowIfNull(pathValues);

        var segments = new List<string>(_segments.Length);
        string? firstMissingOptionalParameter = null;
        foreach (TemplateSegment segment in _segments)
        {
            if (segment.ParameterName is null)
            {
                segments.Add(Uri.EscapeDataString(segment.Literal!));
                continue;
            }

            if (!pathValues.TryGetValue(segment.ParameterName, out string? value) ||
                string.IsNullOrEmpty(value))
            {
                if (segment.IsOptional)
                {
                    firstMissingOptionalParameter ??= segment.ParameterName;
                    continue;
                }

                if (segment.IsCatchAll)
                    continue;

                throw new InvalidOperationException(
                    $"No value was supplied for path parameter '{segment.ParameterName}'.");
            }

            string[]? catchAllSegments = segment.IsCatchAll
                ? value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                : null;
            if (catchAllSegments is { Length: 0 })
                continue;

            if (firstMissingOptionalParameter is not null)
                throw new InvalidOperationException(
                    $"Route template '{Value}' cannot format path parameter '{segment.ParameterName}' " +
                    $"because optional path parameter '{firstMissingOptionalParameter}' was omitted.");

            switch (segment.IsCatchAll)
            {
                case false when !_constraints.Satisfies(value, segment.Constraint):
                    throw new InvalidOperationException(
                        $"Path parameter '{segment.ParameterName}' value '{value}' does not satisfy constraint '{segment.Constraint}'.");
                case true:
                    segments.AddRange(catchAllSegments!.Select(Uri.EscapeDataString));
                    continue;
                default:
                    segments.Add(Uri.EscapeDataString(value));
                    break;
            }
        }

        return "/" + string.Join("/", segments);
    }

    internal bool IsOptionalParameter(string name)
    {
        return _parameters.TryGetValue(name, out TemplateSegment? segment) && segment.IsOptional;
    }

    internal bool IsCatchAllParameter(string name)
    {
        return _parameters.TryGetValue(name, out TemplateSegment? segment) && segment.IsCatchAll;
    }

    internal bool CanOverlap(RouteTemplate other)
    {
        int min = Math.Max(MinimumSegmentCount, other.MinimumSegmentCount);
        int max = MaximumSegmentCount is null || other.MaximumSegmentCount is null
            ? Math.Max(_segments.Length, other._segments.Length)
            : Math.Min(MaximumSegmentCount.Value, other.MaximumSegmentCount.Value);

        if (min > max)
            return false;

        for (var i = 0; i < min; i++)
        {
            TemplateSegment? left = SegmentAt(i);
            TemplateSegment? right = other.SegmentAt(i);
            if (left is null || right is null)
                continue;

            if (!SegmentsCanOverlap(left, other, right))
                return false;
        }

        return true;
    }

    internal int ComparePrecedence(RouteTemplate other)
    {
        int count = Math.Max(_segments.Length, other._segments.Length);
        for (var i = 0; i < count; i++)
        {
            TemplateSegment? left = i < _segments.Length ? _segments[i] : null;
            TemplateSegment? right = i < other._segments.Length ? other._segments[i] : null;
            int comparison = CompareSegmentPrecedence(left, right);
            if (comparison != 0)
                return comparison;
        }

        return _segments.Length.CompareTo(other._segments.Length);
    }

    private static int CompareSegmentPrecedence(TemplateSegment? left, TemplateSegment? right)
    {
        switch (left)
        {
            case null when right is null:
                return 0;
            case null:
                return right.IsOptional || right.IsCatchAll ? -1 : 1;
        }

        if (right is null)
            return left.IsOptional || left.IsCatchAll ? 1 : -1;

        return right.Precedence.CompareTo(left.Precedence);
    }

    private TemplateSegment? SegmentAt(int index)
    {
        if (index < _segments.Length)
            return _segments[index];

        return _segments.Length > 0 && _segments[^1].IsCatchAll
            ? _segments[^1]
            : null;
    }

    private static TemplateSegment ParseSegment(string segment, RouteConstraintRegistry constraints)
    {
        if (segment.StartsWith('{') &&
            segment.EndsWith('}') &&
            segment.Length > 2)
        {
            string body = segment[1..^1];
            if (body.StartsWith('*'))
            {
                string catchAllName = body[1..];
                ArgumentException.ThrowIfNullOrWhiteSpace(catchAllName);
                return TemplateSegment.CatchAll(catchAllName);
            }

            int separator = body.IndexOf(':');
            string name = separator < 0 ? body : body[..separator];
            string? constraint = separator < 0 ? null : body[(separator + 1)..];
            var optional = false;

            if (constraint is not null && constraint.EndsWith('?'))
            {
                optional = true;
                constraint = constraint[..^1];
            }
            else if (name.EndsWith('?'))
            {
                optional = true;
                name = name[..^1];
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (constraint is not null && !constraints.Contains(constraint))
                throw new ArgumentException($"Route constraint '{constraint}' is not supported.");

            return TemplateSegment.Parameter(name, constraint, optional);
        }

        if (segment.Contains('{', StringComparison.Ordinal) || segment.Contains('}', StringComparison.Ordinal))
            throw new ArgumentException($"Route template segment '{segment}' is invalid.");

        return TemplateSegment.ForLiteral(Uri.UnescapeDataString(segment));
    }

    private static void ValidateSegments(string template, TemplateSegment[] segments)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionalStarted = false;

        for (var i = 0; i < segments.Length; i++)
        {
            TemplateSegment segment = segments[i];

            if (segment.ParameterName is not null && !seenNames.Add(segment.ParameterName))
                throw new ArgumentException(
                    $"Route template '{template}' contains duplicate parameter '{segment.ParameterName}'.");

            if (segment.IsCatchAll && i != segments.Length - 1)
                throw new ArgumentException($"Route template '{template}' has a catch-all segment that is not final.");

            if (optionalStarted && segment is { IsOptional: false, IsCatchAll: false })
                throw new ArgumentException(
                    $"Route template '{template}' has a non-optional segment after an optional segment.");

            if (segment.IsOptional)
                optionalStarted = true;
        }
    }

    private bool SegmentsCanOverlap(
        TemplateSegment left,
        RouteTemplate rightTemplate,
        TemplateSegment right)
    {
        if (left.IsCatchAll || right.IsCatchAll)
            return true;

        if (left.Literal is not null && right.Literal is not null)
            return StringComparer.Ordinal.Equals(left.Literal, right.Literal);

        if (left.Literal is not null && right.ParameterName is not null)
            return rightTemplate._constraints.Satisfies(left.Literal, right.Constraint);

        if (right.Literal is not null && left.ParameterName is not null)
            return _constraints.Satisfies(right.Literal, left.Constraint);

        return _constraints.CanOverlap(left.Constraint, rightTemplate._constraints, right.Constraint);
    }

    internal static string[] SplitPath(string path)
    {
        string trimmed = path.Trim('/');
        return trimmed.Length == 0
            ? []
            : trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    internal static string[] SplitPathForMatch(string path)
    {
        string[] segments = SplitPath(path);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Contains('%', StringComparison.Ordinal))
                segments[i] = Uri.UnescapeDataString(segments[i]);
        }

        return segments;
    }

    private static string JoinRemainingSegments(IReadOnlyList<string> pathSegments, int startIndex)
    {
        int remaining = pathSegments.Count - startIndex;
        if (remaining <= 0)
            return string.Empty;

        if (remaining == 1)
            return pathSegments[startIndex];

        return string.Join(
            "/",
            pathSegments.Skip(startIndex));
    }

    private static string[] CreateLiteralPrefix(TemplateSegment[] segments)
    {
        var count = 0;
        while (count < segments.Length && segments[count].Literal is not null)
            count++;

        if (count == 0)
            return [];

        var prefix = new string[count];
        for (var i = 0; i < count; i++)
            prefix[i] = segments[i].Literal!;

        return prefix;
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

        public static TemplateSegment ForLiteral(string value)
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
}
