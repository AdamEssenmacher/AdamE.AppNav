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

`NavigationNode` is externally non-derivable. Unknown shapes are rejected
before presentation. A non-null `ActiveWindowId` must identify an existing
window.

Branch-host topology remains host-neutral. Adapter presentation configuration
may map a stable branch-host ID to a host-specific container without changing
the logical model. The MAUI adapter renders branch hosts as `TabbedPage` by
default and can map a direct window-root host to `FlyoutPage`.

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

Use `IAppNavigationPlanner` directly when an app coordinates multiple models or
has domain-specific topology rules.

## Back and reconciliation

Logical Back first handles modal content, then modal dismissal, stack pop, and
configured branch fallback. In Glyphmere that means Back dismisses the load
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
