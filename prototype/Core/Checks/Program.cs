using System.Threading.Channels;
using AppNav.SwiftUI.Prototype;

var events = Channel.CreateUnbounded<SurfaceTarget>();
await using var router = new ProbeRouter((target, token) => events.Writer.WriteAsync(target, token).AsTask());

async Task<SurfaceTarget> Next() =>
    await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

Task Ack(SurfaceTarget surface) => router.ObserveAsync(surface.Revision, surface.Branch, surface.EntryIds);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var start = router.StartAsync();
var home = await Next();
Check(home.Transition.DestinationScreen == "home-detail", "Start lost its destination screen name");
Check(router.HistoryCount == 0 && router.CurrentEntryIds.Length == 0, "Start committed before native ACK");
await Ack(home);
await start;
var originalStatus = router.Status;
Check(router.HistoryCount == 1 && router.CurrentEntryIds.SequenceEqual(["home", "home-detail"]), originalStatus);

// A failed apply must fence the failed revision and wait for a verified rollback.
using (var cancel = new CancellationTokenSource())
{
    var canceled = router.DeepLinkAsync(cancel.Token);
    var obsolete = await Next();
    cancel.Cancel();
    var rollback = await Next();
    await Ack(obsolete);
    Check(router.Status == originalStatus && !canceled.IsCompleted, "Late ACK accepted or rollback not awaited");
    await Ack(rollback);
    try
    {
        await canceled;
        throw new Exception("Canceled deep link succeeded");
    }
    catch (OperationCanceledException) { }
    Check(router.Status == originalStatus, "Rollback changed state/history");
}

var navigate = router.DeepLinkAsync();
var detail = await Next();
await router.ObserveAsync(detail.Revision, "home", detail.EntryIds);
await router.ObserveAsync(detail.Revision, detail.Branch, ["catalog", "group", "item"]);
await Ack(home);
Check(router.Status == originalStatus && !navigate.IsCompleted, "Wrong branch, stale revision or partial frame acknowledged");
await Ack(detail);
await navigate;
Check(router.CurrentBranch == "catalog" && router.CurrentEntryIds.SequenceEqual(["catalog", "group", "item", "detail"]), router.Status);
Check(router.HistoryCount == 2 && router.HistorySummary.Contains("AppLink@catalog"), router.Status);
Console.WriteLine(router.Status);

var beforeCancel = router.Status;
await Ack(detail); // Native canceled swipe settles on the complete previously accepted stack.
await router.ObserveAsync(detail.Revision, detail.Branch, ["catalog", "detail"]); // Non-prefix.
Check(router.Status == beforeCancel, "Canceled or invalid gesture changed history");
var popped = router.ObserveAsync(detail.Revision, detail.Branch, ["catalog", "group"]);
await popped;
Check(router.CurrentEntryIds.SequenceEqual(["catalog", "group"]) && router.HistoryCount == 3, router.Status);
Check(router.EntriesIn("home").SequenceEqual(["home", "home-detail"]), "Lost inactive home path");
Check(router.HistorySummary == "InAppCommand@home[home,home-detail];AppLink@catalog[catalog,group,item,detail];HostReconciliation@catalog[catalog,group]", router.Status);
await router.ObserveAsync(home.Revision, "home", ["home"]);
await router.ObserveAsync(detail.Revision, detail.Branch, ["catalog", "group"]);
Check(router.HistoryCount == 3, "Duplicate or stale pop created history");
Console.WriteLine(router.Status);
// Two different completed pops, and a duplicate, can arrive before either router operation finishes.
var again = router.DeepLinkAsync();
var secondDetail = await Next();
await Ack(secondDetail);
await again;
var pop1 = router.ObserveAsync(secondDetail.Revision, secondDetail.Branch, ["catalog", "group", "item"]);
var duplicate = router.ObserveAsync(secondDetail.Revision, secondDetail.Branch, ["catalog", "group", "item"]);
var pop2 = router.ObserveAsync(secondDetail.Revision, secondDetail.Branch, ["catalog", "group"]);
await Task.WhenAll(pop1, duplicate, pop2);
Check(router.HistoryCount == 6 && router.HistorySummary.EndsWith(
    "HostReconciliation@catalog[catalog,group,item];HostReconciliation@catalog[catalog,group]"),
    "Back-to-back pops were reordered, duplicated or lost: " + router.Status);
Check(!events.Reader.TryRead(out _), "Replayed an obsolete surface during native reconciliation");
Console.WriteLine("PASS: AppNav fencing, rollback, deep link, canceled settlement, prefix reconciliation and inactive branch retention");
await PresenterChecks.RunAsync();
