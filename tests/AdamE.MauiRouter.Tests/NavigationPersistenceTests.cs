using AdamE.MauiRouter;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;
using System.Text.Json;

namespace AdamE.MauiRouter.Tests;

public sealed class NavigationPersistenceTests
{
    private static readonly Uri BaseUri = new("https://example.com/");

    [Fact]
    public void SnapshotSerializerRoundTripsBranchAwareStateAndHistory()
    {
        var state = CommerceState();
        var history = HistoryFor(state);
        var serializer = new NavigationSnapshotSerializer(TestRoutes.CreateTable(), new NavigationPersistenceOptions
        {
            BaseUri = BaseUri
        });

        var snapshot = serializer.CreateSnapshot(state, history);
        var restored = serializer.Restore(snapshot);

        Assert.True(restored.Accepted);
        Assert.NotNull(restored.State);
        Assert.NotNull(restored.History);

        var tabs = Assert.IsType<TabsNode>(restored.State.ActiveWindow!.Root);
        Assert.Equal("catalog", tabs.SelectedTabId);
        var catalog = Assert.IsType<StackNode>(tabs.SelectedBranch!.Content);
        var product = Assert.IsType<TestRoutes.ProductDetailRoute>(catalog.Top!.Route);
        Assert.Equal(123, product.ProductId);
        Assert.Equal("blue", product.Variant);
        var transition = Assert.IsType<SharedElementNavigationTransition>(catalog.Top.Transition);
        Assert.Equal("product-123", transition.Elements[0].SourceId);
        Assert.IsType<FadeNavigationTransition>(transition.Fallback);
        Assert.Single(restored.History.Entries);
        Assert.Equal(0, restored.History.CurrentIndex);
    }

