using AdamE.AppNav.Leases;
using AdamE.AppNav.State;
using global::Oahu;

namespace AdamE.AppNav.Oahu.Hosting;

/// <summary>Owns the route screen mounts and their lease attachments for one shell. Access on the host UI thread.</summary>
internal sealed class MountedNavigationSurface(RouteEntryLeaseRegistry leases, OahuScreenFactory mount)
{
    private readonly record struct MountKey(string Id, AppRoute Route);

    private sealed record ScreenMount(OahuMountedScreen Screen, RouteEntryLeaseAttachment Attachment)
    {
        public long Id => Screen.Mount.Id;

        public void Release()
        {
            // OAHU invokes its callback even when native close fails. Only a successful return
            // on a later UI turn certifies that close succeeded and the borrower is gone.
            var closeRan = false;
            Screen.Mount.Dispose(() => closeRan = true);
            if (!closeRan)
                throw new InvalidOperationException("Mount teardown must run outside an OAHU callback.");
            Screen.AfterTeardown?.Invoke();
            Attachment.Detach();
        }
    }

    private readonly Dictionary<MountKey, ScreenMount> mounts = new();

    private readonly StateSource<ShellTarget> target = new(new(0, "", false, [], "", [], new("initial", "", "", "", "", "")));

    public IOahuState<ShellTarget> Target => target;

    public void Stage(OahuNavigationTarget next, CancellationToken cancellationToken)
    {
        if (next.ModalOwner is { } owner && next.ModalEntries.All(entry => entry.Id != owner.Id))
            GetLease(owner, out _);
        var prepared = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in DisplayedEntries(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(entry.Id, GetOrMount(entry));
        }
        // A factory may retire its lease, or another entry's, while building screens. Resolve
        // mount IDs again before publishing so a prepared screen cannot spend revoked authority.
        var staged = ToWire(next, entry => ResolveVisibleMount(entry, prepared[entry.Id]));
        if (next.ModalOwner is { } modalOwner) RequireCurrentLease(modalOwner);
        cancellationToken.ThrowIfCancellationRequested();
        target.Set(staged);
    }

    public void Accept(OahuNavigationTarget accepted)
    {
        var current = target.Current;
        var currentMounts = ScreenEntries(current).ToDictionary(e => e.EntryId, e => e.Mount, StringComparer.Ordinal);
        ValidateAcceptedLeases(accepted, currentMounts);
        if (current.Revision == accepted.Revision)
        {
            // A same-revision change confirms a native Back or tab selection. Keep the exact mounts
            // already shown, including any placeholders used for retired entries.
            target.Set(ToWire(accepted, entry => currentMounts[entry.Id]));
            ReleaseExcept(ScreenEntries(target.Current).Select(entry => entry.Mount).ToHashSet());
        }
        else
        {
            var retained = DisplayedEntries(accepted)
                .Select(entry => new MountKey(entry.Id, entry.Route))
                .Where(mounts.ContainsKey).Select(key => mounts[key].Id).ToHashSet();
            retained.UnionWith(currentMounts.Values);
            ReleaseExcept(retained);
        }
        ValidateAcceptedLeases(accepted, currentMounts);
    }

    public void ReleaseAll() => ReleaseExcept([]);

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

    private long GetOrMount(RouteEntry entry)
    {
        var lease = GetLease(entry, out var opened);
        // A retired committed entry can still belong to the graph, but cannot acquire another attachment.
        if (lease.IsRetired) return 0;
        var key = new MountKey(entry.Id, entry.Route);
        if (mounts.TryGetValue(key, out var existing)) return existing.Id;
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
            mounts.Add(key, new(screen, attachment));
            return handle.Id;
        }
        catch
        {
            attachment.Detach();
            throw;
        }
    }

    private void ReleaseExcept(HashSet<long> retained)
    {
        List<Exception> failures = [];
        foreach (var id in mounts.Where(pair => !retained.Contains(pair.Value.Id)).Select(pair => pair.Key).ToArray())
        {
            var screen = mounts[id];
            mounts.Remove(id);
            try { screen.Release(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Navigation mount teardown failed. Affected lease attachments remain held.", failures);
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
