using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal sealed record MauiNavigationStackSegment(
    string RouteEntryId,
    Page RoutePage,
    IReadOnlyList<Page> PresentationPages,
    int StartIndex,
    int EndIndexExclusive)
{
    public Page TopPage => PresentationPages.Count == 0 ? RoutePage : PresentationPages[^1];
}

internal sealed record MauiNavigationStackProjectionError(int PageIndex, string Message);

internal sealed class MauiNavigationStackProjection
{
    private MauiNavigationStackProjection(
        IReadOnlyList<MauiNavigationStackSegment> segments,
        MauiNavigationStackProjectionError? error)
    {
        Segments = segments;
        Error = error;
    }

    public IReadOnlyList<MauiNavigationStackSegment> Segments { get; }

    public MauiNavigationStackProjectionError? Error { get; }

    public bool IsValid => Error is null;

    public static MauiNavigationStackProjection Create(IReadOnlyList<Page> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var builders = new List<SegmentBuilder>();
        var routeEntryIds = new HashSet<string>(StringComparer.Ordinal);
        SegmentBuilder? current = null;

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var routeEntryId = MauiPresentationMetadata.GetRouteEntryId(page);
            var ownerRouteEntryId = MauiPresentationMetadata.GetPresentationOwnerRouteEntryId(page);
            var presentationKey = MauiPresentationMetadata.GetPresentationPageKey(page);

            if (!string.IsNullOrWhiteSpace(routeEntryId))
            {
                if (!string.IsNullOrWhiteSpace(ownerRouteEntryId) || !string.IsNullOrWhiteSpace(presentationKey))
                {
                    return Invalid(builders, index, "A logical route page cannot also carry route-owned presentation metadata.");
                }

                if (!routeEntryIds.Add(routeEntryId))
                {
                    return Invalid(builders, index, $"Logical route entry '{routeEntryId}' appears more than once in the native stack.");
                }

                current = new SegmentBuilder(routeEntryId, page, index);
                builders.Add(current);
                continue;
            }

            if (string.IsNullOrWhiteSpace(ownerRouteEntryId) || string.IsNullOrWhiteSpace(presentationKey))
            {
                return Invalid(
                    builders,
                    index,
                    "A native stack page must identify either a logical route entry or a route-owned presentation page.");
            }

            if (current is null)
            {
                return Invalid(builders, index, "A route-owned presentation page cannot precede its logical route page.");
            }

            if (!StringComparer.Ordinal.Equals(ownerRouteEntryId, current.RouteEntryId))
            {
                return Invalid(
                    builders,
                    index,
                    $"Presentation page owner '{ownerRouteEntryId}' does not match preceding route entry '{current.RouteEntryId}'.");
            }

            if (!current.Keys.Add(presentationKey))
            {
                return Invalid(
                    builders,
                    index,
                    $"Presentation key '{presentationKey}' appears more than once for route entry '{ownerRouteEntryId}'.");
            }

            current.PresentationPages.Add(page);
        }

        return new MauiNavigationStackProjection(BuildSegments(builders), null);
    }

    public int NativePageCountForSegmentPrefix(int segmentCount)
    {
        if (segmentCount < 0 || segmentCount > Segments.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCount));
        }

        return segmentCount == 0 ? 0 : Segments[segmentCount - 1].EndIndexExclusive;
    }

    private static MauiNavigationStackProjection Invalid(
        IReadOnlyList<SegmentBuilder> builders,
        int pageIndex,
        string message)
    {
        return new MauiNavigationStackProjection(
            BuildSegments(builders),
            new MauiNavigationStackProjectionError(pageIndex, message));
    }

    private static IReadOnlyList<MauiNavigationStackSegment> BuildSegments(
        IReadOnlyList<SegmentBuilder> builders)
    {
        return builders
            .Select(static builder => new MauiNavigationStackSegment(
                builder.RouteEntryId,
                builder.RoutePage,
                builder.PresentationPages.ToArray(),
                builder.StartIndex,
                builder.StartIndex + 1 + builder.PresentationPages.Count))
            .ToArray();
    }

    private sealed class SegmentBuilder(string routeEntryId, Page routePage, int startIndex)
    {
        public string RouteEntryId { get; } = routeEntryId;

        public Page RoutePage { get; } = routePage;

        public int StartIndex { get; } = startIndex;

        public List<Page> PresentationPages { get; } = [];

        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }
}
