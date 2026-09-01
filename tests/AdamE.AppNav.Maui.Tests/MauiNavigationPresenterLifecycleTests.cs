using System.Reflection;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using DeviceRunners.UITesting.Xunit3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiNavigationPresenterLifecycleTests
{
    [Fact]
    public async Task DisposeReleasesCurrentStackPagesAndDetachesHandlersOnce()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"))),
            Context(new TestPageRoute("school-riverside")));

        var routePages = fixture.Factory.CreatedPages.ToArray();
        Assert.Equal(2, routePages.Length);

        _ = fixture.Presenter.StartShutdown();
        _ = fixture.Presenter.StartShutdown();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            fixture.Presenter.AttachWindowAsync(new Window()).AsTask());
        await fixture.Presenter.StartShutdown();

        Assert.All(routePages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));
        Assert.Equal(2, fixture.Factory.ReleasedPages.Count);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Contains(fixture.Observer.Events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationHandlerDetached &&
            diagnosticEvent.Data[NavigationDiagnosticDataKeys.HandlerName]?.ToString() == "NavigationPage.Popped/PoppedToRoot");
        Assert.Contains(fixture.Observer.Events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationPresenterDisposed);
    }

    [Fact]
    public async Task DetachWindowKeepsCurrentPageAlive()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        var currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var window = new Window();
        var replacementWindow = new Window();

        await fixture.Presenter.AttachWindowAsync(window);
        Assert.Same(currentPage, window.Page);

        await fixture.Presenter.DetachWindowAsync(window);

        Assert.Null(window.Page);
        Assert.Same(currentPage, fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.ReleasedPages);

        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        Assert.Same(currentPage, replacementWindow.Page);
        Assert.Same(replacementWindow, fixture.Presenter.AttachedWindow);
        Assert.Empty(fixture.Factory.ReleasedPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task DestroyedWindowSuspendsNativePresentationAndBuildsOnlyLatestLogicalState()
    {
        var fixture = new PresenterFixture();
        NavigationPlan initialPlan = Plan(Stack("schools", Entry("schools")));
        await fixture.Presenter.ApplyAsync(
            initialPlan,
            Context(new TestPageRoute("schools")));
        await fixture.Presenter.PushAsync<TestPresentationPage>("transient");
        var destroyedRoot = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Page destroyedRoutePage = destroyedRoot.Navigation.NavigationStack[0];
        Page transientPage = destroyedRoot.Navigation.NavigationStack[1];
        var destroyedWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(destroyedWindow);
        var rootChanges = new List<Page?>();
        fixture.Presenter.RootPageChanged += (_, page) => rootChanges.Add(page);

        RaiseWindowLifecycleEvent(destroyedWindow, "Destroying");

        Assert.Null(fixture.Presenter.AttachedWindow);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Same(destroyedRoot, destroyedWindow.Page);
        Assert.Null(Assert.Single(rootChanges));
        Assert.Contains(destroyedRoutePage, fixture.Factory.AbandonedPages);
        Assert.Contains(transientPage, fixture.Factory.AbandonedPages);
        Assert.DoesNotContain(destroyedRoot, fixture.Factory.AbandonedPages);
        Assert.Null(fixture.Factory.CaptureAbandonment(destroyedRoutePage));
        Assert.Empty(fixture.Factory.ReleasedPages);
        Assert.Empty(fixture.Factory.ReleasedPresentationPages);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Presenter.AttachWindowAsync(destroyedWindow).AsTask());

        int pagesBeforeHostlessNavigation = fixture.Factory.CreatedPages.Count;
        NavigationPlan intermediate = Plan(Stack("schools", Entry("schools"), Entry("details")));
        await fixture.Presenter.ApplyAsync(
            intermediate,
            Context(new TestPageRoute("details"), initialPlan.TargetState));
        NavigationPlan latest = Plan(Stack(
            "schools",
            Entry("schools"),
            Entry("details"),
            Entry("settings")));
        await fixture.Presenter.ApplyAsync(
            latest,
            Context(new TestPageRoute("settings"), intermediate.TargetState));
        Assert.Equal(pagesBeforeHostlessNavigation, fixture.Factory.CreatedPages.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Presenter.PushAsync<TestPresentationPage>("hostless").AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Presenter.PopAsync().AsTask());

        var replacementWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        var replacementRoot = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.NotSame(destroyedRoot, replacementRoot);
        Assert.Same(replacementRoot, replacementWindow.Page);
        Assert.Equal(
            ["schools", "details", "settings"],
            replacementRoot.Navigation.NavigationStack.Select(page => page.Title));
        Assert.Single(fixture.Factory.CreatedPresentationPages);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task FailedReplacementPublishesNoPartialTreeAndRemainsRetryable()
    {
        var verifier = new TogglePresentationVerifier();
        var fixture = new PresenterFixture(presentationVerifier: verifier);
        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));
        var destroyedWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(destroyedWindow);
        RaiseWindowLifecycleEvent(destroyedWindow, "Destroying");

        verifier.Fail = true;
        var rejectedWindow = new Window();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Presenter.AttachWindowAsync(rejectedWindow).AsTask());
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Null(fixture.Presenter.AttachedWindow);
        Assert.Null(rejectedWindow.Page);

        verifier.Fail = false;
        var replacementWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(replacementWindow);
        Assert.Same(fixture.Presenter.CurrentPage, replacementWindow.Page);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReplacementDestroyedByRootObserverIsNotMarkedReadyAndRemainsRetryable()
    {
        var fixture = new PresenterFixture();
        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));
        var destroyedWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(destroyedWindow);
        RaiseWindowLifecycleEvent(destroyedWindow, "Destroying");
        var rejectedWindow = new Window();
        fixture.Presenter.RootPageChanged += DestroyReplacement;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Presenter.AttachWindowAsync(rejectedWindow).AsTask());

        fixture.Presenter.RootPageChanged -= DestroyReplacement;
        Assert.Null(fixture.Presenter.AttachedWindow);
        Assert.Null(fixture.Presenter.CurrentPage);
        var replacementWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(replacementWindow);
        Assert.Same(fixture.Presenter.CurrentPage, replacementWindow.Page);
        await fixture.Presenter.StartShutdown();

        void DestroyReplacement(object? sender, Page? page)
        {
            if (page is not null)
                RaiseWindowLifecycleEvent(rejectedWindow, "Destroying");
        }
    }

    [Fact]
    public async Task EmptyLogicalStateClearsReplacementWindowBootstrapPage()
    {
        var fixture = new PresenterFixture();
        NavigationPlan initialPlan = Plan(Stack("schools", Entry("schools")));
        await fixture.Presenter.ApplyAsync(
            initialPlan,
            Context(new TestPageRoute("schools")));
        var destroyedWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(destroyedWindow);
        RaiseWindowLifecycleEvent(destroyedWindow, "Destroying");
        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(NavigationState.Empty),
            Context(new TestPageRoute("empty"), initialPlan.TargetState));
        var bootstrapPage = new ContentPage { Title = "bootstrap" };
        var replacementWindow = new Window(bootstrapPage);

        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        Assert.Null(replacementWindow.Page);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Same(replacementWindow, fixture.Presenter.AttachedWindow);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task DestroyedEpochStopsAfterUncooperativePageUpdateReturns()
    {
        var factory = new GatedUpdateRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        NavigationPlan initialPlan = Plan(Stack("schools", Entry("schools"), Entry("details")));
        await presenter.ApplyAsync(
            initialPlan,
            Context(new TestPageRoute("details")));
        var destroyedWindow = new Window();
        await presenter.AttachWindowAsync(destroyedWindow);

        Task apply = presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("details"))),
            Context(new TestPageRoute("details"), initialPlan.TargetState)).AsTask();
        await factory.UpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));
        RaiseWindowLifecycleEvent(destroyedWindow, "Destroying");
        factory.AllowUpdateToReturn();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Equal(1, factory.UpdateCalls);
        await presenter.StartShutdown();
    }

    [Fact]
    public async Task DeferredModalCallbackFromDestroyedEpochCannotChangeReplacementState()
    {
        var dispatcher = new ControlledMainThreadDispatcher();
        var fixture = new PresenterFixture(
            mainThreadDispatcher: dispatcher);
        var state = new NavigationState(
            [new WindowNode(
                "main",
                Stack("schools", Entry("schools")),
                [new ModalNode("cart", Entry("cart"))])],
            "main");
        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(state),
            Context(new TestPageRoute("cart")));
        var root = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Page modal = Assert.Single(root.Navigation.ModalStack);
        var reconciliations = new List<NavigationReconciliation>();
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliations.Add(args.Reconciliation);
        var destroyedWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(destroyedWindow);

        fixture.Presenter.ScheduleModalDismissalReconciliation(modal);
        Assert.Equal(1, dispatcher.PendingCallbacks);
        RaiseWindowLifecycleEvent(destroyedWindow, "Destroying");
        dispatcher.RunPendingCallbacks();

        Assert.Empty(reconciliations);
        var replacementWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(replacementWindow);
        Assert.Single(Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage).Navigation.ModalStack);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ShutdownWaitsForUncancelledAbandonmentCleanup()
    {
        var factory = new GatedAbandonmentRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        await presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));
        var window = new Window();
        await presenter.AttachWindowAsync(window);

        RaiseWindowLifecycleEvent(window, "Destroying");
        await factory.Scope.DisposalStarted.WaitAsync(TimeSpan.FromSeconds(5));
        Task shutdown = presenter.StartShutdown();

        Assert.False(shutdown.IsCompleted);
        factory.Scope.AllowDisposal();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.Scope.DisposeCount);
    }

    [Fact]
    public async Task WindowDestructionDuringShutdownReleaseCannotStealPageOwnership()
    {
        var factory = new GatedReleaseRoutePageFactory("schools");
        var presenter = new MauiNavigationPresenter(factory);
        await presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));
        var window = new Window();
        await presenter.AttachWindowAsync(window);

        Task shutdown = presenter.StartShutdown();
        await factory.ReleaseStarted.WaitAsync(TimeSpan.FromSeconds(5));
        RaiseWindowLifecycleEvent(window, "Destroying");
        factory.AllowRelease();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(factory.Inner.AbandonedPages);
        Assert.Single(factory.ReleaseAttempts);
        Assert.Single(factory.Inner.ReleasedPages);
    }

    [Fact]
    public async Task CommittedTreeReleaseStopsBeforeSiblingAfterEpochDestruction()
    {
        var factory = new GatedReleaseRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        NavigationPlan initial = Plan(Stack("schools", Entry("schools"), Entry("details")));
        await presenter.ApplyAsync(initial, Context(new TestPageRoute("details")));
        var window = new Window();
        await presenter.AttachWindowAsync(window);

        Task apply = presenter.ApplyAsync(
            Plan(Stack("replacement", Entry("replacement"))),
            Context(new TestPageRoute("replacement"), initial.TargetState)).AsTask();
        await factory.ReleaseStarted.WaitAsync(TimeSpan.FromSeconds(5));
        RaiseWindowLifecycleEvent(window, "Destroying");
        factory.AllowRelease();
        await apply.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("details", factory.ReleaseAttempts);
        Assert.DoesNotContain("schools", factory.ReleaseAttempts);
        Assert.Contains(factory.Inner.AbandonedPages, page => page.Title == "schools");
        await presenter.StartShutdown();
    }

    [Fact]
    public async Task DestructionCapturesPageDetachedByPendingNativeCallback()
    {
        var fixture = new PresenterFixture();
        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("details"))),
            Context(new TestPageRoute("details")));
        var root = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Page detachedPage = root.Navigation.NavigationStack[^1];
        var window = new Window();
        await fixture.Presenter.AttachWindowAsync(window);
        SemaphoreSlim operationLock = PresentationOperationLock(fixture.Presenter);
        await operationLock.WaitAsync();
        try
        {
            await MauiNativeNavigationOperations.Instance.PopAsync(root, animated: false);
            RaiseWindowLifecycleEvent(window, "Destroying");
        }
        finally
        {
            operationLock.Release();
        }

        Assert.Contains(detachedPage, fixture.Factory.AbandonedPages);
        Assert.DoesNotContain(detachedPage, fixture.Factory.ReleasedPages);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task FailedNormalReleaseRemainsOwnedByItsEpoch()
    {
        var factory = new ThrowingReleaseRoutePageFactory();
        var presenter = new MauiNavigationPresenter(factory);
        await presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));
        var root = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        Page routePage = Assert.Single(root.Navigation.NavigationStack);
        MethodInfo detach = typeof(MauiNavigationPresenter).GetMethod(
            "DetachPageTreeWithFailuresAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("DetachPageTreeWithFailuresAsync was not found.");

        ValueTask release = Assert.IsType<ValueTask>(detach.Invoke(presenter, [routePage]));
        await Assert.ThrowsAsync<AggregateException>(() => release.AsTask());

        FieldInfo epochField = typeof(MauiNavigationPresenter).GetField(
            "_nativeTreeEpoch",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Native-tree epoch field was not found.");
        var epoch = Assert.IsType<MauiNativeTreeEpoch>(epochField.GetValue(presenter));
        Assert.True(epoch.Owns(routePage));
        await presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowReplacementTransfersCurrentPageAndLifecycleHandlers()
    {
        var fixture = new PresenterFixture();
        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        Page currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var originalWindow = new Window();
        var replacementWindow = new Window();
        var originalHandlerCounts = WindowLifecycleEventNames.ToDictionary(
            eventName => eventName,
            eventName => EventHandlerCount(originalWindow, eventName),
            StringComparer.Ordinal);
        var replacementHandlerCounts = WindowLifecycleEventNames.ToDictionary(
            eventName => eventName,
            eventName => EventHandlerCount(replacementWindow, eventName),
            StringComparer.Ordinal);

        await fixture.Presenter.AttachWindowAsync(originalWindow);
        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        Assert.Null(originalWindow.Page);
        Assert.Same(currentPage, replacementWindow.Page);
        Assert.Same(currentPage, fixture.Presenter.CurrentPage);
        Assert.Same(replacementWindow, fixture.Presenter.AttachedWindow);
        Assert.Equal("main", fixture.Presenter.AttachedWindowId);
        Assert.Empty(fixture.Factory.ReleasedPages);
        foreach (string eventName in WindowLifecycleEventNames)
        {
            Assert.Equal(originalHandlerCounts[eventName], EventHandlerCount(originalWindow, eventName));
            Assert.Equal(replacementHandlerCounts[eventName] + 1, EventHandlerCount(replacementWindow, eventName));
        }

        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowReplacementDoesNotClearPageNotOwnedByPresenter()
    {
        var fixture = new PresenterFixture();
        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        Page currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var originalWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(originalWindow);
        var hostOwnedPage = new ContentPage();
        originalWindow.Page = hostOwnedPage;
        var replacementWindow = new Window();

        await fixture.Presenter.AttachWindowAsync(replacementWindow);

        Assert.Same(hostOwnedPage, originalWindow.Page);
        Assert.Same(currentPage, replacementWindow.Page);
        Assert.Same(currentPage, fixture.Presenter.CurrentPage);

        await fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task CancelledAttachmentQueuedForMainThreadDoesNotMutateWindowOwnership()
    {
        Assert.True(MainThread.IsMainThread);
        var fixture = new PresenterFixture();
        var originalPlaceholder = new ContentPage { Title = "original-placeholder" };
        var replacementPlaceholder = new ContentPage { Title = "replacement-placeholder" };
        var originalWindow = new Window(originalPlaceholder);
        var replacementWindow = new Window(replacementPlaceholder);
        await fixture.Presenter.AttachWindowAsync(originalWindow);
        using var cancellation = new CancellationTokenSource();
        SemaphoreSlim operationLock = PresentationOperationLock(fixture.Presenter);

        Task attachment = Task.Run(() => fixture.Presenter
            .AttachWindowAsync(replacementWindow, cancellationToken: cancellation.Token)
            .AsTask());
        Assert.True(SpinWait.SpinUntil(
            () => operationLock.CurrentCount == 0,
            TimeSpan.FromSeconds(5)));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment);
        Assert.Same(originalWindow, fixture.Presenter.AttachedWindow);
        Assert.Same(originalPlaceholder, originalWindow.Page);
        Assert.Same(replacementPlaceholder, replacementWindow.Page);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task PresentationStateTracksRootPageAndAttachedWindow()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        Assert.Null(presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));

        var rootPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var window = new Window();

        await fixture.Presenter.AttachWindowAsync(window, "main");

        Assert.Same(rootPage, presentationState.RootPage);
        Assert.Same(rootPage, fixture.Presenter.CurrentPage);
        Assert.Same(window, presentationState.AttachedWindow);
        Assert.Equal("main", presentationState.AttachedWindowId);

        await fixture.Presenter.DetachWindowAsync(window);

        Assert.Same(rootPage, presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);

        _ = fixture.Presenter.StartShutdown();
        await fixture.Presenter.StartShutdown();

        Assert.Null(presentationState.RootPage);
        Assert.Null(presentationState.AttachedWindow);
        Assert.Null(presentationState.AttachedWindowId);
    }

    [Fact]
    public async Task AttachWindowSameWindowDoesNotDuplicateLifecycleHandlers()
    {
        var fixture = new PresenterFixture();
        var window = new Window();
        var initialHandlerCounts = WindowLifecycleEventNames.ToDictionary(
            eventName => eventName,
            eventName => EventHandlerCount(window, eventName),
            StringComparer.Ordinal);

        await fixture.Presenter.AttachWindowAsync(window);
        await fixture.Presenter.AttachWindowAsync(window);

        foreach (var eventName in WindowLifecycleEventNames)
        {
            Assert.Equal(initialHandlerCounts[eventName] + 1, EventHandlerCount(window, eventName));
        }

        await fixture.Presenter.DetachWindowAsync(window);

        foreach (var eventName in WindowLifecycleEventNames)
        {
            Assert.Equal(initialHandlerCounts[eventName], EventHandlerCount(window, eventName));
        }

        _ = fixture.Presenter.StartShutdown();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AttachWindowRejectsMissingWindowId(string? windowId)
    {
        var fixture = new PresenterFixture();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Presenter.AttachWindowAsync(new Window(), windowId!).AsTask());

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowRejectsPresentedWindowMismatchBeforeMutatingEitherAttachment()
    {
        var fixture = new PresenterFixture();
        var originalWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(originalWindow, "main");
        int originalActivatedHandlers = EventHandlerCount(originalWindow, "Activated");

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"))),
            Context(new TestPageRoute("schools")));
        Page currentPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var replacementWindow = new Window();
        int replacementActivatedHandlers = EventHandlerCount(replacementWindow, "Activated");

        var exception = await Assert.ThrowsAsync<AppNavigationConfigurationException>(() =>
            fixture.Presenter.AttachWindowAsync(replacementWindow, "secondary").AsTask());

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Same(originalWindow, fixture.Presenter.AttachedWindow);
        Assert.Equal("main", fixture.Presenter.AttachedWindowId);
        Assert.Same(currentPage, originalWindow.Page);
        Assert.Null(replacementWindow.Page);
        Assert.Equal(originalActivatedHandlers, EventHandlerCount(originalWindow, "Activated"));
        Assert.Equal(replacementActivatedHandlers, EventHandlerCount(replacementWindow, "Activated"));

        _ = fixture.Presenter.StartShutdown();
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachWindowRejectsMissingIdWithoutDetachingExistingWindow()
    {
        var fixture = new PresenterFixture();
        var originalWindow = new Window();
        await fixture.Presenter.AttachWindowAsync(originalWindow, "main");
        int originalActivatedHandlers = EventHandlerCount(originalWindow, "Activated");

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Presenter.AttachWindowAsync(new Window(), " ").AsTask());

        Assert.Same(originalWindow, fixture.Presenter.AttachedWindow);
        Assert.Equal(originalActivatedHandlers, EventHandlerCount(originalWindow, "Activated"));
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task AttachedWindowMismatchIsRejectedBeforeNativeMutation()
    {
        var fixture = new PresenterFixture();
        var window = new Window();
        await fixture.Presenter.AttachWindowAsync(window, "main");

        var exception = await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => fixture.Presenter
            .ApplyAsync(
                Plan(new WindowNode("secondary", Stack("stack", Entry("detail")))),
                Context(new TestPageRoute("detail")))
            .AsTask());

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Null(window.Page);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.CreatedPages);
    }

    [Fact]
    public async Task AttachedWindowRejectsWindowlessPlanBeforeNativeMutation()
    {
        var fixture = new PresenterFixture();
        var window = new Window();
        await fixture.Presenter.AttachWindowAsync(window, "main");

        var exception = await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => fixture.Presenter
            .ApplyAsync(
                new NavigationPlan(NavigationState.Empty),
                Context(new TestPageRoute("empty")))
            .AsTask());

        Assert.Contains("must contain", exception.Message, StringComparison.Ordinal);
        Assert.Null(window.Page);
        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.CreatedPages);
    }

    [Fact]
    public async Task MultiWindowPlanIsRejectedBeforeNativeMutation()
    {
        var fixture = new PresenterFixture();
        var state = new NavigationState(
            [
                new WindowNode("main", Stack("main-stack", Entry("main"))),
                new WindowNode("secondary", Stack("secondary-stack", Entry("secondary")))
            ],
            "main");

        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Presenter
            .ApplyAsync(
                new NavigationPlan(state),
                Context(new TestPageRoute("main")))
            .AsTask());

        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.Empty(fixture.Factory.CreatedPages);
    }

    [Fact]
    public async Task EmptyStateClearsCurrentPageAndReleasesPreviousTree()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"))),
            Context(new TestPageRoute("school-riverside")));

        var routePages = fixture.Factory.CreatedPages.ToArray();

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(NavigationState.Empty),
            Context(new TestPageRoute("empty"), fixture.PresenterState));

        Assert.Null(fixture.Presenter.CurrentPage);
        Assert.All(routePages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));
    }

    [Fact]
    public async Task GetTopPresentedPageReturnsLeafPageForNavigationStack()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);

        Assert.Same(navigationPage.CurrentPage, presentationState.GetTopPresentedPage());

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task GetTopPresentedPageReturnsLeafPageForSelectedBranch()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;

        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "catalog",
            "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("catalog")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var selectedBranch = Assert.IsType<NavigationPage>(tabbedPage.CurrentPage);

        Assert.Same(selectedBranch.CurrentPage, presentationState.GetTopPresentedPage());

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ConfiguredRootBranchHostUsesFlyoutAndRetainsInactiveBranchTrees()
    {
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add(
                "store-branchHost",
                new MauiBranchHostRegistration(new MauiFlyoutBranchHostFactory("Store", FlyoutLayoutBehavior.Popover, false))));
        BranchHostNode branchHost = StoreBranchHost("catalog");
        var window = new Window(new ContentPage());
        await fixture.Presenter.AttachWindowAsync(window, "main");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("product")));

        var flyoutPage = Assert.IsType<MauiBranchFlyoutPage>(fixture.Presenter.CurrentPage);
        Assert.Same(flyoutPage, window.Page);
        Assert.Equal("Store", flyoutPage.Flyout.Title);
        Assert.Equal(FlyoutLayoutBehavior.Popover, flyoutPage.FlyoutLayoutBehavior);
        Assert.False(flyoutPage.IsGestureEnabled);
        Assert.Equal(new[] { "home", "catalog" }, flyoutPage.Branches.Select(static branch => branch.Id));
        Page homePage = flyoutPage.Branches[0].Page;
        Page catalogPage = flyoutPage.Branches[1].Page;
        Assert.Same(catalogPage, flyoutPage.Detail);
        Assert.Equal("catalog", flyoutPage.SelectedBranchId);

        flyoutPage.IsPresented = true;
        await fixture.Presenter.ApplyAsync(
            Plan(branchHost with { SelectedBranchId = "home" }),
            Context(new TestPageRoute("home"), fixture.PresenterState));

        Assert.Same(flyoutPage, fixture.Presenter.CurrentPage);
        Assert.Same(homePage, flyoutPage.Detail);
        Assert.Same(catalogPage, flyoutPage.Branches[1].Page);
        Assert.False(flyoutPage.IsPresented);
        Assert.Empty(fixture.Factory.ReleasedPages);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task FlyoutMenuSelectionChangesDetailAndRequestsBranchReconciliation()
    {
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add(
                "store-branchHost",
                new MauiBranchHostRegistration(new MauiFlyoutBranchHostFactory("Store", FlyoutLayoutBehavior.Default, true))));
        await fixture.Presenter.ApplyAsync(
            Plan(StoreBranchHost("home")),
            Context(new TestPageRoute("home")));
        var flyoutPage = Assert.IsType<MauiBranchFlyoutPage>(fixture.Presenter.CurrentPage);
        flyoutPage.IsPresented = true;

        NavigationReconciliation reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            () =>
            {
                flyoutPage.RequestBranchSelection("catalog");
                return Task.CompletedTask;
            });

        var updated = Assert.IsType<BranchHostNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal("catalog", updated.SelectedBranchId);
        Assert.Equal(NavigationReconciliationSource.BranchChanged, reconciliation.Source);
        Assert.Equal("catalog", flyoutPage.SelectedBranchId);
        Assert.Same(flyoutPage.Branches[1].Page, flyoutPage.Detail);
        Assert.False(flyoutPage.IsPresented);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ConfiguredFlyoutOutsideDirectWindowRootIsRejectedBeforePageCreation()
    {
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add(
                "store-branchHost",
                new MauiBranchHostRegistration(new MauiFlyoutBranchHostFactory("Store", FlyoutLayoutBehavior.Default, true))));
        var nested = new BranchHostNode(
            "outer-tabs",
            new[] { new NavigationBranch("nested", "Nested", StoreBranchHost("home")) },
            "nested");

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Presenter.ApplyAsync(
                Plan(nested),
                Context(new TestPageRoute("home"))).AsTask());

        Assert.Contains("does not support placement 'Nested'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("window root branch 'nested'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Factory.CreatedPages);
        Assert.Null(fixture.Presenter.CurrentPage);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostCapabilityFailureOccursBeforeFactoryCreation()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.Nested);
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add(
                "store-branchHost",
                new MauiBranchHostRegistration(factory)));

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Presenter.ApplyAsync(
                Plan(StoreBranchHost("home")),
                Context(new TestPageRoute("home"))).AsTask());

        Assert.Contains("does not support placement 'WindowRoot'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(factory.CreatedHosts);
        Assert.Empty(fixture.Factory.CreatedPages);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ModalBranchHostReceivesModalContentPlacementForCreationAndReuse()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.ModalContent);
        var nestedFactory = new RecordingBranchHostFactory(MauiBranchHostPlacement.Nested);
        var fixture = new PresenterFixture(configurePresentation: options =>
        {
            options.BranchHosts.Add("modal-host", new MauiBranchHostRegistration(factory));
            options.BranchHosts.Add("nested-host", new MauiBranchHostRegistration(nestedFactory));
        });
        var nestedHost = new BranchHostNode(
            "nested-host",
            [new NavigationBranch("leaf", "Leaf", Stack("modal-stack", Entry("home")))],
            "leaf",
            "leaf");
        var modalHost = new BranchHostNode(
            "modal-host",
            [new NavigationBranch("home", "Home", nestedHost)],
            "home",
            "home");
        var state = new NavigationState(
            [
                new WindowNode(
                    "main",
                    Stack("root-stack", Entry("root")),
                    [new ModalNode("modal", Entry("modal-route"), modalHost)])
            ],
            "main");

        await fixture.Presenter.ApplyAsync(new NavigationPlan(state), Context(new TestPageRoute("home")));
        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(state),
            Context(new TestPageRoute("home"), state));

        Assert.Equal([MauiBranchHostPlacement.ModalContent], factory.CreationPlacements);
        RecordingBranchHost host = Assert.Single(factory.CreatedHosts);
        Assert.Equal(
            [MauiBranchHostPlacement.ModalContent, MauiBranchHostPlacement.ModalContent],
            host.AppliedPlacements);
        Assert.Equal([MauiBranchHostPlacement.Nested], nestedFactory.CreationPlacements);
        Assert.Equal(
            [MauiBranchHostPlacement.Nested, MauiBranchHostPlacement.Nested],
            Assert.Single(nestedFactory.CreatedHosts).AppliedPlacements);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RetiringBranchHostRootReleasesItsLogicalModals()
    {
        var fixture = new PresenterFixture();
        var state = new NavigationState(
            [
                new WindowNode(
                    "main",
                    StoreBranchHost("home"),
                    [new ModalNode("cart", Entry("cart"))])
            ],
            "main");
        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(state),
            Context(new TestPageRoute("cart")));
        Page modalPage = Assert.Single(Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage).Navigation.ModalStack);

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(NavigationState.Empty),
            Context(new TestPageRoute("empty"), state));

        Assert.Equal(1, fixture.Factory.ReleaseCountFor(modalPage));
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task CustomNavigationPageHostOwnsItsTopologyBeforeItsPageSubtype()
    {
        var factory = new RecordingBranchHostFactory(
            MauiBranchHostPlacement.WindowRoot,
            static () => new NavigationPage(new ContentPage()));
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("custom-root", new MauiBranchHostRegistration(factory)));
        var state = new BranchHostNode(
            "custom-root",
            [
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            ],
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(Plan(state), Context(new TestPageRoute("home")));

        RecordingBranchHost host = Assert.Single(factory.CreatedHosts);
        Assert.IsType<NavigationPage>(host.Page);
        Assert.All(fixture.Factory.CreatedPages, page => Assert.Equal(0, fixture.Factory.ReleaseCountFor(page)));
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task StackReplacesRegisteredNavigationPageBackedBranchHost()
    {
        var factory = new RecordingBranchHostFactory(
            MauiBranchHostPlacement.WindowRoot,
            static () =>
            {
                var infrastructureRoot = new ContentPage();
                MauiPresentationMetadata.SetRouteEntryId(infrastructureRoot, "home");
                return new NavigationPage(infrastructureRoot);
            });
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("shared-host", new MauiBranchHostRegistration(factory)));
        var initial = new BranchHostNode(
            "shared-host",
            [new NavigationBranch("branch", "Branch", Stack("branch-stack", Entry("branch")))],
            "branch",
            "branch");
        await fixture.Presenter.ApplyAsync(Plan(initial), Context(new TestPageRoute("branch")));
        RecordingBranchHost host = Assert.Single(factory.CreatedHosts);
        var hostPage = Assert.IsType<NavigationPage>(host.Page);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("shared-host", Entry("home"))),
            Context(new TestPageRoute("home"), Plan(initial).TargetState));

        var stackPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.NotSame(hostPage, stackPage);
        Assert.Equal("home", MauiPresentationMetadata.GetRouteEntryId(stackPage.CurrentPage));
        Assert.Equal(1, host.DisposeCount);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RetiredBranchHostPageIsForgottenFromNativeTreeEpoch()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.WindowRoot);
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("custom-root", new MauiBranchHostRegistration(factory)));
        var initial = new BranchHostNode(
            "custom-root",
            [new NavigationBranch("home", "Home", Stack("home-stack", Entry("home")))],
            "home",
            "home");
        await fixture.Presenter.ApplyAsync(Plan(initial), Context(new TestPageRoute("home")));
        RecordingBranchHost host = Assert.Single(factory.CreatedHosts);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("replacement-stack", Entry("replacement"))),
            Context(new TestPageRoute("replacement"), Plan(initial).TargetState));

        MethodInfo canMutatePage = typeof(MauiNavigationPresenter).GetMethod(
            "CanMutatePage",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("CanMutatePage was not found.");
        Assert.False(Assert.IsType<bool>(canMutatePage.Invoke(fixture.Presenter, [host.Page])));
        Assert.Equal(1, host.DisposeCount);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ModalStackReplacesRegisteredNavigationPageBackedBranchHost()
    {
        var factory = new RecordingBranchHostFactory(
            MauiBranchHostPlacement.ModalContent,
            static () =>
            {
                var infrastructureRoot = new ContentPage();
                MauiPresentationMetadata.SetRouteEntryId(infrastructureRoot, "home");
                return new NavigationPage(infrastructureRoot);
            });
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("shared-host", new MauiBranchHostRegistration(factory)));
        var modalHost = new BranchHostNode(
            "shared-host",
            [new NavigationBranch("branch", "Branch", Stack("branch-stack", Entry("branch")))],
            "branch",
            "branch");
        var initial = new NavigationState(
            [new WindowNode(
                "main",
                Stack("root-stack", Entry("root")),
                [new ModalNode("modal", Entry("modal-route"), modalHost)])],
            "main");
        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initial),
            Context(new TestPageRoute("branch")));
        RecordingBranchHost host = Assert.Single(factory.CreatedHosts);
        var hostPage = Assert.IsType<NavigationPage>(host.Page);

        var target = new NavigationState(
            [new WindowNode(
                "main",
                Stack("root-stack", Entry("root")),
                [new ModalNode("modal", Entry("modal-route"), Stack("shared-host", Entry("home")))])],
            "main");
        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(target),
            Context(new TestPageRoute("home"), initial));

        Page modalPage = Assert.Single(Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage).Navigation.ModalStack);
        var stackPage = Assert.IsType<NavigationPage>(modalPage);
        Assert.NotSame(hostPage, stackPage);
        Assert.Equal("home", MauiPresentationMetadata.GetRouteEntryId(stackPage.CurrentPage));
        Assert.Equal(1, host.DisposeCount);
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostMustRetainTheExactPagesSuppliedByThePresenter()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.WindowRoot)
        {
            SubstituteBranchPages = true
        };
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("custom-root", new MauiBranchHostRegistration(factory)));
        var state = new BranchHostNode(
            "custom-root",
            [new NavigationBranch("home", "Home", Stack("home-stack", Entry("home")))],
            "home",
            "home");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Presenter.ApplyAsync(Plan(state), Context(new TestPageRoute("home"))).AsTask());

        Assert.Contains("did not retain the supplied page", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(factory.CreatedHosts).DisposeCount);
        Assert.All(fixture.Factory.CreatedPages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task CustomBranchHostCanContainNestedDefaultTabs()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.WindowRoot | MauiBranchHostPlacement.Nested);
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add(
                "custom-root",
                new MauiBranchHostRegistration(factory)));
        var nested = new BranchHostNode(
            "custom-root",
            [
                new NavigationBranch(
                    "home",
                    "Home",
                    new BranchHostNode(
                        "nested-tabs",
                        [new NavigationBranch("inner", "Inner", Stack("inner-stack", Entry("home")))],
                        "inner",
                        "inner")),
                new NavigationBranch("other", "Other", Stack("other-stack", Entry("other")))
            ],
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(Plan(nested), Context(new TestPageRoute("home")));

        var customHost = Assert.IsType<RecordingBranchHost>(Assert.Single(factory.CreatedHosts));
        Assert.IsType<ContentPage>(customHost.Page);
        var nestedHost = Assert.IsType<TabbedPage>(customHost.Branches[0].Page);
        Assert.Equal("inner", MauiPresentationMetadata.GetBranchId(nestedHost.CurrentPage));
        Assert.Equal("home", customHost.SelectedBranchId);

        var selectionEvents = 0;
        customHost.SelectionChanged += (_, _) => selectionEvents++;
        NavigationReconciliation reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            () =>
            {
                customHost.Select("other");
                return Task.CompletedTask;
            });
        Assert.Equal(1, selectionEvents);
        Assert.Equal("other", Assert.IsType<BranchHostNode>(reconciliation.TargetState.ActiveWindow?.Root).SelectedBranchId);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostSelectionsDuringFailedPresentationReplayLatestSelection()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.WindowRoot);
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("custom-root", new MauiBranchHostRegistration(factory)));
        BranchHostNode initial = new(
            "custom-root",
            [
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("other", "Other", Stack("other-stack", Entry("other")))
            ],
            "home",
            "home");

        NavigationPlan initialPlan = Plan(initial);
        await fixture.Presenter.ApplyAsync(initialPlan, Context(new TestPageRoute("home")));
        var host = Assert.Single(factory.CreatedHosts);
        host.SelectionsDuringApply = static selectedHost =>
        {
            selectedHost.Select("home");
            selectedHost.Select("other");
        };

        var reconciliations = new List<NavigationReconciliation>();
        var reconciliationCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Presenter.ReconciliationRequested += (_, args) =>
        {
            reconciliations.Add(args.Reconciliation);
            reconciliationCompletion.TrySetResult();
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Presenter.ApplyAsync(
            Plan(initial),
            Context(new TestPageRoute("home"), initialPlan.TargetState)).AsTask());

        await reconciliationCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(reconciliations);
        Assert.Equal(
            "other",
            Assert.IsType<BranchHostNode>(reconciliations[0].TargetState.ActiveWindow?.Root).SelectedBranchId);
        Assert.Equal("home", host.SelectedBranchId);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ChangingUnmappedBranchHostIdReplacesTheHost()
    {
        var fixture = new PresenterFixture();
        BranchHostNode initial = StoreBranchHost("home");
        await fixture.Presenter.ApplyAsync(
            Plan(initial),
            Context(new TestPageRoute("home")));
        var initialPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);

        await fixture.Presenter.ApplyAsync(
            Plan(initial with { Id = "replacement-host" }),
            Context(new TestPageRoute("home"), fixture.PresenterState));

        var replacementPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        Assert.NotSame(initialPage, replacementPage);
        Assert.Equal("replacement-host", MauiPresentationMetadata.GetHostId(replacementPage));
        await fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ShutdownEnumeratesCustomBranchesBeforeDisposingTheirHost()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.WindowRoot);
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("custom-root", new MauiBranchHostRegistration(factory)));
        var state = new BranchHostNode(
            "custom-root",
            [new NavigationBranch("home", "Home", Stack("home-stack", Entry("home")))],
            "home",
            "home");
        await fixture.Presenter.ApplyAsync(Plan(state), Context(new TestPageRoute("home")));
        RecordingBranchHost host = Assert.Single(factory.CreatedHosts);

        await fixture.Presenter.StartShutdown();

        Assert.Equal(1, host.DisposeCount);
        Assert.False(host.BranchesReadAfterDispose);
        Assert.All(fixture.Factory.CreatedPages, page => Assert.Contains(page, fixture.Factory.ReleasedPages));
    }

    [Fact]
    public async Task RootFlyoutCanContainDefaultTabsAndCustomBranchHosts()
    {
        var customFactory = new RecordingBranchHostFactory(MauiBranchHostPlacement.Nested);
        var fixture = new PresenterFixture(configurePresentation: options =>
        {
            options.BranchHosts.Add(
                "root-flyout",
                new MauiBranchHostRegistration(new MauiFlyoutBranchHostFactory("Navigate")));
            options.BranchHosts.Add(
                "custom-workspace",
                new MauiBranchHostRegistration(customFactory));
        });
        var root = new BranchHostNode(
            "root-flyout",
            [
                new NavigationBranch(
                    "tabs",
                    "Tabs",
                    new BranchHostNode(
                        "nested-tabs",
                        [new NavigationBranch("home", "Home", Stack("home-stack", Entry("home")))],
                        "home",
                        "home")),
                new NavigationBranch(
                    "workspace",
                    "Workspace",
                    new BranchHostNode(
                        "custom-workspace",
                        [new NavigationBranch("editor", "Editor", Stack("editor-stack", Entry("editor")))],
                        "editor",
                        "editor"))
            ],
            "tabs",
            "tabs");

        await fixture.Presenter.ApplyAsync(Plan(root), Context(new TestPageRoute("home")));

        var flyout = Assert.IsType<MauiBranchFlyoutPage>(fixture.Presenter.CurrentPage);
        Assert.IsType<TabbedPage>(flyout.Branches[0].Page);
        RecordingBranchHost customHost = Assert.Single(customFactory.CreatedHosts);
        Assert.Same(customHost.Page, flyout.Branches[1].Page);
        Assert.IsType<ContentPage>(customHost.Page);
        Assert.Same(flyout.Branches[0].Page, flyout.Detail);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BuiltInBranchHostApplyDoesNotRaiseSelectionChanged()
    {
        BranchHostNode branchHost = StoreBranchHost("home");
        var branches = new[]
        {
            new MauiBranchHostBranch("home", "Home", new ContentPage()),
            new MauiBranchHostBranch("catalog", "Catalog", new ContentPage())
        };
        var context = new MauiBranchHostCreationContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            Context(new TestPageRoute("home")),
            new ServiceCollection().BuildServiceProvider());

        IMauiBranchHost tabHost = await new MauiTabbedBranchHostFactory().CreateAsync(context);
        var tabEvents = 0;
        tabHost.SelectionChanged += (_, _) => tabEvents++;
        await (await tabHost.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            branches,
            "catalog",
            context.PresentationContext))).CommitAsync();
        Assert.Equal(0, tabEvents);
        Assert.IsType<TabbedPage>(tabHost.Page).CurrentPage = branches[0].Page;
        Assert.Equal(1, tabEvents);
        await tabHost.DisposeAsync();

        var flyoutBranches = new[]
        {
            new MauiBranchHostBranch("home", "Home", new ContentPage()),
            new MauiBranchHostBranch("catalog", "Catalog", new ContentPage())
        };
        IMauiBranchHost flyoutHost = await new MauiFlyoutBranchHostFactory("Menu").CreateAsync(context);
        var flyoutEvents = 0;
        flyoutHost.SelectionChanged += (_, _) => flyoutEvents++;
        await (await flyoutHost.ApplyAsync(new MauiBranchHostUpdateContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            flyoutBranches,
            "catalog",
            context.PresentationContext))).CommitAsync();
        Assert.Equal(0, flyoutEvents);
        Assert.IsType<MauiBranchFlyoutPage>(flyoutHost.Page).RequestBranchSelection("home");
        Assert.Equal(1, flyoutEvents);
        await flyoutHost.DisposeAsync();
    }

    [Fact]
    public async Task TabbedBranchHostFactoryUsesApplicationPageDelegate()
    {
        BranchHostNode branchHost = StoreBranchHost("home");
        var expectedPage = new TabbedPage();
        MauiBranchHostCreationContext? observedContext = null;
        var factory = new MauiTabbedBranchHostFactory(context =>
        {
            observedContext = context;
            return expectedPage;
        });
        var creationContext = new MauiBranchHostCreationContext(
            branchHost,
            MauiBranchHostPlacement.WindowRoot,
            Context(new TestPageRoute("home")),
            new ServiceCollection().BuildServiceProvider());

        IMauiBranchHost host = await factory.CreateAsync(creationContext);

        Assert.Same(creationContext, observedContext);
        Assert.Same(expectedPage, host.Page);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task BranchHostCommitFailureRollsBackReusableCustomHost()
    {
        var factory = new RecordingBranchHostFactory(MauiBranchHostPlacement.WindowRoot);
        var fixture = new PresenterFixture(configurePresentation: options =>
            options.BranchHosts.Add("custom-root", new MauiBranchHostRegistration(factory)));
        BranchHostNode homeState = new(
            "custom-root",
            [
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("other", "Other", Stack("other-stack", Entry("other")))
            ],
            "home",
            "home");
        await fixture.Presenter.ApplyAsync(Plan(homeState), Context(new TestPageRoute("home")));
        var host = Assert.Single(factory.CreatedHosts);
        factory.FailNextCommit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Presenter.ApplyAsync(
            Plan(homeState with { SelectedBranchId = "other" }),
            Context(new TestPageRoute("other"), fixture.PresenterState)).AsTask());

        Assert.Same(host, Assert.Single(factory.CreatedHosts));
        Assert.Equal("home", host.SelectedBranchId);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostAppliesStackRootIconsToGeneratedNavigationPageTabs()
    {
        var homeIcon = new FontImageSource { Glyph = "home" };
        var catalogIcon = new FontImageSource { Glyph = "catalog" };
        var fixture = new PresenterFixture(
            createPage: entry => new ContentPage
            {
                Title = $"Root {entry.Id}",
                IconImageSource = entry.Id == "home" ? homeIcon : catalogIcon,
                Content = new Label { Text = entry.Id }
            });
        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "catalog",
            "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("catalog")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        var catalogBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[1]);

        Assert.Same(homeIcon, homeBranch.IconImageSource);
        Assert.Same(catalogIcon, catalogBranch.IconImageSource);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostKeepsBranchTitlesAuthoritativeWhenApplyingRootIcons()
    {
        var fixture = new PresenterFixture(
            createPage: entry => new ContentPage
            {
                Title = $"Root {entry.Id}",
                IconImageSource = new FontImageSource { Glyph = entry.Id },
                Content = new Label { Text = entry.Id }
            });
        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "catalog",
            "catalog");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("catalog")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        var catalogBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[1]);

        Assert.Equal("Home", homeBranch.Title);
        Assert.Equal("Catalog", catalogBranch.Title);
        Assert.Equal("Root home", homeBranch.Navigation.NavigationStack[0].Title);
        Assert.Equal("Root catalog", catalogBranch.Navigation.NavigationStack[0].Title);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchHostRefreshesGeneratedNavigationPageTabIconWhenRootPageIsReused()
    {
        var initialIcon = new FontImageSource { Glyph = "home-initial" };
        var updatedIcon = new FontImageSource { Glyph = "home-updated" };
        var fixture = new PresenterFixture(
            createPage: entry => new ContentPage
            {
                Title = entry.Id,
                IconImageSource = initialIcon,
                Content = new Label { Text = entry.Id }
            },
            updatePage: (page, entry, _) =>
            {
                if (entry.Id == "home")
                {
                    page.IconImageSource = updatedIcon;
                }
            });
        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("home")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranch = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        Assert.Same(initialIcon, homeBranch.IconImageSource);

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("home"), fixture.PresenterState));

        Assert.Same(homeBranch, tabbedPage.Children[0]);
        Assert.Same(updatedIcon, homeBranch.IconImageSource);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task GetTopPresentedPagePrefersTopModalPage()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;
        var root = Stack("schools", Entry("schools"));
        var stateWithModal = new NavigationState(
            new[] { new WindowNode("main", root, new[] { new ModalNode("cart-modal", Entry("cart-modal")) }) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(navigationPage.Navigation.ModalStack);

        Assert.Same(modalPage, presentationState.GetTopPresentedPage());

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RootlessWindowWithModalUsesSyntheticHostAndPresentsModal()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;
        var state = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    null,
                    new[]
                    {
                        new ModalNode("cart-modal", Entry("cart-modal"))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(state),
            Context(new TestPageRoute("cart-modal")));

        var rootPage = Assert.IsType<ContentPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(rootPage.Navigation.ModalStack);
        Assert.NotSame(rootPage, modalPage);
        Assert.Same(modalPage, presentationState.GetTopPresentedPage());

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReplacingStackRootReleasesOldStackPages()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"))),
            Context(new TestPageRoute("school-riverside")));

        var firstStackPages = fixture.Factory.CreatedPages.ToArray();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("account", Entry("account"))),
            Context(new TestPageRoute("account"), fixture.PresenterState));

        Assert.All(firstStackPages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));
        Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedStackPageReceivesUpdatedRouteEntryWithoutRecreation()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("play", new RouteEntry("play-root", new TestPageRoute("hub")))),
            Context(new TestPageRoute("hub")));

        var firstNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedPage = Assert.Single(firstNavigationPage.Navigation.NavigationStack);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("play", new RouteEntry("play-root", new TestPageRoute("missions")))),
            Context(new TestPageRoute("missions"), fixture.PresenterState));

        var secondNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedPage, Assert.Single(secondNavigationPage.Navigation.NavigationStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedPage));
        Assert.Equal("missions", Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedPage)!.Route).Name);
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ExplicitTarget, fixture.Factory.LastUpdateContextFor(retainedPage)?.ReuseKind);
        Assert.Single(fixture.Factory.CreatedPages);
        Assert.Empty(fixture.Factory.ReleasedPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RetainedParentStackPageIsNotMarkedAsNavigationTargetWhenChildIsPushed()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("play", new RouteEntry("play-root", new TestPageRoute("hub")))),
            Context(new TestPageRoute("hub")));

        var firstNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedPage = Assert.Single(firstNavigationPage.Navigation.NavigationStack);

        await fixture.Presenter.ApplyAsync(
            Plan(Stack(
                "play",
                new RouteEntry("play-root", new TestPageRoute("hub")),
                Entry("mission-detail"))),
            Context(new TestPageRoute("mission-detail"), fixture.PresenterState));

        var secondNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedPage, secondNavigationPage.Navigation.NavigationStack[0]);
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedPage));
        Assert.False(fixture.Factory.LastUpdateContextFor(retainedPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.NonTargetReuse, fixture.Factory.LastUpdateContextFor(retainedPage)?.ReuseKind);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedTopStackPageIsMarkedAsResurfacedTargetWhenPageAboveItIsPopped()
    {
        var fixture = new PresenterFixture();
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    Stack("play", new RouteEntry("play-root", new TestPageRoute("hub")), Entry("mission-detail")))
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    Stack("play", new RouteEntry("play-root", new TestPageRoute("hub"))))
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("mission-detail"), NavigationState.Empty));

        var initialNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedPage = initialNavigationPage.Navigation.NavigationStack[0];

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("hub"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedPage, Assert.Single(updatedNavigationPage.Navigation.NavigationStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedPage));
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ResurfacedTarget, fixture.Factory.LastUpdateContextFor(retainedPage)?.ReuseKind);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedTopModalPageIsMarkedAsNavigationTarget()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-updated")))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart"), NavigationState.Empty));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = Assert.Single(navigationPage.Navigation.ModalStack);

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart-updated"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedModalPage, Assert.Single(updatedNavigationPage.Navigation.ModalStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedModalPage));
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedModalPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ExplicitTarget, fixture.Factory.LastUpdateContextFor(retainedModalPage)?.ReuseKind);
        Assert.Equal("cart-updated", Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedModalPage)!.Route).Name);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedModalIdWithDifferentRouteEntryIdRebuildsRouteOnlyModalPage()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-v1", new TestPageRoute("cart")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-v2", new TestPageRoute("cart-updated")))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart"), NavigationState.Empty));

        var initialNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var initialModalPage = Assert.Single(initialNavigationPage.Navigation.ModalStack);

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart-updated"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var updatedModalPage = Assert.Single(updatedNavigationPage.Navigation.ModalStack);
        Assert.NotSame(initialModalPage, updatedModalPage);
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(initialModalPage));

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedTopModalPageIsMarkedAsResurfacedTargetWhenModalAboveItIsDismissed()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart"))),
                        new ModalNode(
                            "detail-modal",
                            new RouteEntry("detail-modal-route", new TestPageRoute("detail")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart")))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("detail"), NavigationState.Empty));

        var initialNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = initialNavigationPage.Navigation.ModalStack[0];

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart"), initialState));

        var updatedNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        Assert.Same(retainedModalPage, Assert.Single(updatedNavigationPage.Navigation.ModalStack));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedModalPage));
        Assert.True(fixture.Factory.LastUpdateContextFor(retainedModalPage)?.IsNavigationTarget);
        Assert.Equal(MauiRoutePageReuseKind.ResurfacedTarget, fixture.Factory.LastUpdateContextFor(retainedModalPage)?.ReuseKind);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedModalContentRootIsReconciledInPlaceWhenCompatibilityMatches()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack(
                                "cart-stack",
                                new RouteEntry("cart-root", new TestPageRoute("cart")),
                                new RouteEntry("cart-detail", new TestPageRoute("cart-detail"))))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack(
                                "cart-stack",
                                new RouteEntry("cart-root", new TestPageRoute("cart")),
                                new RouteEntry("cart-detail", new TestPageRoute("cart-detail-updated"))))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart-detail"), NavigationState.Empty));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var retainedDetailPage = retainedModalPage.Navigation.NavigationStack[1];

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("cart-detail-updated"), initialState));

        var updatedRootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var updatedModalPage = Assert.IsType<NavigationPage>(Assert.Single(updatedRootNavigationPage.Navigation.ModalStack));
        Assert.Same(retainedModalPage, updatedModalPage);
        Assert.Same(retainedDetailPage, updatedModalPage.Navigation.NavigationStack[1]);
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedDetailPage));
        Assert.Equal(
            "cart-detail-updated",
            Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedDetailPage)!.Route).Name);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedNestedRouteOnlyModalUpdatesVisiblePageWithoutRecreation()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-route", new TestPageRoute("outer-shell")),
                            new ModalNode(
                                "inner-modal",
                                new RouteEntry("inner-route", new TestPageRoute("inner-v1"))))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-route", new TestPageRoute("outer-shell")),
                            new ModalNode(
                                "inner-modal",
                                new RouteEntry("inner-route", new TestPageRoute("inner-v2"))))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("inner-v1"), NavigationState.Empty));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = Assert.Single(rootNavigationPage.Navigation.ModalStack);
        int createdPageCount = fixture.Factory.CreatedPages.Count;

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("inner-v2"), initialState));

        Assert.Same(retainedModalPage, Assert.Single(rootNavigationPage.Navigation.ModalStack));
        Assert.Equal(createdPageCount, fixture.Factory.CreatedPages.Count);
        Assert.Equal(0, fixture.Factory.ReleaseCountFor(retainedModalPage));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedModalPage));
        Assert.Equal(MauiRoutePageReuseKind.ExplicitTarget, fixture.Factory.LastUpdateContextFor(retainedModalPage)?.ReuseKind);
        Assert.Equal("inner-v2", Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedModalPage)!.Route).Name);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedNestedRouteOnlyModalIsMarkedAsResurfacedWhenModalAboveItIsDismissed()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-route", new TestPageRoute("outer-shell")),
                            new ModalNode(
                                "inner-modal",
                                new RouteEntry("inner-route", new TestPageRoute("inner-v1")))),
                        new ModalNode(
                            "detail-modal",
                            new RouteEntry("detail-route", new TestPageRoute("detail")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-route", new TestPageRoute("outer-shell")),
                            new ModalNode(
                                "inner-modal",
                                new RouteEntry("inner-route", new TestPageRoute("inner-v2"))))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("detail"), NavigationState.Empty));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = rootNavigationPage.Navigation.ModalStack[0];
        var dismissedModalPage = rootNavigationPage.Navigation.ModalStack[1];
        int createdPageCount = fixture.Factory.CreatedPages.Count;

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("inner-v2"), initialState));

        Assert.Same(retainedModalPage, Assert.Single(rootNavigationPage.Navigation.ModalStack));
        Assert.Equal(createdPageCount, fixture.Factory.CreatedPages.Count);
        Assert.Equal(0, fixture.Factory.ReleaseCountFor(retainedModalPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(dismissedModalPage));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedModalPage));
        Assert.Equal(MauiRoutePageReuseKind.ResurfacedTarget, fixture.Factory.LastUpdateContextFor(retainedModalPage)?.ReuseKind);
        Assert.Equal("inner-v2", Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedModalPage)!.Route).Name);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedNestedStackModalIsMarkedAsResurfacedWhenModalAboveItIsDismissed()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-route", new TestPageRoute("outer-shell")),
                            Stack(
                                "inner-stack",
                                new RouteEntry("inner-root", new TestPageRoute("inner")),
                                new RouteEntry("inner-detail", new TestPageRoute("detail-v1")))),
                        new ModalNode(
                            "detail-modal",
                            new RouteEntry("detail-route", new TestPageRoute("detail")))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-route", new TestPageRoute("outer-shell")),
                            Stack(
                                "inner-stack",
                                new RouteEntry("inner-root", new TestPageRoute("inner")),
                                new RouteEntry("inner-detail", new TestPageRoute("detail-v2"))))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("detail"), NavigationState.Empty));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var retainedModalPage = Assert.IsType<NavigationPage>(rootNavigationPage.Navigation.ModalStack[0]);
        var retainedDetailPage = retainedModalPage.Navigation.NavigationStack[1];
        var dismissedModalPage = rootNavigationPage.Navigation.ModalStack[1];
        int createdPageCount = fixture.Factory.CreatedPages.Count;

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("detail-v2"), initialState));

        Assert.Same(retainedModalPage, Assert.Single(rootNavigationPage.Navigation.ModalStack));
        Assert.Same(retainedDetailPage, retainedModalPage.Navigation.NavigationStack[1]);
        Assert.Equal(createdPageCount, fixture.Factory.CreatedPages.Count);
        Assert.Equal(0, fixture.Factory.ReleaseCountFor(retainedDetailPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(dismissedModalPage));
        Assert.Equal(1, fixture.Factory.UpdateCountFor(retainedDetailPage));
        Assert.Equal(
            MauiRoutePageReuseKind.ResurfacedTarget,
            fixture.Factory.LastUpdateContextFor(retainedDetailPage)?.ReuseKind);
        Assert.Equal(
            "detail-v2",
            Assert.IsType<TestPageRoute>(fixture.Factory.LastUpdatedEntryFor(retainedDetailPage)!.Route).Name);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task ReusedModalIdWithIncompatibleContentRootRebuildsModalSuffix()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack(
                                "cart-stack",
                                new RouteEntry("cart-root", new TestPageRoute("cart")),
                                new RouteEntry("cart-detail", new TestPageRoute("cart-detail"))))
                    })
            },
            "main");
        var updatedState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    root,
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            new BranchHostNode(
                                "cart-branchHost",
                                new[]
                                {
                                    new NavigationBranch("summary", "Summary", Stack("summary-stack", Entry("summary"))),
                                    new NavigationBranch("history", "History", Stack("history-stack", Entry("history")))
                                },
                                "summary",
                                "summary"))
                    })
            },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("cart-detail"), NavigationState.Empty));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var initialModalPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var releasedPages = initialModalPage.Navigation.NavigationStack.ToArray();

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("summary"), initialState));

        var updatedRootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var updatedModalPage = Assert.IsType<TabbedPage>(Assert.Single(updatedRootNavigationPage.Navigation.ModalStack));
        Assert.NotSame(initialModalPage, updatedModalPage);
        Assert.All(releasedPages, page => Assert.Equal(1, fixture.Factory.ReleaseCountFor(page)));

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task IsModalPresentedTracksNativeModalPresence()
    {
        var fixture = new PresenterFixture();
        IMauiPresentationState presentationState = fixture.Presenter;
        var root = Stack("schools", Entry("schools"));
        var stateWithModal = new NavigationState(
            new[] { new WindowNode("main", root, new[] { new ModalNode("cart-modal", Entry("cart-modal")) }) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(navigationPage.Navigation.ModalStack);

        Assert.True(presentationState.IsModalPresented(modalPage));

        await navigationPage.Navigation.PopModalAsync(animated: false);

        Assert.False(presentationState.IsModalPresented(modalPage));

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopReconcilesToActualRemainingStackEntries()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await navigationPage.Navigation.PopAsync(animated: false));

        var stack = Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal(new[] { "schools", "school-riverside" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.Single(fixture.Factory.ReleasedPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RouteOwnedPresentationPagePushesWithoutChangingLogicalState()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;
        var state = Plan(Stack("home", Entry("home"), Entry("create")));

        await fixture.Presenter.ApplyAsync(state, Context(new TestPageRoute("create")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var ownerPage = navigationPage.Navigation.NavigationStack[^1];
        var bindingContext = new object();
        ownerPage.BindingContext = bindingContext;

        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var presentationPage = Assert.IsType<TestPresentationPage>(navigationPage.Navigation.NavigationStack[^1]);
        Assert.Null(MauiPresentationMetadata.GetRouteEntryId(presentationPage));
        Assert.Equal("create", MauiPresentationMetadata.GetPresentationOwnerRouteEntryId(presentationPage));
        Assert.Equal("setting", MauiPresentationMetadata.GetPresentationPageKey(presentationPage));
        Assert.Same(bindingContext, presentationPage.BindingContext);
        Assert.Null(reconciliation);
        Assert.Single(fixture.Factory.CreatedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RejectedPresentationNavigationContainerDisposesItsScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<PresentationScopeMarker>();
        RejectedPresentationNavigationPage? createdPage = null;
        services.AddTransient<RejectedPresentationNavigationPage>(serviceProvider =>
        {
            var page = new RejectedPresentationNavigationPage(
                serviceProvider.GetRequiredService<PresentationScopeMarker>());
            createdPage = page;
            return page;
        });
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPage<TestPageRoute>((_, _) => new ContentPage());
        var presenter = new MauiNavigationPresenter(
            new MauiRoutePageFactory(provider, options),
            presentationOptions: options);

        await presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));
        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        var originalStack = navigationPage.Navigation.NavigationStack.ToArray();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.PushAsync<RejectedPresentationNavigationPage>(
                "invalid",
                new MauiRoutePresentationPageOptions { Animated = false }).AsTask());

        var page = Assert.IsType<RejectedPresentationNavigationPage>(createdPage);
        Assert.Contains("cannot be a navigation container", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, page.Marker.DisposeCount);
        Assert.Equal(originalStack, navigationPage.Navigation.NavigationStack.ToArray());

        _ = presenter.StartShutdown();
        Assert.Equal(1, page.Marker.DisposeCount);
    }

    [Fact]
    public async Task RejectedAttachedPresentationPageDisposesItsScopeAndPreservesItsTree()
    {
        var services = new ServiceCollection();
        services.AddScoped<PresentationScopeMarker>();
        RejectedAttachedPresentationPage? createdPage = null;
        NavigationPage? externalNavigationPage = null;
        services.AddTransient<RejectedAttachedPresentationPage>(serviceProvider =>
        {
            var page = new RejectedAttachedPresentationPage(
                serviceProvider.GetRequiredService<PresentationScopeMarker>());
            createdPage = page;
            externalNavigationPage = new NavigationPage(page);
            return page;
        });
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPage<TestPageRoute>((_, _) => new ContentPage());
        var presenter = new MauiNavigationPresenter(
            new MauiRoutePageFactory(provider, options),
            presentationOptions: options);

        await presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));

        var navigationPage = Assert.IsType<NavigationPage>(presenter.CurrentPage);
        var originalStack = navigationPage.Navigation.NavigationStack.ToArray();
        originalStack[^1].BindingContext = new object();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presenter.PushAsync<RejectedAttachedPresentationPage>(
                "invalid",
                new MauiRoutePresentationPageOptions
                {
                    Animated = false,
                    InheritBindingContext = true
                }).AsTask());

        var page = Assert.IsType<RejectedAttachedPresentationPage>(createdPage);
        var externalOwner = Assert.IsType<NavigationPage>(externalNavigationPage);
        Assert.Contains("already attached to a visual tree", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, page.Marker.DisposeCount);
        Assert.Null(page.BindingContext);
        Assert.Same(externalOwner, page.Parent);
        Assert.Same(page, Assert.Single(externalOwner.Navigation.NavigationStack));
        Assert.Equal(originalStack, navigationPage.Navigation.NavigationStack.ToArray());

        _ = presenter.StartShutdown();
        Assert.Equal(1, page.Marker.DisposeCount);
    }

    [UIFact]
    public async Task NativePopOfRouteOwnedPageDoesNotReconcileLogicalState()
    {
        var fixture = new PresenterFixture();
        NavigationReconciliation? reconciliation = null;
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliation = args.Reconciliation;

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var presentationPage = navigationPage.Navigation.NavigationStack[^1];
        await ReleasePresentationPageAfterNativeMutationAsync(
            fixture.Factory,
            presentationPage,
            async () => await navigationPage.Navigation.PopAsync(animated: false));

        Assert.Null(reconciliation);
        Assert.Equal(new[] { "home", "create" },
            navigationPage.Navigation.NavigationStack.Select(MauiPresentationMetadata.GetRouteEntryId));
        Assert.Contains(presentationPage, fixture.Factory.ReleasedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task LogicalRouteAboveOwnerPreservesOwnedPresentationSegment()
    {
        var fixture = new PresenterFixture();
        var createState = Plan(Stack("home", Entry("home"), Entry("create")));
        var coveredState = Plan(Stack("home", Entry("home"), Entry("create"), Entry("detail")));

        await fixture.Presenter.ApplyAsync(createState, Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var settingPage = navigationPage.Navigation.NavigationStack[^1];

        await fixture.Presenter.ApplyAsync(
            coveredState,
            Context(new TestPageRoute("detail"), createState.TargetState));

        Assert.Equal(4, navigationPage.Navigation.NavigationStack.Count);
        Assert.Same(settingPage, navigationPage.Navigation.NavigationStack[2]);
        Assert.Equal("detail", MauiPresentationMetadata.GetRouteEntryId(navigationPage.Navigation.NavigationStack[3]));

        await fixture.Presenter.ApplyAsync(
            createState,
            Context(new TestPageRoute("create"), coveredState.TargetState));

        Assert.Equal(3, navigationPage.Navigation.NavigationStack.Count);
        Assert.Same(settingPage, navigationPage.Navigation.NavigationStack[^1]);
        Assert.DoesNotContain(settingPage, fixture.Factory.ReleasedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task RemovingOwnerRouteReleasesItsPresentationPages()
    {
        var fixture = new PresenterFixture();
        var createState = Plan(Stack("home", Entry("home"), Entry("create")));

        await fixture.Presenter.ApplyAsync(createState, Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var settingPage = navigationPage.Navigation.NavigationStack[^1];

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"))),
            Context(new TestPageRoute("home"), createState.TargetState));

        Assert.Single(navigationPage.Navigation.NavigationStack);
        Assert.Contains(settingPage, fixture.Factory.ReleasedPresentationPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task PresentationKeysAreIdempotentAtTopAndUniqueWithinOwnerSegment()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));

        var options = new MauiRoutePresentationPageOptions { Animated = false };
        await fixture.Presenter.PushAsync<TestPresentationPage>("setting", options);
        await fixture.Presenter.PushAsync<TestPresentationPage>("setting", options);
        Assert.Single(fixture.Factory.CreatedPresentationPages);

        await fixture.Presenter.PushAsync<TestPresentationPage>("vibe", options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Presenter.PushAsync<TestPresentationPage>("setting", options).AsTask());

        Assert.Contains("already exists below the top", exception.Message, StringComparison.Ordinal);
        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task PresentationNavigatorPopStopsAtLogicalRoutePage()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("home", Entry("home"), Entry("create"))),
            Context(new TestPageRoute("create")));
        await fixture.Presenter.PushAsync<TestPresentationPage>(
            "setting",
            new MauiRoutePresentationPageOptions { Animated = false });

        Assert.True(await fixture.Presenter.PopAsync(animated: false));
        Assert.False(await fixture.Presenter.PopAsync(animated: false));

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopToRootReconcilesToActualRootEntry()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(Stack("schools", Entry("schools"), Entry("school-riverside"), Entry("school-middleton"))),
            Context(new TestPageRoute("school-middleton")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await navigationPage.Navigation.PopToRootAsync(animated: false));

        var stack = Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal(new[] { "schools" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.Equal(2, fixture.Factory.ReleasedPages.Count);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideModalContentReconcilesOwningModalState()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    Stack("schools", Entry("schools")),
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            Stack("cart-stack", Entry("cart"), Entry("cart-detail"), Entry("cart-receipt")))
                    })),
            Context(new TestPageRoute("cart-receipt")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalNavigationPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await modalNavigationPage.Navigation.PopAsync(animated: false));

        var modal = Assert.Single(reconciliation.TargetState.ActiveWindow?.Modals ?? []);
        var stack = Assert.IsType<StackNode>(modal.Content);
        Assert.Equal(new[] { "cart", "cart-detail" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(new TestPageRoute("cart-detail"), reconciliation.Route);
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.IsType<StackNode>(reconciliation.TargetState.ActiveWindow?.Root);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideNestedModalContentReconcilesToVisibleNestedRoute()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    Stack("schools", Entry("schools")),
                    new[]
                    {
                        new ModalNode(
                            "outer-modal",
                            new RouteEntry("outer-modal-route", new TestPageRoute("outer-shell")),
                            new ModalNode(
                                "inner-modal",
                                new RouteEntry("inner-modal-route", new TestPageRoute("inner-shell")),
                                Stack("inner-stack", Entry("inner-root"), Entry("inner-detail"))))
                    })),
            Context(new TestPageRoute("inner-detail")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalNavigationPage = Assert.IsType<NavigationPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await modalNavigationPage.Navigation.PopAsync(animated: false));

        var outerModal = Assert.Single(reconciliation.TargetState.ActiveWindow?.Modals ?? []);
        var innerModal = Assert.IsType<ModalNode>(outerModal.Content);
        var stack = Assert.IsType<StackNode>(innerModal.Content);
        Assert.Equal(new[] { "inner-root" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(new TestPageRoute("inner-root"), reconciliation.Route);
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideRootModalContentReconcilesToVisibleRoute()
    {
        var fixture = new PresenterFixture();

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    new ModalNode(
                        "root-modal",
                        new RouteEntry("root-modal-route", new TestPageRoute("root-shell")),
                        Stack("root-modal-stack", Entry("root"), Entry("detail"))))),
            Context(new TestPageRoute("detail")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await navigationPage.Navigation.PopAsync(animated: false));

        var rootModal = Assert.IsType<ModalNode>(reconciliation.TargetState.ActiveWindow?.Root);
        var stack = Assert.IsType<StackNode>(rootModal.Content);
        Assert.Equal(new[] { "root" }, stack.Entries.Select(entry => entry.Id));
        Assert.Equal(new TestPageRoute("root"), reconciliation.Route);
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeStackPopInsideRootlessModalContentReconcilesOwningModalState()
    {
        var nativeOperations = new CountingNativeNavigationOperations();
        var fixture = new PresenterFixture(nativeOperations: nativeOperations);
        var initialState = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    null,
                    new[]
                    {
                        new ModalNode(
                            "login-modal",
                            new RouteEntry("login-modal-route", new TestPageRoute("login-shell")),
                            Stack("login-stack", Entry("login"), Entry("challenge")))
                    })
            },
            "main");
        var reconciliations = new List<NavigationReconciliation>();
        fixture.Presenter.ReconciliationRequested += (_, args) => reconciliations.Add(args.Reconciliation);

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("challenge")));

        var rootHost = Assert.IsType<ContentPage>(fixture.Presenter.CurrentPage);
        var modalNavigationPage = Assert.IsType<NavigationPage>(Assert.Single(rootHost.Navigation.ModalStack));
        var loginPage = modalNavigationPage.Navigation.NavigationStack[0];
        var challengePage = modalNavigationPage.Navigation.NavigationStack[1];
        var createdPageCount = fixture.Factory.CreatedPages.Count;
        var stackPushCount = nativeOperations.StackPushCount;
        var modalPushCount = nativeOperations.ModalPushCount;

        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            async () => await modalNavigationPage.Navigation.PopAsync(animated: false));

        Assert.Same(reconciliation, Assert.Single(reconciliations));
        Assert.Equal(NavigationReconciliationSource.HostBack, reconciliation.Source);
        Assert.Equal(new TestPageRoute("login"), reconciliation.Route);
        var updatedWindow = Assert.IsType<WindowNode>(reconciliation.TargetState.ActiveWindow);
        Assert.Null(updatedWindow.Root);
        var updatedModal = Assert.Single(updatedWindow.Modals);
        Assert.Equal("login-modal", updatedModal.Id);
        var updatedStack = Assert.IsType<StackNode>(updatedModal.Content);
        Assert.Equal(new[] { "login" }, updatedStack.Entries.Select(entry => entry.Id));
        Assert.Same(modalNavigationPage, Assert.Single(rootHost.Navigation.ModalStack));
        Assert.Same(loginPage, Assert.Single(modalNavigationPage.Navigation.NavigationStack));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(challengePage));

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(reconciliation.TargetState),
            Context(new TestPageRoute("login"), reconciliation.TargetState));

        Assert.Equal(createdPageCount, fixture.Factory.CreatedPages.Count);
        Assert.Equal(stackPushCount, nativeOperations.StackPushCount);
        Assert.Equal(modalPushCount, nativeOperations.ModalPushCount);
        Assert.Same(rootHost, fixture.Presenter.CurrentPage);
        Assert.Same(modalNavigationPage, Assert.Single(rootHost.Navigation.ModalStack));
        Assert.Same(loginPage, Assert.Single(modalNavigationPage.Navigation.NavigationStack));
        Assert.Single(reconciliations);

        await fixture.Presenter.StartShutdown();

        Assert.Equal(1, fixture.Factory.ReleaseCountFor(rootHost));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(loginPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(challengePage));
        Assert.Equal(3, fixture.Factory.ReleasedPages.Count);
        Assert.Single(reconciliations);
    }

    [Fact]
    public async Task ModalRemovalReleasesDismissedModalPage()
    {
        var fixture = new PresenterFixture();
        var root = Stack("schools", Entry("schools"));
        var stateWithModal = new NavigationState(
            new[] { new WindowNode("main", root, new[] { new ModalNode("cart-modal", Entry("cart-modal")) }) },
            "main");
        var stateWithoutModal = new NavigationState(
            new[] { new WindowNode("main", root) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var navigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(navigationPage.Navigation.ModalStack);
        Assert.True(fixture.Presenter.IsModalPresented(modalPage));

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithoutModal),
            Context(new TestPageRoute("schools"), stateWithModal));

        Assert.Empty(navigationPage.Navigation.ModalStack);
        Assert.False(fixture.Presenter.IsModalPresented(modalPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(modalPage));

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchRootNavigationFromDeepBranchKeepsTabbedHost()
    {
        var fixture = new PresenterFixture();
        var initialRoot = StoreBranchHost("catalog");
        var initialState = new NavigationState(
            new[] { new WindowNode("main", initialRoot) },
            "main");
        var updatedState = new NavigationState(
            new[] { new WindowNode("main", StoreBranchHost("home")) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(initialState),
            Context(new TestPageRoute("product")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var homeBranchPage = Assert.IsType<NavigationPage>(tabbedPage.Children[0]);
        var catalogBranchPage = Assert.IsType<NavigationPage>(tabbedPage.Children[1]);
        Assert.Same(catalogBranchPage, tabbedPage.CurrentPage);
        Assert.Equal(new[] { "catalog", "product" }, catalogBranchPage.Navigation.NavigationStack.Select(static page => page.Title).ToArray());

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(updatedState),
            Context(new TestPageRoute("home"), initialState));

        var updatedTabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        Assert.Same(tabbedPage, updatedTabbedPage);
        Assert.Same(homeBranchPage, updatedTabbedPage.CurrentPage);
        Assert.Equal(new[] { "home" }, homeBranchPage.Navigation.NavigationStack.Select(static page => page.Title).ToArray());
        Assert.Equal(new[] { "catalog", "product" }, catalogBranchPage.Navigation.NavigationStack.Select(static page => page.Title).ToArray());
        Assert.Equal(3, fixture.Factory.CreatedPages.Count);
        Assert.Empty(fixture.Factory.ReleasedPages);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public async Task BranchRootNavigationWithNoTargetModalsDismissesModalAndKeepsTabbedHost()
    {
        var fixture = new PresenterFixture();
        var stateWithModal = new NavigationState(
            new[]
            {
                new WindowNode(
                    "main",
                    StoreBranchHost("catalog"),
                    new[] { new ModalNode("cart-modal", Entry("cart-modal")) })
            },
            "main");
        var stateWithoutModal = new NavigationState(
            new[] { new WindowNode("main", StoreBranchHost("home")) },
            "main");

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithModal),
            Context(new TestPageRoute("cart-modal")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var modalPage = Assert.Single(tabbedPage.Navigation.ModalStack);
        Assert.True(fixture.Presenter.IsModalPresented(modalPage));

        await fixture.Presenter.ApplyAsync(
            new NavigationPlan(stateWithoutModal),
            Context(new TestPageRoute("home"), stateWithModal));

        var updatedTabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        Assert.Same(tabbedPage, updatedTabbedPage);
        Assert.Empty(tabbedPage.Navigation.ModalStack);
        Assert.False(fixture.Presenter.IsModalPresented(modalPage));
        Assert.Equal(1, fixture.Factory.ReleaseCountFor(modalPage));
        var selectedBranch = Assert.IsType<NavigationPage>(updatedTabbedPage.CurrentPage);
        Assert.Equal("home", Assert.Single(selectedBranch.Navigation.NavigationStack).Title);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeTabSelectionReconcilesSelectedBranch()
    {
        var fixture = new PresenterFixture();

        var branchHost = new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(
            Plan(branchHost),
            Context(new TestPageRoute("home")));

        var tabbedPage = Assert.IsType<TabbedPage>(fixture.Presenter.CurrentPage);
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            () =>
            {
                tabbedPage.CurrentPage = tabbedPage.Children[1];
                return Task.CompletedTask;
            });

        var updatedBranchHost = Assert.IsType<BranchHostNode>(reconciliation.TargetState.ActiveWindow?.Root);
        Assert.Equal("catalog", updatedBranchHost.SelectedBranchId);
        Assert.Equal(NavigationReconciliationSource.BranchChanged, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [UIFact]
    public async Task NativeTabSelectionInsideModalContentReconcilesSelectedBranch()
    {
        var fixture = new PresenterFixture();

        var modalBranchHost = new BranchHostNode(
            "cart-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog")))
            },
            "home",
            "home");

        await fixture.Presenter.ApplyAsync(
            Plan(
                new WindowNode(
                    "main",
                    Stack("schools", Entry("schools")),
                    new[]
                    {
                        new ModalNode(
                            "cart-modal",
                            new RouteEntry("cart-modal-route", new TestPageRoute("cart-shell")),
                            modalBranchHost)
                    })),
            Context(new TestPageRoute("home")));

        var rootNavigationPage = Assert.IsType<NavigationPage>(fixture.Presenter.CurrentPage);
        var modalTabbedPage = Assert.IsType<TabbedPage>(Assert.Single(rootNavigationPage.Navigation.ModalStack));
        var reconciliation = await ReconcileAfterNativeMutationAsync(
            fixture.Presenter,
            () =>
            {
                modalTabbedPage.CurrentPage = modalTabbedPage.Children[1];
                return Task.CompletedTask;
            });

        var modal = Assert.Single(reconciliation.TargetState.ActiveWindow?.Modals ?? []);
        var updatedBranchHost = Assert.IsType<BranchHostNode>(modal.Content);
        Assert.Equal("catalog", updatedBranchHost.SelectedBranchId);
        var activeWindow = Assert.IsType<WindowNode>(reconciliation.TargetState.ActiveWindow);
        Assert.IsType<StackNode>(activeWindow.Root);
        Assert.Equal(NavigationReconciliationSource.BranchChanged, reconciliation.Source);

        _ = fixture.Presenter.StartShutdown();
    }

    [Fact]
    public void AddAppNavRegistersPresentationStateWithBlessedRuntime()
    {
        var services = new ServiceCollection();
        services.AddAppNav<ThrowingPlanner>(
            RouteTable.Create(routes => routes.MapRoute<TestPageRoute>("/tests/{name}")),
            pages => pages.MapPage<TestPageRoute>((_, _) => new ContentPage()));

        using var provider = services.BuildServiceProvider();
        var presenter = provider.GetRequiredService<MauiNavigationPresenter>();
        var navigator = provider.GetRequiredService<IRouterNavigator>();
        var presentationState = provider.GetRequiredService<IMauiPresentationState>();

        Assert.IsAssignableFrom<IAppNavRuntime>(navigator);
        Assert.Same(presenter, presentationState);
    }

    private static NavigationPresentationContext Context(
        AppRoute route,
        NavigationState? currentState = null)
    {
        return new NavigationPresentationContext(
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            currentState ?? NavigationState.Empty,
            Guid.NewGuid().ToString("N"));
    }

    private static NavigationPlan Plan(NavigationNode root)
    {
        var state = new NavigationState(
            new[] { new WindowNode("main", root) },
            "main");
        return new NavigationPlan(state);
    }

    private static NavigationPlan Plan(WindowNode window)
    {
        return new NavigationPlan(new NavigationState(new[] { window }, window.Id));
    }

    private static readonly string[] WindowLifecycleEventNames =
    [
        "Activated",
        "Deactivated",
        "Stopped",
        "Resumed",
        "Destroying"
    ];

    private static int EventHandlerCount(Window window, string eventName)
    {
        for (var type = window.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is null)
            {
                continue;
            }

            return field.GetValue(window) is MulticastDelegate handlers
                ? handlers.GetInvocationList().Length
                : 0;
        }

        throw new InvalidOperationException($"Window event backing field '{eventName}' was not found.");
    }

    private static StackNode Stack(string id, params RouteEntry[] entries)
    {
        return new StackNode(id, entries);
    }

    private static BranchHostNode StoreBranchHost(string selectedBranchId)
    {
        return new BranchHostNode(
            "store-branchHost",
            new[]
            {
                new NavigationBranch("home", "Home", Stack("home-stack", Entry("home"))),
                new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", Entry("catalog"), Entry("product")))
            },
            selectedBranchId,
            "home");
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private static async Task<NavigationReconciliation> ReconcileAfterNativeMutationAsync(
        MauiNavigationPresenter presenter,
        Func<Task> nativeMutation)
    {
        var completion = new TaskCompletionSource<NavigationReconciliation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnReconciliationRequested(object? sender, NavigationReconciliationRequestedEventArgs args)
        {
            completion.TrySetResult(args.Reconciliation);
        }

        presenter.ReconciliationRequested += OnReconciliationRequested;

        try
        {
            await MainThread.InvokeOnMainThreadAsync(nativeMutation);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            presenter.ReconciliationRequested -= OnReconciliationRequested;
        }
    }

    private static async Task ReleasePresentationPageAfterNativeMutationAsync(
        InstrumentedRoutePageFactory pageFactory,
        Page expectedPage,
        Func<Task> nativeMutation)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPresentationPageReleased(Page page)
        {
            if (ReferenceEquals(page, expectedPage))
                completion.TrySetResult();
        }

        pageFactory.PresentationPageReleased += OnPresentationPageReleased;

        try
        {
            await nativeMutation();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pageFactory.PresentationPageReleased -= OnPresentationPageReleased;
        }
    }

    private static SemaphoreSlim PresentationOperationLock(MauiNavigationPresenter presenter)
    {
        FieldInfo field = typeof(MauiNavigationPresenter).GetField(
            "_presentationOperationLock",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Presentation operation lock field was not found.");
        return Assert.IsType<SemaphoreSlim>(field.GetValue(presenter));
    }

    private static void RaiseWindowLifecycleEvent(Window window, string eventName)
    {
        for (Type? type = window.GetType(); type is not null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(window) is EventHandler handlers)
            {
                handlers(window, EventArgs.Empty);
                return;
            }
        }

        throw new InvalidOperationException($"Window event backing field '{eventName}' was not found.");
    }

    private sealed class PresenterFixture
    {
        public PresenterFixture(
            Action<MauiRoutePageRegistry>? configurePages = null,
            Func<RouteEntry, Page>? createPage = null,
            Action<Page, RouteEntry, MauiRoutePageUpdateContext>? updatePage = null,
            IMauiNativeNavigationOperations? nativeOperations = null,
            Action<MauiRoutePresentationOptions>? configurePresentation = null,
            IMauiPresentationVerifier? presentationVerifier = null,
            IMauiMainThreadDispatcher? mainThreadDispatcher = null)
        {
            Diagnostics = new NavigationDiagnostics();
            Diagnostics.AddObserver(Observer);
            PresentationOptions = new MauiRoutePresentationOptions();
            configurePages?.Invoke(PresentationOptions.Pages);
            configurePresentation?.Invoke(PresentationOptions);
            Factory = new InstrumentedRoutePageFactory(createPage, updatePage);
            Presenter = new MauiNavigationPresenter(
                Factory,
                diagnostics: Diagnostics,
                presentationOptions: PresentationOptions,
                presentationVerifier: presentationVerifier,
                nativeOperations: nativeOperations,
                mainThreadDispatcher: mainThreadDispatcher);
        }

        public InstrumentedRoutePageFactory Factory { get; }

        public MauiRoutePresentationOptions PresentationOptions { get; }

        public RecordingNavigationDiagnosticObserver Observer { get; } = new();

        public NavigationDiagnostics Diagnostics { get; }

        public MauiNavigationPresenter Presenter { get; }

        public NavigationState PresenterState
        {
            get
            {
                return Presenter.CurrentPage is null
                    ? NavigationState.Empty
                    : new NavigationState(new[] { new WindowNode("main") }, "main");
            }
        }
    }

    private sealed class CountingNativeNavigationOperations : IMauiNativeNavigationOperations
    {
        public int StackPushCount { get; private set; }

        public int ModalPushCount { get; private set; }

        public async Task PushAsync(NavigationPage navigationPage, Page page, bool animated)
        {
            StackPushCount++;
            await MauiNativeNavigationOperations.Instance.PushAsync(navigationPage, page, animated);
        }

        public Task<Page?> PopAsync(NavigationPage navigationPage, bool animated) =>
            MauiNativeNavigationOperations.Instance.PopAsync(navigationPage, animated);

        public async Task PushModalAsync(Page host, Page page, bool animated)
        {
            ModalPushCount++;
            await MauiNativeNavigationOperations.Instance.PushModalAsync(host, page, animated);
        }

        public Task<Page?> PopModalAsync(Page host, bool animated) =>
            MauiNativeNavigationOperations.Instance.PopModalAsync(host, animated);

        public void InsertTab(TabbedPage tabbedPage, int index, Page page) =>
            MauiNativeNavigationOperations.Instance.InsertTab(tabbedPage, index, page);

        public void RemoveTab(TabbedPage tabbedPage, Page page) =>
            MauiNativeNavigationOperations.Instance.RemoveTab(tabbedPage, page);

        public void SetCurrentTab(TabbedPage tabbedPage, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetCurrentTab(tabbedPage, page);

        public void SetFlyoutDetail(FlyoutPage flyoutPage, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetFlyoutDetail(flyoutPage, page);

        public void SetFlyoutPresented(FlyoutPage flyoutPage, bool isPresented) =>
            MauiNativeNavigationOperations.Instance.SetFlyoutPresented(flyoutPage, isPresented);

        public void SetFlyoutBranches(
            MauiBranchFlyoutPage flyoutPage,
            IReadOnlyList<MauiFlyoutBranchPresentation> branches) =>
            MauiNativeNavigationOperations.Instance.SetFlyoutBranches(flyoutPage, branches);

        public void SetSelectedFlyoutBranch(MauiBranchFlyoutPage flyoutPage, string? branchId) =>
            MauiNativeNavigationOperations.Instance.SetSelectedFlyoutBranch(flyoutPage, branchId);

        public void SetWindowPage(Window window, Page? page) =>
            MauiNativeNavigationOperations.Instance.SetWindowPage(window, page);
    }

    private sealed class ThrowingReleaseRoutePageFactory : IMauiRoutePageFactory
    {
        public ValueTask<Page> CreatePageAsync(
            RouteEntry entry,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Page>(new ContentPage { Title = entry.Id });

        public ValueTask<Page> CreatePresentationPageAsync(
            Type pageType,
            Page ownerRoutePage,
            bool inheritBindingContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UpdatePageAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ReleasePageAsync(Page page) =>
            ValueTask.FromException(new InvalidOperationException("Release failed before relinquishing resources."));

        public ValueTask ReleasePresentationPageAsync(Page page) =>
            ValueTask.FromException(new InvalidOperationException("Release failed before relinquishing resources."));

        public MauiPageAbandonment? CaptureAbandonment(Page page) => null;
    }

    private sealed class GatedAbandonmentRoutePageFactory : IMauiRoutePageFactory
    {
        private Page? _ownedPage;

        public GatedAsyncDisposable Scope { get; } = new();

        public ValueTask<Page> CreatePageAsync(
            RouteEntry entry,
            CancellationToken cancellationToken = default)
        {
            _ownedPage = new ContentPage { Title = entry.Id };
            return ValueTask.FromResult(_ownedPage);
        }

        public ValueTask<Page> CreatePresentationPageAsync(
            Type pageType,
            Page ownerRoutePage,
            bool inheritBindingContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UpdatePageAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ReleasePageAsync(Page page) => ValueTask.CompletedTask;

        public ValueTask ReleasePresentationPageAsync(Page page) => ValueTask.CompletedTask;

        public MauiPageAbandonment? CaptureAbandonment(Page page) =>
            ReferenceEquals(page, _ownedPage)
                ? new MauiPageAbandonment(Scope, page.GetType().FullName ?? page.GetType().Name)
                : null;
    }

    private sealed class GatedUpdateRoutePageFactory : IMauiRoutePageFactory
    {
        private readonly InstrumentedRoutePageFactory _inner = new();
        private readonly TaskCompletionSource _updateStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowUpdateToReturn =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task UpdateStarted => _updateStarted.Task;

        public int UpdateCalls { get; private set; }

        public void AllowUpdateToReturn() => _allowUpdateToReturn.TrySetResult();

        public ValueTask<Page> CreatePageAsync(
            RouteEntry entry,
            CancellationToken cancellationToken = default) =>
            _inner.CreatePageAsync(entry, cancellationToken);

        public ValueTask<Page> CreatePresentationPageAsync(
            Type pageType,
            Page ownerRoutePage,
            bool inheritBindingContext,
            CancellationToken cancellationToken = default) =>
            _inner.CreatePresentationPageAsync(
                pageType,
                ownerRoutePage,
                inheritBindingContext,
                cancellationToken);

        public async ValueTask UpdatePageAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            if (UpdateCalls == 1)
            {
                _updateStarted.TrySetResult();
                await _allowUpdateToReturn.Task;
            }

            await _inner.UpdatePageAsync(page, entry, context, cancellationToken);
        }

        public ValueTask ReleasePageAsync(Page page) => _inner.ReleasePageAsync(page);

        public ValueTask ReleasePresentationPageAsync(Page page) =>
            _inner.ReleasePresentationPageAsync(page);

        public MauiPageAbandonment? CaptureAbandonment(Page page) => _inner.CaptureAbandonment(page);
    }

    private sealed class GatedReleaseRoutePageFactory(string? gatedTitle = null) : IMauiRoutePageFactory
    {
        private readonly TaskCompletionSource _releaseStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InstrumentedRoutePageFactory Inner { get; } = new();

        public Task ReleaseStarted => _releaseStarted.Task;

        public List<string?> ReleaseAttempts { get; } = [];

        public void AllowRelease() => _allowRelease.TrySetResult();

        public ValueTask<Page> CreatePageAsync(
            RouteEntry entry,
            CancellationToken cancellationToken = default) =>
            Inner.CreatePageAsync(entry, cancellationToken);

        public ValueTask<Page> CreatePresentationPageAsync(
            Type pageType,
            Page ownerRoutePage,
            bool inheritBindingContext,
            CancellationToken cancellationToken = default) =>
            Inner.CreatePresentationPageAsync(pageType, ownerRoutePage, inheritBindingContext, cancellationToken);

        public ValueTask UpdatePageAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default) =>
            Inner.UpdatePageAsync(page, entry, context, cancellationToken);

        public async ValueTask ReleasePageAsync(Page page)
        {
            ReleaseAttempts.Add(page.Title);
            if (gatedTitle is null || StringComparer.Ordinal.Equals(page.Title, gatedTitle))
            {
                _releaseStarted.TrySetResult();
                await _allowRelease.Task;
            }

            await Inner.ReleasePageAsync(page);
        }

        public ValueTask ReleasePresentationPageAsync(Page page) =>
            Inner.ReleasePresentationPageAsync(page);

        public MauiPageAbandonment? CaptureAbandonment(Page page) => Inner.CaptureAbandonment(page);
    }

    private sealed class GatedAsyncDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposalStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDisposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DisposalStarted => _disposalStarted.Task;

        public int DisposeCount { get; private set; }

        public void AllowDisposal() => _allowDisposal.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposalStarted.TrySetResult();
            await _allowDisposal.Task;
        }
    }

    private sealed class ThrowingPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Lifecycle registration tests do not execute navigation.");
        }
    }

    private sealed class TogglePresentationVerifier : IMauiPresentationVerifier
    {
        public bool Fail { get; set; }

        public MauiPresentationVerificationMismatch? Verify(MauiPresentationVerificationContext context) =>
            Fail
                ? new MauiPresentationVerificationMismatch("$.root", "valid", "invalid")
                : MauiPresentationVerifier.Instance.Verify(context);
    }

    private sealed class ControlledMainThreadDispatcher : IMauiMainThreadDispatcher
    {
        private readonly Queue<Action> _callbacks = new();

        public bool IsMainThread => true;

        public int PendingCallbacks => _callbacks.Count;

        public Task InvokeAsync(Func<Task> callback) => callback();

        public Task<T> InvokeAsync<T>(Func<Task<T>> callback) => callback();

        public void BeginInvoke(Action callback) => _callbacks.Enqueue(callback);

        public void RunPendingCallbacks()
        {
            while (_callbacks.TryDequeue(out Action? callback))
                callback();
        }
    }


    private sealed class RejectedPresentationNavigationPage(PresentationScopeMarker marker) : NavigationPage
    {
        public PresentationScopeMarker Marker { get; } = marker;
    }

    private sealed class RejectedAttachedPresentationPage(PresentationScopeMarker marker) : ContentPage
    {
        public PresentationScopeMarker Marker { get; } = marker;
    }

    private sealed class PresentationScopeMarker : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class RecordingBranchHostFactory(
        MauiBranchHostPlacement supportedPlacements,
        Func<Page>? createHostPage = null)
        : IMauiBranchHostFactory
    {
        public MauiBranchHostPlacement SupportedPlacements => supportedPlacements;

        public List<RecordingBranchHost> CreatedHosts { get; } = [];

        public List<MauiBranchHostPlacement> CreationPlacements { get; } = [];

        public bool FailNextCommit { get; set; }

        public bool SubstituteBranchPages { get; set; }

        public ValueTask<IMauiBranchHost> CreateAsync(
            MauiBranchHostCreationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreationPlacements.Add(context.Placement);
            var host = new RecordingBranchHost(() =>
            {
                if (!FailNextCommit)
                    return false;

                FailNextCommit = false;
                return true;
            }, createHostPage?.Invoke(), SubstituteBranchPages);
            CreatedHosts.Add(host);
            return ValueTask.FromResult<IMauiBranchHost>(host);
        }
    }

    private sealed class RecordingBranchHost : IMauiBranchHost
    {
        private readonly Func<bool> _shouldFailCommit;
        private IReadOnlyList<MauiBranchHostBranch> _branches = [];
        private bool _disposed;

        public RecordingBranchHost(
            Func<bool> shouldFailCommit,
            Page? page = null,
            bool substituteBranchPages = false)
        {
            _shouldFailCommit = shouldFailCommit;
            Page = page ?? new ContentPage();
            SubstituteBranchPages = substituteBranchPages;
        }

        public Page Page { get; }

        public IReadOnlyList<MauiBranchHostBranch> Branches
        {
            get
            {
                BranchesReadAfterDispose |= _disposed;
                return _branches;
            }
        }

        public bool BranchesReadAfterDispose { get; private set; }

        public int DisposeCount { get; private set; }

        private bool SubstituteBranchPages { get; }

        public List<MauiBranchHostPlacement> AppliedPlacements { get; } = [];

        public Action<RecordingBranchHost>? SelectionsDuringApply { get; set; }

        public string? SelectedBranchId { get; private set; }

        public Page? SelectedBranchPage => _branches.FirstOrDefault(branch =>
            StringComparer.Ordinal.Equals(branch.Id, SelectedBranchId))?.Page;

        public event EventHandler<MauiBranchHostSelectionChangedEventArgs>? SelectionChanged;

        public ValueTask<IMauiBranchHostUpdate> ApplyAsync(
            MauiBranchHostUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            AppliedPlacements.Add(context.Placement);
            var previousBranches = _branches;
            string? previousSelected = SelectedBranchId;
            _branches = SubstituteBranchPages
                ? context.Branches.Select(SubstituteBranchPage).ToArray()
                : context.Branches.ToArray();
            SelectedBranchId = context.SelectedBranchId;
            foreach (MauiBranchHostBranch branch in _branches)
            {
                branch.Page.Title = branch.Title;
                MauiPresentationMetadata.SetBranchId(branch.Page, branch.Id);
            }

            SelectionsDuringApply?.Invoke(this);

            return ValueTask.FromResult<IMauiBranchHostUpdate>(
                new RecordingBranchHostUpdate(this, previousBranches, previousSelected, _shouldFailCommit()));
        }

        private static MauiBranchHostBranch SubstituteBranchPage(MauiBranchHostBranch branch)
        {
            Page replacement = branch.Page;
            if (branch.Page is NavigationPage navigationPage &&
                navigationPage.Navigation.NavigationStack.FirstOrDefault() is { } originalRoot)
            {
                var replacementRoot = new ContentPage();
                MauiPresentationMetadata.SetRouteEntryId(
                    replacementRoot,
                    MauiPresentationMetadata.GetRouteEntryId(originalRoot));
                replacement = new NavigationPage(replacementRoot);
                MauiPresentationMetadata.SetHostId(
                    replacement,
                    MauiPresentationMetadata.GetHostId(navigationPage));
            }

            return new MauiBranchHostBranch(branch.Id, branch.Title, replacement);
        }

        public void Select(string branchId)
        {
            if (_disposed || !_branches.Any(branch =>
                    StringComparer.Ordinal.Equals(branch.Id, branchId)))
                return;

            SelectedBranchId = branchId;
            SelectionChanged?.Invoke(this, new MauiBranchHostSelectionChangedEventArgs(branchId));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposed = true;
            SelectionChanged = null;
            return ValueTask.CompletedTask;
        }

        private sealed class RecordingBranchHostUpdate(
            RecordingBranchHost host,
            IReadOnlyList<MauiBranchHostBranch> branches,
            string? selectedBranchId,
            bool failCommit) : IMauiBranchHostUpdate
        {
            private bool _completed;

            public ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failCommit)
                    throw new InvalidOperationException("Synthetic branch-host commit failure.");

                _completed = true;
                return ValueTask.CompletedTask;
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_completed)
                {
                    host._branches = branches.ToArray();
                    host.SelectedBranchId = selectedBranchId;
                    _completed = true;
                }

                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public sealed class TestPresentationPage : ContentPage;
}
