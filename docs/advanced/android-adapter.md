# Android adapter design

[Documentation home](../index.md)

This is the design note for `AdamE.AppNav.Android`, the Android presenter.
Application developers should start with
[Android integration](../guides/09-android-integration.md).

## Origin

The presenter was built and proven in a production .NET for Android app, and
extracted here once every route in that app had moved onto
[entry leases](entry-leases.md). Extraction made its app-specific parts generic:

| In the original app | Here |
| --- | --- |
| The app's own window, stack and modal IDs, and one modal owned by a pushed screen | Core's `NavigationTopology`, checked against `AndroidNavigationPresenter.Support` before each plan is presented |
| The app's screen types mapped to views and sheets | `IAndroidPageFactory` and `AndroidPage` |
| The app's composition receiving Back and refresh requests | `IAndroidNavigationHost`, with `AndroidRouterNavigationHost` for an app that lets the router decide |
| The app's own cleanup-failure exception | `AndroidCleanupFailureException`, which a host wraps its own signal in |
| Lease attachment and placeholders in the app's screens | The presenter, through `AndroidNavigationPresenterOptions.EntryLeases` |
| The Back key caught on API levels before 33, and dialogs cancelling into their own view models | One rule on every API level: Back and cancel on a modal are Back requests for it, through the dialog's back dispatcher |

## No Fragments

Pages are plain views and dialogs in one frame the presenter owns. Fragments
were considered and left out: their `FragmentManager` keeps a back stack of its
own, a second navigation state beside AppNav's, and restores fragments on
activity recreation from saved state, which would rebuild pages without their
entries' leases. The presenter instead builds every page again, from the
factory, for a recreated activity.

## Supported shape, and how it grows

This version presents one window whose root is one stack of one or two entries,
and at most one declared modal with no nested topology: what has been proven on
devices. A deeper stack widens `AndroidNavigationPresenter.Support`; a modal with
its own stack also needs core's `NavigationTopology` to declare that stack. The
in-app predictive-back preview of a pushed page (the page sliding with the
gesture) is drawn by the presenter's own Back callback in a later version; this
one answers the committed Back.

## Activities

The presenter outlives activities. `Attach` makes one activity current and
returns a new frame for its content, detaching any other first; `Detach` ends
it. Detaching an activity:

1. stops its first-draw gate and any layout wait,
2. removes the presenter's Back callback,
3. cancels whatever the current operation was doing in it,
4. dismisses the shown dialog, empties the frame and retires every page built in
   it, staged or accepted,
5. removes and disposes the frame.

An operation waits for an attached activity. One whose activity detaches under
it waits for the next, and builds its pages again there. An activity found
finishing or destroyed during an operation is detached by the presenter, as its
`OnDestroy` would: Android can finish an activity before its `OnCreate` returns.

A fresh frame's first-draw gate holds the activity's whole window from drawing
until the accepted pages are in the frame and laid out, or until the first
operation in it ends whatever its outcome, so a recreated activity never shows
an empty frame between its creation and the refresh that fills it, and a failed
or empty presentation still draws.

Native changes only in a started activity. An operation waits for its activity
to be started before it builds pages, and checks again in the same main-thread
turn as it changes native, as it waits for a detached activity to be replaced.
Android neither lays out nor draws a stopped activity, and on API 28 a dialog
shown into one was not reliably drawn once it started again. Canceling an
operation while it waits ends it with nothing touched. An operation that changed
native and then fails after its activity stopped (it was waiting for a frame)
shows nothing in its rollback: it gives the surface up, keeping its current
pages, and once the activity starts the presenter asks for a refresh, which
presents the router's state again. A rollback never waits for layout.

## One operation

`ApplyAsync` serializes operations. Each one, on the main thread:

1. **Checks the plan** against the topology and `Support`. A plan of any other
   shape fails with `NotSupportedException` before anything is built.
2. **Builds pages.** A page still current for the same entry, route and
   placement is reused; any other is built, attached to its entry's lease
   first, or replaced by a placeholder if that lease retired while the router
   still shows it. A page of the wrong kind, a view that already has a parent, a
   dialog already showing, or one shared by two entries is retired and rejected
   before native changes. A view or dialog shared with an accepted page is never
   disposed by the rejected one.
