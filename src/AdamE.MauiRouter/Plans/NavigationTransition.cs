using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.Plans;

public abstract record NavigationTransition
{
    private protected NavigationTransition()
    {
    }

    protected NavigationTransition(NavigationTransition original)
    {
    }

    // This non-public abstract member closes the hierarchy to built-ins in this assembly.
    private protected abstract bool BuiltInTransitionMarker { get; }
}

public sealed record NoNavigationTransition : NavigationTransition
{
    private protected override bool BuiltInTransitionMarker => true;
}

public sealed record PlatformDefaultNavigationTransition : NavigationTransition
{
    private protected override bool BuiltInTransitionMarker => true;
}

public sealed record FadeNavigationTransition(TimeSpan? Duration = null) : NavigationTransition
{
    private protected override bool BuiltInTransitionMarker => true;
}

public sealed record SlideNavigationTransition(
    NavigationSlideDirection Direction,
    TimeSpan? Duration = null) : NavigationTransition
{
    private protected override bool BuiltInTransitionMarker => true;
}

public sealed record SharedElementNavigationTransition : NavigationTransition
{
    private IReadOnlyList<SharedElementPair> _elements = CollectionSnapshot.List<SharedElementPair>(null);

    public SharedElementNavigationTransition(
        IReadOnlyList<SharedElementPair> Elements,
        NavigationTransition? Fallback = null,
        TimeSpan? Duration = null)
    {
        this.Elements = Elements;
        this.Fallback = Fallback;
        this.Duration = Duration;
    }

    public IReadOnlyList<SharedElementPair> Elements
    {
        get => _elements;
        init => _elements = CollectionSnapshot.List(value);
    }

    public NavigationTransition? Fallback { get; init; }

    public TimeSpan? Duration { get; init; }

    public void Deconstruct(
        out IReadOnlyList<SharedElementPair> Elements,
        out NavigationTransition? Fallback,
        out TimeSpan? Duration)
    {
        Elements = this.Elements;
        Fallback = this.Fallback;
        Duration = this.Duration;
    }

    private protected override bool BuiltInTransitionMarker => true;
}

public sealed record SharedElementPair(string SourceId, string DestinationId)
{
    public static SharedElementPair SameId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new SharedElementPair(id, id);
    }
}

public enum NavigationSlideDirection
{
    Left,
    Right,
    Up,
    Down
}
