using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.Plans;

public abstract record NavigationTransition;

public sealed record NoNavigationTransition : NavigationTransition;

public sealed record PlatformDefaultNavigationTransition : NavigationTransition;

public sealed record FadeNavigationTransition(TimeSpan? Duration = null) : NavigationTransition;

public sealed record SlideNavigationTransition(
    NavigationSlideDirection Direction,
    TimeSpan? Duration = null) : NavigationTransition;

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
