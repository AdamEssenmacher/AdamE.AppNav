# Source-generator diagnostics

[Documentation home](../index.md)

AppNav reports route and MAUI page mapping problems during compilation. Treat
errors as contract failures rather than suppressing them. `APPNAV011` is the
only warning in the current set.

## Route generator

| ID | Severity | Meaning | Remediation |
| --- | --- | --- | --- |
| `APPNAV001` | Error | Route template is invalid | Use a valid absolute route template with well-formed parameters. |
| `APPNAV002` | Error | Path parameter has no matching property | Add an accessible readable property matching the parameter name. |
| `APPNAV003` | Error | Query property is invalid | Point `AppNavQuery` at a supported accessible property. |
| `APPNAV004` | Error | Query name is registered more than once | Give every bound query value a unique name. |
| `APPNAV005` | Error | A member is bound by both path and query | Remove the query binding for the path-owned member. |
| `APPNAV006` | Error | No public constructor can materialize the route | Add a constructor compatible with all required path and query values. |
| `APPNAV007` | Error | More than one constructor is compatible | Remove the ambiguity so exactly one public constructor can be selected. |
| `APPNAV008` | Error | Query constructor parameter is not missing-safe | Make the optional query parameter nullable or give it a default. |
| `APPNAV009` | Error | Route template is duplicated | Keep one route type per canonical template. |
| `APPNAV010` | Error | Route templates can match the same path | Make templates structurally or constraint-wise unambiguous. |
| `APPNAV011` | Warning | Route value type has no built-in codec | Register a value codec with `RouteTableBuilder.AddValueCodec`. |
| `APPNAV012` | Error | Attributed route type is invalid | Use a concrete, non-generic, accessible `AppRoute`. |
| `APPNAV013` | Error | Route has duplicate property names ignoring case | Rename or remove the conflicting public properties. |
| `APPNAV014` | Error | Optional path constructor parameter is not missing-safe | Make the parameter nullable or give it a default. |
| `APPNAV015` | Error | Repeated-query collection type is unsupported | Use an array or a supported list/read-only-list collection shape. |
| `APPNAV016` | Error | Query property and constructor types differ | Make the property and matching constructor parameter types identical. |

## MAUI page generator

| ID | Severity | Meaning | Remediation |
| --- | --- | --- | --- |
| `APPNAV020` | Error | Page maps a route that is not an attributed AppNav route | Add `AppNavRoute` to the route or use explicit page registration. |
| `APPNAV021` | Error | Attributed type is not a MAUI `Page` | Move `MauiRoutePage` to a type derived from `Microsoft.Maui.Controls.Page`. |
| `APPNAV022` | Error | Page constructor selection is ambiguous | Mark one constructor with `ActivatorUtilitiesConstructorAttribute`. |
| `APPNAV023` | Error | Selected page constructor cannot receive the mapped route | Add a parameter assignable from the mapped route type. |
| `APPNAV024` | Error | A route is mapped to more than one page | Keep one generated page mapping for the route. |
| `APPNAV025` | Error | Page model type is inaccessible or generic | Use a non-generic page model accessible to generated code. |

## Generated output

The route generator produces `AppNavRoutes.g.cs`; the MAUI generator produces
`AppNavMauiPages.g.cs`. Both expose members on `AppNavGenerated`. Generated
files normally live under `obj/`; inspect them for debugging, but do not edit
them.

If generation succeeds but runtime registration is missing, confirm that
`AppNavGenerated.CreateRouteTable()` and `AppNavGenerated.MauiPageModule` are
passed to `AddAppNav`.

## Next steps

- Review [routing and metadata](../concepts/routing-and-metadata.md).
- Use [Troubleshooting](../guides/troubleshooting.md) for runtime failures.
- Return to [Getting started](../guides/getting-started.md).
