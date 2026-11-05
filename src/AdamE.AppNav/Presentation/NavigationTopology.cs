using AdamE.AppNav.State;

namespace AdamE.AppNav.Presentation;

/// <summary>
/// The navigation shape an application presents: one window, whose root is one stack, and the modals it may show over
/// that stack, each with its placement. A native presenter checks every plan against it, and against its own
/// <see cref="NavigationTopologySupport"/>, before it builds or changes anything.
/// </summary>
/// <remarks>
/// The IDs are the structural IDs the application's planner gives the window, its root stack and its modals. The
/// declaration is host-neutral: a UIKit and an Android presenter of the same app take the same topology. What a
/// presenter can show, such as how deep a stack, is that presenter's <see cref="NavigationTopologySupport"/>.
/// </remarks>
public sealed class NavigationTopology
{
    private readonly Dictionary<string, NavigationModalDeclaration> _modals;

    /// <summary>Declares the window and its root stack.</summary>
    /// <param name="windowId">The <see cref="WindowNode"/> ID the planner gives the presented window.</param>
    /// <param name="stackId">The ID of the <see cref="StackNode"/> that is that window's root.</param>
    public NavigationTopology(string windowId, string stackId)
        : this(
            RequireId(windowId, nameof(windowId)),
            RequireId(stackId, nameof(stackId)),
            new Dictionary<string, NavigationModalDeclaration>(StringComparer.Ordinal))
    {
    }

    private NavigationTopology(string windowId, string stackId, Dictionary<string, NavigationModalDeclaration> modals)
    {
        WindowId = windowId;
        StackId = stackId;
        _modals = modals;
    }

    /// <summary>The presented window's ID.</summary>
    public string WindowId { get; }

    /// <summary>The ID of the window's root stack.</summary>
    public string StackId { get; }

    /// <summary>The declared modals, by <see cref="ModalNode"/> ID.</summary>
    public IReadOnlyDictionary<string, NavigationModalDeclaration> Modals => _modals;

    /// <summary>
    /// A copy that also accepts the modal <paramref name="modalId"/> with <paramref name="placement"/>
    /// <see cref="NavigationModalPlacement.OverRoot"/> or <see cref="NavigationModalPlacement.OverAnyEntry"/>.
    /// </summary>
    public NavigationTopology WithModal(string modalId, NavigationModalPlacement placement)
    {
        if (placement is NavigationModalPlacement.OverOwnerEntry)
            throw new ArgumentException(
                "A modal over its owner entry needs that owner: declare it with WithOwnedModal.",
                nameof(placement));
        if (!Enum.IsDefined(placement))
            throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown modal placement.");
        return With(RequireId(modalId, nameof(modalId)), new NavigationModalDeclaration(placement, null));
    }

    /// <summary>
    /// A copy that also accepts the modal <paramref name="modalId"/>, which belongs to one stack entry and is
    /// presented only while that entry is the top of the stack. A modal over a pushed page that belongs to it (an
    /// answer sheet over its question) is one.
    /// </summary>
    /// <param name="modalId">The modal's <see cref="ModalNode"/> ID.</param>
    /// <param name="ownerEntryId">The ID of the stack entry the modal's own entry belongs to, typically read from its
    /// <see cref="RouteEntry.Metadata"/>; null when it has none, which refuses the plan. It must not throw.</param>
    public NavigationTopology WithOwnedModal(string modalId, Func<RouteEntry, string?> ownerEntryId)
    {
        ArgumentNullException.ThrowIfNull(ownerEntryId);
        return With(
            RequireId(modalId, nameof(modalId)),
            new NavigationModalDeclaration(NavigationModalPlacement.OverOwnerEntry, ownerEntryId));
    }

