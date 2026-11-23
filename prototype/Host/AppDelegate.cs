using AdamE.AppNav.Back;
using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using AdamE.AppNav.State;
using AdamE.AppNav.SwiftUI;
using AppNav.SwiftUI.Prototype;
using CoreFoundation;
using Foundation;
using Oahu.iOS;
using PresenterProbe.Shell;
using UIKit;

namespace PresenterProbe.Host;

[Register(nameof(AppDelegate))]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        OahuScreens.Initialize();
        return true;
    }

    public override UISceneConfiguration GetConfiguration(
        UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default", connectingSceneSession.Role) { DelegateType = typeof(SceneDelegate) };
}

[Register(nameof(SceneDelegate))]
public sealed class SceneDelegate : UIWindowSceneDelegate
{
    private CancellationTokenSource? sceneLifetime;
    private ShellViewModel? shell;
    private SwiftUINavigationHost? host;
    private ProbeRouter? router;
    private HomeBackPolicy? homeBack;
    private OahuNavigationPresenter? presenter;
    private RouteEntryLeaseRegistry? leases;
    private bool replaceDuringPush;
    private (long Epoch, long Revision)? replacedPresentation;
    private NativeObservation? heldReading;
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
            return;

        sceneLifetime = new CancellationTokenSource();
        var lifetime = sceneLifetime.Token;
        leases = new RouteEntryLeaseRegistry(new SwiftUIRouteEntryLeaseHost(OnFault, OnFault));
        presenter = ProbeRouter.CreatePresenter();
        homeBack = new HomeBackPolicy();
        // The split probe shows Home beside the selected game's own stack.
        var split = NSProcessInfo.ProcessInfo.Arguments.Contains("--probe-split");
        var currentRouter = new ProbeRouter(presenter, leases, [homeBack], split: split);
        router = currentRouter;
        currentRouter.Committed += OnCommit;

