# Routing and metadata

[Documentation home](../index.md)

AppNav routes are typed semantic destinations. A route should contain durable
domain identity, not a `Page`, view model, service, callback, native handle, or
transport provenance.

## Attributed routes

Annotate a concrete, accessible `AppRoute` with one canonical template:

```csharp
[AppNavRoute("/stores/{storeId}/products/{productId:int}")]
public sealed record ProductDetailRoute(string StoreId, int ProductId) : AppRoute;
```

Template member names match public readable route properties without regard to
case. The generator selects a compatible public constructor. Ambiguous or
missing constructors are compile errors.

Templates support:

- literal path segments, such as `/stores`;
- required parameters, such as `{storeId}`;
- optional parameters, such as `{productId:int?}`;
- a terminal catch-all parameter, such as `{*path}`;
- built-in `int`, `long`, `guid`, `bool`, `decimal`, and `alpha` constraints.

Optional constructor parameters must be nullable or have a default. A route
type has one canonical attributed template; duplicate or overlapping templates
are rejected when AppNav cannot prove them disjoint.

## Query-bound route properties

Use `AppNavQuery` for values that belong to route identity but are carried in
the query string:

```csharp
[AppNavRoute("/search")]
[AppNavQuery(nameof(Tags), Name = "tag")]
public sealed record SearchRoute(IReadOnlyList<string>? Tags = null) : AppRoute;
```

When `Name` is omitted, AppNav uses the camel-cased property name. Query values
are optional, so constructor parameters must be nullable or have a default.
Arrays and common list/read-only-list shapes support repeated query parameters.
`OmitWhenNull` defaults to `true`.

The built-in codecs cover strings, booleans, integral and floating-point
numbers, decimals, and GUIDs. Register an enum or custom value codec explicitly
with `RouteTableBuilder` when a route uses another value type.

## Route-owned metadata

`AppRouteRequest` combines a route with app-owned metadata:

```csharp
public static RouteMetadataKey<string> Campaign { get; } = new("campaign");

AppRouteRequest request = AppRouteRequest
    .For(new ProductDetailRoute("northwind", 123))
    .WithMetadata(Campaign, "spring-sale");
```

Use `AppNavQueryMetadata` when registered metadata should round-trip through a
canonical URI. The referenced member must be an accessible static
`RouteMetadataKey<T>`:

```csharp
[AppNavQueryMetadata(typeof(RouteMetadata), nameof(RouteMetadata.Campaign))]
```

Define metadata lifetime in a `RouteStateRegistry`:

| Lifetime | Meaning |
| --- | --- |
| `Canonical` | Participates in canonical formatting and sharing |
| `Restorable` | May be persisted and restored, but is not canonical URL identity |
| `Ephemeral` | Remains only in live in-memory navigation state |

Do not put request source, referrer, correlation IDs, auth tokens, or other
transport context in route metadata. Those values belong to runtime
provenance.

## Generated modules

The core generator emits `AppNavRoutes.g.cs`, including
`AppNavGenerated.CreateRouteTable()`. The MAUI generator emits
`AppNavMauiPages.g.cs` and `AppNavGenerated.MauiPageModule` for pages annotated
with `MauiRoutePage`.

Register both through `AddAppNav`. See the
[source-generator diagnostics](../reference/source-generator-diagnostics.md)
when generation fails.

## Next steps

- Follow [MAUI integration](../guides/02-maui-integration.md).
- Understand [requests and provenance](requests-and-provenance.md).
- Review [topology and planning](topology-and-planning.md).
