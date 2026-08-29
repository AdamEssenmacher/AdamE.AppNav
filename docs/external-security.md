# External navigation security

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
