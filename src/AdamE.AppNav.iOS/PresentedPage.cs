using AdamE.AppNav.Leases;

namespace AdamE.AppNav.iOS;

/// <summary>
/// One native page the presenter owns: the controller it shows, what the page borrows, and the lease attachment it
/// holds. Disposing the controller never disposes what the page borrows; that is released only after.
/// </summary>
internal sealed class PresentedPage
{
    private readonly IDisposable? _loan;
    private readonly RouteEntryLeaseAttachment? _attachment;
    private readonly Func<bool>? _isCurrent;
    private bool _loanReleased;

    public PresentedPage(
        UIViewController controller,
        IDisposable? loan,
        RouteEntryLease? lease,
        RouteEntryLeaseAttachment? attachment,
        Func<bool>? isCurrent,
        CancellationToken pageRetirement,
        string? ownerEntryId,
        bool stack,
        bool placeholder = false)
    {
        Controller = controller;
        PageRetirement = pageRetirement;
        _loan = loan;
        Lease = lease;
        _attachment = attachment;
        _isCurrent = isCurrent;
        OwnerEntryId = ownerEntryId;
        IsPlaceholder = placeholder;
        if (stack)
        {
            Stack = controller as UINavigationController
                    ?? throw new InvalidOperationException("A navigation-stack page must be a UINavigationController.");
            OriginalContent = Stack.ViewControllers is [var only]
                ? only
                : throw new InvalidOperationException(
                    "A navigation-stack page's navigation controller must hold exactly its own page.");
            OriginalNavigationBarHidden = Stack.NavigationBarHidden;
        }
    }

    /// <summary>The controller the presenter shows: for a root page in a navigation stack, that stack.</summary>
    public UIViewController Controller { get; }

    /// <summary>For a root page in a navigation stack, the page's own controller at the bottom of it.</summary>
    public UIViewController? OriginalContent { get; }

    public bool OriginalNavigationBarHidden { get; }

    /// <summary>The navigation stack the presenter pushes onto for this root page, or null.</summary>
    public UINavigationController? Stack { get; }

    /// <summary>The entry's lease, or null for an entry without one and for a placeholder.</summary>
    public RouteEntryLease? Lease { get; }

    /// <summary>For a modal declared over its owner, that owner stack entry's ID.</summary>
    public string? OwnerEntryId { get; }

    public bool IsPlaceholder { get; }

    public InvalidOperationException? ValidationFailure { get; init; }

    /// <summary>The page's own retirement, besides its lease's.</summary>
    public CancellationToken PageRetirement { get; }

    public CancellationToken LeaseRetirement => Lease?.RetiredToken ?? CancellationToken.None;

    /// <summary>A placeholder borrows nothing, so it is always current.</summary>
    public bool IsCurrent => Lease?.IsRetired != true
                             && !PageRetirement.IsCancellationRequested
                             && (_isCurrent?.Invoke() ?? true);

    /// <summary>Whether something is released only after the controller has detached.</summary>
    public bool HasLoan => _loan is not null || _attachment is not null;

    /// <summary>The page's own loan first; the lease is detached only if that succeeded, so a failed release keeps
    /// everything the entry borrows.</summary>
    public void ReleaseLoan()
    {
        if (_loanReleased)
            return;
        _loan?.Dispose();
        _attachment?.Detach();
        _loanReleased = true;
    }
}
