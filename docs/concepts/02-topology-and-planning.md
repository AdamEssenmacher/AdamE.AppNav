# Topology and planning

[Documentation home](../index.md)

AppNav plans a logical tree before an adapter mutates UI. This guide continues
the [Glyphmere pause-menu example](01-routing-and-metadata.md#meet-glyphmere).

Routes say where the player wants to go. Topology describes the native
navigation shape needed to get there. For example, an
`InventoryItemRoute(itemId)` does not select a tab or push a page itself. The
navigation model turns it into a plan that selects Inventory and places the
Inventory root and requested item on that branch's stack.

Those stack entries are logical `RouteEntry` values, not declarations of page
or view-model instances. The presenter decides how each entry appears. In the
built-in MAUI adapter an entry has one anchor route page, but that route can own
additional native presentation pages and the anchor can render route-specific
controls or state. The topology therefore describes navigation meaning and
structure without fixing every presentation artifact.

## Glyphmere's logical tree

Glyphmere models one game window whose pause menu is a branch host. Each branch
owns an independent navigation tree. In this model each tree is a stack, so
switching to World Map does not erase the Inventory subgroup or item the player
was viewing:

```text
WindowNode "main"
├── BranchHostNode "pause-menu" (selected: "inventory")
│   ├── branch "inventory" -> StackNode "inventory-stack"
│   │   ├── InventoryRoute
│   │   ├── InventoryCategoryRoute("weapons")
│   │   └── InventoryItemRoute(itemId)
│   ├── branch "spellbook" -> StackNode "spellbook-stack"
│   │   ├── SpellbookRoute
│   │   └── SpellRoute(spellId)
│   ├── branch "world-map" -> StackNode "world-map-stack"
│   │   ├── WorldMapRoute
│   │   └── MapRegionRoute(regionId)
│   ├── branch "save-games" -> StackNode "save-games-stack"
│   │   ├── SaveGamesRoute
│   │   └── SaveSlotRoute(slot)
│   └── branch "options" -> StackNode "options-stack"
│       └── OptionsRoute
└── ModalNode -> LoadSaveConfirmationRoute(slot)
```

If the player selects World Map, `SelectedBranchId` changes to `world-map`, but
the complete `inventory-stack` remains in the logical tree. Selecting Inventory
again reveals `Inventory -> Weapons -> Iron Sword` exactly where the player
left it.

Names such as `main`, `pause-menu`, and `inventory-stack` are stable,
app-defined structural IDs. They let AppNav compare the planned tree with the
tree already on screen. Route-entry IDs separately identify occurrences such
as one particular inventory subgroup or item within a stack.

## Supported v1 shapes

- `WindowNode` owns one root and zero or more modals. Glyphmere uses `main`.
- `StackNode` owns ordered `RouteEntry` values, such as Inventory followed by
  one item.
- `BranchHostNode` owns independent navigation trees and one selected branch,
  such as the five pause-menu areas. Changing the selected branch does not
  discard the inactive branch trees.
- `ModalNode` owns a modal route and optional nested topology, such as the
  confirmation shown before loading a save.
- `SplitNode` owns a primary column and an optional secondary column, each with
  its own navigation tree, and says which column a collapsed presentation shows
  on top. A split cannot be nested, at any depth, inside a modal or another
  split; validation rejects such a state. See [split layouts](#split-layouts).

`NavigationNode` is externally non-derivable. Unknown shapes are rejected
before presentation. A non-null `ActiveWindowId` must identify an existing
window.

Branch-host topology remains host-neutral. Adapter presentation configuration
may map a stable branch-host ID to a host-specific container without changing
the logical model. The MAUI adapter renders branch hosts as `TabbedPage` by
default and can map each host independently to tabs, a direct window-root
`FlyoutPage`, or an application-owned custom host. A factory advertises its
supported placements; preflight rejects an incompatible topology before page
creation or native mutation.

## Split layouts

A wide window, such as a tablet or an opened foldable, can show two columns:
a primary column beside a secondary column holding the selected item's own
navigation tree. A narrow window shows the same logical state as one stack.
A mail app is the usual example: the mailbox list is the primary column, and
the selected message, with anything pushed from it, is the secondary column.

```text
WindowNode "main"
└── SplitNode "mail" (compact column: Secondary)
    ├── primary -> StackNode "mailboxes"
    │   └── InboxRoute
    └── secondary -> StackNode "message-stack"
        ├── MessageRoute(messageId)
        └── AttachmentRoute(messageId, attachmentId)
```

`Secondary` may be null when nothing is selected; a presenter may show a
placeholder beside the primary column. `CompactColumn` names the column a
collapsed presentation shows on top: `Primary` shows only the mailbox list,
and `Secondary` shows the mailbox list with the message stack above it, so the
user sees the attachment. When the constructor is not given a
`CompactColumn`, it shows the secondary column if there is one and the primary
column otherwise. `CompactColumn = Secondary` requires a secondary column.
Passing `Primary` with a secondary column keeps a default message beside the
list on wide windows while a collapsed window keeps the list on top:

```csharp
new SplitNode("mail", mailboxes, messageStack);                      // CompactColumn: Secondary
new SplitNode("mail", mailboxes);                                    // CompactColumn: Primary
new SplitNode("mail", mailboxes, welcomeStack, SplitColumn.Primary); // list on top when collapsed
```

Change the selection with `split.Select(messageStack)` and
`split.ClearSecondary()`. `Select` resolves `CompactColumn` as the constructor
does unless you pass one, and `ClearSecondary` also shows the primary column.
A `with { Secondary = ... }` expression does not apply that default again: it
keeps the current `CompactColumn`, so selecting a message that way on a split
showing its list leaves the list on top.

What a column shows is its tree's presented content. A column holding a branch
host shows its selected branch's content, so a collapsed split stacks that
content.

Width changes never change the logical state. Collapsing a two-column window
to one column, or expanding it again, is purely presentation: the split, both
columns and `CompactColumn` stay as they are. Route entries in both columns are
committed state whichever column is shown, so their [route-entry
leases](../advanced/entry-leases.md) stay open and entry IDs must be unique
across both columns.

Core does not know the presented layout, so the presenter reports it. A
presenter that shows splits calls `IRouterNavigator.ReportSplitLayout(windowId,
layout)` before its first apply to a window and whenever its layout there
changes, with `SplitLayout.Collapsed` or `SplitLayout.SideBySide`; application
code does not call it. A window never reported is collapsed, and a layout is
forgotten once a commit no longer has its window. A layout reported before its
window exists is forgotten by any commit that does not add the window, so the
presenter reports again after the commit that adds it, as well as when it
attaches. The first navigation that creates a window therefore records its
route collapsed, unless a report made before it is still kept. A report racing the commit that removes a window can carry
over to a window re-added with the same ID, which the presenter's report on
attaching replaces. Reporting is presentation only: it changes neither
`CurrentState` nor history, is not queued behind navigation, and writes a
`SplitLayoutReported` diagnostic when the layout changes. App code then calls
`BackAsync()` as usual and gets the Back that matches what the user sees.

Collapsed, Back treats every split as one stack. While `CompactColumn` is
`Secondary`, Back first goes back inside the secondary column; from its root,
Back returns to the primary column by setting `CompactColumn` to `Primary`.
Otherwise Back goes back inside the primary column. Side by side, Back goes
back inside the secondary column, then inside the primary column, and never
plans a Back that only changes `CompactColumn`. A
`BackNavigationRequest.SplitLayout` overrides the reported layout for one
request. The router resolves the layout into
`BackNavigationContext.SplitLayout`, and the default Back navigator, when the
window holds a split, records it as `splitLayout` on `BackEvaluated`. An
adapter can also ask `IBackNavigator.CreateBackPlan` with a context of its own
whether Back would do anything in its layout, for example to decide whether to
enable a system Back callback. `DefaultBackNavigator.CreateBackPlan(state,
windowId)` always plans collapsed.

In both layouts, Back inside the primary column, whether a stack pop or a
branch host's default-branch fallback, clears the secondary column and shows
the primary column: the selected message belonged to the mailbox that was just
left. A default message shown beside the list with `CompactColumn = Primary`
is cleared the same way.

The route a navigation result and its history entry report follows the layout
reported when the operation was planned. Collapsed, it is the route on top of
the collapsed stack. Side by side, it is the secondary column's top whenever
the split has a secondary column, since that column has the focus, and the
primary column's top otherwise.

Apps build splits through `IAppNavigationPlanner`; the standard models do not
produce them. The MAUI, UIKit, Android and OAHU presenters do not present
splits yet and refuse a plan containing one with `NotSupportedException`
before any native change.

## Standard models

`StackNavigationModel<TRoute>` declares a canonical stack and contextual
push/replace behavior. `BranchHostNavigationModel<TRoute>` additionally
declares branches, sanitized inactive roots, and independent stacks. Glyphmere
uses the branch-host model for its pause menu. Both implement
`INavigationModel<TRoute>`.

`NavigationModelPlanner<TRoute>` applies standard dispositions:

| Disposition | Glyphmere behavior |
| --- | --- |
| `Auto` | An in-game item tap uses contextual navigation; an external map link uses canonical navigation |
| `Contextual` | Push the item or spell on a compatible current branch, then use its canonical topology if that is not possible |
| `ReplaceCurrent` | Replace the top entry on a compatible branch, then use canonical topology if that is not possible |
| `Canonical` | Build the route's declared branch and stack shape regardless of the current pause-menu state |

For example, canonical navigation to `MapRegionRoute("ashen-coast")` selects
World Map and constructs `WorldMapRoute -> MapRegionRoute`. Contextual
navigation can build `InventoryRoute -> InventoryCategoryRoute("weapons") ->
InventoryItemRoute(itemId)`, and that whole stack remains available while
another branch is selected.

A direct branch selection preserves those inactive stacks. A new canonical
plan instead rebuilds the declared destination shape and sanitizes inactive
branches to their configured roots; use canonical navigation when that reset is
the intended behavior.

A `Contextual` or `ReplaceCurrent` request that cannot be satisfied falls back
to canonical topology, which discards the accumulated stack and branch state
those dispositions were asked to preserve. That fallback is reported in the
plan's `ContextualFallback` flag, which the `PlanningCompleted` diagnostic
reports as a structural value in every data mode, so the fallback is observable
rather than silent. The plan's reason carries the prose explanation; because a
custom planner controls that text, it is not emitted as diagnostic data.

Use `IAppNavigationPlanner` directly when an app coordinates multiple models or
has domain-specific topology rules.

## Back and reconciliation

Logical Back first handles modal content, then modal dismissal, stack pop,
[split columns](#split-layouts), and configured branch fallback. In Glyphmere that means Back dismisses the load
confirmation before leaving its save slot, pops an item back to Inventory, and
can finally apply the pause menu's configured branch fallback.

When a candidate Back plan exists, ordered asynchronous
`IBackNavigationPolicy` services can inspect that exact validated plan and
cancel before presentation. Policy cancellation leaves presentation, logical
state, and history unchanged. Host changes that have already committed are
reconciliation inputs rather than cancellable Back requests.

Native host changes are reconciled with host-neutral sources:

- `HostBack` for completed native stack/back gestures;
- `BranchChanged` for native branch selection;
- `HostReconciliation` for the synthesized router request recorded in history.

Cancelled native gestures do not commit logical state.

Every reconciliation names the committed state it changes from
(`BaseState`) and the state the host now shows (`TargetState`). It waits
behind any in-flight navigation; if that navigation commits first, the
reconciliation is rejected with `StaleNavigationReconciliationException` and a
`ReconciliationRejected` diagnostic rather than resurrecting the older state.

A host-dispatched Back request is not reconciliation. It runs the ordinary Back
pipeline, including `IBackNavigationPolicy`, and records the `HostBack` request
source in presentation context and history, so a hardware or system Back is
distinguishable from an in-app `BackAsync` call.

## MAUI window rule

Core retains multi-window types for future adapters. The v1 MAUI presenter
rejects plans containing multiple windows. Unattached startup presentation is
allowed. Once attached, the plan window ID must match the attached MAUI window
ID. Preflight validation occurs before page creation or native mutation.

## Next steps

- See how Glyphmere's routes enter the pipeline in
  [requests and provenance](03-requests-and-provenance.md).
- Apply the model in [MAUI integration](../guides/02-maui-integration.md).
- Read the [adapter contract](../advanced/adapter-contract.md) before
  implementing another host.
