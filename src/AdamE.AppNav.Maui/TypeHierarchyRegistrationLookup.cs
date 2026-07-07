using System.Diagnostics.CodeAnalysis;

namespace AdamE.AppNav.Maui;

internal static class TypeHierarchyRegistrationLookup
{
    public static bool TryGetMostSpecific<TValue>(
        IReadOnlyDictionary<Type, TValue> registrations,
        Type runtimeType,
        [MaybeNullWhen(false)]
        out TValue value)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(runtimeType);

        for (var currentType = runtimeType; currentType is not null && currentType != typeof(object); currentType = currentType.BaseType)
        {
            if (registrations.TryGetValue(currentType, out var resolvedValue))
            {
                value = resolvedValue!;
                return true;
            }
        }

        value = default!;
        return false;
    }
}
