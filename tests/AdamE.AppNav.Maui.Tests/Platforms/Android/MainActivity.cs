#if ANDROID
using Android.App;
using Android.Content.PM;
using Microsoft.Maui;

namespace AdamE.AppNav.Maui.Tests;

[Activity(
    Name = "com.adame.appnav.maui.tests.MainActivity",
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize |
                           ConfigChanges.Orientation |
                           ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize |
                           ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
}
#endif
