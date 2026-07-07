using System.ComponentModel;
using System.Globalization;

namespace AdamE.AppNav.Routing;

internal static class RouteValueConverter
{
    public static T Convert<T>(string value, string name)
    {
        return (T)Convert(value, typeof(T), name)!;
    }

    public static object? Convert(string value, Type targetType, string name)
    {
        Type conversionType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (conversionType == typeof(string))
                return value;

            if (conversionType.IsEnum)
                return Enum.Parse(conversionType, value, true);

            TypeConverter converter = TypeDescriptor.GetConverter(conversionType);
            if (converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            throw new FormatException($"Route value '{name}' could not be converted to {conversionType.Name}.", ex);
        }

        throw new NotSupportedException($"Route values cannot be converted to {conversionType.FullName}.");
    }

    public static object? DefaultFor(Type type)
    {
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
