using AdamE.AppNav.Back;
using AdamE.AppNav.Compose;
using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using Android.App;
using Android.OS;
using Android.Util;
using AndroidX.Activity;
using AppNav.SwiftUI.Prototype;
using Oahu;
using Oahu.Android;

namespace PresenterProbe.Android;

[Activity(Name = "com.adamessenmacher.appnav.probe.MainActivity", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : ComponentActivity
{
    private static Session? retained;
    private Session? session;
    private AndroidNavigationHost? host;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var fresh = retained is null;
        var current = retained ??= new Session();
        session = current;

        var attached = AndroidNavigationHost.Attach<IDemoShellViewModel>(this, current.Presenter, current.Leases,
            (entry, _) => OahuScreens.Mount<IPageViewModel>(new PageViewModel(entry.Id)),
            shell => new DemoShellViewModel(shell, current, Command, Observe), Fault);
        host = attached;
        SetContentView(attached.View);
        Trace($"attached epoch={attached.Epoch}");

        Run(fresh ? () => current.Router.StartAsync() : () => Refresh(current));
    }

    private void Observe(INavigationShellViewModel shell, NativeObservation observation)
    {
        if (host is not { } attached || session is not { } current)
            return;

        var epoch = attached.Epoch;
        var target = shell.Target.Current;
        if (target.Revision == observation.Revision && observation is { Branch: "home", Entries: [.., "game"] })
            Trace($"game surface epoch={epoch} rev={observation.Revision} operation={target.Transition.Operation} sourceKey='{target.Transition.SourceKey}'");
        Trace($"surface epoch={epoch} rev={observation.Revision} {observation.Branch} " +
            $"[{string.Join(",", observation.Entries)}] modal={observation.ModalId} " +
            $"[{string.Join(",", observation.ModalEntries)}]");
        if (current.Withheld is { } withheld && withheld.Epoch != epoch &&
            withheld.Observation.Revision == observation.Revision)
        {
            current.Withheld = null;
            Run(() => ReplayWithheld(current, withheld, shell, observation));
        }
        else if (current.RecreateOnLink && observation is { Branch: "catalog", Entries: [.., "detail"] })
        {
            // Preserve the actual observation from the outgoing activity. The replacement is already
            // showing this revision when the probe replays the delayed callback.
            current.RecreateOnLink = false;
            current.Withheld = (epoch, observation);
            Trace($"withheld epoch={epoch} rev={observation.Revision} history={current.Router.HistoryCount}");
            Run(() => AndroidNavigationHost.DispatchAsync(Recreate));
        }
        else
        {
            shell.Report(observation);
        }
    }

    private static async Task ReplayWithheld(Session current, (long Epoch, NativeObservation Observation) withheld,
        INavigationShellViewModel replacement, NativeObservation replacementObservation)
    {
        var history = current.Router.HistoryCount;
        var observation = withheld.Observation;
        await current.Presenter.ObserveAsync(withheld.Epoch, observation.Revision, observation.Branch,
            observation.Entries, observation.ModalId, observation.ModalEntries);
        // Keep the legitimate callback paused while any continuations scheduled by the old one would run.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        if (current.Router.HistoryCount != history)
            throw new InvalidOperationException("The old activity committed after it was detached.");
        Trace($"stale ignored epoch={withheld.Epoch} rev={observation.Revision} history={history}");
        replacement.Report(replacementObservation);
    }

    private void Command(string command)
    {
        if (session is not { } current)
            return;

        switch (command)
        {
            case "recreate-link":
                current.RecreateOnLink = true;
                goto case "deep-link";
            case "deep-link":
                Run(() => current.Router.DeepLinkAsync());
                break;
            case "recreate-game":
                current.RecreateOnGamePush = true;
                goto case "open-game";
            case "open-game":
                Run(() => current.Router.OpenGameAsync());
                break;
            case "game-push-in-progress" when current.RecreateOnGamePush && host is { } active:
                current.RecreateOnGamePush = false;
                Trace($"game push interrupted epoch={active.Epoch} history={current.Router.HistoryCount}");
                Recreate();
                break;
            case "open-modal":
                Run(() => current.Router.ModalAsync());
                break;
            case "deny-next-back":
                current.Policy.DenyNext();
                break;
            case "step-policy-back":
                current.StepPolicyBack();
                break;
            case "drop-next-back-completion":
                current.DropNextBackCompletion();
                break;
            case "recreate-activity":
                Recreate();
                break;
        }
    }

    private static async Task Refresh(Session current) => await current.Router.Navigator.RefreshPresentationAsync();

    protected override void OnDestroy()
    {
        if (host is { } attached)
        {
            var epoch = attached.Epoch;
            attached.Dispose();
            Trace($"detached epoch={epoch}");
        }
        host = null;
        if (IsFinishing && session is { } current)
        {
            retained = null;
            current.Dispose();
        }
        session = null;
        base.OnDestroy();
    }

    private static async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { Fault(error); }
    }

    private static void Fault(Exception error) => Trace($"FAULT {error}");
    private static void Trace(string message) => Log.Info("AppNavProbe", message);

    private sealed class Session
    {
        private readonly SemaphoreSlim policySteps = new(0);
        private volatile bool stepPolicyBack;
        private int dropNextBackCompletion;
        private long activeBackRequest;

        public Session()
        {
            Policy = new DenyNextBackPolicy(token => stepPolicyBack ? policySteps.WaitAsync(token) : Task.CompletedTask);
            Router = new ProbeRouter(Presenter, Leases, [Policy]);
            Router.Committed += OnCommit;
        }

        public OahuNavigationPresenter Presenter { get; } = ProbeRouter.CreatePresenter();
        public ProbeRouter Router { get; }
        public RouteEntryLeaseRegistry Leases { get; } = new(AndroidNavigationHost.CreateLeaseHost(Fault));
        public DenyNextBackPolicy Policy { get; }
        public StateSource<string> Status { get; } = new("Starting");
        public bool RecreateOnLink { get; set; }
        public bool RecreateOnGamePush { get; set; }
        public (long Epoch, NativeObservation Observation)? Withheld { get; set; }

        public void StepPolicyBack()
        {
            if (stepPolicyBack)
                policySteps.Release();
            else
                stepPolicyBack = true;
        }

        public void DropNextBackCompletion()
        {
            Interlocked.Exchange(ref dropNextBackCompletion, 1);
            Trace("policy back completion drop armed");
        }

        public async Task RequestBackAsync(long request, Action<long> complete, Action<long> reject)
        {
            var pending = Interlocked.CompareExchange(ref activeBackRequest, request, 0);
            if (pending == request)
                return;
            if (pending != 0)
            {
                await AndroidNavigationHost.DispatchAsync(() =>
                {
                    reject(request);
                    Trace($"policy back rejected id={request}");
                });
                return;
            }

            try
            {
                await EvaluateBackAsync(request, complete);
            }
            finally
            {
                Interlocked.CompareExchange(ref activeBackRequest, 0, request);
            }
        }

        private async Task EvaluateBackAsync(long request, Action<long> complete)
        {
            if (Interlocked.Exchange(ref dropNextBackCompletion, 0) == 1)
            {
                Trace($"policy back completion dropped id={request}");
                return;
            }

            try
            {
                Trace($"policy back requested id={request} history={Router.HistoryCount}");
                var result = await Router.Navigator.BackAsync(new BackNavigationRequest("main", BackNavigationSource.Host));
                Trace($"policy back {result.Status} history={Router.HistoryCount}");
                if (result.NavigationResult is { Presented: true })
                {
                    OnCommit();
                    // An accepted projection can appear before the caller finishes its work.
                    if (stepPolicyBack)
                        await policySteps.WaitAsync();
                }
            }
            finally
            {
                await AndroidNavigationHost.DispatchAsync(() =>
                {
                    try
                    {
                        complete(request);
                        Trace($"policy back finished id={request}");
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref activeBackRequest, 0, request);
                    }
                });
            }
        }

        private void OnCommit()
        {
            var value = Router.Status;
            Run(() => AndroidNavigationHost.DispatchAsync(() =>
            {
                Trace(value);
                Status.Set(value);
            }));
        }

        public void Dispose()
        {
            Router.Committed -= OnCommit;
            Run(() => Router.DisposeAsync().AsTask());
        }
    }

    private sealed class DenyNextBackPolicy(Func<CancellationToken, Task> wait) : IBackNavigationPolicy
    {
        private int denyNext;

        public void DenyNext() => Interlocked.Exchange(ref denyNext, 1);

        public async ValueTask<BackNavigationPolicyDecision> EvaluateAsync(
            BackNavigationPolicyContext context, CancellationToken cancellationToken = default)
        {
            Trace("policy back evaluating");
            await wait(cancellationToken);
            return Interlocked.Exchange(ref denyNext, 0) == 1
                ? BackNavigationPolicyDecision.Cancel
                : BackNavigationPolicyDecision.Continue;
        }
    }

    private sealed class DemoShellViewModel(INavigationShellViewModel shell, Session session,
        Action<string> command, Action<INavigationShellViewModel, NativeObservation> observe) : IDemoShellViewModel
    {
        public IOahuState<ShellTarget> Target => shell.Target;
        public IOahuState<string> Status => session.Status;
        private readonly StateSource<long> completedBack = new(0);
        private readonly StateSource<long> rejectedBack = new(0);

        public IOahuState<long> CompletedBack => completedBack;
        public IOahuState<long> RejectedBack => rejectedBack;
        public Task RequestBack(long request) => session.RequestBackAsync(request, completedBack.Set, rejectedBack.Set);
        public void Report(NativeObservation observation) => observe(shell, observation);
        public void RunCommand(string value) => Run(() => AndroidNavigationHost.DispatchAsync(() => command(value)));
        public void RecordTrace(string message) => Trace(message);
    }

    private sealed class PageViewModel(string entryId) : IPageViewModel
    {
        public IOahuState<string> Title { get; } = new StateSource<string>(entryId);
    }
}