    [Fact]
    public void SnapshotSerializerRoundTripsRequestDisposition()
    {
        var route = new TestRoutes.StoreRoute("northwind");
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("main-stack", new[]
                {
                    new RouteEntry("store", route)
                }))
        }, "main");
        var request = RouterNavigationRequest.FromRoute(
            route,
            NavigationRequestSource.InAppCommand,
            disposition: RouterNavigationDisposition.ReplaceCurrent);
        var history = new NavigationHistory(new[]
        {
            new NavigationHistoryEntry(
                "operation",
                request,
                route,
                state,
                "test",
                DateTimeOffset.UtcNow)
        }, 0);
        var serializer = new NavigationSnapshotSerializer(TestRoutes.CreateTable(), new NavigationPersistenceOptions
        {
            BaseUri = BaseUri
        });

        var snapshot = serializer.CreateSnapshot(state, history);
        var restored = serializer.Restore(snapshot);

        Assert.True(restored.Accepted);
        Assert.Equal(
            RouterNavigationDisposition.ReplaceCurrent,
            restored.History!.Entries[0].Request.Disposition);
    }

    [Fact]
    public void SnapshotSerializerRoundTripsRequestProvenance()
    {
        var route = new TestRoutes.StoreRoute("northwind");
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("main-stack", new[]
                {
                    new RouteEntry("store", route)
                }))
        }, "main");
        var provenance = new NavigationRequestProvenance(
            provider: "branch",
            originalUri: new Uri("https://example.com/stores/northwind?utm=spring"),
            referrerUri: new Uri("https://referrer.example/invite"),
            correlationId: "correlation-1",
            isColdStart: true,
            attributes: new Dictionary<string, string?>
            {
                ["campaign"] = "spring",
                ["nullable"] = null
            });
        var request = RouterNavigationRequest.FromRoute(
            route,
            NavigationRequestSource.AppLink,
            provenance: provenance);
        var history = new NavigationHistory(new[]
        {
            new NavigationHistoryEntry(
                "operation",
                request,
                route,
                state,
                "test",
                DateTimeOffset.UtcNow)
        }, 0);
        var serializer = new NavigationSnapshotSerializer(TestRoutes.CreateTable(), new NavigationPersistenceOptions
        {
            BaseUri = BaseUri
        });

        var snapshot = serializer.CreateSnapshot(state, history);
        var restored = serializer.Restore(snapshot);

        var snapshotProvenance = snapshot.History!.Entries[0].Request.Provenance!;
        Assert.Equal("branch", snapshotProvenance.Provider);
        Assert.Equal("https://example.com/stores/northwind?utm=spring", snapshotProvenance.OriginalUri);
        Assert.Equal("https://referrer.example/invite", snapshotProvenance.ReferrerUri);
        Assert.Equal("correlation-1", snapshotProvenance.CorrelationId);
        Assert.True(snapshotProvenance.IsColdStart);
        Assert.Equal("spring", snapshotProvenance.Attributes!["campaign"]);
        Assert.Null(snapshotProvenance.Attributes["nullable"]);

        Assert.True(restored.Accepted);
        Assert.Equal(provenance, restored.History!.Entries[0].Request.Provenance);
    }

    [Fact]
    public void SnapshotSerializerIgnoresMalformedProvenanceUrisWithoutDroppingRequest()
    {
        var route = new TestRoutes.StoreRoute("northwind");
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("main-stack", new[]
                {
                    new RouteEntry("store", route)
                }))
        }, "main");
        var request = RouterNavigationRequest.FromRoute(
            route,
            NavigationRequestSource.AppLink,
            provenance: new NavigationRequestProvenance(
                provider: "branch",
                originalUri: new Uri("https://example.com/stores/northwind"),
                referrerUri: new Uri("https://referrer.example/invite")));
        var history = new NavigationHistory(new[]
        {
            new NavigationHistoryEntry(
                "operation",
                request,
                route,
                state,
                "test",
                DateTimeOffset.UtcNow)
        }, 0);
        var serializer = new NavigationSnapshotSerializer(TestRoutes.CreateTable(), new NavigationPersistenceOptions
        {
            BaseUri = BaseUri
        });
        var snapshot = serializer.CreateSnapshot(state, history);
        var entry = snapshot.History!.Entries[0];
        var malformed = snapshot with
        {
            History = snapshot.History with
            {
                Entries = new[]
                {
                    entry with
                    {
                        Request = entry.Request with
                        {
                            Provenance = entry.Request.Provenance! with
                            {
                                OriginalUri = "http://[::1",
                                ReferrerUri = "https://referrer.example/invite"
                            }
                        }
                    }
                }
            }
        };

        var restored = serializer.Restore(malformed);

        Assert.True(restored.Accepted);
        var restoredProvenance = restored.History!.Entries[0].Request.Provenance!;
        Assert.Equal("branch", restoredProvenance.Provider);
        Assert.Null(restoredProvenance.OriginalUri);
        Assert.Equal(new Uri("https://referrer.example/invite"), restoredProvenance.ReferrerUri);
    }

    [Fact]
    public void NavigationSnapshotJsonRoundTripsPolymorphicNodes()
    {
        var serializer = new NavigationSnapshotSerializer(TestRoutes.CreateTable(), new NavigationPersistenceOptions
        {
            BaseUri = BaseUri
        });
        var snapshot = serializer.CreateSnapshot(CommerceState(), HistoryFor(CommerceState()));

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deserialized = JsonSerializer.Deserialize<NavigationSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = serializer.Restore(deserialized!);

        Assert.True(restored.Accepted);
        var tabs = Assert.IsType<TabsNode>(restored.State!.ActiveWindow!.Root);
        Assert.Equal("catalog", tabs.SelectedTabId);
        var catalog = Assert.IsType<StackNode>(tabs.SelectedBranch!.Content);
        Assert.IsType<SharedElementNavigationTransition>(catalog.Top!.Transition);
        Assert.Equal(NavigationSnapshot.CurrentSchemaVersion, deserialized!.SchemaVersion);
    }

    [Fact]
    public void SnapshotSerializerOmitsModalsByDefaultAndIncludesThemWhenConfigured()
    {
        var route = new TestRoutes.StoreRoute("northwind");
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("stack", new[] { new RouteEntry("home", route) }),
                new[] { new ModalNode("modal", new RouteEntry("modal-entry", route)) })
        }, "main");

        var defaultSnapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, NavigationHistory.Empty);
        var modalSnapshot = new NavigationSnapshotSerializer(
            TestRoutes.CreateTable(),
            new NavigationPersistenceOptions { PersistModals = true }).CreateSnapshot(state, NavigationHistory.Empty);

        Assert.Empty(defaultSnapshot.State.Windows[0].Modals);
        Assert.Single(modalSnapshot.State.Windows[0].Modals);
    }

    [Fact]
    public void RouteEntryMetadataIsOptIn()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("stack", new[]
                {
                    new RouteEntry(
                        "home",
                        new TestRoutes.StoreRoute("northwind"),
                        Metadata: new Dictionary<string, object?> { ["section"] = "featured" })
                }))
        }, "main");

        var withoutMetadata = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, NavigationHistory.Empty);
        var withMetadata = new NavigationSnapshotSerializer(
            TestRoutes.CreateTable(),
            new NavigationPersistenceOptions { MetadataSerializer = new PassThroughMetadataSerializer() })
            .CreateSnapshot(state, NavigationHistory.Empty);
        var restored = new NavigationSnapshotSerializer(
            TestRoutes.CreateTable(),
            new NavigationPersistenceOptions { MetadataSerializer = new PassThroughMetadataSerializer() })
            .Restore(withMetadata);

        var omittedStack = Assert.IsType<StackNodeSnapshot>(withoutMetadata.State.Windows[0].Root);
        Assert.Null(omittedStack.Entries[0].Metadata);

        var includedStack = Assert.IsType<StackNodeSnapshot>(withMetadata.State.Windows[0].Root);
        Assert.Equal("featured", includedStack.Entries[0].Metadata!["section"].Value);

        var restoredStack = Assert.IsType<StackNode>(restored.State!.ActiveWindow!.Root);
        Assert.Equal("featured", restoredStack.Entries[0].Metadata!["section"]);
    }

    [Fact]
    public void RouteEntryQueryMetadataRoundTripsThroughRouteUri()
    {
        var missionId = new RouteMetadataKey<string>("missionId");
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route.QueryMetadata(missionId)));
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("stack", new[]
                {
                    new RouteEntry(
                        "store",
                        new TestRoutes.StoreRoute("northwind"),
                        Metadata: new Dictionary<string, object?> { [missionId.Name] = "mission-1" })
                }))
        }, "main");
        var serializer = new NavigationSnapshotSerializer(table, new NavigationPersistenceOptions
        {
            BaseUri = BaseUri
        });

        var snapshot = serializer.CreateSnapshot(state, NavigationHistory.Empty);
        var restored = serializer.Restore(snapshot);

        var snapshotStack = Assert.IsType<StackNodeSnapshot>(snapshot.State.Windows[0].Root);
        Assert.Equal("https://example.com/stores/northwind?missionId=mission-1", snapshotStack.Entries[0].RouteUri);
        Assert.Null(snapshotStack.Entries[0].Metadata);

        var restoredStack = Assert.IsType<StackNode>(restored.State!.ActiveWindow!.Root);
        Assert.Equal("mission-1", restoredStack.Entries[0].Metadata![missionId.Name]);
    }

    [Fact]
    public void RouteStateRegistryControlsSnapshotAndRestoreMetadataLifetimes()
    {
        var missionId = new RouteMetadataKey<string>("missionId");
        var draftId = new RouteMetadataKey<string>("coverImageDraftId");
        var replyId = new RouteMetadataKey<string>("replyCommentId");
        var registry = RouteStateRegistry.Create(builder => builder
            .Canonical(missionId)
            .Restorable(draftId)
            .Ephemeral(replyId));
        var table = RouteTable.Create(routes => routes.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route
                .QueryMetadata(missionId)
                .QueryMetadata(draftId)
                .QueryMetadata(replyId)));
        IReadOnlyDictionary<string, object?> metadata = new Dictionary<string, object?>
        {
            [missionId.Name] = "mission-1",
            [draftId.Name] = "draft-1",
            [replyId.Name] = "reply-1",
            ["request-id"] = "abc-123"
        };
        var route = new TestRoutes.StoreRoute("northwind");
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("stack", new[]
                {
                    new RouteEntry("store", route, Metadata: metadata)
                }))
        }, "main");
        var history = NavigationHistory.Empty.Push(new NavigationHistoryEntry(
            "history",
            RouterNavigationRequest.FromRoute(
                route,
                NavigationRequestSource.Test,
                metadata: metadata),
            route,
            state,
            "test",
            DateTimeOffset.UtcNow));
        var serializer = new NavigationSnapshotSerializer(table, new NavigationPersistenceOptions
        {
            BaseUri = BaseUri,
            MetadataSerializer = new PassThroughMetadataSerializer(),
            RouteStateRegistry = registry
        });

        var snapshot = serializer.CreateSnapshot(state, history);
        var restored = serializer.Restore(snapshot);

        var snapshotStack = Assert.IsType<StackNodeSnapshot>(snapshot.State.Windows[0].Root);
        var snapshotEntryMetadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>>(
            snapshotStack.Entries[0].Metadata);
        Assert.Equal("https://example.com/stores/northwind?missionId=mission-1", snapshotStack.Entries[0].RouteUri);
        Assert.False(snapshotEntryMetadata.ContainsKey(missionId.Name));
        Assert.Equal("draft-1", snapshotEntryMetadata[draftId.Name].Value);
        Assert.False(snapshotEntryMetadata.ContainsKey(replyId.Name));
        Assert.Equal("abc-123", snapshotEntryMetadata["request-id"].Value);

        var historyEntrySnapshot = Assert.Single(snapshot.History!.Entries);
        var historyRequestMetadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>>(
            historyEntrySnapshot.Request.Metadata);
        Assert.False(historyRequestMetadata.ContainsKey(missionId.Name));
        Assert.Equal("draft-1", historyRequestMetadata[draftId.Name].Value);
        Assert.False(historyRequestMetadata.ContainsKey(replyId.Name));
        Assert.Equal("abc-123", historyRequestMetadata["request-id"].Value);

        var restoredStack = Assert.IsType<StackNode>(restored.State!.ActiveWindow!.Root);
        var restoredEntryMetadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            restoredStack.Entries[0].Metadata);
        Assert.Equal("mission-1", restoredEntryMetadata[missionId.Name]);
        Assert.Equal("draft-1", restoredEntryMetadata[draftId.Name]);
        Assert.False(restoredEntryMetadata.ContainsKey(replyId.Name));
        Assert.Equal("abc-123", restoredEntryMetadata["request-id"]);

        var restoredRequestMetadata = restored.History!.Entries[0].Request.Metadata;
        Assert.Equal("mission-1", restoredRequestMetadata[missionId.Name]);
        Assert.Equal("draft-1", restoredRequestMetadata[draftId.Name]);
        Assert.False(restoredRequestMetadata.ContainsKey(replyId.Name));
        Assert.Equal("abc-123", restoredRequestMetadata["request-id"]);
    }

    [Fact]
    public async Task RestorePresentsStateAndRestoresHistoryWithoutPushingNewEntry()
    {
        var state = CommerceState();
        var history = HistoryFor(state);
        var snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, history);
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);

        var result = await navigator.RestoreAsync(snapshot);

        Assert.True(result.Accepted);
        Assert.True(result.Presented);
        Assert.Equal(NavigationPlanKind.Restore, presenter.LastPlan!.Kind);
        var expectedRoute = new TestRoutes.ProductDetailRoute("northwind", 123, "blue", "spring");
        var restoredTabs = Assert.IsType<TabsNode>(navigator.CurrentState.ActiveWindow!.Root);
        Assert.Equal("catalog", restoredTabs.SelectedTabId);
        var restoredStack = Assert.IsType<StackNode>(restoredTabs.SelectedBranch!.Content);
        Assert.Equal(expectedRoute, restoredStack.Top!.Route);
        Assert.Single(navigator.History.Entries);
        Assert.Equal(history.Current!.Id, navigator.History.Current!.Id);
        Assert.Equal(expectedRoute, navigator.History.Current!.Route);
        Assert.Equal(navigator.CurrentState, navigator.History.Current!.State);
        Assert.Equal(expectedRoute, navigator.History.Current!.Request.Route);
    }

    [Fact]
    public async Task RestoreUsesPresentedModalRouteForPlanPolicyAndPresenterContext()
    {
        var state = ModalCommerceState();
        var history = HistoryFor(state, new TestRoutes.ProductDetailRoute("northwind", 123));
        var snapshot = new NavigationSnapshotSerializer(
            TestRoutes.CreateTable(),
            new NavigationPersistenceOptions
            {
                BaseUri = BaseUri,
                PersistModals = true
            }).CreateSnapshot(state, history);
        var presenter = new RecordingNavigationPresenter();
        var policy = new CapturePlanPolicy();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions
            {
                PlanPolicies = new[] { policy }
            });

        var result = await navigator.RestoreAsync(snapshot);

        Assert.True(result.Accepted);
        var expectedRoute = new TestRoutes.ProductDetailRoute("northwind", 123);
        Assert.Equal(expectedRoute, policy.LastRoute);
        Assert.Equal(expectedRoute, presenter.LastContext!.Route);
    }

    [Fact]
    public async Task InvalidCurrentStateRouteRejectsRestoreWithoutMutation()
    {
        var snapshot = SnapshotWithRouteUri("https://example.com/missing");
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);

        var result = await navigator.RestoreAsync(snapshot);

        Assert.False(result.Accepted);
        Assert.Equal("Navigation snapshot current state contains an invalid route.", result.RejectionReason);
        Assert.Empty(navigator.History.Entries);
        Assert.Null(navigator.CurrentState.ActiveWindow);
        Assert.Equal(0, presenter.ApplyCount);
    }

    [Fact]
    public async Task InvalidHistoryEntriesAreDroppedWhileCurrentStateRestores()
    {
        var state = CommerceState();
        var history = HistoryFor(state);
        var serializer = new NavigationSnapshotSerializer(TestRoutes.CreateTable());
        var validSnapshot = serializer.CreateSnapshot(state, history);
        var validEntry = validSnapshot.History!.Entries[0];
        var invalidEntry = validEntry with
        {
            RouteUri = "https://example.com/missing",
            Request = validEntry.Request with { RouteUri = "https://example.com/missing" }
        };
        var snapshot = validSnapshot with
        {
            History = new NavigationHistorySnapshot(new[] { invalidEntry, validEntry }, CurrentIndex: 1)
        };
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance);

        var result = await navigator.RestoreAsync(snapshot);

        Assert.True(result.Accepted);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Single(navigator.History.Entries);
        Assert.Equal(validEntry.Id, navigator.History.Current!.Id);
    }

    [Fact]
    public void UnsupportedSchemaAndExpiredSnapshotsReject()
    {
        var snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable())
            .CreateSnapshot(CommerceState(), NavigationHistory.Empty);
        var unsupported = snapshot with { SchemaVersion = 99 };
        var expired = snapshot with { CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) };

        var unsupportedResult = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).Restore(unsupported);
        var expiredResult = new NavigationSnapshotSerializer(
            TestRoutes.CreateTable(),
            new NavigationPersistenceOptions { MaxSnapshotAge = TimeSpan.FromHours(1) }).Restore(expired);

        Assert.False(unsupportedResult.Accepted);
        Assert.Contains(unsupportedResult.Diagnostics, diagnostic => diagnostic.Code == "snapshot.schema.unsupported");
        Assert.False(expiredResult.Accepted);
        Assert.Contains(expiredResult.Diagnostics, diagnostic => diagnostic.Code == "snapshot.expired");
    }

    [Fact]
    public async Task PlanPoliciesRunDuringRestore()
    {
        var state = CommerceState();
        var snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, NavigationHistory.Empty);
        var policy = new CountingPlanPolicy();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { PlanPolicies = new[] { policy } });

        await navigator.RestoreAsync(snapshot);

        Assert.Equal(1, policy.ApplyCount);
        Assert.Equal(NavigationPlanKind.Restore, policy.LastPlanKind);
    }

    [Fact]
    public async Task RestorePlanPolicyRewriteFinalizesPresentedRouteForPresenterContext()
    {
        var initialState = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("home-stack", new[]
                {
                    new RouteEntry("home", new TestRoutes.StoreRoute("northwind"))
                }))
        }, "main");
        var history = HistoryFor(initialState, new TestRoutes.StoreRoute("northwind"));
        var snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(initialState, history);
        var rewrittenState = ModalCommerceState();
        var expectedRoute = new TestRoutes.ProductDetailRoute("northwind", 123);
        var presenter = new RecordingNavigationPresenter();
        var policy = new RewritePlanPolicy(rewrittenState);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions { PlanPolicies = new[] { policy } });

        var result = await navigator.RestoreAsync(snapshot);

        Assert.True(result.Accepted);
        Assert.Equal(new TestRoutes.StoreRoute("northwind"), policy.LastRoute);
        Assert.Equal(rewrittenState, navigator.CurrentState);
        Assert.Equal(expectedRoute, presenter.LastContext!.Route);
        Assert.Equal(history.Current!.Id, navigator.History.Current!.Id);
        Assert.Equal(expectedRoute, navigator.History.Current!.Route);
        Assert.Equal(rewrittenState, navigator.History.Current!.State);
        Assert.Equal(expectedRoute, navigator.History.Current!.Request.Route);
    }

    [Fact]
    public async Task RestorePlanPolicyRewrite_SavesNormalizedHistoryEntry()
    {
        var initialState = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("home-stack", new[]
                {
                    new RouteEntry("home", new TestRoutes.StoreRoute("northwind"))
                }))
        }, "main");
        var store = new InMemoryNavigationStateStore();
        var history = HistoryFor(initialState, new TestRoutes.StoreRoute("northwind"));
        var snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(initialState, history);
        var rewrittenState = ModalCommerceState();
        var expectedRoute = new TestRoutes.ProductDetailRoute("northwind", 123);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Persistence = new NavigationPersistenceOptions
                {
                    Store = store,
                    BaseUri = BaseUri,
                    PersistModals = true
                },
                PlanPolicies = new[] { new RewritePlanPolicy(rewrittenState) }
            });

        var result = await navigator.RestoreAsync(snapshot);

        Assert.True(result.Accepted);
        Assert.NotNull(store.Snapshot);

        var saved = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).Restore(store.Snapshot!);

        Assert.True(saved.Accepted);
        var savedWindow = saved.State!.ActiveWindow!;
        var savedModalStack = Assert.IsType<StackNode>(Assert.Single(savedWindow.Modals).Content);
        Assert.Equal(expectedRoute, savedModalStack.Top!.Route);
        Assert.Equal(history.Current!.Id, saved.History!.Current!.Id);
        Assert.Equal(expectedRoute, saved.History!.Current!.Route);
        var savedHistoryWindow = saved.History.Current!.State.ActiveWindow!;
        var savedHistoryModalStack = Assert.IsType<StackNode>(Assert.Single(savedHistoryWindow.Modals).Content);
        Assert.Equal(expectedRoute, savedHistoryModalStack.Top!.Route);
        Assert.Equal(expectedRoute, saved.History.Current!.Request.Route);
    }

    [Fact]
    public async Task ConfiguredStoreSavesAfterSuccessfulOperationsAndCanClear()
    {
        var store = new InMemoryNavigationStateStore();
        var options = new RouterNavigatorOptions
        {
            Persistence = new NavigationPersistenceOptions
            {
                Store = store,
                BaseUri = BaseUri
            }
        };
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            options);

        await navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test);
        Assert.Equal(1, store.SaveCount);
        Assert.NotNull(store.Snapshot);

        await navigator.ReconcileAsync(new NavigationReconciliation(
            CommerceState(),
            NavigationReconciliationSource.NativeBackGesture,
            new TestRoutes.ProductDetailRoute("northwind", 123),
            "test"));
        Assert.Equal(2, store.SaveCount);

        await store.ClearAsync();
        Assert.Null(store.Snapshot);
    }

    [Fact]
    public async Task RestoreFromStoreLoadsAndSavesFreshSnapshot()
    {
        var state = CommerceState();
        var history = HistoryFor(state);
        var store = new InMemoryNavigationStateStore
        {
            Snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, history)
        };
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Persistence = new NavigationPersistenceOptions
                {
                    Store = store,
                    BaseUri = BaseUri
                }
            });

        var result = await navigator.RestoreFromStoreAsync();

        Assert.True(result.Accepted);
        Assert.Equal(1, store.LoadCount);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task StoreSaveFailuresEmitDiagnosticsWithoutFailingNavigation()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                Persistence = new NavigationPersistenceOptions
                {
                    Store = new ThrowingSaveStore(),
                    BaseUri = BaseUri
                }
            });

        var result = await navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test);

        Assert.True(result.Presented);
        Assert.Contains(NavigationDiagnosticEventKind.SnapshotSaveFailed, events);
    }

    [Fact]
    public async Task StoreLoadFailuresEmitDiagnosticsAndDoNotMutateNavigation()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                Persistence = new NavigationPersistenceOptions
                {
                    Store = new ThrowingLoadStore(),
                    BaseUri = BaseUri
                }
            });

        var result = await navigator.RestoreFromStoreAsync();

        Assert.False(result.Accepted);
        Assert.Contains("Navigation snapshot load failed", result.RejectionReason);
        Assert.Contains(NavigationDiagnosticEventKind.SnapshotLoadFailed, events);
        Assert.Null(navigator.CurrentState.ActiveWindow);
        Assert.Empty(navigator.History.Entries);
    }

    [Fact]
    public async Task PresentationFailureDoesNotSaveSnapshot()
    {
        var store = new InMemoryNavigationStateStore();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new ThrowingPresenter(),
            new RouterNavigatorOptions
            {
                Persistence = new NavigationPersistenceOptions
                {
                    Store = store,
                    BaseUri = BaseUri
                }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test).AsTask());

        Assert.Equal(0, store.SaveCount);
        Assert.Null(store.Snapshot);
    }

    private static NavigationState CommerceState()
    {
        return new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new TabsNode(
                    "store-tabs",
                    new[]
                    {
                        new NavigationBranch(
                            "home",
                            "Home",
                            new StackNode("home-stack", new[]
                            {
                                new RouteEntry("home", new TestRoutes.StoreRoute("northwind"))
                            })),
                        new NavigationBranch(
                            "catalog",
                            "Catalog",
                            new StackNode("catalog-stack", new[]
                            {
                                new RouteEntry("catalog", new TestRoutes.CatalogRoute("northwind")),
                                new RouteEntry(
                                    "product",
                                    new TestRoutes.ProductDetailRoute("northwind", 123, "blue", "spring"),
                                    ProductTransition())
                            }))
                    },
                    "catalog",
                    "home"))
        }, "main");
    }

    private static NavigationState ModalCommerceState()
    {
        return new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("home-stack", new[]
                {
                    new RouteEntry("home", new TestRoutes.StoreRoute("northwind"))
                }),
                new[]
                {
                    new ModalNode(
                        "catalog-modal",
                        new RouteEntry("catalog-modal-shell", new TestRoutes.StoreRoute("catalog-shell")),
                        new StackNode("catalog-stack", new[]
                        {
                            new RouteEntry("catalog", new TestRoutes.CatalogRoute("northwind")),
                            new RouteEntry("product", new TestRoutes.ProductDetailRoute("northwind", 123))
                        }))
                })
        }, "main");
    }

    private static NavigationHistory HistoryFor(NavigationState state)
    {
        return HistoryFor(state, new TestRoutes.ProductDetailRoute("northwind", 123, "blue", "spring"));
    }

    private static NavigationHistory HistoryFor(NavigationState state, AppRoute route)
    {
        return NavigationHistory.Empty.Push(new NavigationHistoryEntry(
            "history",
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            state,
            "test",
            DateTimeOffset.UtcNow));
    }

    private static SharedElementNavigationTransition ProductTransition()
    {
        return new SharedElementNavigationTransition(
            new[] { SharedElementPair.SameId("product-123") },
            new FadeNavigationTransition(TimeSpan.FromMilliseconds(180)),
            TimeSpan.FromMilliseconds(240));
    }

    private static NavigationSnapshot SnapshotWithRouteUri(string routeUri)
    {
        return new NavigationSnapshot
        {
            State = new NavigationStateSnapshot(
                new[]
                {
                    new WindowNodeSnapshot(
                        "main",
                        new StackNodeSnapshot(
                            "stack",
                            new[]
                            {
                                new RouteEntrySnapshot("missing", routeUri, null, null)
                            }),
                        Array.Empty<ModalNodeSnapshot>())
                },
                "main")
        };
    }

    private sealed class PassThroughMetadataSerializer : INavigationSnapshotMetadataSerializer
    {
        public IReadOnlyDictionary<string, object?>? Serialize(IReadOnlyDictionary<string, object?> metadata)
        {
            return metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, object?>? Deserialize(IReadOnlyDictionary<string, object?> metadata)
        {
            return metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
    }

    private sealed class InMemoryNavigationStateStore : INavigationStateStore
    {
        public NavigationSnapshot? Snapshot { get; set; }

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public ValueTask<NavigationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask SaveAsync(NavigationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Snapshot = snapshot;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSaveStore : INavigationStateStore
    {
        public ValueTask<NavigationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NavigationSnapshot?>(null);
        }

        public ValueTask SaveAsync(NavigationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Save failed.");
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingLoadStore : INavigationStateStore
    {
        public ValueTask<NavigationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        {
            throw new JsonException("Snapshot JSON was corrupt.");
        }

        public ValueTask SaveAsync(NavigationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingPlanPolicy : INavigationPlanPolicy
    {
        public int ApplyCount { get; private set; }

        public NavigationPlanKind? LastPlanKind { get; private set; }

        public ValueTask<NavigationPlan> ApplyAsync(
            NavigationPlanPolicyContext context,
            NavigationPlan plan,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            LastPlanKind = plan.Kind;
            return ValueTask.FromResult(plan);
        }
    }

    private sealed class CapturePlanPolicy : INavigationPlanPolicy
    {
        public AppRoute? LastRoute { get; private set; }

        public ValueTask<NavigationPlan> ApplyAsync(
            NavigationPlanPolicyContext context,
            NavigationPlan plan,
            CancellationToken cancellationToken = default)
        {
            LastRoute = context.Route;
            return ValueTask.FromResult(plan);
        }
    }

    private sealed class RewritePlanPolicy(NavigationState rewrittenState) : INavigationPlanPolicy
    {
        public AppRoute? LastRoute { get; private set; }

        public ValueTask<NavigationPlan> ApplyAsync(
            NavigationPlanPolicyContext context,
            NavigationPlan plan,
            CancellationToken cancellationToken = default)
        {
            LastRoute = context.Route;
            return ValueTask.FromResult(plan with { TargetState = rewrittenState });
        }
    }

    private sealed class ThrowingPresenter : INavigationPresenter
    {
        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add { }
            remove { }
        }

        public ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Presentation failed.");
        }
    }
}
