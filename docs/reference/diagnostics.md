# Logging, tracing, and diagnostics

[Documentation home](../index.md)

AppNav emits one structured diagnostic event stream to standard .NET logging,
`ActivitySource` tracing, and optional in-process observers. These are three
views of the same prepared events, with the same privacy mode and operation
correlation.

Diagnostics are observational. Do not use a log entry, activity event, or
observer callback to drive application behavior or infer that router state has
committed. Await `NavigateAsync`, `BackAsync`, or startup coordination and use
its return value as the operation outcome.

## Defaults and registration

`AddAppNav(...)` registers a singleton `NavigationDiagnostics`. When the app
has an `ILoggerFactory`, the MAUI composition root automatically creates the
logger category `AdamE.AppNav.Diagnostics`. The app still chooses and
configures its normal .NET logging providers and filters.

For example, this standard logging filter retains normal AppNav operation
events and all higher-severity events:

```csharp
builder.Logging.AddFilter(
    "AdamE.AppNav.Diagnostics",
    LogLevel.Information);
```

Set the category to `Debug` when investigating phase starts or detailed MAUI
page and handler lifecycles. A provider that filters the category above
`Information` will naturally omit normal completion events.

Safe data mode is the default whether or not `AddAppNavDiagnostics(...)` is
called. That method configures the shared diagnostics instance; it does not
install a logging provider. It can be registered before or after
`AddAppNav(...)`.

```csharp
builder.Services.AddAppNavDiagnostics(options =>
{
    options.DataMode = NavigationDiagnosticDataMode.Safe;
});
```

Advanced core-only composition can supply a `NavigationDiagnostics`, `ILogger`,
or `ILoggerFactory` through `RouterNavigatorFactoryOptions`.

## One event, three sinks

| Sink | Use | Registration and lifetime |
| --- | --- | --- |
| `ILogger` | Structured application logs and normal provider filtering | MAUI uses category `AdamE.AppNav.Diagnostics` when `ILoggerFactory` is available |
| `NavigationDiagnostics.EventWritten` or `INavigationDiagnosticObserver` | Tests, telemetry bridges, and app-specific troubleshooting tools | Callbacks are synchronous; `EventWritten` supports removal, while `AddObserver(...)` retains the observer for the diagnostics singleton's lifetime |
| `ActivitySource` | Distributed tracing and performance correlation | Listen to source `AdamE.AppNav`; router activities are named `Navigation.Navigate`, `Navigation.Back`, and `Navigation.Reconcile` |

Logger, tracing, event-handler, observer, and redactor failures are isolated
from navigation. A failed observer produces a `DiagnosticObserverFailed` event
for the remaining diagnostic sinks, without recursively calling the observer
that failed.

Callbacks should still do little synchronous work. Copy the fields needed by a
telemetry bridge and enqueue expensive processing elsewhere.

## Structured logging

Each logger entry uses the template:

```text
Navigation {Kind} ({Phase}) operation {OperationId}: {Message} {@Data}
```

Query the structured properties rather than parsing the rendered message.
`NavigationDiagnosticDataKeys` contains stable keys for route type and
template, request source and disposition, plan kind, duration, failure type,
retry attempts, and other event-specific data.

Default severity follows the event kind:

| Level | Typical events |
| --- | --- |
| `Debug` | Phase-start events; MAUI page creation/release and handler attachment/detachment |
| `Information` | Successful matching, transformation, policy, planning, presentation, startup, Back, reconciliation, and queue operations |
| `Warning` | Unmatched routes, unhandled logical Back, deferred-store quarantine, and deferred-store overflow |
| `Error` | Failed phases, top-level navigation failure, redirect loops, and diagnostic-observer failure |

The exception type can remain available in Safe mode. Exception messages are
not Safe data and should not be assumed present.

## Observe events in process

Resolve the shared `NavigationDiagnostics` when an app-specific tool needs the
event model directly. Prefer `EventWritten` when the subscription must be
removed:

```csharp
var diagnostics = services.GetRequiredService<NavigationDiagnostics>();

void RecordFailure(
    object? sender,
    NavigationDiagnosticEvent diagnosticEvent)
{
    if (diagnosticEvent.Severity >= LogLevel.Warning)
    {
        failureBuffer.Enqueue(diagnosticEvent);
    }
}

diagnostics.EventWritten += RecordFailure;
try
{
    // Run the bounded operation or diagnostic session.
}
finally
{
    diagnostics.EventWritten -= RecordFailure;
}
```

`AddObserver(...)` is intended for objects that should remain attached for the
complete diagnostics lifetime. DI registration of an
`INavigationDiagnosticObserver` alone does not attach it; resolve
`NavigationDiagnostics` and call `AddObserver(...)` from the owning composition
root.

## Event fields and correlation

Every `NavigationDiagnosticEvent` contains:

| Field | Meaning |
| --- | --- |
| `Kind` | The specific event, such as `RouteMatched`, `PlanningFailed`, or `ExternalNavigationRetrying` |
| `Phase` | The pipeline area: transformation, matching, policy, planning, presentation, startup, Back, reconciliation, app links, persistence, or diagnostics |
| `OperationId` | Router-generated correlation shared by events from one admitted navigation, Back, or reconciliation operation |
| `Severity` | The corresponding `LogLevel` |
| `Timestamp` | UTC event creation time |
| `Message` | Human-readable context; intentionally generic in Safe mode |
| `Data` | Structured values, preferably keyed by `NavigationDiagnosticDataKeys` |

