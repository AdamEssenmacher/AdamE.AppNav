using AdamE.AppNav.Leases;
using AdamE.AppNav.State;
using Android.Views;
using AndroidX.Activity;

namespace AdamE.AppNav.Android;

/// <summary>Optional choices for an <see cref="AndroidNavigationPresenter"/>.</summary>
public sealed record AndroidNavigationPresenterOptions
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
    /// The placeholder's view for a root or pushed entry whose lease retired while the router still shows it: a page
    /// that borrows nothing, shown until the router drops the entry. Null for an empty view. It must have no parent. The
    /// presenter owns what this returns, and disposes it once it has left the window.
    /// </summary>
    public Func<RouteEntry, AndroidPagePlacement, ComponentActivity, View>? CreatePlaceholderView { get; init; }

    /// <summary>
    /// The placeholder's dialog for a modal entry whose lease retired while the router still shows it. Null for a
    /// dialog with an empty view. It must not be showing. Back and cancel on it are Back requests, as on any modal. The
    /// presenter owns what this returns and dismisses it when the entry goes; like every dialog it shows, it is not
    /// disposed (<see cref="AndroidPage"/>).
    /// </summary>
    public Func<RouteEntry, ComponentActivity, ComponentDialog>? CreatePlaceholderDialog { get; init; }
}

/// <summary>
/// A page factory, or a page's native retirement, could not retire native work it had started. Whatever that work
/// borrows may still be in use, so the presenter keeps every lease and loan it holds and fails each later operation
/// with this failure.
/// </summary>
public sealed class AndroidCleanupFailureException : AggregateException
{
    /// <summary>The operation that failed, and the cleanup that failed after it.</summary>
    public AndroidCleanupFailureException(string message, Exception operation, Exception cleanup)
        : base(message, operation, cleanup)
    {
    }

    /// <summary>Wraps a host's own cleanup-failure signal, such as a view kit's exception, so the presenter can
    /// recognise it.</summary>
    public AndroidCleanupFailureException(string message, Exception cleanupFailure)
        : base(message, cleanupFailure)
    {
    }

    internal static bool IsIn(Exception failure) =>
        failure is AndroidCleanupFailureException
        || (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(IsIn))
        || (failure.InnerException is { } inner && IsIn(inner));
}
