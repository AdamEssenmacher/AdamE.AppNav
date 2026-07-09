namespace AdamE.AppNav.Routing;

/// <summary>
/// Declares route-owned metadata that should round-trip through a source-generated query parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class AppNavQueryMetadataAttribute : Attribute
{
    public AppNavQueryMetadataAttribute(Type declaringType, string memberName)
    {
        DeclaringType = declaringType ?? throw new ArgumentNullException(nameof(declaringType));
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        MemberName = memberName;
    }

    public Type DeclaringType { get; }

    public string MemberName { get; }

    public bool OmitWhenNull { get; set; } = true;
}
