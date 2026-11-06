# UIKit integration

[Documentation home](../index.md)

`AdamE.AppNav.iOS` presents AppNav's logical state with plain UIKit: no MAUI, no
storyboards, no navigation framework of its own. It is for a .NET iOS app whose
screens are `UIViewController`s, whether written in C#, or SwiftUI or anything
else hosted in a controller. This guide connects one.

The package is one way to present AppNav on iOS, not the only one. It uses only
core's public API, so an app whose navigation does not fit it can write its own
`INavigationPresenter` against the [adapter contract](../advanced/adapter-contract.md).

## What it presents

One `UIWindow`, in the shape a `NavigationTopology` from AppNav core declares:

```text
WindowNode "main"
├── StackNode "main-stack"
│   ├── RouteEntry  ← the window's root page
│   └── RouteEntry  ← pushed onto the root page's UINavigationController
└── ModalNode "sheet" (declared) ← presented over the stack
```

This version presents a stack of one or two entries and one modal at a time,
with no topology nested inside the modal. A deeper stack is a wider
`UIKitNavigationPresenter.Support` and changes nothing you declare; modals with
their own stacks need a way to declare those stacks, which comes with them. The
presenter checks every plan against the topology
before it builds anything, and refuses any other shape with
`NotSupportedException`, so an unsupported plan never half-presents.

UIKit keeps what is UIKit's:

- **Pushed pages.** The stack's back button and back-swipes are UIKit's. When
  UIKit commits a pop, the presenter reports it as a fact, and the router
  records it. It never cancels or refuses one.
- **Modals.** The presenter presents every modal with `ModalInPresentation`
  set, so a swipe-down does not dismiss it. It becomes a Back request, which
  your `IBackNavigationPolicy` can refuse, for example while a form is saving.
- **Foreign UI.** An alert, a share sheet or a sign-in SDK's controller that a
  page presents itself stays that page's. The presenter refuses to navigate
  over a still-current page's foreign UI and keeps it through a refresh.

## Declare the topology

Use the structural IDs your planner already gives its window, stack and modals.
`NavigationTopology` is host-neutral (`AdamE.AppNav.Presentation`): an app with a
UIKit and an Android presenter declares it once and gives both the same one.

```csharp
var topology = new NavigationTopology("main", "main-stack")
    .WithModal("sheet", NavigationModalPlacement.OverRoot)
    // The owner lookup must not throw: an entry without the key has no owner, which refuses the plan.
    .WithOwnedModal("answer", entry => entry.Metadata?.TryGetValue("question-entry", out object? id) == true
        ? id as string
        : null);
```

What this presenter can show of it is `UIKitNavigationPresenter.Support`: today a
stack of two entries. Core's `NavigationTopology.Match` itself matches one modal
at a time with nothing nested in it, whatever a presenter supports. A plan beyond
either fails with a `NotSupportedException` that says which limit refused it.

- `OverRoot`: only while the stack holds the root page alone.
- `OverAnyEntry`: over any stack the presenter supports.
- `WithOwnedModal`: the modal belongs to one stack entry, read from the
  modal's own entry, and is presented only while that entry is the top of the
  stack. An answer sheet over the question it answers is one.

## Build pages

An `IUIKitPageFactory` turns an entry into a `UIKitPage`. The presenter calls it
on the main thread, only for an entry it is about to present:

```csharp
sealed class Pages : IUIKitPageFactory
{
    public ValueTask<UIKitPage> CreatePageAsync(UIKitPageRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Entry.Route switch
        {
            HomeRoute => UIKitPage.InNavigationStack(new HomeViewController()),
            ItemRoute item => new UIKitPage(new ItemViewController(item.Id)),
            SettingsRoute => new UIKitPage(SettingsSheet()),
            _ => throw new InvalidOperationException($"No page for {request.Entry.Route}.")
        });

    static UIViewController SettingsSheet()
    {
        var sheet = new UINavigationController(new SettingsViewController());
        sheet.SheetPresentationController?.Detents = [UISheetPresentationControllerDetent.CreateMediumDetent()];
        return sheet;
    }
}
```

- **A root page that others are pushed over** is a navigation stack:
  `UIKitPage.InNavigationStack(content)`, or your own `UINavigationController`
  (a subclass with its own bar appearance, say) holding just that page, with
  `IsNavigationStack = true`.
- **A pushed page is a plain controller**, never a navigation controller,
  which UIKit cannot push.
- **The presenter is the delegate** of the root page's navigation controller
  and of each modal's presentation controller; a page that already has one is
  rejected. Observe detent changes inside the modal's own controllers instead.
