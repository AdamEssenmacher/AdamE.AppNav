using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace AdamE.MauiRouter.Maui.Tests;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<TestApp>();

#if ANDROID
        builder.Services.AddSingleton<HeadlessTestRunner>();
#endif

        return builder.Build();
    }
}
