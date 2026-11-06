# Android integration

[Documentation home](../index.md)

`AdamE.AppNav.Android` presents AppNav's logical state with plain Android views
and dialogs: no MAUI, no Fragments, no Navigation component. It is for a .NET
for Android app whose screens are `View`s, written in C# or hosting Jetpack
Compose or anything else that fits in a view. This guide connects one.

The package is one way to present AppNav on Android, not the only one. It uses
only core's public API, so an app whose navigation does not fit it can write its
own `INavigationPresenter` against the [adapter contract](../advanced/adapter-contract.md).

## What it presents

The window of whichever activity is attached, in the shape a `NavigationTopology`
from AppNav core declares:

```text
WindowNode "main"
├── StackNode "main-stack"
│   ├── RouteEntry  ← the frame's content while nothing is pushed over it
│   └── RouteEntry  ← takes the frame; the root page stays built underneath
└── ModalNode "sheet" (declared) ← a dialog over the stack
```

This version presents a stack of one or two entries and one modal at a time,
with no topology nested inside the modal. A deeper stack is a wider
`AndroidNavigationPresenter.Support` and changes nothing you declare. The
presenter checks every plan against the topology before it builds anything, and
refuses any other shape with `NotSupportedException`, so an unsupported plan
never half-presents.

On Android every Back is a request to the router:

- **Pushed pages.** While a page is pushed over the root page, or a modal is
  open, the presenter takes system Back through the activity's
  `OnBackPressedDispatcher` and asks the router, whose `IBackNavigationPolicy`
  decides. Over the root page alone it leaves Back to the activity, which by
  default finishes.
- **Modals.** Back on a modal's dialog, and the dialog's own cancel (a tap
  outside it, a bottom sheet dragged away), are Back requests for that modal.
  Android cannot refuse a cancel before it happens, so a dialog the router keeps
  is shown again. A dialog dismissed any other way asks for a refresh.

## Declare the topology

Use the structural IDs your planner already gives its window, stack and modals.
`NavigationTopology` is host-neutral (`AdamE.AppNav.Presentation`): an app with
an Android and a UIKit presenter declares it once and gives both the same one.

```csharp
var topology = new NavigationTopology("main", "main-stack")
    .WithModal("sheet", NavigationModalPlacement.OverRoot)
    // The owner lookup must not throw: an entry without the key has no owner, which refuses the plan.
    .WithOwnedModal("answer", entry => entry.Metadata?.TryGetValue("question-entry", out object? id) == true
        ? id as string
        : null);
```

- `OverRoot`: only while the stack holds the root page alone.
- `OverAnyEntry`: over any stack the presenter supports.
- `WithOwnedModal`: the modal belongs to one stack entry, read from the modal's
  own entry, and is presented only while that entry is the top of the stack.

## Build pages

An `IAndroidPageFactory` turns an entry into an `AndroidPage`: a view for the
root and pushed entries, a `ComponentDialog` for a modal. The presenter calls
it on the main thread, only for an entry it is about to present, and hands it
the attached activity to build in:

```csharp
sealed class Pages : IAndroidPageFactory
{
    public ValueTask<AndroidPage> CreatePageAsync(AndroidPageRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Entry.Route switch
        {
            HomeRoute => AndroidPage.ForView(new HomeView(request.Activity)),
            ItemRoute item => AndroidPage.ForView(new ItemView(request.Activity, item.Id)),
            SettingsRoute => AndroidPage.ForDialog(SettingsSheet(request.Activity)),
            _ => throw new InvalidOperationException($"No page for {request.Entry.Route}.")
        });

    static ComponentDialog SettingsSheet(Context context)
    {
        var sheet = new BottomSheetDialog(context); // Material's sheet is a ComponentDialog.
        sheet.SetContentView(new SettingsView(context));
        return sheet;
    }
}
```

- **A view must have no parent, and a dialog must not be showing**, when you
  return it. Two entries never share one.
- **How a dialog looks is yours.** Its style, its window flags and whether a
  tap outside it cancels it are the dialog's own. How it closes is the
  presenter's: do not turn its cancel into navigation yourself, or the router
  is asked twice. Use its `CancelEvent` and `DismissEvent` (or none), never
  `SetOnCancelListener` or `SetOnDismissListener`, which replace the
  presenter's listeners.
- **The presenter owns the page from the moment you return it.** When the page
  retires, it disposes `Binding` first (your subscriptions), takes the view out
  of its parent or dismisses the dialog, checks it has left the window, disposes
  a view, and only then disposes `Loan`, anything the page borrows that its
  native objects do not own. It does not dispose dialogs, because Android may
  still deliver the dismiss messages it queued; the garbage collector frees them.
