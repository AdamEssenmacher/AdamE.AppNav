using AdamE.AppNav.State;

namespace AdamE.AppNav.iOS;

/// <summary>
/// The navigation shape one <see cref="UIKitNavigationPresenter"/> presents, declared by the structural IDs the
/// application's planner uses. The presenter checks every plan against it before it builds or changes anything, and
/// rejects a plan of any other shape with <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The shape is one <see cref="WindowNode"/> whose root is one <see cref="StackNode"/>, shown in one
/// <c>UIWindow</c>. The stack's first entry is the window's root page, and the entries above it are pushed onto that
/// page's <c>UINavigationController</c>. A declared <see cref="ModalNode"/> is presented over the stack.
/// </para>
/// <para>
/// This version supports at most <see cref="MaxStackEntries"/> stack entries and one modal at a time, and a modal
/// that holds no nested <see cref="ModalNode.Content"/>. The topology API is shaped for deeper stacks and modals with
/// their own stacks, which later versions add without changing it.
/// </para>
/// </remarks>
public sealed class UIKitNavigationTopology
{
    private readonly Dictionary<string, UIKitModalDeclaration> _modals;

    /// <summary>Declares the window and its root stack.</summary>
    /// <param name="windowId">The <see cref="WindowNode"/> ID the planner gives the presented window.</param>
    /// <param name="stackId">The ID of the <see cref="StackNode"/> that is that window's root.</param>
    public UIKitNavigationTopology(string windowId, string stackId)
        : this(
            RequireId(windowId, nameof(windowId)),
            RequireId(stackId, nameof(stackId)),
            new Dictionary<string, UIKitModalDeclaration>(StringComparer.Ordinal))
    {
    }

    private UIKitNavigationTopology(
        string windowId,
        string stackId,
        Dictionary<string, UIKitModalDeclaration> modals)
    {
        WindowId = windowId;
        StackId = stackId;
        _modals = modals;
    }

    /// <summary>The most stack entries this version presents: the root page and one page pushed over it.</summary>
    public static int MaxStackEntries => 2;

    /// <summary>The presented window's ID.</summary>
    public string WindowId { get; }

    /// <summary>The ID of the window's root stack.</summary>
    public string StackId { get; }

    /// <summary>The declared modals, by <see cref="ModalNode"/> ID.</summary>
    public IReadOnlyDictionary<string, UIKitModalDeclaration> Modals => _modals;

    /// <summary>
    /// A copy that also accepts the modal <paramref name="modalId"/> with <paramref name="placement"/>
    /// <see cref="UIKitModalPlacement.OverRoot"/> or <see cref="UIKitModalPlacement.OverAnyEntry"/>.
    /// </summary>
    public UIKitNavigationTopology WithModal(string modalId, UIKitModalPlacement placement)
    {
        if (placement is UIKitModalPlacement.OverOwnerEntry)
            throw new ArgumentException(
                "A modal over its owner entry needs that owner: declare it with WithOwnedModal.",
                nameof(placement));
        if (!Enum.IsDefined(placement))
            throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown modal placement.");
        return With(RequireId(modalId, nameof(modalId)), new UIKitModalDeclaration(placement, null));
    }

    /// <summary>
    /// A copy that also accepts the modal <paramref name="modalId"/>, which belongs to one stack entry and is
    /// presented only while that entry is the top of the stack. A modal over a pushed page that belongs to it (an
    /// answer sheet over its question) is one.
    /// </summary>
    /// <param name="modalId">The modal's <see cref="ModalNode"/> ID.</param>
    /// <param name="ownerEntryId">The ID of the stack entry the modal's own entry belongs to, typically read from its
    /// <see cref="RouteEntry.Metadata"/>. It must not throw.</param>
    public UIKitNavigationTopology WithOwnedModal(string modalId, Func<RouteEntry, string?> ownerEntryId)
    {
        ArgumentNullException.ThrowIfNull(ownerEntryId);
        return With(
            RequireId(modalId, nameof(modalId)),
            new UIKitModalDeclaration(UIKitModalPlacement.OverOwnerEntry, ownerEntryId));
    }