    /// <summary>
    /// The plan's shape, checked against this declaration and against what <paramref name="support"/> says a presenter
    /// can show, or null for a plan with no window. Nothing is built or changed by this.
    /// </summary>
    /// <exception cref="NotSupportedException">The plan has another shape, or one the presenter cannot show; the
    /// message names which.</exception>
    public NavigationTopologyMatch? Match(NavigationState state, NavigationTopologySupport support)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(support);
        if (state.Windows.Count == 0)
            return null;
        if (state.Windows.Count != 1 || state.ActiveWindow is not { } window || window.Id != WindowId)
            throw new NotSupportedException(
                $"The topology declares the one window '{WindowId}'; the plan has {state.Windows.Count} window(s).");
        if (window.Root is not StackNode stack || stack.Id != StackId)
            throw new NotSupportedException($"The window's root must be the stack '{StackId}'.");
        if (stack.Entries.Count < 1)
            throw new NotSupportedException($"The stack '{StackId}' has no entry.");
        if (stack.Entries.Count > support.MaxStackEntries)
            throw new NotSupportedException(
                $"This presenter shows a stack of 1 to {support.MaxStackEntries} entries; the plan has "
                + $"{stack.Entries.Count}.");
        if (window.Modals.Count > support.MaxModals)
            throw new NotSupportedException(
                $"This presenter shows at most {support.MaxModals} modal(s) at a time; the plan has "
                + $"{window.Modals.Count}.");
        var modals = new List<NavigationTopologyModal>(window.Modals.Count);
        foreach (ModalNode modal in window.Modals)
        {
            if (!_modals.TryGetValue(modal.Id, out NavigationModalDeclaration? declaration))
                throw new NotSupportedException($"Modal '{modal.Id}' is not declared in the topology.");
            if (modal.Content is not null && !support.ModalContent)
                throw new NotSupportedException(
                    $"This presenter shows no topology nested in a modal; modal '{modal.Id}' has some.");
            string? owner = null;
            switch (declaration.Placement)
            {
                case NavigationModalPlacement.OverRoot when stack.Entries.Count != 1:
                    throw new NotSupportedException(
                        $"Modal '{modal.Id}' is declared over the root entry only, but the stack has a pushed entry.");
                case NavigationModalPlacement.OverOwnerEntry:
                    owner = declaration.OwnerEntryId?.Invoke(modal.RouteEntry);
                    if (owner is null || owner != stack.Top?.Id)
                        throw new NotSupportedException(
                            $"Modal '{modal.Id}' belongs to entry '{owner ?? "(none)"}', which is not the top of the stack.");
                    break;
            }

            modals.Add(new NavigationTopologyModal(modal, owner));
        }

        return new NavigationTopologyMatch(window, stack, modals);
    }

    private NavigationTopology With(string modalId, NavigationModalDeclaration declaration)
    {
        if (_modals.ContainsKey(modalId))
            throw new ArgumentException($"Modal '{modalId}' is already declared.", nameof(modalId));
        return new NavigationTopology(
            WindowId,
            StackId,
            new Dictionary<string, NavigationModalDeclaration>(_modals, StringComparer.Ordinal)
            {
                [modalId] = declaration
            });
    }

    private static string RequireId(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, name);
        return id;
    }
}

/// <summary>How a declared modal may be placed over the stack.</summary>
/// <param name="Placement">Where the modal may stand.</param>
/// <param name="OwnerEntryId">For <see cref="NavigationModalPlacement.OverOwnerEntry"/>, the owner stack entry's ID of
/// a modal entry; otherwise null.</param>
public sealed record NavigationModalDeclaration(
    NavigationModalPlacement Placement,
    Func<RouteEntry, string?>? OwnerEntryId);

/// <summary>Where a declared modal may stand over the stack.</summary>
public enum NavigationModalPlacement
{
    /// <summary>Only while the stack holds the root entry alone.</summary>
    OverRoot = 0,

    /// <summary>Over any stack.</summary>
    OverAnyEntry = 1,

    /// <summary>Only while the entry it belongs to is the top of the stack; declared with
    /// <see cref="NavigationTopology.WithOwnedModal"/>.</summary>
    OverOwnerEntry = 2
}

/// <summary>What one presenter can show of a <see cref="NavigationTopology"/>: limits that grow as the presenter does,
/// without changing the declaration.</summary>
/// <param name="MaxStackEntries">The deepest stack it shows, the root entry included; at least 1.</param>
/// <param name="MaxModals">The most modals it shows at once.</param>
/// <param name="ModalContent">Whether it shows topology nested in a modal (<see cref="ModalNode.Content"/>).</param>
public sealed record NavigationTopologySupport(int MaxStackEntries, int MaxModals = 1, bool ModalContent = false)
{
    /// <summary>The deepest stack it shows, the root entry included; at least 1.</summary>
    public int MaxStackEntries { get; init; } = MaxStackEntries >= 1
        ? MaxStackEntries
        : throw new ArgumentOutOfRangeException(nameof(MaxStackEntries), MaxStackEntries, "A stack holds its root.");

    /// <summary>The most modals it shows at once.</summary>
    public int MaxModals { get; init; } = MaxModals >= 0
        ? MaxModals
        : throw new ArgumentOutOfRangeException(nameof(MaxModals), MaxModals, "A count of modals is not negative.");
}

/// <summary>A plan's shape, matched against a <see cref="NavigationTopology"/>.</summary>
/// <param name="Window">The presented window.</param>
/// <param name="Stack">Its root stack.</param>
/// <param name="Modals">Its modals, in order, each with its owner entry.</param>
public sealed record NavigationTopologyMatch(
    WindowNode Window,
    StackNode Stack,
    IReadOnlyList<NavigationTopologyModal> Modals);

/// <summary>One modal of a matched plan.</summary>
/// <param name="Node">The modal.</param>
/// <param name="OwnerEntryId">For a modal declared over its owner entry, that entry's ID; otherwise null.</param>
public sealed record NavigationTopologyModal(ModalNode Node, string? OwnerEntryId);
