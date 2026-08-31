# AdamE.AppNav

Route-first navigation for .NET MAUI with a host-independent .NET core.

AppNav models typed semantic destinations, logical navigation topology,
request policy, history, and Back before the MAUI adapter changes native UI.
The same destination can be requested by in-app code, an app link, startup,
deferred replay, or a test without making a page or view-model type its
identity.

## Preview packages

| Package | Responsibility |
| --- | --- |
| `AdamE.AppNav` | Routes, matching, policy, planning, state, history, diagnostics, and orchestration |
| `AdamE.AppNav.Maui` | MAUI page mapping, native presentation, lifecycle ingress, startup, and file-backed deferred navigation |

`AdamE.AppNav` targets plain `net10.0`. The MAUI package supports Android 28+,
iOS 15+, and Mac Catalyst 15+ in this preview.

The preview does not support Windows MAUI, `Microsoft.Maui.Controls.Shell`,
Shell routing, Prism navigation, mixed ownership of one navigation surface,
true multi-window MAUI presentation, or a production Blazor or Avalonia
adapter.

## Core model

Application code requests a typed semantic route:

```csharp
await navigator.NavigateAsync(new InventoryItemRoute(itemId));
```

The route says where the user wants to go. It does not name a page, view model,
tab selection, or push operation. The application defines the logical topology
for that destination, and the MAUI adapter decides how the resulting plan is
rendered with native controls.

Navigation succeeds with a `NavigationResult`; failures generally throw.
Logical state and history commit only after presentation succeeds. An
unhandled `BackAsync` result is a normal signal that the host can apply its
root-level Back behavior.

## Documentation and source

- [Documentation home](https://github.com/AdamEssenmacher/AdamE.AppNav/blob/preview/docs/index.md)
- [Why AppNav?](https://github.com/AdamEssenmacher/AdamE.AppNav/blob/preview/docs/concepts/00-why-appnav.md)
- [Getting started](https://github.com/AdamEssenmacher/AdamE.AppNav/blob/preview/docs/guides/01-getting-started.md)
- [MAUI integration](https://github.com/AdamEssenmacher/AdamE.AppNav/blob/preview/docs/guides/02-maui-integration.md)
- [Navigation outcomes and failure handling](https://github.com/AdamEssenmacher/AdamE.AppNav/blob/preview/docs/guides/04-navigation-outcomes-and-failure-handling.md)
- [Logging, tracing, and diagnostics](https://github.com/AdamEssenmacher/AdamE.AppNav/blob/preview/docs/reference/diagnostics.md)
- [Buildable minimal sample](https://github.com/AdamEssenmacher/AdamE.AppNav/tree/preview/samples/GettingStarted.Sample)
- [Source repository](https://github.com/AdamEssenmacher/AdamE.AppNav)
- [MIT License](https://github.com/AdamEssenmacher/AdamE.AppNav/blob/preview/LICENSE)

Package installation commands will be documented when the selected public
publication workflow is ready. Preview packages are attached to the matching
GitHub prerelease as `.nupkg` and `.snupkg` files; this preview is not published
to NuGet.org.
