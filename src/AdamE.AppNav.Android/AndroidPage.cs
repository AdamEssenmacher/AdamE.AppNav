using AdamE.AppNav.Leases;
using AdamE.AppNav.State;
using Android.Views;
using AndroidX.Activity;

namespace AdamE.AppNav.Android;

/// <summary>Builds the native page for one route entry.</summary>
/// <remarks>
/// The presenter calls it on the main thread, and only for an entry it is about to present in the attached activity.
/// When the entry has a <see cref="RouteEntryLease"/>, the presenter has attached to it first, so what the lease owns
/// stays alive while the page exists, and detaches once the page has left the window and been disposed. It never calls
/// the factory for an entry whose lease has retired: it shows a placeholder until the router drops that entry.
/// <para>
/// An activity that is recreated (a configuration change, or the system destroying it in the background) gets new
/// pages: the presenter calls the factory again for each entry it shows, with the new activity. The entry, its lease and
/// whatever the lease owns are the same, so a factory builds views over the application's existing state.
/// </para>
/// <para>
/// A factory that fails must leave nothing native behind. If it cannot retire what it had started building, it
/// throws <see cref="AndroidCleanupFailureException"/>, and the presenter then keeps the entry's lease attached and
/// fails every later operation, so nothing the page might still use is released.
/// </para>
/// </remarks>
public interface IAndroidPageFactory
{
    /// <summary>The page for <paramref name="request"/>'s entry.</summary>
    /// <param name="request">The entry, where it is placed, its lease, and the activity to build in.</param>
    /// <param name="cancellationToken">Canceled when the operation ends, the activity detaches, or the entry's lease
    /// retires.</param>
    ValueTask<AndroidPage> CreatePageAsync(AndroidPageRequest request, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IAndroidPageFactory"/> is asked to build.</summary>
/// <param name="Entry">The route entry.</param>
/// <param name="Placement">Where the page is placed: <see cref="AndroidPagePlacement.Modal"/> takes a dialog
/// (<see cref="AndroidPage.ForDialog"/>), any other placement a view (<see cref="AndroidPage.ForView"/>).</param>
/// <param name="Lease">The entry's lease, already attached by the presenter; null for an entry without one.</param>
/// <param name="Activity">The attached activity: the context to build views and dialogs in. Do not keep it beyond the
/// page's life.</param>
public sealed record AndroidPageRequest(
    RouteEntry Entry,
    AndroidPagePlacement Placement,
    RouteEntryLease? Lease,
    ComponentActivity Activity);

/// <summary>Where a page is placed.</summary>
public enum AndroidPagePlacement
{
    /// <summary>The stack's first entry: the frame's content while nothing is pushed over it.</summary>
    Root = 0,

    /// <summary>An entry pushed over the root page. It takes the frame; the root page stays built underneath.</summary>
    Pushed = 1,

    /// <summary>A modal's entry, shown as a dialog over the stack.</summary>
    Modal = 2
}

/// <summary>A native page for one route entry, which the presenter owns from the moment the factory returns it.</summary>
/// <remarks>
/// <para>
/// An operation that replaces a page takes its view out of the frame, or dismisses its dialog, before it accepts the
/// new state, and puts it back if it fails. Once the new state is accepted the page retires, in this order, stopping at
/// the first failure: <see cref="Binding"/> is disposed; the view leaves its parent or the dialog is dismissed, if that
/// has not happened yet; the presenter checks that it is no longer attached to a window; a view is disposed; then
/// <see cref="Loan"/> is disposed and the entry's lease detached. A dialog is not disposed: Android may still deliver the
/// dismiss and cancel messages it queued as the dialog closed, so the garbage collector frees it once nothing
/// references it. Its listeners must cope with being called after the page is gone, which is what <see cref="Binding"/>
/// is for. A failure keeps the loan and the lease,
/// and the presenter fails every later operation.
/// </para>
/// <para>
/// A modal page's dialog is the application's to style, and a <c>BottomSheetDialog</c> is a <c>ComponentDialog</c>.
/// The presenter owns how it closes: Back on it, and its own cancel (a tap outside it, a sheet dragged away), are Back
/// requests for that modal through <see cref="IAndroidNavigationHost.RequestModalBackAsync"/>, so the router's Back
/// policy decides. If the router keeps the modal, the presenter shows the dialog again. Do not turn a cancel into
/// navigation yourself, or it is asked for twice. A dialog the application dismisses some other way asks for a
/// refresh of the accepted state.
/// </para>
/// </remarks>
public sealed record AndroidPage
{
    private AndroidPage(View? view, ComponentDialog? dialog)
    {
        View = view;
        Dialog = dialog;
    }

    /// <summary>A root or pushed page: <paramref name="view"/> fills the presenter's frame while it is the top entry.
    /// It must have no parent.</summary>
    public static AndroidPage ForView(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new AndroidPage(view, null);
    }

    /// <summary>A modal page: <paramref name="dialog"/> is shown over the stack. It must not be showing yet.</summary>
    public static AndroidPage ForDialog(ComponentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        return new AndroidPage(null, dialog);
    }

    /// <summary>The page's view, for a root or pushed page; otherwise null.</summary>
    public View? View { get; }

    /// <summary>The page's dialog, for a modal page; otherwise null.</summary>
    public ComponentDialog? Dialog { get; }

    /// <summary>
    /// Disposed first when the page retires, before its view is disposed: the page's own subscriptions and event
    /// handlers, so that none of them reaches a disposed native object, or reaches what the page binds to after the page
    /// is gone. Optional.
    /// </summary>
    public IDisposable? Binding { get; init; }

    /// <summary>
    /// Released after the view or dialog has left the window (and a view has been disposed), never before: what the
    /// page borrows that its native objects do not own. Not released if native retirement failed.
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
