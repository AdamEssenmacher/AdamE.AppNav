# OAHU presenter integration checks

The shared presenter maps AppNav plans onto one window with a primary stack or retained branches and an optional modal stack. It waits for a matching native observation before acknowledging a transaction. Native pops, branch selections and modal dismissals reconcile against the observed base. The hosting layer attaches route-entry leases to OAHU mounts. It keeps outgoing mounts alive until AppNav commits its state and history, and reuses those mounts if the presentation rolls back. If owner cleanup throws, the host reports it and retains the lease attachment for a later cleanup attempt.

Build from this directory to select the .NET 11 SDK used by OAHU hosting. The repository root continues to select .NET 10 for the existing solution. AppNav core builds from this checkout. Set `OAHU_FEED` to a NuGet source containing OAHU preview `0.1.0-preview.g7a56b3ba`. Public dependencies come from NuGet.org.

```sh
cd prototype
dotnet restore AppNav.Oahu.slnx --source "$OAHU_FEED" --source https://api.nuget.org/v3/index.json
dotnet build AppNav.Oahu.slnx -c Release --no-restore
dotnet run --project Core/Checks -c Release --no-build
dotnet run --project ../tests/Hosting.Checks -c Release --no-build
```

The transaction checks drive the presenter through its public APIs, including deep links, native reconciliation, cancellation, stale observations, rollback and reattachment. The hosting checks exercise mount lifetime and release through OAHU's test host. Retained-router recreation preserves navigation history within the process. Apps own persistence across process death.

Native acceptance validates and publishes the target while leaving mounts available for rollback. AppNav then settles the presentation after committing state, history, and leases. The host schedules outgoing mount release on its UI thread. Navigation can return and later operations can begin while that cleanup is queued. Cleanup failures are reported separately. If app cleanup throws after a confirmed native close, it must be safe to resume on a later release attempt. OAHU can invoke its disposal callback even when native teardown fails. When successful native close cannot be established the host keeps the lease held, and router shutdown may remain pending.