`OperationId` is not `NavigationRequestProvenance.CorrelationId`. The former
correlates one router operation internally. The latter is optional context
owned by an external provider or app boundary and is removed in Safe mode.

Use `Kind`, `Phase`, and structured data as the stable diagnostic vocabulary.
Messages are useful to people but are not a parsing or control-flow contract.

## Tracing

Register `NavigationActivitySources.DefaultName` (`AdamE.AppNav`) with the
application's tracing provider. AppNav starts these activities when a listener
is present:

- `Navigation.Navigate`
- `Navigation.Back`
- `Navigation.Reconcile`

Activities include `navigation.operation_id`, source, disposition, and later
route, template, plan, or failure tags when applicable. Prepared diagnostic
events are also added to the current activity as `ActivityEvent` values with
`navigation.*` tags.

Safe-mode preparation occurs before data is mirrored into diagnostic activity
events. Configure tracing exporters with the same privacy expectations as log
providers. AppNav does not install or select a tracing exporter.

## Safe and Full data modes

Safe mode is designed for normal development, production telemetry, and issue
reports. It preserves enough structure to identify the failing phase without
emitting most application- or user-controlled values.

| Data | `Safe` | `Full` |
| --- | --- | --- |
| Event kind, phase, severity, operation ID, timestamp | Preserved | Preserved |
| Route type, route template, diagnostic code, plan kind, component types | Preserved when supplied | Preserved |
| Counts, durations, retry attempts, schema version, structural reasons | Preserved when supplied | Preserved |
| Absolute request URI | Reduced to scheme and server; credentials, path, query, and fragment removed | Raw producer value |
| Relative or invalid URI | Replaced with a structural marker | Raw producer value |
| Redirect targets and traces | Route values, URI details, and window IDs removed; source and disposition may remain | Raw producer value |
| Application navigation IDs and presentation mismatch values | Removed | Raw producer value |
| Provenance provider, URIs, correlation ID, cold-start flag, and attributes | Removed | Raw producer value |
| Exception and route-diagnostic messages | Removed | Raw producer value |
| Human-readable event message | Replaced by a generic event-kind message | Raw producer message |

Full mode is an explicit application choice for controlled local diagnosis:

```csharp
builder.Services.AddAppNavDiagnostics(options =>
{
    options.DataMode = NavigationDiagnosticDataMode.Full;
});
```

Full mode may expose paths, query values, route values, app-defined IDs,
provenance, and exception messages to every configured sink. Do not enable it
indiscriminately in production or attach its output to a public issue without
review.

## Application-specific redaction

Register one `INavigationDiagnosticRedactor` when the app must apply stricter
rules or selectively prepare Full-mode data for its telemetry environment:

```csharp
builder.Services.AddSingleton<
    INavigationDiagnosticRedactor,
    GlyphmereNavigationDiagnosticRedactor>();
```

In Safe mode the redactor receives the built-in sanitized event. In Full mode
it receives the raw producer event. The redactor's returned event is what all
sinks observe.

If the redactor throws, returns `null`, or returns an invalid event, AppNav
emits its built-in Safe representation. Redaction and telemetry failures never
change the navigation result.

## Troubleshoot one operation

Use this sequence when a request does not reach its expected destination:

1. Keep Safe mode enabled and capture `Information` or `Debug` events for the
   bounded reproduction.
2. Find the relevant entry event and group subsequent events by `OperationId`.
3. Locate the first warning or failure and its `Phase`.
4. Inspect stable structural keys: route type/template and diagnostic code for
   matching; transformer/policy type and redirect count for request handling;
   plan kind for planning; page type and failure type for presentation.
5. Correlate the events with the exception or result observed by the caller.
6. Include only reviewed Safe-mode events, AppNav version, target platform,
   expected destination/topology, and reproduction steps in an issue report.

Common phase interpretations:

| Last successful area | Check next |
| --- | --- |
| Request transformation | Replacement target validity and redirect trace |
| Route matching | Template, constraint, generated route module, and route diagnostic code |
| Request policy | Access decision, redirect target, and deferred-store behavior |
| Planning | Registered model/planner and the planned topology invariants |
| Presentation | MAUI page mapping, attached window, lifecycle hook, rollback, and consistency diagnostics |
| App-link ingress | Trusted origin, queue outcome, expiry, deduplication, and failure classification |
| Persistence | Schema, bounds, expiry, quarantine, lease, and acknowledgement |

External-ingress and persistence events are the bounded dead-letter signal.
There is no durable API that retains raw rejected requests for later inspection.

## Privacy checklist

- Keep Safe mode as the production default.
- Prefer stable structured fields over message parsing.
- Treat Full-mode output as potentially sensitive application data.
- Do not log raw external query strings, original or referrer URIs, provider
  correlation IDs, arbitrary provenance attributes, auth material, or
  credential-bearing configuration values.
- Apply additional redaction required by the application's domain and telemetry
  provider.
- Review diagnostic output before sharing it outside the development team.

## Next steps

- Check diagnostic terminology in the [Glossary](glossary.md).
- Interpret [navigation outcomes and failure handling](../guides/04-navigation-outcomes-and-failure-handling.md).
- Use the symptom-oriented [Troubleshooting guide](../guides/07-troubleshooting.md).
- Review [external navigation](../guides/05-external-navigation.md) diagnostics.
- Review [deferred navigation](../guides/06-deferred-navigation.md) persistence events.
- Interpret routes and provenance with
  [Requests and provenance](../concepts/03-requests-and-provenance.md).