3. **Mutates native:** dismisses a dialog that must go, puts the top entry's
   view in the frame, shows the modal's dialog.
4. **Waits for a laid-out frame**, then **verifies** the frame and the dialog
   against the plan.
5. **Accepts** the new surface, then retires what left it: each page's binding,
   its native objects out of the window, a view's disposal, then its loan and
   lease attachment, in that order.

A failure or cancellation before acceptance rolls native back to the accepted
surface and verifies it (a dialog shown again attaches its window on Android's
next traversal, so it is verified as showing), and
retires every page the operation built, all before `ApplyAsync` throws. When
that surface is no longer current, or cannot be restored, the presenter clears
the frame and presents nothing: pages that are still current are kept out of
the window for the next operation to reuse, so a root page keeps its state, and
the others are retired at once. The router then
commits nothing. If a page's lease retires after it was built while the router
still shows its entry, the operation applies the same plan again, and the retired
lease then gets its placeholder.

Dialogs are not disposed. `Dialog.Dismiss` queues its dismiss message, and a
disposed peer of a dialog subclass cannot be activated again when that message
arrives, which crashes the app. A dialog out of the window is what makes its
loan safe to release; the collector frees its peer.

## Unsafe retirement

A page whose binding, native retirement or loan throws, or a factory that
reports `AndroidCleanupFailureException`, leaves native objects that may still
use what their entry borrows. The presenter keeps that page's loan and lease
attachment, and fails each later operation, attachment and its own disposal with
that same failure. A detached activity whose teardown failed fails the operation
waiting for the next one the same way.

## Native events

The presenter raises no `ReconciliationRequested` events. It reports through
`IAndroidNavigationHost`, on the main thread in a later turn of the looper, with
no execution context from the operation that noticed the event, so a host can
call the router without AppNav refusing it as reentrant:

- system Back while a page is pushed or a modal is open, as `RequestBackAsync`
  (or `RequestModalBackAsync` for an open modal whose dialog was canceled);
  Back during an operation is swallowed, since the operation decides what shows;
- Back on a modal's dialog, through its `OnBackPressedDispatcher` on every API
  level, as `RequestModalBackAsync` for its entry;
- the dialog's own cancel, as `RequestModalBackAsync`; when that request leaves
  the modal in the router's state, a refresh shows the dialog again, once any
  operation applying has ended. A dialog closed this way while an operation
  waits for it does not fail that operation;
- a dialog dismissed by something other than the presenter or its cancel, as a
  reported failure and a refresh;
- after a rollback in a stopped activity gave its surface up, a refresh request
  once the activity starts again, so the router's state is presented;
- failures it recovered from.

Each request is made in a later turn of the looper, and only if then its
activity is still attached and the page it came from still current; a window
Back also needs that page still shown, with no modal or operation over it.

## How it is checked

`tests/AdamE.AppNav.Android.NativeChecks` is a plain `ComponentActivity` app that
runs every check on an emulator: `eng/run-android-native-checks.sh`. The checks
cover topology refusal and owned modals; presentation, retirement order, refused
and shared pages; sticky unsafe retirement and factory cleanup failures; system
Back on the activity and on a dialog, both through the dispatcher and as the
real Back key, and a Back whose activity detached before delivery; cancel
refused and accepted, dismissal from outside, a rollback that restores a closed
modal, and a modal canceled while an operation keeps it or is showing it; a second activity
attaching, a real `Recreate()` with a dialog showing, an operation on a stopped
activity, one canceled there, an activity stopped during a page build, a rollback
after the activity stopped, an operation waiting for the next activity, a finishing activity and
disposal mid-operation; the first-draw gate holding and then opening; and, through a real router, lease release order,
placeholders, presenter-opened leases and the router host. The release workflow
runs them on API 28 and API 36 emulators. `eng/verify.sh native-android`
publishes the same app fully trimmed, so the package stays trim-clean.

## Next steps

- Read the [adapter contract](adapter-contract.md).
- Read the [entry-leases](entry-leases.md) design note.