- **A page is current** while its entry's lease is not retired, its optional
  `Retirement` token is not canceled, and its optional `IsCurrent` says so. An
  operation presenting a page that stops being current is canceled and rolled
  back.

### When building fails

A factory that throws must leave nothing native behind. If it started building
native UI it then could not retire, throw `AndroidCleanupFailureException`: the
presenter keeps everything the entry borrows and fails every later operation,
attachment and its own disposal with that failure, because releasing something
a live native object may still use is worse than stopping. Wrap your own signal
in one.

## Connect native events

The presenter tells an `IAndroidNavigationHost` about Back requests, refresh
requests and failures it recovered from. It calls them on the main thread, in a
later turn of the looper and outside any router operation, so the host may call
the router and touch views. `AndroidRouterNavigationHost` sends each to the
router:

```csharp
IRouterNavigator? router = null;
var host = new AndroidRouterNavigationHost(
    () => router ?? throw new InvalidOperationException("The router is not built yet."),
    topology,
    failure => logger.LogWarning(failure, "Recovered navigation failure."));
```

A modal's Back request names its entry, and the router host sends it with
`ExpectedModalEntryId`, so a cancel that arrives after its modal has gone closes
nothing else. An app that coordinates its own navigation state implements
`IAndroidNavigationHost` itself, and should check the entry the same way.

## Create the presenter and the router

The presenter and the router outlive activities: build them once, for the
process, typically in your `Application` or the first activity's `OnCreate`.

```csharp
var leases = new RouteEntryLeaseRegistry(new MainThreadLeaseHost());
var presenter = new AndroidNavigationPresenter(
    topology,
    new Pages(),
    host,
    new AndroidNavigationPresenterOptions { EntryLeases = leases });
router = RouterNavigatorFactory.Create(
    routes,
    planner,
    presenter,
    new RouterNavigatorFactoryOptions { EntryLeases = leases });
```

`MainThreadLeaseHost` is your `IRouteEntryLeaseHost`: it runs lease work on the
main thread and receives release defects and fatal failures. See
[entry leases](../advanced/entry-leases.md).

## Attach each activity

Your activity must be a `ComponentActivity` (an `AppCompatActivity` is one).
Attach it in `OnCreate`, put the frame you get back in its content, and present:
start the router the first time, refresh it after that. Detach it in
`OnDestroy`:

```csharp
protected override void OnCreate(Bundle? savedInstanceState)
{
    base.OnCreate(savedInstanceState);
    SetContentView(App.Presenter.Attach(this));
    if (App.Started)
    {
        _ = App.Router.RefreshPresentationAsync();
        return;
    }

    App.Started = true;
    _ = App.Router.NavigateAsync(App.StartRequest);
}

protected override void OnDestroy()
{
    App.Presenter.Detach(this);
    base.OnDestroy();
}
```

A recreated activity (a rotation, a theme change, the system reclaiming it in
the background) gets new pages for the same entries: the factory is called
again, with the new activity, while each entry's lease keeps what it owns. Build
pages over your existing state, never a new copy. An operation that is building
or presenting when its activity goes waits for the next one; an activity that is
already finishing when an operation reaches it is detached for you. The frame
draws nothing until the accepted pages are in it, so a fresh activity never
flashes empty.

The presenter keeps no state in the activity's saved instance state: if the
system ends the process, AppNav starts again from your startup request.

### Leases

With `EntryLeases`, open the lease of the entry a request presents before you
send it (`RouteEntryLeaseNavigation.NavigateAsync` sends it with the request),
and own what that entry borrows on it. An entry you opened no lease for, such as
an ancestor your planner added for a deep link, gets one the presenter opens
during presentation, which core closes when the operation ends. Each page is
attached to its entry's lease before the factory runs, and detached only after
the page has left the window. Whatever the lease owns therefore outlives every
native object that uses it, across activities. The factory receives the lease in
`AndroidPageRequest.Lease`.

An entry whose lease retires while the router still shows it gets a
placeholder, a page that borrows nothing, until the router drops the entry. The
defaults are an empty view and a dialog with an empty view;
`CreatePlaceholderView` and `CreatePlaceholderDialog` supply your own.

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

`eng/run-android-native-checks.sh` runs the presenter's native checks in a plain
Android app on an emulator you name: real presentation, Back and cancel,
activity recreation, rollback, leases and placeholders. See the
[Android adapter design](../advanced/android-adapter.md) for what the presenter
guarantees and how it is checked.

## Next steps

- Read the [Android adapter design](../advanced/android-adapter.md).
- Model your shape in [topology and planning](../concepts/02-topology-and-planning.md).
- Handle [navigation outcomes and failures](04-navigation-outcomes-and-failure-handling.md).
