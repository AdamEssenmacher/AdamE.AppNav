#if MACCATALYST
using UIKit;

namespace AdamE.MauiRouter.Maui.Tests;

public static class Program
{
    public static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
#endif
