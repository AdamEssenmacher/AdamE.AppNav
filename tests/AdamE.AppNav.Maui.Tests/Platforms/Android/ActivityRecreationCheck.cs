#if ANDROID
using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Android.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;

namespace AdamE.AppNav.Maui.Tests;

// A separate process keeps the test runner's window and activity out of the recreation.
[Activity(Name = "com.adame.appnav.maui.tests.RecreationActivity", Theme = "@style/Maui.SplashTheme",
    Process = ":recreation", Exported = true)]
public sealed class RecreationActivity : MauiAppCompatActivity;

internal sealed class RecreationCheckApp(IAppNavStartupService startup, IRouterNavigator router) :
    Microsoft.Maui.Controls.Application
{
    internal const string LogTag = "AppNavRecreation";
    private readonly TaskCompletionSource<Window> _replacement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;

    public static MauiApp Build()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<RecreationCheckApp>();
        builder.Services.AddAppNavStartup(options =>
        {
            options.AppLinkGracePeriod = TimeSpan.Zero;
            options.FallbackRouteFactory = (_, _) => ValueTask.FromResult<AppRoute?>(new Home());
        });
        builder.Services.AddAppNav<ModalPlanner>(
            RouteTable.Create(routes =>
            {
                routes.MapRoute<Home>("/home");
                routes.MapRoute<Editor>("/editor/{id}");
            }),
            pages =>
            {
                pages.MapPage<Home>((_, _) => new ContentPage());
                pages.MapPage<Editor>((_, route) => new ContentPage { Content = new Label { Text = route.Id } });
            });
        return builder.Build();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new ContentPage());
        window.Created += (_, _) =>
        {
            if (_started)
                _replacement.TrySetResult(window);
            else
            {
                _started = true;
                _ = RunAsync(window);
            }
        };
        startup.Start(window, "main");
        return window;
    }

    private async Task RunAsync(Window window)
    {
        var activity = Assert.IsType<RecreationActivity>(window.Handler!.PlatformView);
        var run = activity.Intent?.GetStringExtra("run");
        try
        {
            await WaitUntilAsync(() => window.Page is NavigationPage);
            await router.NavigateAsync(new Editor("draft"));
            var oldRoot = Assert.IsType<NavigationPage>(window.Page);
            var oldModal = Assert.Single(oldRoot.Navigation.ModalStack);
            await WaitUntilVisibleAsync(oldModal);
            Android.Util.Log.Info(LogTag, $"{run} Editor visible before Activity.Recreate");
            activity.Recreate();

            var replacement = await _replacement.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(activity.IsDestroyed);
            Assert.NotSame(activity, replacement.Handler!.PlatformView);
            await WaitUntilAsync(() => replacement.Page?.Navigation.ModalStack.Count == 1);
            Assert.Equal(2, router.History.Entries.Count);
            var root = Assert.IsType<NavigationPage>(replacement.Page);
            Assert.NotSame(oldRoot, root);
            var modal = Assert.Single(root.Navigation.ModalStack);
            Assert.NotSame(oldModal, modal);
            Assert.Equal("draft", Assert.IsType<Label>(Assert.IsType<ContentPage>(modal).Content).Text);
            Assert.Equal(new Editor("draft"), Assert.Single(router.CurrentState.ActiveWindow!.Modals).RouteEntry.Route);
            await WaitUntilVisibleAsync(modal);
            Android.Util.Log.Info(LogTag, $"{run} PASS recreated activity shows the restored editor on a fresh root");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(LogTag, $"{run} FAIL {ex}");
        }
    }

    private static Task WaitUntilVisibleAsync(Page page) =>
        WaitUntilAsync(() => page.Handler?.PlatformView is Android.Views.View { IsAttachedToWindow: true, IsShown: true });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(50, timeout.Token);
    }

    private sealed record Home : AppRoute;
    private sealed record Editor(string Id) : AppRoute;

    private sealed class ModalPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var root = new StackNode("stack", [new RouteEntry("home", new Home())]);
            ModalNode[] modals = context.Route is Editor editor
                ? [new ModalNode("editor", new RouteEntry(editor.Id, editor))]
                : [];
            return ValueTask.FromResult(new NavigationPlan(new NavigationState([new WindowNode("main", root, modals)], "main")));
        }
    }
}
#endif
