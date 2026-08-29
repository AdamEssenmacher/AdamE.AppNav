using DeviceRunners.UITesting;
using DeviceRunners.VisualRunners;
using Microsoft.Maui.Hosting;

namespace AdamE.AppNav.Maui.Tests;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .ConfigureUITesting()
            .UseVisualTestRunner(configuration => configuration
                .AddCliConfiguration()
                .AddConsoleResultChannel()
                .AddTestAssembly(typeof(MauiProgram).Assembly)
                .AddXunit());

        return builder.Build();
    }
}
