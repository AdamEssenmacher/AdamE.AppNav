using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using static AdamE.AppNav.iOS.NativeChecks.Checks;

namespace AdamE.AppNav.iOS.NativeChecks;

/// <summary>The declared topology: what it accepts, and that a plan of any other shape is refused before a page is
/// built or native changes.</summary>
internal static class TopologyChecks
{
    private const string Owned = "owned";
    private const string OwnerKey = "owner";

    public static IEnumerable<(string Name, Func<UIWindow, CancellationToken, Task> Check)> All =>
    [
        ("topology-declaration", DeclarationAsync),
        ("topology-refused-before-building", RefusedBeforeBuildingAsync),
        ("topology-owned-modal", OwnedModalAsync),
        ("topology-misplaced-stack", MisplacedStackAsync),
        ("topology-modal-over-any-entry", OverAnyEntryAsync),
        ("topology-adapted-popover", AdaptedPopoverAsync)
    ];

    private static NavigationTopology OwningTopology { get; } = Topology.WithOwnedModal(
        Owned,
        static entry => entry.Metadata?.TryGetValue(OwnerKey, out object? owner) == true ? owner as string : null);

    private static Task DeclarationAsync(UIWindow window, CancellationToken token)
    {
        Throws<ArgumentException>(
            () => Topology.WithModal("other", NavigationModalPlacement.OverOwnerEntry),
            "An owned modal was declared without its owner.");
        Throws<ArgumentException>(
            () => Topology.WithModal(Sheet, NavigationModalPlacement.OverAnyEntry),
            "A modal was declared twice.");
        Throws<ArgumentException>(() => new NavigationTopology(" ", Stack), "A blank window ID was accepted.");
        NavigationTopology wider = Topology.WithModal("other", NavigationModalPlacement.OverAnyEntry);
        Require(
            !Topology.Modals.ContainsKey("other") && wider.Modals.Count == 2 && wider.Modals.ContainsKey(Sheet),
            "Declaring a modal changed the topology it was declared on, or lost its modals.");
        return Task.CompletedTask;
    }

