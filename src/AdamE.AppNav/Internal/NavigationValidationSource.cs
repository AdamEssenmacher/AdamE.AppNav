namespace AdamE.AppNav.Internal;

internal readonly struct NavigationValidationSource
{
    private readonly string? _description;
    private readonly Type? _backNavigatorType;

    private NavigationValidationSource(string description) => _description = description;

    private NavigationValidationSource(Type backNavigatorType) => _backNavigatorType = backNavigatorType;

    internal static NavigationValidationSource ForBackNavigator(Type navigatorType) => new(navigatorType);

    public static implicit operator NavigationValidationSource(string description) => new(description);

    public override string ToString() =>
        _backNavigatorType is null ? _description ?? string.Empty : $"Back navigator '{_backNavigatorType.Name}'";
}
