using AdamE.AppNav.Leases;
using AdamE.AppNav.State;

namespace AdamE.AppNav.iOS;

/// <summary>Optional choices for a <see cref="UIKitNavigationPresenter"/>.</summary>
public sealed record UIKitNavigationPresenterOptions
{
    /// <summary>
    /// The registry the router's leases come from (<c>RouterNavigatorFactoryOptions.EntryLeases</c>). With it, the
    /// presenter attaches every page to its entry's lease before the factory runs, and shows a placeholder for an entry
    /// whose lease has retired while the router still shows it. An entry the application opened no lease for, such as
    /// an ancestor its planner added, gets one the presenter opens during presentation
    /// (<see cref="RouteEntryLeaseRegistry.GetOrOpen"/>), which core closes when the operation ends. Without a registry,
    /// pages have no leases.
    /// </summary>
    public RouteEntryLeaseRegistry? EntryLeases { get; init; }

    /// <summary>
    /// Called on the main thread just before a page that is not on the stack yet is put over the root page: pushed,
    /// or replacing the page that was there, which UIKit does without animation. Set the pushed controller's transition
    /// here, such as a zoom from the row that opened it. Null for UIKit's default push.
    /// </summary>
    public Action<UIKitPushContext>? ConfiguringPush { get; init; }

    /// <summary>
    /// The placeholder's controller for an entry whose lease retired while the router still shows it: a page that
    /// borrows nothing, shown until the router drops the entry. Null for a blank view in the system background colour,
    /// presented as a page sheet when it is a modal. A root placeholder is shown in a navigation stack, the returned
    /// <c>UINavigationController</c> when it holds exactly one controller, otherwise a new one around it; a pushed
    /// placeholder must not be a navigation controller, and no placeholder may have its own navigation or presentation
    /// delegate. The presenter owns and disposes what this returns, including one it rejects.
    /// </summary>
    public Func<RouteEntry, UIKitPagePlacement, UIViewController>? CreatePlaceholder { get; init; }
}

/// <summary>An entry about to be pushed over the root page.</summary>
/// <param name="RootContent">The root page's own controller, at the bottom of the stack.</param>
/// <param name="Pushed">The pushed page's controller.</param>
/// <param name="Entry">The pushed entry.</param>
/// <param name="Animated">Whether UIKit animates this push. A push that is not animated should clear any transition an
/// earlier push left on the controller.</param>
public sealed record UIKitPushContext(
    UIViewController RootContent,
    UIViewController Pushed,
    RouteEntry Entry,
    bool Animated);

/// <summary>
/// A page factory, or a controller's disposal, could not retire native work it had started. Whatever that work borrows
/// may still be in use, so the presenter keeps every lease and loan it holds and fails each later operation with this
/// failure.
/// </summary>
public sealed class UIKitCleanupFailureException : AggregateException
{
    /// <summary>The operation that failed, and the cleanup that failed after it.</summary>
    public UIKitCleanupFailureException(string message, Exception operation, Exception cleanup)
        : base(message, operation, cleanup)
    {
    }

    /// <summary>Wraps a host's own cleanup-failure signal, such as a renderer kit's exception, so the presenter can
    /// recognise it.</summary>
    public UIKitCleanupFailureException(string message, Exception cleanupFailure)
        : base(message, cleanupFailure)
    {
    }

    internal static bool IsIn(Exception failure) =>
        failure is UIKitCleanupFailureException
        || (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(IsIn))
        || (failure.InnerException is { } inner && IsIn(inner));
}

/// <summary>
/// An operation lost to a pop the person had already committed with UIKit's back button or back-swipe. The operation
/// failed before it touched native, and the pop's own record, through
/// <see cref="IUIKitNavigationHost.RecordNativePopAsync"/>, follows. It is an expected race, not a defect.
/// </summary>
public sealed class UIKitNativePopWonException()
    : InvalidOperationException("The native stack changed under the operation: a committed pop wins.");
