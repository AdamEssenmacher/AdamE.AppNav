using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Commerce.Sample;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
#if DEBUG
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "appnav-commerce",
    DataHost = "shop")]
#endif
public class MainActivity : MauiAppCompatActivity
{
}
