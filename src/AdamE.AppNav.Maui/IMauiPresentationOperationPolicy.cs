using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Selects presentation behavior for an animation-eligible MAUI navigation operation.
/// </summary>
/// <remarks>
/// The presenter invokes this policy only for a singular visible stack or modal mutation. Initial
/// presentation, reconciliation, composite changes, rollback, and recovery are always unanimated.
/// Implementations must not re-enter the router from <see cref="Resolve"/>.
/// </remarks>
public interface IMauiPresentationOperationPolicy
{
    /// <summary>
    /// Resolves presentation options for one animation-eligible native operation.
    /// </summary>
    MauiPresentationOperationOptions Resolve(MauiPresentationOperationContext context);
}

/// <summary>
/// Describes one animation-eligible native presentation operation.
/// </summary>
public sealed class MauiPresentationOperationContext
{
    public MauiPresentationOperationContext(
        NavigationPlan plan,
        NavigationPresentationContext presentationContext,
        MauiPresentationOperationKind operationKind,
        RouteEntry? sourceEntry,
        RouteEntry? targetEntry)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        PresentationContext = presentationContext ?? throw new ArgumentNullException(nameof(presentationContext));
        OperationKind = operationKind;
        SourceEntry = sourceEntry;
        TargetEntry = targetEntry;
    }

    public NavigationPlan Plan { get; }

    public NavigationPresentationContext PresentationContext { get; }

    public MauiPresentationOperationKind OperationKind { get; }

    public RouteEntry? SourceEntry { get; }

    public RouteEntry? TargetEntry { get; }
}

/// <summary>
/// Configures execution of one animation-eligible MAUI presentation operation.
/// </summary>
public sealed record MauiPresentationOperationOptions
{
    /// <summary>
    /// Gets the requested motion behavior.
    /// </summary>
    public MauiPresentationMotion Motion { get; init; } = MauiPresentationMotion.Automatic;
}

/// <summary>
/// Identifies the native mutation selected for presentation policy.
/// </summary>
public enum MauiPresentationOperationKind
{
    StackPush,
    StackPop,
    ModalPush,
    ModalPop
}

/// <summary>
/// Selects whether an eligible MAUI navigation operation uses platform-native motion.
/// </summary>
public enum MauiPresentationMotion
{
    /// <summary>
    /// Uses the adapter default, which is the platform-native animation for eligible operations.
    /// </summary>
    Automatic,

    /// <summary>
    /// Suppresses native navigation animation.
    /// </summary>
    Suppressed,

    /// <summary>
    /// Explicitly requests the platform-native navigation animation.
    /// </summary>
    PlatformDefault
}

internal sealed class DefaultMauiPresentationOperationPolicy : IMauiPresentationOperationPolicy
{
    public MauiPresentationOperationOptions Resolve(MauiPresentationOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new MauiPresentationOperationOptions();
    }
}
