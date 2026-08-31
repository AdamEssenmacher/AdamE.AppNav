# Deferred navigation

[Documentation home](../index.md)

Deferred persistence supports auth or policy flows that must replay a canonical destination after process restart.
It is not a raw deep-link cache.

In the fictional RPG
[Glyphmere](../concepts/routing-and-metadata.md#meet-glyphmere), a cloud-save
notification might target `SaveSlotRoute(3)` while the player is signed out.
The auth flow can defer that navigation and replay it after the player signs in,
even if the process restarts in between.

## Registration

```csharp
services.AddAppNavFileDeferredNavigationRequests(options =>
{
    options.BaseUri = new Uri("https://links.glyphmere.example/");
    options.RouteStateRegistry = routeStateRegistry;
});
```

`BaseUri` is explicit, required, and absolute. The route-state registry determines which metadata is restorable.
The configuration callback is required, and invalid options fail immediately during service registration.

## Schema 3

Schema 3 persists only canonical route URI, source, disposition, timestamp, window ID, restorable metadata, and
provenance provider. It excludes raw request URI, original/referrer URI, correlation ID, arbitrary provenance
attributes, and cold-start state. Restore rematches the canonical route URI and returns a route-backed request.

For the Glyphmere example, the store persists the canonical
`https://links.glyphmere.example/pause/saves/3` destination and the safe fields
needed to replay it. It does not preserve the raw notification payload or its
arbitrary provider attributes.

The file store defaults to 32 requests, 64 KiB, and seven days. It prunes expired items and drops the oldest on count
or size overflow. Atomic temporary-file replacement protects the previous complete state from partial writes.

Schema-2 preview data is deleted on first load and emits a reset diagnostic. A future schema is quarantined byte-for-byte
rather than deleted, preserving downgrade safety. Corrupt and oversized files are quarantined. Failure to quarantine
preserves the original and fails safely.

Replay is lease-based and at-least-once. A request is removed only after durable acknowledgement. A crash between
presentation and acknowledgement can replay once more, but cannot lose the request. Terminal poison requests must not
starve later valid requests.

## Next steps

- Keep transport context separate with [requests and provenance](../concepts/requests-and-provenance.md).
- Configure Safe-mode [diagnostics](../reference/diagnostics.md).
- Diagnose reset and quarantine behavior with [Troubleshooting](05-troubleshooting.md).
