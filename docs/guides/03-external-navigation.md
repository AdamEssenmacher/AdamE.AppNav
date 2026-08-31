# External navigation

[Documentation home](../index.md)

External navigation is a security boundary. Enable it only after the
application has defined the production origins it owns and intends to accept.

Enable MAUI lifecycle ingress only with `UseAppNavExternalNavigation` and at least one trusted origin.

```csharp
builder.UseAppNavExternalNavigation(options =>
{
    options.AllowOrigin(new Uri("https://example.com"));
    options.AllowOrigin(new Uri("myapp://open"));
});
```

## Origin rules

Configured origins must be absolute root origins with a scheme and host. Credentials, non-root paths, query strings,
and fragments are rejected. Comparison uses normalized scheme, IDN host, and effective port. Default and explicit ports
therefore compare consistently, while a wrong port fails closed.

Before route matching, persistence, analytics, or `ShouldDispatch`, AppNav rejects relative, oversized,
credential-bearing, wrong-port, and untrusted URIs. An empty allowlist prevents enablement.

## Queue and retry policy

`MauiExternalNavigationOptions` defaults to:

- `MaximumUriLength = 2048`
- `MaximumPendingRequests = 32`
- `MaximumDispatchAttempts = 3`
- `RetryDelay = 250ms`
- `MaximumRequestAge = 5 minutes`

Both bootstrap and runtime queues drop the oldest item on overflow. Retryable failures move to the tail with a
next-attempt timestamp, allowing later intent to proceed. Lifecycle cancellation preserves a request without consuming
an attempt. Known routing, configuration, argument, and consistency failures drop immediately; unknown failures retry.

Override `ClassifyFailure` to return `Retry` or `Drop`. A classifier that throws fails closed to `Drop`.

## App-owned providers

Branch, push, QR, and provider SDK integrations create a complete `RouterNavigationRequest`, attach provider provenance,
then call `IMauiExternalNavigationDispatcher.TryDispatch`. Its boolean result reports whether a new request was accepted.
Rejected, expired, duplicate, disposed, and null requests return `false`.

Diagnostics report structural rejection, retry, expiry, overflow, deduplication, and terminal-drop events without raw
query strings or provenance values.

## Platform ownership

`UseAppNavExternalNavigation` validates and dispatches URIs delivered to the
MAUI host. It does not prove domain ownership or configure operating-system
association on the application's behalf.

For production HTTPS links, the application still owns:

- Android intent filters and the hosted Digital Asset Links association;
- Apple Associated Domains entitlements and the hosted site association;
- signing identities, package or bundle identifiers, and deployed-domain verification;
- cold- and warm-start testing on every supported platform.

The [Commerce sample](../../samples/Commerce.Sample/README.md) registers a
Debug-only custom scheme for runnable local checks. Its HTTPS origins illustrate
AppNav trust configuration, but become production links only after the app owns
the domains and completes the platform association work. Release builds neither
register nor trust the sample custom scheme.

## Boundary checklist

1. Register the platform link or provider integration.
2. Configure only root origins in AppNav.
3. Convert app-owned provider output into one complete
   `RouterNavigationRequest` with explicit source and provenance.
4. Use `TryDispatch` for fire-and-forget host ingress, or direct navigator
   navigation when interactive UI must observe failure.
5. Test trusted, wrong-host, wrong-port, credential-bearing, oversized,
   duplicate, expired, and retryable requests.
6. Confirm diagnostics remain structural in Safe mode.

## Next steps

- Define field ownership with [requests and provenance](../concepts/requests-and-provenance.md).
- Add durable auth recovery with [deferred navigation](04-deferred-navigation.md).
- Diagnose rejection with [Troubleshooting](05-troubleshooting.md).
