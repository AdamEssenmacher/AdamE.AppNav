# UIKit adapter design

[Documentation home](../index.md)

This is the design note for `AdamE.AppNav.iOS`, the UIKit presenter. Application
developers should start with [UIKit integration](../guides/08-uikit-integration.md).

## Origin

The presenter was built and proven in a production .NET iOS app, and extracted
here once every route in that app had moved onto [entry leases](entry-leases.md).
Extraction made its app-specific parts generic:

| In the original app | Here |
| --- | --- |
| The app's own window, stack and modal IDs, and one modal owned by a pushed screen | Core's `NavigationTopology`, declared by the app and checked against `UIKitNavigationPresenter.Support` before each plan is presented |
| The app's screen types mapped to native pages | `IUIKitPageFactory` and `UIKitPage` |
| The home screen's own navigation controller | A root page that `IsNavigationStack`, the app's or `UIKitPage.InNavigationStack`'s |
| A zoom from a list row into the screen it opens | `UIKitNavigationPresenterOptions.ConfiguringPush` |
| The app's composition recording pops and Back | `IUIKitNavigationHost`, with `UIKitRouterNavigationHost` for an app that lets the router decide |
| The app's own cleanup-failure exception | `UIKitCleanupFailureException`, which a host wraps its own signal in |
| Lease attachment and placeholders in the app's host | The presenter, through `UIKitNavigationPresenterOptions.EntryLeases` |

## Supported shape, and how it grows

This version presents one window whose root is one stack of one or two entries,
and at most one declared modal with no nested topology. That is exactly what has
been proven on devices. A real app needs more: a deeper stack, and modals with their
own stacks. Each is added in its own change, with its own native checks. A deeper
stack only widens `UIKitNavigationPresenter.Support`; a modal with its own stack
also needs core's `NavigationTopology` to declare that stack:

1. A stack of any depth: multi-entry pushes, pops of several entries, and
   rollback across them.
2. A modal holding its own stack (`ModalNode.Content`), such as a creation flow
   or a capture flow that pushes inside itself.

A modal over a modal is out of scope until an app needs it as a route; most
such cases are a page's own foreign UI, which the presenter already leaves
alone.

## One operation

`ApplyAsync` serializes operations. Each one:

1. **Checks the plan** against the topology and `UIKitNavigationPresenter.Support`. A plan of any other shape fails
   with `NotSupportedException` before anything is built. A push over a root
   page that is not a navigation stack fails once that page is built, with
   `InvalidOperationException`, since only the page says what it is.
2. **Resolves pages.** A page still current for the same entry and route is
   reused; any other is built, attached to its entry's lease first, or replaced
   by a placeholder if that lease retired while the router still shows it. A
   page UIKit would refuse, or one with its own navigation or presentation
   delegate, is retired and rejected before native changes.
3. **Stands aside for a committed pop.** If the person popped the stack while
   the operation prepared, an operation that would present the same pages again
   lets that pop's record win. Any other fails with
   `UIKitNativePopWonException` before touching native.
4. **Mutates native:** dismisses what must go, sets the stack (animating only a
   single push or pop on a visible stack), installs the root, presents the
   modal with `ModalInPresentation`.
5. **Waits for a displayed frame**, lets the stack's transition settle, and
   **verifies** the window, the stack and the modal against the plan.
6. **Accepts** the new surface, then disposes what left it, then releases each
   page's loan and lease attachment, in that order.

A failure or cancellation before acceptance rolls native back to the accepted
surface, or clears the window when that surface is no longer current, and
retires every page the operation built, all before `ApplyAsync` throws. The
router then commits nothing. A refresh of an unchanged, still-current surface is
a read-only acknowledgment: it never dismisses foreign UI.

If a page's lease retires after it was built while the router still shows its
entry, the operation applies the same plan again, and the retired lease then
gets its placeholder.

## Unsafe retirement

A controller whose disposal throws, a loan whose release throws, or a factory
that reports `UIKitCleanupFailureException` leaves native objects that may still
use what their entry borrows. The presenter keeps that page's loan and lease
attachment, and fails each later operation, and its own disposal, with that same
failure. Pages it had already retired release; the pages still shown are never
retired again, so they keep theirs too.
The host decides what to do with an app in that state, such as ending the process.

## Native events

The presenter raises no `ReconciliationRequested` events. Instead it reports
through `IUIKitNavigationHost`, on the main thread in a later turn of its run
loop, with no execution context
from the operation that noticed the event, so a host can call the router without
AppNav refusing it as reentrant:

- a pop UIKit committed, once, when its transition has ended and never while an
  operation is applying;
- a modal swipe-down, as a host Back request tied to the modal entry. Repeated
  attempts while its request is pending are coalesced;
- a modal dismissed by something other than the presenter, as a refresh
  request;
- failures it recovered from.

`UIKitRouterNavigationHost` records a pop as a `HostBack` reconciliation from
the router's current state, retrying when another operation committed first. A
modal owned by a popped entry goes with it. If the host cannot record a pop,
the presenter reports that through `ReportRecoveredFailure` and nothing more: the
router still shows the popped entry until a later operation, and the next
refresh re-presents the router's stack. An app that wants that at once requests a
refresh from its failure sink. The router checks
queued modal Back requests against the top modal when it admits them.

## How it is checked

`tests/AdamE.AppNav.iOS.NativeChecks` is a plain UIKit app that runs every check
in its own scene window on a simulator: `eng/run-ios-native-checks.sh`. The checks
cover topology declaration and refusal; presentation, rollback and cancellation at
each native completion; foreign UI; animated pushes and pops crossing operations;
confirmations over a pushed page during a root change; sticky unsafe retirement;
and, through a real router, lease release order, placeholders and the router host.
`eng/verify.sh native-ios` publishes the same app with NativeAOT for a device, so
the package stays trim- and AOT-clean.

## Next steps

- Read the [adapter contract](adapter-contract.md).
- Read the [entry-leases](entry-leases.md) design note.
