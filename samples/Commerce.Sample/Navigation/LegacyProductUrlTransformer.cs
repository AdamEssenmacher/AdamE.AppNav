using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;

namespace Commerce.Sample.Navigation;

public sealed class LegacyProductUrlTransformer : INavigationRequestTransformer
{
    public ValueTask<RouterNavigationRequest> TransformAsync(
        NavigationRequestTransformContext context,
        CancellationToken cancellationToken = default)
    {
        RouterNavigationRequest request = context.Request;
        if (request.Uri is not { } uri)
        {
            return ValueTask.FromResult(request);
        }

        string path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        int suffixIndex = path.IndexOfAny(['?', '#']);
        if (suffixIndex >= 0)
            path = path[..suffixIndex];

        if (!path.StartsWith("/p/", StringComparison.Ordinal) ||
            path[3..] is not { Length: > 0 } productId)
        {
            return ValueTask.FromResult(request);
        }

        var normalized = new Uri(
            $"https://example.com/stores/northwind/products/{Uri.EscapeDataString(productId)}");
        return ValueTask.FromResult(request.WithTarget(normalized));
    }
}