- **How a modal looks is the controller's own business.** Set its
  `ModalPresentationStyle`, detents and navigation items before returning it.
  A full-screen modal may cover the stack; the presenter accepts that the pages
  under it leave the window while it is shown.
- **The presenter owns the page from the moment you return it.** It disposes
  the controller once it has left the window, and only then disposes the page's
  `Loan`, anything the page borrows that the controller does not own.
- **A page is current** while its entry's lease is not retired, its optional
  `Retirement` token is not canceled, and its optional `IsCurrent` says so. An
  operation presenting a page that stops being current is canceled and rolled
  back.

### When building fails

A factory that throws must leave nothing native behind. If it started building
native UI it then could not retire, throw `UIKitCleanupFailureException`: the
presenter keeps everything the entry borrows and fails every later operation and
its own disposal with that failure, because releasing something a live native
object may still use is worse than stopping. Wrap your own signal, such as a
renderer kit's cleanup exception, in one.

## Connect native events

The presenter tells an `IUIKitNavigationHost` about Back requests, committed
pops, refresh requests and failures it recovered from. It calls them on the
main thread, in a later turn of its run loop and outside any router operation, so the host may
call the router and touch UIKit. `UIKitRouterNavigationHost`
sends each to the router:

```csharp
IRouterNavigator? router = null;
var host = new UIKitRouterNavigationHost(
    () => router ?? throw new InvalidOperationException("The router is not built yet."),
    topology,
    failure => logger.LogWarning(failure, "Recovered navigation failure."));
```

An app that coordinates its own navigation state, and so records a pop or a
Back through its own code, implements `IUIKitNavigationHost` itself.

## Create the presenter and the router

```csharp
var leases = new RouteEntryLeaseRegistry(new MainThreadLeaseHost());
var presenter = new UIKitNavigationPresenter(
    window,
    topology,
    new Pages(),
    host,
    new UIKitNavigationPresenterOptions { EntryLeases = leases });
router = RouterNavigatorFactory.Create(
    routes,
    planner,
    presenter,
    new RouterNavigatorFactoryOptions { EntryLeases = leases });
```

Create the presenter on the main thread. It reveals the window itself, once it
has installed the first page, so never show an empty window first.

`MainThreadLeaseHost` is your `IRouteEntryLeaseHost`: it runs lease work on the
main thread and receives release defects and fatal failures. See
[entry leases](../advanced/entry-leases.md).

### Leases

With `EntryLeases`, open the lease of the entry a request presents before you
send it (`RouteEntryLeaseNavigation.NavigateAsync` sends it with the request),
and own what that entry borrows on it. An entry you opened no lease for, such as
an ancestor your planner added for a deep link, gets one the presenter opens
during presentation, which core closes when the operation ends. Each page is
attached to its entry's lease before the factory runs, and detached only after
the page has left the window and been disposed. Whatever the lease owns, a view model or a database scope, therefore
outlives every native object that uses it. The factory receives the lease in
`UIKitPageRequest.Lease`.

An entry whose lease retires while the router still shows it gets a
placeholder, a page that borrows nothing, until the router drops the entry. The
default is a blank view in the system background colour;
`UIKitNavigationPresenterOptions.CreatePlaceholder` supplies your own.

## Push transitions

`ConfiguringPush` runs just before a page that is not on the stack yet is put
over the root page, whether pushed or replacing the page there. Set
the pushed controller's transition there, for example a zoom from the cell that
opened it on iOS 18 and later:

```csharp
ConfiguringPush = push =>
{
    if (!OperatingSystem.IsIOSVersionAtLeast(18))
        return;
    push.Pushed.PreferredTransition = push.Animated && push.RootContent is ListViewController list
        ? UIViewControllerTransition.Zoom(new UIZoomTransitionOptions(), _ => list.CellFor(push.Entry))
        : null;
}
```

A push that is not animated, such as a replacement, should clear what an earlier
push set.

## Shut down

Start the presenter's disposal alongside the router's, never after awaiting it:
the router waits for every lease to release, and leases release only once the
presenter has taken their pages down.

```csharp
Task routerDisposal = router.DisposeAsync().AsTask();
await presenter.DisposeAsync();
await routerDisposal;
```

## Verify it

`eng/run-ios-native-checks.sh` runs the presenter's native checks in a UIKit app
on a simulator you name: real presentation, pops, rollback, leases and
placeholders. See the [UIKit adapter design](../advanced/uikit-adapter.md) for
what the presenter guarantees and how it is checked.

## Next steps

- Read the [UIKit adapter design](../advanced/uikit-adapter.md).
- Model your shape in [topology and planning](../concepts/02-topology-and-planning.md).
- Handle [navigation outcomes and failures](04-navigation-outcomes-and-failure-handling.md).
