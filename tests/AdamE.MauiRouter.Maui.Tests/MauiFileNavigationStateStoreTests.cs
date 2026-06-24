using AdamE.MauiRouter.Maui.Persistence;
using AdamE.MauiRouter.Persistence;
using Microsoft.Maui.Storage;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiFileNavigationStateStoreTests
{
#if IOS
    private const string IosSkipReason =
        "The iOS simulator XHarness host validates presenter and transition behavior; file-store IO coverage runs on Mac Catalyst and Android.";
#endif

#if IOS
    [Fact(Skip = IosSkipReason)]
#else
    [Fact]
#endif
    public async Task SaveAsyncWritesSnapshotAtomicallyAndRemovesTemporaryFile()
    {
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "navigation-state.json");
        var store = new MauiFileNavigationStateStore(path);
        var snapshot = new NavigationSnapshot();

        try
        {
            await store.SaveAsync(snapshot);

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            var restored = await store.LoadAsync();
            Assert.NotNull(restored);
            Assert.Equal(NavigationSnapshot.CurrentSchemaVersion, restored.SchemaVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

#if IOS
    [Fact(Skip = IosSkipReason)]
#else
    [Fact]
#endif
    public async Task LoadAsyncThrowsForCorruptSnapshotJson()
    {
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "navigation-state.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json");
        var store = new MauiFileNavigationStateStore(path);

        try
        {
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync().AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateStoreDirectory()
    {
#if IOS || MACCATALYST || ANDROID
        var root = FileSystem.CacheDirectory;
#else
        var root = Path.GetTempPath();
#endif
        return Path.Combine(root, $"maui-router-store-{Guid.NewGuid():N}");
    }
}