        IosPortHost.Shared.Faulted += OnFault;
        IosPortHost.Shared.MethodFailed += OnFault;
        Window = new UIWindow(windowScene);
        AttachHost(presenter, leases, Window);
        Window.MakeKeyAndVisible();
        DispatchQueue.MainQueue.DispatchAsync(() => Run(() => currentRouter.StartAsync(lifetime)));
    }

    private void AttachHost(OahuNavigationPresenter currentPresenter, RouteEntryLeaseRegistry currentLeases, UIWindow window)
    {
        host = SwiftUINavigationHost.Attach<IShellViewModel>(currentPresenter, currentLeases,
            (entry, _) => OahuScreens.Mount<IPageViewModel>(new PageViewModel(entry.Id)),
            navigation => shell = new ShellViewModel(navigation, OnNativeTrace, OnCommand, HoldNativeReading), OnFault);
        window.RootViewController = host.ViewController;
    }

    private void OnCommand(string command)
    {
        if (router is not { } current || sceneLifetime is null) return;
        var token = sceneLifetime.Token;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (token.IsCancellationRequested) return;
            switch (command)
            {
                case "open-game": Run(() => current.OpenGameAsync(token)); break;
                case "open-game-crossfade": Run(() => current.OpenGameAsync("game-crossfade", token)); break;
                case "open-fade-sheet": Run(() => current.OpenFadeSheetAsync(token)); break;
                case "replace-during-push":
                    if (heldReading is { } reading)
                    {
                        heldReading = null;
                        replacedPresentation = null;
                        Trace($"releasing actual replacement-host observation rev={reading.Revision}");
                        shell?.Forward(reading);
                    }
                    else
                    {
                        replaceDuringPush = true;
                        shell?.SetStatus($"{current.Status} replacement=armed");
                    }
                    break;
                case "deep-link": Run(() => current.DeepLinkAsync(token)); break;
                case "select-game-a": Run(() => current.SelectGameAsync("game-a", "split-cover-game-a", token)); break;
                case "select-game-b": Run(() => current.SelectGameAsync("game-b", "split-cover-game-b", token)); break;
                case "switch-game-a": Run(() => current.SelectGameAsync("game-a", "", token)); break;
                case "switch-game-b": Run(() => current.SelectGameAsync("game-b", "", token)); break;
                case "push-game-page": Run(() => current.PushGamePageAsync(token)); break;
                case "push-home-page": Run(() => current.PushHomePageAsync(token)); break;
                case "open-modal": Run(() => current.ModalAsync(token)); break;
                case "push-review": Run(() => current.PushReviewAsync(token)); break;
                case "allow-back": homeBack?.Allow(); break;
                case "request-back": Run(async () =>
                {
                    var result = await current.Navigator.BackAsync(cancellationToken: token);
                    await SwiftUINavigationHost.PostAsync(() =>
                    {
                        if (ReferenceEquals(router, current))
                            shell?.SetStatus($"{current.Status} back={result.Status}");
                    });
                }); break;
            }
        });
    }

    private void OnNativeTrace(string message)
    {
        Trace(message);
        // GameFrameView sampled a UIKit transition coordinator on an actual display-link tick.
        if (!replaceDuringPush || !message.StartsWith("game managed content frame=", StringComparison.Ordinal) ||
            !message.Contains("animated=true interactive=false", StringComparison.Ordinal))
            return;

        replaceDuringPush = false;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (Window is not { } window || host is not { } oldHost || shell is not { } oldShell ||
                presenter is not { } currentPresenter || leases is not { } currentLeases ||
                router is not { HistoryCount: 1 })
                return;

            var oldController = oldHost.ViewController;
            var revision = oldShell.Target.Current.Revision;
            replacedPresentation = (oldShell.Epoch, revision);
            oldHost.Detach();
            window.RootViewController = null;
            oldHost.Dispose();
            AttachHost(currentPresenter, currentLeases, window);
            if (ReferenceEquals(window.RootViewController, oldController))
                throw new InvalidOperationException("Expected a replacement hosting controller.");
            Trace($"replaced root controller during animated push epoch={oldShell.Epoch}->{shell!.Epoch} revision={revision}");
        });
    }

    private bool HoldNativeReading(ShellViewModel reportingShell, NativeObservation reading)
    {
        if (!ReferenceEquals(shell, reportingShell) || replacedPresentation is not { } previous ||
            reading.Entries.LastOrDefault() != "game")
            return false;

        if (heldReading is null)
        {
            heldReading = reading;
            var target = reportingShell.Target.Current;
            Run(async () =>
            {
                if (target.Transition.Operation != "reattach" || target.Transition.SourceKey.Length != 0 ||
                    reading.Revision != previous.Revision)
                    throw new InvalidOperationException("Expected the same pending revision with a source-free reattachment transition.");
                // Inject a matching late result with the old epoch while the actual replacement
                // host reading is held. The revision and stack alone cannot identify its owner.
                await presenter!.ObserveAsync(previous.Epoch, reading.Revision, reading.Branch, reading.Entries);
                await SwiftUINavigationHost.PostAsync(() =>
                {
                    if (ReferenceEquals(reportingShell, shell) && router is { } current)
                    {
                        var status = $"{current.Status} replacement=held epoch={previous.Epoch}->{reportingShell.Epoch}";
                        Trace($"after injected stale observation rev={reading.Revision} operation={target.Transition.Operation} {status}");
                        reportingShell.SetStatus(status);
                    }
                });
            });
        }
        return true;
    }

    private void OnCommit() => DispatchQueue.MainQueue.DispatchAsync(() =>
    {
        if (router is not { } current) return;
        Trace(current.Status);
        shell?.SetStatus(current.Status);
    });

    private async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) when (sceneLifetime is null || sceneLifetime.IsCancellationRequested) { }
        catch (Exception error) { OnFault(error); }
    }

    private static void Trace(string message) => Console.WriteLine($"PROBE {message}");

    private void OnFault(Exception error)
    {
        Trace($"FAULT {error}");
        DispatchQueue.MainQueue.DispatchAsync(() => shell?.SetStatus($"FAULT {error.Message}"));
    }

    public override void DidDisconnect(UIScene scene)
    {
        sceneLifetime?.Cancel();
        IosPortHost.Shared.Faulted -= OnFault;
        IosPortHost.Shared.MethodFailed -= OnFault;
        host?.Detach();
        var current = router;
        if (current is not null)
            current.Committed -= OnCommit;
        router = null;
        presenter = null;
        leases = null;
        heldReading = null;
        replacedPresentation = null;
        replaceDuringPush = false;
        Window?.RootViewController = null;
        Window = null;
        try { host?.Dispose(); }
        finally
        {
            if (current is not null) Run(() => current.DisposeAsync().AsTask());
            host = null;
            shell = null;
        }
        sceneLifetime?.Dispose();
        sceneLifetime = null;
    }

    private sealed class HomeBackPolicy : IBackNavigationPolicy
    {
        private volatile bool allowed;

        public void Allow() => allowed = true;

        public ValueTask<BackNavigationPolicyDecision> EvaluateAsync(
            BackNavigationPolicyContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var guarded = context.NavigationContext.Window is
                { Modals.Count: 0, Root: BranchHostNode { SelectedBranch.Content: StackNode { Top.Id: "home-detail" } } };
            return ValueTask.FromResult(guarded && !allowed
                ? BackNavigationPolicyDecision.Cancel : BackNavigationPolicyDecision.Continue);
        }
    }
}