    // Each shape this version does not present is refused with NotSupportedException, with nothing built and the
    // accepted presentation unchanged.
    private static async Task RefusedBeforeBuildingAsync(UIWindow window, CancellationToken token)
    {
        var root = new ProbeController();
        var navigation = new UINavigationController(root);
        var built = new List<string>();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) =>
            {
                if (entry.Id == "refuse-root")
                    return ValueTask.FromResult(StackPage(navigation));
                built.Add(entry.Id);
                return ValueTask.FromResult(Page(new ProbeController()));
            },
            topology: OwningTopology);
        NavigationState rootState = Shows("refuse-root");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        NavigationState[] refused =
        [
            // Three stack entries.
            Plan([Entry("refuse-root"), Entry("refuse-a"), Entry("refuse-b")]),
            // A modal declared over the root page alone, over a pushed page.
            Plan([Entry("refuse-root"), Entry("refuse-a")], new ModalNode(Sheet, Entry("refuse-sheet"))),
            // An owned modal whose owner is not the top of the stack.
            Plan(
                [Entry("refuse-root"), Entry("refuse-a")],
                new ModalNode(Owned, Entry("refuse-owned", owner: "refuse-root"))),
            // An owned modal with no owner.
            Plan([Entry("refuse-root")], new ModalNode(Owned, Entry("refuse-owned"))),
            // Two modals.
            Plan(
                [Entry("refuse-root")],
                new ModalNode(Sheet, Entry("refuse-sheet")),
                new ModalNode(Owned, Entry("refuse-owned", owner: "refuse-root"))),
            // A modal holding nested topology.
            Plan(
                [Entry("refuse-root")],
                new ModalNode(Sheet, Entry("refuse-sheet"), new StackNode("nested", [Entry("refuse-nested")]))),
            // Another stack, and another window.
            new NavigationState([new WindowNode(Window, new StackNode("other", [Entry("refuse-root")]))], Window),
            new NavigationState([new WindowNode("other", new StackNode(Stack, [Entry("refuse-root")]))], "other"),
            // Two windows, and an empty stack.
            new NavigationState(
                [
                    new WindowNode(Window, new StackNode(Stack, [Entry("refuse-root")])),
                    new WindowNode("second", new StackNode("second-stack", [Entry("refuse-a")]))
                ],
                Window),
            Plan([])
        ];
        foreach (NavigationState state in refused)
            try
            {
                await ApplyAsync(presenter, state, rootState, token);
                throw new InvalidOperationException("A plan the topology does not declare was presented.");
            }
            catch (NotSupportedException) { }

        Require(
            built.Count == 0
            && ReferenceEquals(window.RootViewController, navigation)
            && navigation.ViewControllers is [var only]
            && ReferenceEquals(only, root)
            && root.PresentedViewController is null
            && !root.Retired,
            $"A refused plan built pages ({string.Join(", ", built)}) or changed the accepted presentation.");
    }

    // An owned modal stands over its owner while that owner is the top of the stack: here full screen, so UIKit takes
    // the stack out of the window under it, which the presenter accepts while the modal is mounted.
    private static async Task OwnedModalAsync(UIWindow window, CancellationToken token)
    {
        var root = new ProbeController();
        var pushed = new ProbeController();
        var owned = new ProbeController { ModalPresentationStyle = UIModalPresentationStyle.FullScreen };
        var failures = new List<Exception>();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id switch
                {
                    "owned-root" => UIKitPage.InNavigationStack(root),
                    "owned-pushed" => Page(pushed),
                    "owned-modal" => Page(owned),
                    _ => throw new InvalidOperationException("Unknown owned-modal route.")
                }),
            report: failures.Add,
            topology: OwningTopology);
        NavigationState rootState = Shows("owned-root");
        NavigationState pushedState = Plan([Entry("owned-root"), Entry("owned-pushed")]);
        NavigationState ownedState = Plan(
            [Entry("owned-root"), Entry("owned-pushed")],
            new ModalNode(Owned, Entry("owned-modal", owner: "owned-pushed")));
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        await ApplyAsync(presenter, pushedState, rootState, token);
        await ApplyAsync(presenter, ownedState, pushedState, token);
        var navigation = window.RootViewController as UINavigationController
                         ?? throw new InvalidOperationException("The root page has no navigation stack.");
        Require(
            ReferenceEquals(navigation.PresentedViewController, owned)
            && ReferenceEquals(owned.View?.Window, window)
            && owned.ModalInPresentation
            && navigation.ViewControllers is { Length: 2 },
            "The owned modal was not presented over its owner on the stack.");
        await ApplyAsync(presenter, ownedState, ownedState, token, AdamE.AppNav.Plans.NavigationPlanKind.Refresh);
        // Leaving the modal keeps its owner on the stack; then leaving the owner too.
        await ApplyAsync(presenter, pushedState, ownedState, token);
        Require(
            navigation.PresentedViewController is null
            && owned is { Retired: true, DetachedAtRetirement: true }
            && !pushed.Retired
            && ReferenceEquals(pushed.View?.Window, window)
            && navigation.ViewControllers is { Length: 2 },
            "Leaving the owned modal did not keep its owner shown on the stack.");
        await ApplyAsync(presenter, rootState, pushedState, token);
        Require(
            navigation.PresentedViewController is null
            && owned is { Retired: true, DetachedAtRetirement: true }
            && pushed is { Retired: true, DetachedAtRetirement: true }
            && navigation.ViewControllers is [var remaining] && ReferenceEquals(remaining, root)
            && failures.Count == 0,
            "Leaving the owned modal and its pushed page did not retire both and show the root.");
    }

    // Only a root page can be a navigation stack: a pushed one is refused, and the presenter retires it, since it owns
    // every page a factory returns.
    private static async Task MisplacedStackAsync(UIWindow window, CancellationToken token)
    {
        var root = new ProbeController();
        var misplacedContent = new ProbeController();
        var misplaced = new UINavigationController(misplacedContent);
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id == "misplaced-root"
                    ? UIKitPage.InNavigationStack(root)
                    : new UIKitPage(misplaced) { IsNavigationStack = true }));
        NavigationState rootState = Shows("misplaced-root");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        try
        {
            await ApplyAsync(presenter, Plan([Entry("misplaced-root"), Entry("misplaced")]), rootState, token);
            throw new InvalidOperationException("A pushed page that is a navigation stack was accepted.");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains(
                                                            "Only a root page",
                                                            StringComparison.Ordinal))
        { }

        Require(
            misplacedContent.Retired && !root.Retired && root.View?.Window is not null,
            "A refused navigation-stack page was not retired, or the accepted root page changed.");
    }

    // A modal declared over any entry stands over a pushed page too.
    private static async Task OverAnyEntryAsync(UIWindow window, CancellationToken token)
    {
        const string anywhere = "anywhere";
        NavigationTopology topology = Topology.WithModal(anywhere, NavigationModalPlacement.OverAnyEntry);
        var root = new ProbeController();
        var pushed = new ProbeController();
        // Full screen, so UIKit takes the pushed page out of the window under it, which the stack's verification accepts.
        var modal = new ProbeController { ModalPresentationStyle = UIModalPresentationStyle.FullScreen };
        var replacement = new ProbeController();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id switch
                {
                    "any-root" => UIKitPage.InNavigationStack(root),
                    "any-pushed" => Page(pushed),
                    "any-replacement" => Page(replacement),
                    _ => Page(modal)
                }),
            topology: topology);
        NavigationState pushedState = Plan([Entry("any-root"), Entry("any-pushed")]);
        await ApplyAsync(presenter, Shows("any-root"), NavigationState.Empty, token);
        await ApplyAsync(presenter, pushedState, Shows("any-root"), token);
        await ApplyAsync(
            presenter,
            Plan([Entry("any-root"), Entry("any-pushed")], new ModalNode(anywhere, Entry("any-modal"))),
            pushedState,
            token);
        Require(
            ReferenceEquals(window.RootViewController?.PresentedViewController, modal)
            && ReferenceEquals(modal.View?.Window, window),
            "A modal declared over any entry was not presented over a pushed page.");

        // The modal's own alert is the modal's, not foreign UI over the pushed page: replacing the pushed page under
        // them keeps both.
        using var alert = UIAlertController.Create("Keep?", null, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        await PresentForeignAsync(modal, alert, token);
        await ApplyAsync(
            presenter,
            Plan([Entry("any-root"), Entry("any-replacement")], new ModalNode(anywhere, Entry("any-modal"))),
            Plan([Entry("any-root"), Entry("any-pushed")], new ModalNode(anywhere, Entry("any-modal"))),
            token);
        Require(
            ReferenceEquals(window.RootViewController?.PresentedViewController, modal)
            && ReferenceEquals(modal.PresentedViewController, alert)
            && pushed.Retired
            && (window.RootViewController as UINavigationController)?.ViewControllers is { Length: 2 } replaced
            && ReferenceEquals(replaced[1], replacement),
            "Replacing the pushed page under a modal with its own alert refused, or took the modal or its alert down.");
    }

    // A popover modal in a compact width: UIKit adapts it to a sheet, which keeps the root page in the window, and the
    // presenter presents and verifies it like any other modal.
    private static async Task AdaptedPopoverAsync(UIWindow window, CancellationToken token)
    {
        var root = new ProbeController();
        var popover = new ProbeController { ModalPresentationStyle = UIModalPresentationStyle.Popover };
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) =>
            {
                if (entry.Id == "popover-root")
                    return ValueTask.FromResult(UIKitPage.InNavigationStack(root));
                UIPopoverPresentationController anchor = popover.PopoverPresentationController
                                                         ?? throw new InvalidOperationException("No popover controller.");
                anchor.SourceView = root.View ?? throw new InvalidOperationException("The root page has no view.");
                anchor.SourceRect = new CoreGraphics.CGRect(0, 0, 1, 1);
                return ValueTask.FromResult(Page(popover));
            });
        Require(
            window.TraitCollection.HorizontalSizeClass == UIUserInterfaceSizeClass.Compact,
            "This check needs a compact-width simulator, where a popover adapts.");
        NavigationState rootState = Shows("popover-root");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        await ApplyAsync(presenter, Shows("popover-root", "popover"), rootState, token);
        Require(
            ReferenceEquals(window.RootViewController?.PresentedViewController, popover)
            && ReferenceEquals(popover.View?.Window, window),
            "An adapted popover was not presented and verified.");
    }

    private static RouteEntry Entry(string id, string? owner = null) =>
        new(id, new CheckRoute(), owner is null ? null : new Dictionary<string, object?> { [OwnerKey] = owner });

    private static NavigationState Plan(RouteEntry[] entries, params ModalNode[] modals) =>
        new([new WindowNode(Window, new StackNode(Stack, entries), modals)], Window);

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