    private UIKitNavigationTopology With(string modalId, UIKitModalDeclaration declaration)
    {
        if (_modals.ContainsKey(modalId))
            throw new ArgumentException($"Modal '{modalId}' is already declared.", nameof(modalId));
        return new UIKitNavigationTopology(
            WindowId,
            StackId,
            new Dictionary<string, UIKitModalDeclaration>(_modals, StringComparer.Ordinal)
            {
                [modalId] = declaration
            });
    }

    private static string RequireId(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, name);
        return id;
    }

    /// <summary>The shape this presenter supports, checked before anything is built: the plan's windows, stack depth
    /// and modals, and each modal's placement.</summary>
    internal (StackNode Stack, ModalNode? Modal, string? ModalOwner)? Validate(NavigationState state)
    {
        if (state.Windows.Count == 0)
            return null;
        if (state.Windows.Count != 1
            || state.ActiveWindow is not { } window
            || window.Id != WindowId)
            throw new NotSupportedException(
                $"This presenter shows the one window '{WindowId}'; the plan has {state.Windows.Count} window(s).");
        if (window.Root is not StackNode stack || stack.Id != StackId)
            throw new NotSupportedException($"The window's root must be the stack '{StackId}'.");
        if (stack.Entries.Count is < 1 || stack.Entries.Count > MaxStackEntries)
            throw new NotSupportedException(
                $"This version presents a stack of 1 to {MaxStackEntries} entries; the plan has {stack.Entries.Count}.");
        if (window.Modals.Count > 1)
            throw new NotSupportedException(
                $"This version presents one modal at a time; the plan has {window.Modals.Count}.");
        if (window.Modals.SingleOrDefault() is not { } modal)
            return (stack, null, null);
        if (modal.Content is not null)
            throw new NotSupportedException(
                $"This version presents no topology nested in a modal; modal '{modal.Id}' has some.");
        if (!_modals.TryGetValue(modal.Id, out UIKitModalDeclaration? declaration))
            throw new NotSupportedException($"Modal '{modal.Id}' is not declared in this presenter's topology.");
        string? owner = null;
        switch (declaration.Placement)
        {
            case UIKitModalPlacement.OverRoot when stack.Entries.Count != 1:
                throw new NotSupportedException(
                    $"Modal '{modal.Id}' is declared over the root page only, but the stack has a pushed page.");
            case UIKitModalPlacement.OverOwnerEntry:
                owner = declaration.OwnerEntryId?.Invoke(modal.RouteEntry);
                if (owner is null || owner != stack.Top?.Id)
                    throw new NotSupportedException(
                        $"Modal '{modal.Id}' belongs to entry '{owner ?? "(none)"}', which is not the top of the stack.");
                break;
        }

        return (stack, modal, owner);
    }
}

/// <summary>How a declared modal may be placed over the stack.</summary>
/// <param name="Placement">Where the modal may stand.</param>
/// <param name="OwnerEntryId">For <see cref="UIKitModalPlacement.OverOwnerEntry"/>, the owner stack entry's ID of a
/// modal entry; otherwise null.</param>
public sealed record UIKitModalDeclaration(UIKitModalPlacement Placement, Func<RouteEntry, string?>? OwnerEntryId);

/// <summary>Where a declared modal may stand over the stack.</summary>
public enum UIKitModalPlacement
{
    /// <summary>Only while the stack holds the root page alone.</summary>
    OverRoot = 0,

    /// <summary>Over any stack the topology supports.</summary>
    OverAnyEntry = 1,

    /// <summary>Only while the entry it belongs to is the top of the stack; declared with
    /// <see cref="UIKitNavigationTopology.WithOwnedModal"/>.</summary>
    OverOwnerEntry = 2
}
