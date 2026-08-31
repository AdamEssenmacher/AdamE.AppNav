# Topology and planning

[Documentation home](../index.md)

AppNav plans a logical tree before an adapter mutates UI.

## Supported v1 shapes

- `WindowNode` owns one root and zero or more modals.
- `StackNode` owns ordered `RouteEntry` values.
- `BranchHostNode` owns independent branches and one selected branch.
- `ModalNode` owns a modal route and optional nested topology.

`NavigationNode` is externally non-derivable. Unknown shapes are rejected before presentation.
A non-null `ActiveWindowId` must identify an existing window.

## Standard models

`StackNavigationModel<TRoute>` declares a canonical stack and contextual push/replace behavior.
`BranchHostNavigationModel<TRoute>` additionally declares branches, sanitized inactive roots, and independent stacks.
Both implement `INavigationModel<TRoute>`.

`NavigationModelPlanner<TRoute>` applies standard dispositions:

| Disposition | Behavior |
| --- | --- |
| `Auto` | Contextual push for in-app/test requests; canonical for external sources |
| `Contextual` | Contextual push, then canonical fallback |
| `ReplaceCurrent` | Contextual replace-top, then canonical fallback |
| `Canonical` | Declared canonical topology |

Use `IAppNavigationPlanner` directly when an app coordinates multiple models or has domain-specific topology rules.

## Back and reconciliation

Logical Back first handles modal content, then modal dismissal, stack pop, and configured branch fallback.
Native host changes are reconciled with host-neutral sources:

- `HostBack` for completed native stack/back gestures;
- `BranchChanged` for native branch selection;
- `HostReconciliation` for the synthesized router request recorded in history.

Cancelled native gestures do not commit logical state.

## MAUI window rule

Core retains multi-window types for future adapters. The v1 MAUI presenter rejects plans containing multiple windows.
Unattached startup presentation is allowed. Once attached, the plan window ID must match the attached MAUI window ID.
Preflight validation occurs before page creation or native mutation.

## Next steps

- Apply the model in [MAUI integration](../guides/maui-integration.md).
- Choose the right envelope with [requests and provenance](requests-and-provenance.md).
- Read the [adapter contract](../advanced/adapter-contract.md) before implementing another host.
