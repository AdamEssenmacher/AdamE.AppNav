#if IOS || MACCATALYST
using Microsoft.Maui.Storage;

namespace AdamE.AppNav.Maui.Tests;

internal static class AppleTestDiagnostics
{
    public const string ResultFileName = "AdamE.AppNav.Maui.Tests.xml";

    public const string LogFileName = "AdamE.AppNav.Maui.Tests.runner.log";

    public static string DocumentsDirectory
    {
        get
        {
            var directory = FileSystem.AppDataDirectory;
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string ResultPath => Path.Combine(DocumentsDirectory, ResultFileName);

    public static string LogPath => Path.Combine(DocumentsDirectory, LogFileName);

    public static void Reset()
    {
        DeleteIfExists(ResultPath);
        DeleteIfExists(LogPath);
    }

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}";
        Console.Write(line);
        File.AppendAllText(LogPath, line);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
#endif
