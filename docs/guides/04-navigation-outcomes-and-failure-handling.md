# Navigation outcomes and failure handling

[Documentation home](../index.md)

Navigation is an operation with an observable result, not a command whose
success should be inferred from logging or from the next rendered frame.
Interactive callers should await AppNav and handle the operation at the
boundary that can recover the initiating UI.

This guide continues the fictional RPG
[Glyphmere](../concepts/01-routing-and-metadata.md#meet-glyphmere). Imagine an
Inventory action that opens an item while keeping the pause menu responsive if
the operation is cancelled or cannot be presented.

## Successful navigation returns a result

`NavigateAsync` returns `NavigationResult` only after AppNav has accepted the
final route, produced a valid plan, completed presentation, and committed the
new logical state and history.

```csharp
NavigationResult result = await navigator.NavigateAsync(
    new InventoryItemRoute(itemId),
    cancellationToken);

AppRoute presentedRoute = result.Route;
NavigationPlan appliedPlan = result.Plan;
NavigationState committedState = result.State;
```

The result describes what actually completed:

| Member | Meaning |
| --- | --- |
| `Route` | The route presented at the top of the selected window in the committed target state |
| `Plan` | The logical plan AppNav applied to produce that state |
| `State` | The committed router state after the operation |
| `Presented` | Whether this result represents presenter-driven navigation |

`NavigationResult` is not a success/failure union. If `NavigateAsync` returns
one, the operation succeeded. A failure is reported by an exception.

`Route` normally matches the destination accepted after matching and policy,
but target-state presentation is authoritative. For example, if a planner
updates an underlying stack while retaining an existing modal, `Route` is the
modal route that remains visibly presented. When the target state has no
presented route, AppNav falls back to the resolved request route. Do not use
`result.Route` as a record of the original request target.

Do not treat `Presented == false` as failure. `ReconcileAsync` returns a
successful result with `Presented == false` because it records a state change
originating from the host, such as a native Back action or branch change,
rather than a new presenter-driven navigation request.

## Back can be unhandled without failing

`BackAsync` has one additional normal outcome: no AppNav host can go back.

```csharp
BackNavigationResult back = await navigator.BackAsync(
    windowId: "main",
    cancellationToken: cancellationToken);

if (!back.Handled)
{
    // Let the host close the pause menu or apply its normal root-level Back behavior.
    return;
}

NavigationResult completed = back.HandledNavigationResult!;
```

`Handled == false` is not an error and has no nested navigation result. When
`Handled == true`, `HandledNavigationResult` describes the presented and
committed Back plan.

For Glyphmere, Back from an inventory item may return to Weapons. Back at the
root of the pause-menu model may be unhandled so the MAUI host can close the
pause surface.

## Failures are exceptions

Await `NavigateAsync`, `BackAsync`, and `ReconcileAsync` when the caller must
react to failure. Common categories include:

| Failure | What it usually means | Caller response |
| --- | --- | --- |
| `RouteNotMatchedException` | A URI did not match a registered template or constraint | Treat an external URI as invalid; fix app-authored routes or registrations |
| `RouteRedirectLoopException` | Transforms or policies repeated a target or exceeded the redirect limit | Fix the redirect chain; inspect `Redirects` in trusted diagnostics |
| `RoutePlannerNotFoundException` | No planner can handle the resolved route type | Fix application registration or topology |
| `AppNavigationConfigurationException` | Deterministic topology or configuration is invalid | Fail the affected flow and correct configuration; retrying the same request cannot help |
| `OperationCanceledException` | The caller, lifecycle, or navigator shutdown cancelled the operation | Restore transient caller UI and preserve cancellation semantics |
| Presenter, transformer, or policy exception | App-owned or adapter work failed | Handle only where the application has a specific recovery; otherwise let the owning boundary report it |
| `MauiPresentationConsistencyException` | Presentation, rollback, and verified full recovery all failed | Stop using that presenter and recreate the AppNav host/runtime |

Argument errors, use after disposal, and same-navigator reentrancy are also
programming errors rather than alternate navigation outcomes. Avoid a blanket
catch that silently converts them into "nothing happened."

Policies deserve one distinction: `AccessGateNavigationPolicy` expresses a
denial by redirecting, optionally after deferring the original request. A
successful redirect therefore returns a normal `NavigationResult` whose
`Route` is the final redirected destination. A policy exception is a failure.

## State commits after presentation

The core pipeline is transactional at its logical boundary:

```text
resolve -> plan -> present -> commit state and history -> return result
                         \
                          failure: no logical commit
```

If matching, redirect evaluation, planning, presentation, or cancellation
fails before commit, AppNav does not publish the target state or push its
history entry. The MAUI presenter also attempts to roll back partial native
work. If rollback fails, it attempts verified full-state recovery. Only when
presentation, rollback, and recovery all fail does it throw
`MauiPresentationConsistencyException` and fault closed.

This guarantee does not make arbitrary application side effects transactional.
A policy, page constructor, lifecycle hook, or app-owned presenter component
that writes a database or calls a remote service still owns compensation for
that work.

## Recover at the initiating UI boundary

An interactive Glyphmere inventory action can disable itself while navigation
is in flight, restore that transient UI state in `finally`, and distinguish
cancellation from a failure the player can act on:

```csharp
public async Task OpenItemAsync(Guid itemId, CancellationToken cancellationToken)
{
    IsOpeningItem = true;

    try
    {
        await navigator.NavigateAsync(
            new InventoryItemRoute(itemId),
            cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // The initiating surface is going away; no error message is required.
        throw;
    }
    catch (RoutePlannerNotFoundException exception)
    {
        // The typed route needs a planner. Report the configuration defect safely.
        navigationErrors.ReportCannotOpenItem(itemId, exception);
    }
    finally
    {
        IsOpeningItem = false;
    }
}
```

The example catches only a failure for which that boundary has an intentional
response. Whether an application reports and rethrows, shows a message, or
falls back is an app decision. Do not log route values or exception data
without applying the application's data-handling policy.

## Host boundaries have different outcomes

Not every navigation entry point is an interactive call:

- `IMauiExternalNavigationDispatcher.TryDispatch` reports whether the request
  was accepted into the dispatcher, not whether navigation later completed.
  The dispatcher classifies execution failures as retry or drop and records
  diagnostics.
- `IAppNavStartupService.StartAsync` returns `AppNavStartupResult`. Its
  `Outcome` distinguishes app-link navigation, fallback navigation, no
  navigation, and failure; `Exception` contains a captured startup failure.
  Caller-requested cancellation still throws `OperationCanceledException`.
- Deferred replay returns `DeferredNavigationReplayResult` with attempted,
  replayed, and failed counts. It acknowledges a leased request only after
  navigation succeeds; a failed request remains unacknowledged for a later
  replay, while processing continues so it does not starve later requests.
  Caller-requested cancellation still propagates.
- The fire-and-forget `IAppNavStartupService.Start` overload observes and
  diagnoses its own failure. Use `StartAsync` when application startup must
  make a decision from the outcome.

Choose the API whose completion contract matches the boundary. Do not use a
queue-acceptance boolean as proof that the player reached the destination.

## Diagnostics support outcomes; they do not define them

Logging, tracing, and diagnostic events explain which phase failed and share
an operation ID. They are observational: observer, logger, tracing, and
redaction failures are isolated from navigation. The authoritative outcome is
the awaited result, an unhandled Back result, a boundary-specific result, or a
thrown exception.

Use [Logging, tracing, and diagnostics](../reference/diagnostics.md) to capture
the evidence needed to diagnose an exception, and
[Troubleshooting](07-troubleshooting.md) for symptom-oriented recovery.

## Next steps

- Understand the request pipeline in
  [Requests and provenance](../concepts/03-requests-and-provenance.md).
- Configure host ingress with [External navigation](05-external-navigation.md).
- Review [Deferred navigation](06-deferred-navigation.md) for replay outcomes.
- Diagnose failures with [Troubleshooting](07-troubleshooting.md) and
  [Logging, tracing, and diagnostics](../reference/diagnostics.md).
