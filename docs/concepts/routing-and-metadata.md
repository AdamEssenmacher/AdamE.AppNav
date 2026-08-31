# Routing and metadata

[Documentation home](../index.md)

## Meet Glyphmere

The concept guides use **Glyphmere**, a fictional role-playing game, to make
AppNav's abstractions concrete. Its pause menu has Inventory, Spellbook, World
Map, Save Games, and Options areas. Each area owns an independent navigation
tree. Inventory, for example, can retain
`Inventory -> Weapons -> Iron Sword` while the player briefly switches to a
World Map region. Returning to Inventory restores the same subgroup and item
instead of resetting that area to its root.

Armor, Weapons, and Potions are semantic destinations too, represented by
`InventoryCategoryRoute(category)`. Opening the Iron Sword then adds
`InventoryItemRoute(itemId)` to the active path beneath Weapons. The route
types describe those destinations; they do not hard-code how the pause menu
renders the hierarchy.

AppNav routes are typed semantic destinations:

- **Semantic** means a route describes where the player wants to go, such as
  “the iron key in Inventory.” It does not say “push this page” or “select this
  tab.” The navigation model and MAUI presenter decide how to display it.
- **Typed** means app code represents that destination with a compiler-known
  `AppRoute`, including the durable values that identify it.

For example, `InventoryItemRoute(itemId)` identifies one inventory item. That
route can remain unchanged if Glyphmere replaces its MAUI page, moves item
details into a split view, or presents them differently on another platform.
Presentation can evolve without changing what the destination means.

A route should therefore contain durable domain identity, not a `Page`, view
model, service, callback, native handle, or information about how a request
entered the app.

## Attributed routes

Annotate a concrete, accessible `AppRoute` with one canonical template. These
conceptual Glyphmere routes identify the Inventory root and one item:

```csharp
[AppNavRoute("/pause/inventory")]
public sealed record InventoryRoute : AppRoute;

[AppNavRoute("/pause/inventory/items/{itemId:guid}")]
public sealed record InventoryItemRoute(Guid ItemId) : AppRoute;
```

`/pause/inventory/items/2f1c9eb9-2d31-4ec4-9fb1-6f881685b634` can be matched
back to a typed `InventoryItemRoute`. The `guid` constraint prevents arbitrary
text from being accepted as an item identifier.

Template member names match public readable route properties without regard to
case. The generator selects a compatible public constructor. Ambiguous or
missing constructors are compile errors.

Templates support:

- literal path segments, such as `/pause/inventory/items`;
- required parameters, such as `{itemId}`;
- optional parameters, such as `{slot:int?}`;
- a terminal catch-all parameter, such as `{*path}`;
- built-in `int`, `long`, `guid`, `bool`, `decimal`, and `alpha` constraints.

Optional constructor parameters must be nullable or have a default. A route
type has one canonical attributed template; duplicate or overlapping templates
are rejected when AppNav cannot prove them disjoint.

## Query-bound route properties

Use `AppNavQuery` for values that belong to route identity but are carried in
the query string. If Glyphmere wants filtered Inventory roots to be distinct
destinations, it can extend `InventoryRoute` to preserve a rarity filter and
sort order in a URI such as `/pause/inventory?rarity=rare&sort=recent`:

```csharp
[AppNavRoute("/pause/inventory")]
[AppNavQuery(nameof(Rarity))]
[AppNavQuery(nameof(Sort))]
public sealed record InventoryRoute(string? Rarity = null, string? Sort = null)
    : AppRoute;
```

When `Name` is omitted, AppNav uses the camel-cased property name. Query values
are optional, so constructor parameters must be nullable or have a default.
Arrays and common list/read-only-list shapes support repeated query parameters.
`OmitWhenNull` defaults to `true`.

The built-in codecs cover strings, booleans, integral and floating-point
numbers, decimals, and GUIDs. Register an enum or custom value codec explicitly
with `RouteTableBuilder` when a route uses another value type.

## Route-owned metadata

Route properties identify a destination. `AppRouteRequest` adds state owned by
one occurrence of that destination. For example, Glyphmere can open an item in
a comparison mode without making comparison state part of
`InventoryItemRoute`:

