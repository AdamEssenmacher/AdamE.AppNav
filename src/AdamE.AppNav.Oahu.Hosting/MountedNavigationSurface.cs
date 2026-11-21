using AdamE.AppNav.Leases;
using AdamE.AppNav.State;
using global::Oahu;

namespace AdamE.AppNav.Oahu.Hosting;

/// <summary>Owns the route screen mounts and their lease attachments for one shell. Access on the host UI thread.</summary>
internal sealed class MountedNavigationSurface(RouteEntryLeaseRegistry leases, OahuScreenFactory mount, Action<Exception> reportFailure)
{
    private readonly record struct MountKey(RouteEntryLease Lease, AppRoute Route);

    private sealed record ScreenMount(OahuMountedScreen Screen, RouteEntryLeaseAttachment Attachment)
    {
        public long Id => Screen.Mount.Id;
        public long LastStagedRevision { get; set; }

        public bool NativeClosed { get; private set; }

        public void Release()
        {
            if (!NativeClosed)
            {
                // OAHU runs its callback even on failure. Only a synchronous successful
                // return certifies close. A second Dispose cannot certify the original teardown.
                var completed = false;
                Screen.Mount.Dispose(() => completed = true);
                if (!completed)
                    throw new InvalidOperationException("Mount teardown must run outside an OAHU callback.");
                NativeClosed = true;
            }

            Screen.AfterTeardown?.Invoke();
            Attachment.Detach();
        }
    }

    private readonly Dictionary<MountKey, ScreenMount> mounts = new();

    private readonly HashSet<ScreenMount> pendingReleases = new(ReferenceEqualityComparer.Instance);

    // Retain borrowers with uncertified native teardown for the lifetime of this host.
    // They cannot safely retry and have already reported their failure.
    private readonly List<ScreenMount> uncertifiedCloses = [];

    private readonly StateSource<ShellTarget> target = new(new(0, "", false, [], "", [], new("initial", "", "", "", "", "")));

    private long settledRevision;

    public IOahuState<ShellTarget> Target => target;

    public void Stage(OahuNavigationTarget next, CancellationToken cancellationToken)
    {
        if (next.ModalOwner is { } owner && next.ModalEntries.All(entry => entry.Id != owner.Id))
            GetLease(owner, out _);
        var prepared = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in DisplayedEntries(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(entry.Id, GetOrMount(entry, next.Revision));
        }
        // A factory may retire its lease, or another entry's, while building screens. Resolve
        // mount IDs again before publishing so a prepared screen cannot spend revoked authority.
        var staged = ToWire(next, entry => ResolveVisibleMount(entry, prepared[entry.Id]));
        if (next.ModalOwner is { } modalOwner) RequireCurrentLease(modalOwner);
        cancellationToken.ThrowIfCancellationRequested();
        target.Set(staged);
    }

    public void Accept(OahuNavigationTarget accepted, Func<Action, bool> applyIfCurrent)
    {
        var current = target.Current;
        var currentMounts = ScreenEntries(current).ToDictionary(e => e.EntryId, e => e.Mount, StringComparer.Ordinal);
        if (!applyIfCurrent(() =>
        {
            ValidateAcceptedLeases(accepted, currentMounts);
            if (current.Revision == accepted.Revision)
                target.Set(ToWire(accepted, entry => currentMounts[entry.Id]));
        }))
        {
            // A superseded gesture can still enter history, without publishing to the shell.
            ValidateAcceptedLeases(accepted, currentMounts);
        }
    }

    public void Settle(OahuNavigationTarget committed)
    {
        if (committed.Revision < settledRevision) return;
        settledRevision = committed.Revision;
        // Keep everything referenced by the wire and anything acquired by a later Show,
        // even when that Show failed before publishing and may still need to roll back.
        var retained = ScreenEntries(target.Current).Select(entry => entry.Mount).ToHashSet();
        foreach (var screen in mounts.Values)
            if (screen.LastStagedRevision > committed.Revision)
                retained.Add(screen.Id);
        foreach (var entry in DisplayedEntries(committed))
        {
            if (leases.TryGet(entry.Id, out var lease) && !lease.IsRetired &&
                mounts.TryGetValue(new MountKey(lease, entry.Route), out var screen))
                retained.Add(screen.Id);
        }

        if (ReleaseExcept(retained) is { } failure)
        {
            // The old transaction is over. A teardown reporter must not unwind this navigation.
            try { reportFailure(failure); }
            catch (Exception reportingFailure) { System.Diagnostics.Trace.TraceError("{0}", reportingFailure); }
        }
    }

