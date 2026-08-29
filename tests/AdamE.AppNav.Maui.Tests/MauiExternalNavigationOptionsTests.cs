using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Requests;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.AppNav.Maui.Tests;

[Collection(ExternalNavigationBridgeTestCollection.Name)]
public sealed class MauiExternalNavigationOptionsTests
{
    [Fact]
    public void Defaults_AreBoundedPreviewDefaults()
    {
        var options = new MauiExternalNavigationOptions();

        Assert.Empty(options.AllowedOrigins);
        Assert.Equal(2048, options.MaximumUriLength);
        Assert.Equal(32, options.MaximumPendingRequests);
        Assert.Equal(3, options.MaximumDispatchAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RetryDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), options.MaximumRequestAge);
    }

    [Fact]
    public void EnablingUriIngressWithoutAnOrigin_FailsClosed()
    {
        var options = new MauiExternalNavigationOptions();

        var exception = Assert.Throws<InvalidOperationException>(options.ValidateForEnablement);

        Assert.Contains("at least one trusted origin", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?query")]
    [InlineData("https://example.com/?")]
    [InlineData("https://example.com#fragment")]
    public void AllowOrigin_RejectsValuesThatAreNotAbsoluteRootOrigins(string value)
    {
        var options = new MauiExternalNavigationOptions();
        var origin = new Uri(value, UriKind.RelativeOrAbsolute);

        Assert.Throws<ArgumentException>(() => options.AllowOrigin(origin));
    }

    [Fact]
    public void AllowOrigin_NormalizesIdnHostAndDefaultPort()
    {
        var options = new MauiExternalNavigationOptions()
            .AllowOrigin(new Uri("https://bücher.example"));

        Assert.True(Accepted(options, "https://xn--bcher-kva.example:443/catalog?q=private"));
        Assert.False(Accepted(options, "https://xn--bcher-kva.example:444/catalog"));
    }

    [Fact]
    public void AllowOrigin_DeduplicatesNormalizedOriginsAndExposesReadOnlyView()
    {
        var options = new MauiExternalNavigationOptions()
            .AllowOrigin(new Uri("https://EXAMPLE.com"))
            .AllowOrigin(new Uri("https://example.com:443/"));

        Uri allowed = Assert.Single(options.AllowedOrigins);
        Assert.Equal("example.com", allowed.IdnHost);
        Assert.True(Assert.IsAssignableFrom<IList<Uri>>(options.AllowedOrigins).IsReadOnly);
    }

    [Fact]
    public void Validation_RejectsBeforeCallingApplicationFilter()
    {
        var filterCalls = 0;
        var options = new MauiExternalNavigationOptions
        {
            MaximumUriLength = 64,
            ShouldDispatch = _ =>
            {
                filterCalls++;
                return true;
            }
        };
        options.AllowOrigin(new Uri("https://example.com"));

        Assert.False(Accepted(options, "/relative"));
        Assert.False(Accepted(options, "https://user:secret@example.com/catalog"));
        Assert.False(Accepted(options, $"https://example.com/{new string('a', 80)}"));
        Assert.False(Accepted(options, "https://attacker.example/catalog"));
        Assert.False(Accepted(options, "https://example.com:444/catalog"));
        Assert.Equal(0, filterCalls);

        Assert.True(Accepted(options, "https://example.com/catalog"));
        Assert.Equal(1, filterCalls);
    }

    [Fact]
    public void EmptyAllowlist_RejectsUriButAllowsCanonicalRouteRequests()
    {
        var options = new MauiExternalNavigationOptions();
        using ServiceProvider provider = CreateProvider(options);
        IMauiExternalNavigationDispatcher dispatcher =
            provider.GetRequiredService<IMauiExternalNavigationDispatcher>();

        Assert.False(dispatcher.TryDispatch(RouterNavigationRequest.FromUri(
            new Uri("https://example.com/catalog"),
            NavigationRequestSource.AppLink)));
        Assert.True(dispatcher.TryDispatch(RouterNavigationRequest.FromRoute(
            new TestRoute("catalog"),
            NavigationRequestSource.Push)));
    }

    [Fact]
    public void FutureTimestamp_IsRejectedBeforeApplicationFilter()
    {
        var filterCalls = 0;
        var options = new MauiExternalNavigationOptions
        {
            ShouldDispatch = _ =>
            {
                filterCalls++;
                return true;
            }
        };
        RouterNavigationRequest request = RouterNavigationRequest.FromRoute(
            new TestRoute("future"),
            NavigationRequestSource.Push) with
        {
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        Assert.False(options.TryAccept(
            request,
            DateTimeOffset.UtcNow,
            out MauiExternalNavigationRejectionReason reason));
        Assert.Equal(MauiExternalNavigationRejectionReason.FutureTimestamp, reason);
        Assert.Equal(0, filterCalls);
    }

    [Fact]
    public void RejectionDiagnostics_DoNotContainUriOrProvenanceSecrets()
    {
        var options = new MauiExternalNavigationOptions()
            .AllowOrigin(new Uri("https://example.com"));
        var diagnostics = new NavigationDiagnostics(options: new NavigationDiagnosticsOptions
        {
            DataMode = NavigationDiagnosticDataMode.Full
        });
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        using ServiceProvider provider = CreateProvider(options, diagnostics);
        IMauiExternalNavigationDispatcher dispatcher =
            provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var uri = new Uri("https://attacker.example/catalog?token=secret");
        var request = RouterNavigationRequest.FromUri(
            uri,
            NavigationRequestSource.Push,
            provenance: new NavigationRequestProvenance(
                provider: "branch",
                originalUri: uri,
                correlationId: "secret-correlation",
                attributes: new Dictionary<string, string?> { ["secret-key"] = "secret-value" }));

        Assert.False(dispatcher.TryDispatch(request));

        NavigationDiagnosticEvent rejected = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationRejected);
        Assert.DoesNotContain(rejected.Data.Values, value =>
            value?.ToString()?.Contains("secret", StringComparison.OrdinalIgnoreCase) == true);
        Assert.False(rejected.Data.ContainsKey(NavigationDiagnosticDataKeys.Uri));
        Assert.False(rejected.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceOriginalUri));
        Assert.False(rejected.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceAttributes));
        Assert.False(rejected.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceProvider));
    }

    [Fact]
    public void DefaultFailureClassifier_DropsConfigurationFailuresAndRetriesUnknownFailures()
    {
        var options = new MauiExternalNavigationOptions();

        Assert.Equal(
            MauiExternalNavigationFailureDisposition.Drop,
            options.Classify(new AppNavigationConfigurationException("Invalid AppNav configuration.")));
        Assert.Equal(
            MauiExternalNavigationFailureDisposition.Retry,
            options.Classify(new InvalidOperationException("Unknown application failure.")));
    }

    private static bool Accepted(MauiExternalNavigationOptions options, string uri)
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri(uri, UriKind.RelativeOrAbsolute),
            NavigationRequestSource.AppLink);
        return options.TryAccept(
            request,
            DateTimeOffset.UtcNow,
            out MauiExternalNavigationRejectionReason _);
    }

    private static ServiceProvider CreateProvider(
        MauiExternalNavigationOptions options,
        NavigationDiagnostics? diagnostics = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(diagnostics ?? new NavigationDiagnostics());
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());
        return services.BuildServiceProvider();
    }

    private sealed record TestRoute(string Id) : AppRoute;
}
