# Application architecture and testing

[Documentation home](../index.md)

AppNav can keep navigation decisions in ordinary .NET code instead of making
them a side effect of MAUI pages. That boundary is useful when an application
has more than one renderer, but it also pays off when MAUI is the only renderer:
most navigation behavior can be tested without starting MAUI, a simulator, a
device, or a UI thread.

This guide continues the fictional RPG
[Glyphmere](../concepts/01-routing-and-metadata.md#meet-glyphmere). The examples
are conceptual; the buildable [Getting Started sample](01-getting-started.md)
stays in one project so its minimum setup remains easy to inspect.

## One possible project boundary

`Presentation -> Application -> Domain` is one common naming convention, not a
structure required by AppNav. In that architecture, `Presentation` means
render-independent presentation logic such as view models. It is distinct from
AppNav's `INavigationPresenter`, which is the host adapter that applies a
logical navigation plan to visible UI.

```text
Outer render hosts
  Glyphmere.Maui --------\
  Glyphmere.Blazor -------+----> Glyphmere.Presentation (net10.0)
  Glyphmere.Avalonia ----/                  |
                                             v
                                    Glyphmere.Application
                                             |
                                             v
                                      Glyphmere.Domain

  Glyphmere.Maui -----------------> AdamE.AppNav.Maui
  Glyphmere.Presentation ---------> AdamE.AppNav (net10.0)
```

The arrows show compile-time dependencies. Each outer host depends inward on
the shared presentation project. The inner projects do not reference MAUI,
Blazor, Avalonia, native platform target frameworks, or page types.

`AdamE.AppNav` targets plain `net10.0`.
`AdamE.AppNav.Maui` is the production adapter supplied by this preview.
The diagram shows where another renderer
could connect; AppNav does not currently ship production Blazor or Avalonia
adapters.

## What belongs on each side

For Glyphmere, a useful division is:

| Render-independent application code | Outer host and adapter code |
| --- | --- |
| `InventoryRoute`, `InventoryCategoryRoute`, and `InventoryItemRoute` | MAUI page classes and route-to-page mappings |
| Presentation logic, including Inventory view models, that requests typed destinations | Native page construction and transitions |
| Stack and branch topology decisions | `NavigationPage`, tab controls, modals, and UI-thread work |
| Request policies and app-owned transforms | MAUI lifecycle and app-link dispatch |
| Route metadata keys and lifetime declarations | Android and Apple association and manifest configuration |
| Tests for routes, policies, planning, Back, and history | File-backed persistence and other platform service implementations |

The exact placement is an application decision. Domain entities should not
need to depend on AppNav merely because presentation logic requests a route.
Conversely, platform ingress and native presentation should not be pulled
inward just to make every navigation-related file shareable.

This separation does not turn view models into routes. `InventoryViewModel`
can request `InventoryItemRoute(itemId)`, and a view model associated with the
result can consume that route, but the view-model types remain replaceable
presentation details. The semantic route stays stable if Glyphmere splits the
destination across view models, removes a view model, or renders the same
destination differently in another host.

## Request semantic destinations from presentation logic

Render-independent presentation logic can depend on the core
`IRouterNavigator` and request a semantic destination without knowing whether
a MAUI page, a split view, or another host will render it. Here that logic
happens to be an inventory view model:

```csharp
public sealed class InventoryViewModel(IRouterNavigator navigator)
{
    public ValueTask<NavigationResult> OpenItemAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return navigator.NavigateAsync(
            new InventoryItemRoute(itemId),
            cancellationToken);
    }
}
```

Some applications instead inject a narrow app-owned interface such as
`IGlyphmereNavigator` and implement it with `IRouterNavigator` at composition
time. That can make a view model's allowed destinations more explicit. It is an
application architecture choice, not an AppNav requirement.

The MAUI project composes the generated route table, navigation model, page
module, and MAUI services. It therefore connects inner navigation decisions to
the outer renderer without requiring the inner project to reference
`AdamE.AppNav.Maui`.

## Test at the cheapest useful boundary

### 1. Test requested destinations

Use a mock, spy, or app-owned navigation gateway to verify that an action asks
for `InventoryItemRoute(itemId)`. This test checks the presentation logic's
decision; it does not need to construct a page or predict native stack
operations.

These tests are usually the smallest and fastest. They are valuable even when
Glyphmere will never have a second renderer.

### 2. Test the real navigation model

Route matching, transforms, policies, topology planning, logical Back, history,
and commit behavior are all in `AdamE.AppNav`. Tests can run the real router
against an in-memory `INavigationPresenter` that records each
`NavigationPlan`. Advanced composition uses `RouterNavigatorFactory` to join
the route table, planner, presenter, and optional initial state.

The test presenter is deliberately small: `ApplyAsync` records or inspects the
plan, and `ReconciliationRequested` can simulate a host-originated Back or
branch change. Because the presenter does not create native controls, the test
remains an ordinary `net10.0` test.

This level can verify, for example, that opening the Iron Sword selects
Inventory, preserves `Inventory -> Weapons -> Iron Sword`, keeps the World Map
branch unchanged, and restores the Inventory path after switching back.

### 3. Test the MAUI boundary selectively

Keep focused integration or UI tests for behavior that actually belongs to the
outer boundary:

- generated route-to-page mappings and dependency-injection scopes;
- native push, pop, branch selection, modal presentation, and rollback;
- lifecycle attachment, app-link dispatch, and UI-thread coordination;
- file-backed persistence and platform configuration.

These tests are more expensive because they may require MAUI initialization,
native target builds, a simulator, or a device. The inner boundary keeps them
from becoming the only way to validate navigation behavior.

## Platform work stays explicit

Render independence does not make platform work disappear. Core owns the
contracts and semantics; a host supplies implementations where the environment
matters. For example, `AdamE.AppNav` defines deferred-navigation serialization,
replay behavior, and the `IDeferredNavigationRequestStore` port, while
`AdamE.AppNav.Maui` supplies the file-backed implementation.

The same boundary applies to native presentation through
`INavigationPresenter`. Application code plans a logical destination; the host
adapter decides how to reflect that plan in its UI and reports native changes
back for reconciliation. See the advanced
[adapter contract](../advanced/adapter-contract.md) when implementing another
host. It is adapter material, not required MAUI onboarding.

## Next steps

- Define render-independent destinations in
  [Routing and metadata](../concepts/01-routing-and-metadata.md).
- Model branches and stacks in
  [Topology and planning](../concepts/02-topology-and-planning.md).
- Connect the inner model to native UI with [MAUI integration](02-maui-integration.md).
- Read the advanced [adapter contract](../advanced/adapter-contract.md) only if
  you are implementing another host.
