using System.Globalization;

namespace AdamE.AppNav.Routing;

internal sealed class RouteValueCodecRegistry
{
    private readonly IReadOnlyDictionary<Type, IRouteValueCodec> _codecs;

    public RouteValueCodecRegistry(IReadOnlyDictionary<Type, IRouteValueCodec> codecs)
    {
        _codecs = new Dictionary<Type, IRouteValueCodec>(codecs);
    }

    public bool Contains(Type type)
    {
        return _codecs.ContainsKey(Normalize(type));
    }

    public T Convert<T>(string value, string name)
    {
        return (T)Convert(value, typeof(T), name)!;
    }

    public object? Convert(string value, Type targetType, string name)
    {
        Type conversionType = Normalize(targetType);
        if (!_codecs.TryGetValue(conversionType, out IRouteValueCodec? codec))
            throw new NotSupportedException(
                $"Route value type '{conversionType.FullName}' has no registered codec.");

        try
        {
            return codec.Parse(value);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException(
                $"Route value '{name}' could not be converted to {conversionType.Name}.",
                ex);
        }
    }

    public string Format(object value, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Format(value, value.GetType(), name);
    }

    public string Format(object value, Type declaredType, string name)
    {
        ArgumentNullException.ThrowIfNull(value);

        Type valueType = Normalize(declaredType);
        if (!_codecs.TryGetValue(valueType, out IRouteValueCodec? codec))
            throw new NotSupportedException(
                $"Route value type '{valueType.FullName}' has no registered codec.");

        try
        {
            return codec.Format(value) ?? throw new InvalidOperationException(
                $"The route value codec for '{valueType.FullName}' returned null while formatting '{name}'.");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException(
                $"Route value '{name}' could not be formatted as {valueType.Name}.",
                ex);
        }
    }

    internal static Type Normalize(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Nullable.GetUnderlyingType(type) ?? type;
    }
}

internal sealed class RouteValueCodecCollection
{
    private readonly Dictionary<Type, IRouteValueCodec> _codecs = CreateBuiltIns();

    public bool Contains(Type type)
    {
        return _codecs.ContainsKey(RouteValueCodecRegistry.Normalize(type));
    }

    public void Add<TValue>(Func<string, TValue> parse, Func<TValue, string> format)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(format);

        Type valueType = typeof(TValue);
        if (Nullable.GetUnderlyingType(valueType) is not null)
            throw new ArgumentException(
                $"Register the non-nullable underlying type for '{valueType.FullName}'.",
                nameof(TValue));

        if (valueType.ContainsGenericParameters)
            throw new ArgumentException("Open generic route value codecs are not supported.", nameof(TValue));

        if (!_codecs.TryAdd(valueType, new RouteValueCodec<TValue>(parse, format)))
            throw new InvalidOperationException(
                $"A route value codec for '{valueType.FullName}' is already registered.");
    }

    public void AddEnum<TEnum>()
        where TEnum : struct, Enum
    {
        if (_codecs.ContainsKey(typeof(TEnum)))
            return;

        _codecs.Add(
            typeof(TEnum),
            new RouteValueCodec<TEnum>(
                static value => Enum.TryParse(value, true, out TEnum result)
                    ? result
                    : throw new FormatException($"'{value}' is not a valid {typeof(TEnum).Name} value."),
                static value => value.ToString()));
    }

    public RouteValueCodecRegistry Build()
    {
        return new RouteValueCodecRegistry(_codecs);
    }

    private static Dictionary<Type, IRouteValueCodec> CreateBuiltIns()
    {
        var codecs = new Dictionary<Type, IRouteValueCodec>();
        Add(codecs, static value => value, static value => value);
        Add(codecs, bool.Parse, static value => value.ToString());
        Add(codecs, static value => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => sbyte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
            static value => value.ToString(CultureInfo.InvariantCulture));
        Add(codecs, static value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            static value => value.ToString("R", CultureInfo.InvariantCulture));
        Add(codecs, static value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            static value => value.ToString("R", CultureInfo.InvariantCulture));
        Add(codecs, Guid.Parse, static value => value.ToString());
        return codecs;
    }

    private static void Add<TValue>(
        IDictionary<Type, IRouteValueCodec> codecs,
        Func<string, TValue> parse,
        Func<TValue, string> format)
    {
        codecs.Add(typeof(TValue), new RouteValueCodec<TValue>(parse, format));
    }
}

internal interface IRouteValueCodec
{
    object? Parse(string value);

    string Format(object value);
}

internal sealed class RouteValueCodec<TValue>(
    Func<string, TValue> parse,
    Func<TValue, string> format) : IRouteValueCodec
{
    public object? Parse(string value)
    {
        return parse(value);
    }

    public string Format(object value)
    {
        return format((TValue)value);
    }
}

internal static class RouteValueFormatting
{
    public static string? Format(object? value, string name, RouteValueCodecRegistry codecs)
    {
        return value is null ? null : codecs.Format(value, name);
    }

    public static IEnumerable<string?> FormatMany(object? value, string name, RouteValueCodecRegistry codecs)
    {
        switch (value)
        {
            case null or string:
                yield return Format(value, name, codecs);
                yield break;
            case System.Collections.IEnumerable enumerable:
                foreach (object? item in enumerable)
                    yield return Format(item, name, codecs);
                yield break;
            default:
                yield return Format(value, name, codecs);
                yield break;
        }
    }
}
