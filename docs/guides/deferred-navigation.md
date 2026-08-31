# Deferred navigation

[Documentation home](../index.md)

Deferred persistence supports auth or policy flows that must replay a canonical destination after process restart.
It is not a raw deep-link cache.

## Registration

```csharp
services.AddAppNavFileDeferredNavigationRequests(options =>
{
    options.BaseUri = new Uri("https://example.com/");
    options.RouteStateRegistry = routeStateRegistry;
});
```

`BaseUri` is explicit, required, and absolute. The route-state registry determines which metadata is restorable.
The configuration callback is required, and invalid options fail immediately during service registration.

## Schema 3

Schema 3 persists only canonical route URI, source, disposition, timestamp, window ID, restorable metadata, and
provenance provider. It excludes raw request URI, original/referrer URI, correlation ID, arbitrary provenance
attributes, and cold-start state. Restore rematches the canonical route URI and returns a route-backed request.

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
- Diagnose reset and quarantine behavior with [Troubleshooting](troubleshooting.md).