```csharp
public static class GlyphmereRouteMetadata
{
    public static RouteMetadataKey<Guid> CompareWithItemId { get; } =
        new("compareWithItemId");
    public static RouteMetadataKey<string> SelectedPanel { get; } =
        new("selectedPanel");
    public static RouteMetadataKey<bool> HighlightNewItem { get; } =
        new("highlightNewItem");
}

AppRouteRequest request = AppRouteRequest
    .For(new InventoryItemRoute(itemId))
    .WithMetadata(GlyphmereRouteMetadata.CompareWithItemId, equippedItemId)
    .WithMetadata(GlyphmereRouteMetadata.SelectedPanel, "stats")
    .WithMetadata(GlyphmereRouteMetadata.HighlightNewItem, true);
```

Use `AppNavQueryMetadata` when registered metadata should round-trip through a
canonical URI. The referenced member must be an accessible static
`RouteMetadataKey<T>`:

```csharp
[AppNavRoute("/pause/inventory/items/{itemId:guid}")]
[AppNavQueryMetadata(
    typeof(GlyphmereRouteMetadata),
    nameof(GlyphmereRouteMetadata.CompareWithItemId))]
public sealed record InventoryItemRoute(Guid ItemId) : AppRoute;
```

Canonical formatting then includes `compareWithItemId` in a shareable item URI.
The restorable selected panel and ephemeral highlight are deliberately omitted
from that canonical URI.

Define each metadata lifetime in a `RouteStateRegistry`:

```csharp
var routeStateRegistry = RouteStateRegistry.Create(builder => builder
    .Canonical(GlyphmereRouteMetadata.CompareWithItemId)
    .Restorable(GlyphmereRouteMetadata.SelectedPanel)
    .Ephemeral(GlyphmereRouteMetadata.HighlightNewItem));
```

| Glyphmere state | Lifetime | Meaning |
| --- | --- | --- |
| Item being compared | `Canonical` | Participates in canonical formatting and sharing |
| Selected details panel | `Restorable` | May be persisted and restored, but is not canonical URL identity |
| “New item” highlight | `Ephemeral` | Remains only in live in-memory navigation state |

Do not put request source, referrer, correlation IDs, auth tokens, or other
external-request context in route metadata. Those values belong to runtime
provenance.

## The rest of the Glyphmere routes

The other concept guides use these destination names consistently:

| Pause-menu area | Root destination and template | Nested destination and template |
| --- | --- | --- |
| Inventory | `InventoryRoute`, `/pause/inventory` | `InventoryCategoryRoute(category)`, `/pause/inventory/categories/{category:alpha}`; `InventoryItemRoute(itemId)`, `/pause/inventory/items/{itemId:guid}` |
| Spellbook | `SpellbookRoute`, `/pause/spellbook` | `SpellRoute(spellId)`, `/pause/spellbook/spells/{spellId}` |
| World Map | `WorldMapRoute`, `/pause/world-map` | `MapRegionRoute(regionId)`, `/pause/world-map/regions/{regionId}` |
| Save Games | `SaveGamesRoute`, `/pause/saves` | `SaveSlotRoute(slot)`, `/pause/saves/{slot:int}` |
| Options | `OptionsRoute`, `/pause/options` | None in the running example |

`LoadSaveConfirmationRoute(slot)` is an app-authored route shown as a modal; it
is not externally addressable in the running example.

These are conceptual examples, not an additional sample application. The
[Getting Started guide](../guides/01-getting-started.md) remains the buildable,
source-backed Home -> Detail walkthrough.

## Generated modules

The core generator emits `AppNavRoutes.g.cs`, including
`AppNavGenerated.CreateRouteTable()`. The MAUI generator emits
`AppNavMauiPages.g.cs` and `AppNavGenerated.MauiPageModule` for pages annotated
with `MauiRoutePage`.

Register both through `AddAppNav`. See the
[source-generator diagnostics](../reference/source-generator-diagnostics.md)
when generation fails.

## Next steps

- See how Glyphmere's destinations become UI in
  [topology and planning](topology-and-planning.md).
- Choose the right request type with
  [requests and provenance](requests-and-provenance.md).
- Follow the buildable [Getting Started guide](../guides/01-getting-started.md).