    public void ReleaseAll()
    {
        if (ReleaseExcept([]) is { } failure)
            throw failure;
    }

    private RouteEntryLease GetLease(RouteEntry entry, out bool opened)
    {
        var lease = leases.GetOrOpen(entry.Id, out opened);
        ValidateLease(lease);
        return lease;
    }

    private RouteEntryLease RequireCurrentLease(RouteEntry entry)
    {
        if (!leases.TryGet(entry.Id, out var lease))
            throw new OperationCanceledException($"Route entry '{entry.Id}' lost its lease during presentation.");
        ValidateLease(lease);
        return lease;
    }

    private static void ValidateLease(RouteEntryLease lease)
    {
        if (lease.IsRetired && !lease.IsCommitted)
            throw new OperationCanceledException($"Route entry '{lease.EntryId}' retired during presentation.");
    }

    private long ResolveVisibleMount(RouteEntry entry, long prepared) =>
        RequireCurrentLease(entry).IsRetired ? 0 : prepared;

    private void ValidateAcceptedLeases(OahuNavigationTarget accepted, IReadOnlyDictionary<string, long> current)
    {
        foreach (var entry in DisplayedEntries(accepted))
        {
            var mountId = current.GetValueOrDefault(entry.Id);
            if (ResolveVisibleMount(entry, mountId) != mountId)
                throw new OperationCanceledException($"The mounted route entry '{entry.Id}' retired before acceptance.");
        }
        if (accepted.ModalOwner is { } owner) RequireCurrentLease(owner);
    }

    private long GetOrMount(RouteEntry entry, long revision)
    {
        var lease = GetLease(entry, out var opened);
        // A retired committed entry can still belong to the graph, but cannot acquire another attachment.
        if (lease.IsRetired) return 0;
        var key = new MountKey(lease, entry.Route);
        if (mounts.TryGetValue(key, out var existing))
        {
            existing.LastStagedRevision = revision;
            return existing.Id;
        }
        if (!lease.TryAttach(out var attachment))
        {
            if (lease.IsCommitted) return 0;
            throw new OperationCanceledException($"Route entry '{entry.Id}' retired before it was mounted.");
        }
        try
        {
            var screen = mount(entry, lease, opened);
            if (screen?.Mount is not { IsDisposed: false } handle || mounts.Values.Any(existing => existing.Id == handle.Id))
                throw new InvalidOperationException($"The screen factory must return a fresh mount for '{entry.Id}'.");
            mounts.Add(key, new(screen, attachment) { LastStagedRevision = revision });
            return handle.Id;
        }
        catch
        {
            attachment.Detach();
            throw;
        }
    }

    private AggregateException? ReleaseExcept(HashSet<long> retained)
    {
        foreach (var (key, screen) in mounts.ToArray())
        {
            if (retained.Contains(screen.Id)) continue;
            mounts.Remove(key);
            pendingReleases.Add(screen);
        }

        List<Exception> failures = [];
        foreach (var screen in pendingReleases.ToArray())
        {
            try
            {
                screen.Release();
                pendingReleases.Remove(screen);
            }
            catch (Exception failure)
            {
                if (!screen.NativeClosed)
                {
                    pendingReleases.Remove(screen);
                    uncertifiedCloses.Add(screen);
                }
                failures.Add(failure);
            }
        }
        return failures.Count == 0 ? null :
            new AggregateException("Navigation mount teardown failed. Affected lease attachments remain held.", failures);
    }

    private static IEnumerable<ScreenEntry> ScreenEntries(ShellTarget value) =>
        value.Branches.SelectMany(stack => stack.Entries).Concat(value.ModalEntries);

    private static IEnumerable<RouteEntry> DisplayedEntries(OahuNavigationTarget value) =>
        value.Branches.Values.SelectMany(stack => stack).Concat(value.ModalEntries);

    private static ShellTarget ToWire(OahuNavigationTarget value, Func<RouteEntry, long> mountId)
    {
        ScreenEntry Entry(RouteEntry entry) => new(entry.Id, mountId(entry));
        var context = value.Transition;
        return new(value.Revision, value.Branch, value.IsBranchHost,
            value.Branches.Select(branch => new BranchStack(
                branch.Key, value.BranchTitles[branch.Key], branch.Value.Select(Entry).ToArray())).ToArray(),
            value.ModalId ?? "", value.ModalEntries.Select(Entry).ToArray(),
            new(context.Operation, context.SourceEntryId, context.DestinationEntryId, context.SourceKey,
                context.SourceScreen, context.DestinationScreen));
    }
}
