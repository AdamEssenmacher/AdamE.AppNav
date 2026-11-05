using AdamE.AppNav.Leases;
using AdamE.AppNav.State;

namespace AdamE.AppNav.iOS;

/// <summary>Builds the native page for one route entry.</summary>
/// <remarks>
/// The presenter calls it on the main thread, and only for an entry it is about to present. When the entry has a
/// <see cref="RouteEntryLease"/>, the presenter has attached to it first, so what the lease owns stays alive while the
/// page exists, and detaches once the page has left the window and been disposed. It never calls the factory for an
/// entry whose lease has retired: it shows a placeholder until the router drops that entry.
/// <para>
/// A factory that fails must leave nothing native behind. If it cannot retire what it had started building, it
/// throws <see cref="UIKitCleanupFailureException"/>, and the presenter then keeps the entry's lease attached and
/// fails every later operation, so nothing the page might still use is released.
/// </para>
/// </remarks>
public interface IUIKitPageFactory
{
    /// <summary>The page for <paramref name="request"/>'s entry.</summary>
    /// <param name="request">The entry, where it is placed, and its lease.</param>
    /// <param name="cancellationToken">Canceled when the operation ends or the entry's lease retires.</param>
    ValueTask<UIKitPage> CreatePageAsync(UIKitPageRequest request, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IUIKitPageFactory"/> is asked to build.</summary>
/// <param name="Entry">The route entry.</param>
/// <param name="Placement">Where the page is placed.</param>
/// <param name="Lease">The entry's lease, already attached by the presenter; null for an entry without one.</param>
public sealed record UIKitPageRequest(RouteEntry Entry, UIKitPagePlacement Placement, RouteEntryLease? Lease);

/// <summary>Where a page is placed.</summary>
public enum UIKitPagePlacement
{
    /// <summary>The stack's first entry: the window's root page.</summary>
    Root = 0,

    /// <summary>An entry pushed over the root page.</summary>
    Pushed = 1,

    /// <summary>A modal's entry, presented over the stack.</summary>
    Modal = 2
}

/// <summary>A native page for one route entry, which the presenter owns from the moment the factory returns it.</summary>
/// <remarks>
/// The presenter disposes <see cref="Controller"/> once it has taken it out of the window, and only then disposes
/// <see cref="Loan"/>. Set the controller up as UIKit should show it: a modal page's
/// <c>ModalPresentationStyle</c>, sheet detents and navigation items are the application's choice. The presenter
/// presents every modal with <c>ModalInPresentation</c> set, so a swipe-down becomes a Back request through the
/// router's Back policy instead of a dismissal.
/// <para>
/// The presenter is the delegate of a root page's navigation controller and of a modal page's presentation controller,
/// and it rejects a page that already has one. A pushed page cannot be a navigation controller, which UIKit cannot push.
/// A rejected page is retired like any other.
/// </para>
/// </remarks>
/// <param name="Controller">The page's view controller; for a page that <see cref="IsNavigationStack"/>, the
/// navigation controller holding it.</param>
public sealed record UIKitPage(UIViewController Controller)
{
    /// <summary>
    /// A root page shown at the bottom of a new <c>UINavigationController</c>, so entries can be pushed over it.
    /// </summary>
    /// <param name="content">The root page's own controller.</param>
    /// <param name="navigationBarHidden">Whether the navigation controller hides its bar.</param>
    public static UIKitPage InNavigationStack(UIViewController content, bool navigationBarHidden = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new UIKitPage(new UINavigationController(content) { NavigationBarHidden = navigationBarHidden })
        {
            IsNavigationStack = true
        };
    }

    /// <summary>
    /// For a <see cref="UIKitPagePlacement.Root"/> page: <see cref="Controller"/> is a <c>UINavigationController</c>
    /// whose only child is the root page's own controller, and the presenter pushes entries onto it and pops them.
    /// The presenter never changes its bottom controller and never sets its bar's visibility to anything but what the
    /// controller had when it was returned. A root page that is not a navigation stack supports no pushed entry.
    /// Use <see cref="InNavigationStack"/>, or return a navigation controller of your own, such as a subclass with its
    /// own appearance.
    /// </summary>
    public bool IsNavigationStack { get; init; }

    /// <summary>
    /// Released after <see cref="Controller"/> has detached and been disposed, never before: what the page borrows
    /// that its controller does not own. Not released if native retirement failed.
    /// </summary>
    public IDisposable? Loan { get; init; }

    /// <summary>
    /// Canceled when the page stops belonging to the application's current state, besides its lease's retirement,
    /// which the presenter always observes. An operation presenting the page is canceled with it.
    /// </summary>
    public CancellationToken Retirement { get; init; }

    /// <summary>
    /// Whether the page still belongs to the application's current state, besides its lease and
    /// <see cref="Retirement"/>, which the presenter always checks. Null when those alone decide. It must be cheap and
    /// must not throw.
    /// </summary>
    public Func<bool>? IsCurrent { get; init; }
}
