using OpenClaw.Connection.LocalAi;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace OpenClaw.Connection.Tests;

public sealed class OllamaHealthClientTests
{
    private const string ExactTag = "qwen3.6:35b-a3b-mtp-q4_K_M";
    private static readonly Uri Endpoint = new("http://127.0.0.1:11434");

    [Fact]
    public async Task ProbeModelAvailability_ExactTagOnlyInTags_ReturnsDownloaded()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json($$"""{"models":[{"name":"{{ExactTag}}"}]}"""),
            "/api/ps" => Json("""{"models":[]}"""),
            _ => new(HttpStatusCode.NotFound),
        });

        var availability = await client.ProbeModelAvailabilityAsync(Endpoint, ExactTag, CancellationToken.None);

        Assert.Equal(LocalAiModelAvailabilityState.Downloaded, availability);
    }

    [Fact]
    public async Task ProbeModelAvailability_NonExactNamesNeverMatch_ReturnsNotInstalled()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json($$"""{"models":[{"name":"{{ExactTag}}-extra","model":"library/{{ExactTag}}"}]}"""),
            "/api/ps" => Json($$"""{"models":[{"name":"{{ExactTag.ToUpperInvariant()}}"}]}"""),
            _ => new(HttpStatusCode.NotFound),
        });

        var availability = await client.ProbeModelAvailabilityAsync(Endpoint, ExactTag, CancellationToken.None);

        Assert.Equal(LocalAiModelAvailabilityState.NotInstalled, availability);
    }

    [Fact]
    public async Task ProbeModelAvailability_ExactModelIdentityInPs_ReturnsLoaded()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json($$"""{"models":[{"name":"{{ExactTag}}"}]}"""),
            "/api/ps" => Json($$"""{"models":[{"model":"{{ExactTag}}"}]}"""),
            _ => new(HttpStatusCode.NotFound),
        });

        var availability = await client.ProbeModelAvailabilityAsync(Endpoint, ExactTag, CancellationToken.None);

        Assert.Equal(LocalAiModelAvailabilityState.Loaded, availability);
    }

    [Theory]
    [InlineData("/api/tags")]
    [InlineData("/api/ps")]
    public async Task ProbeModelAvailability_EitherEvidenceEndpointFailure_ReturnsUnknown(string failedPath)
    {
        var requested = new ConcurrentBag<string>();
        using var client = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requested.Add(path);
            return path == failedPath
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("""{"models":[]}""");
        });

        var availability = await client.ProbeModelAvailabilityAsync(Endpoint, ExactTag, CancellationToken.None);

        Assert.Equal(LocalAiModelAvailabilityState.Unknown, availability);
        Assert.Contains("/api/tags", requested);
        Assert.Contains("/api/ps", requested);
    }

    [Fact]
    public async Task ProbeModelAvailability_MalformedEvidenceShape_ReturnsUnknown()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json("""{"models":"not-an-array"}"""),
            "/api/ps" => Json("""{"models":[]}"""),
            _ => new(HttpStatusCode.NotFound),
        });

        var availability = await client.ProbeModelAvailabilityAsync(Endpoint, ExactTag, CancellationToken.None);

        Assert.Equal(LocalAiModelAvailabilityState.Unknown, availability);
    }

    [Fact]
    public async Task ProbeModelAvailability_ResponseOverOneMiB_ReturnsUnknown()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json($$"""{"models":[],"padding":"{{new string('x', 1024 * 1024)}}"}"""),
            "/api/ps" => Json("""{"models":[]}"""),
            _ => new(HttpStatusCode.NotFound),
        });

        var availability = await client.ProbeModelAvailabilityAsync(Endpoint, ExactTag, CancellationToken.None);

        Assert.Equal(LocalAiModelAvailabilityState.Unknown, availability);
    }

    private static OllamaHealthClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
