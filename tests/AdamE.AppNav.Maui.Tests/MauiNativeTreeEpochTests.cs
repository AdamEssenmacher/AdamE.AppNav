using AdamE.AppNav.Maui;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiNativeTreeEpochTests
{
    [UIFact]
    public async Task CloseCancelsAndSnapshotsAllRemainingNativeOwners()
    {
        var epoch = new MauiNativeTreeEpoch();
        var attached = new ContentPage();
        var detached = new ContentPage();
        var staged = new ContentPage();
        var window = new Window();
        epoch.Register(attached);
        epoch.Register(detached);
        epoch.Register(staged);
        epoch.Register(window);

        MauiNativeTreeEpochClosure closure = epoch.Close();

        Assert.False(epoch.IsOpen);
        Assert.True(epoch.CancellationToken.IsCancellationRequested);
        Assert.Equal(3, closure.Pages.Count);
        Assert.Contains(attached, closure.Pages);
        Assert.Contains(detached, closure.Pages);
        Assert.Contains(staged, closure.Pages);
        Assert.Same(window, Assert.Single(closure.Windows));
        await closure.CompleteAsync();
    }

    [UIFact]
    public async Task NormalReleaseRemovesPageFromCloseSnapshot()
    {
        var epoch = new MauiNativeTreeEpoch();
        var released = new ContentPage();
        var abandoned = new ContentPage();
        epoch.Register(released);
        epoch.Register(abandoned);
        epoch.Forget(released);

        MauiNativeTreeEpochClosure closure = epoch.Close();

        Assert.Same(abandoned, Assert.Single(closure.Pages));
        await closure.CompleteAsync();
    }

    [UIFact]
    public async Task ClosedEpochRejectsRegistrationAndCannotOwnNativeObjects()
    {
        var epoch = new MauiNativeTreeEpoch();
        var page = new ContentPage();
        var window = new Window();
        epoch.Register(page);
        epoch.Register(window);

        MauiNativeTreeEpochClosure closure = epoch.Close();

        Assert.False(epoch.Owns(page));
        Assert.False(epoch.Owns(window));
        Assert.Throws<MauiNativeTreeInvalidatedException>(() => epoch.Register(new ContentPage()));
        Assert.Throws<MauiNativeTreeInvalidatedException>(() => epoch.Register(new Window()));
        await closure.CompleteAsync();
    }

    [Fact]
    public async Task EpochCallbackLatchesAreIsolatedFromReplacementTree()
    {
        var oldEpoch = new MauiNativeTreeEpoch
        {
            SuppressedNavigationPopDrainQueued = true,
            HostBackReconciliationPending = true
        };
        var replacementEpoch = new MauiNativeTreeEpoch();

        Assert.False(replacementEpoch.SuppressedNavigationPopDrainQueued);
        Assert.False(replacementEpoch.HostBackReconciliationPending);

        await oldEpoch.Close().CompleteAsync();
        await replacementEpoch.Close().CompleteAsync();
    }
}
