using Microsoft.DotNet.XHarness.TestRunners.Common;

namespace AdamE.AppNav.Maui.Tests;

internal sealed class PlatformTestDevice : IDevice
{
    public string BundleIdentifier => "com.adame.appnav.maui.tests";

    public string UniqueIdentifier => Environment.MachineName;

    public string Name => Environment.MachineName;

    public string Model => "local";

    public string SystemName => OperatingSystem.IsAndroid()
        ? "Android"
        : OperatingSystem.IsIOS()
            ? "iOS"
            : OperatingSystem.IsMacCatalyst()
                ? "Mac Catalyst"
                : Environment.OSVersion.Platform.ToString();

    public string SystemVersion => Environment.OSVersion.VersionString;

    public string Locale => System.Globalization.CultureInfo.CurrentCulture.Name;
}
