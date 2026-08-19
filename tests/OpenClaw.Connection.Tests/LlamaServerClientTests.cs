using OpenClaw.Connection.LocalAi;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:18803/v1");
    private const string Alias = "qwen3.6-35b-a3b-mtp-q4-k-m";
    private const string ModelPath = @"C:\OpenClaw\LocalAI\models\qwen.gguf";

    [Theory]
    [InlineData("unloaded", LocalAiModelAvailabilityState.Verified)]
    [InlineData("loading", LocalAiModelAvailabilityState.Verified)]
    [InlineData("sleeping", LocalAiModelAvailabilityState.Verified)]
    [InlineData("loaded", LocalAiModelAvailabilityState.Loaded)]
    public async Task ProbeRouter_ReportsExactModelWithoutTriggeringAutoload(
        string serverState,
        LocalAiModelAvailabilityState expectedState)
    {
        var requests = new List<Uri>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath == "/health"
                ? Json("{\"status\":\"ok\"}")
                : Json(ModelResponse(Alias, ModelPath, serverState));
        });

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(Endpoint, Alias, ModelPath);

        Assert.True(result.IsHealthy);
        Assert.Equal(expectedState, result.ModelState);
        Assert.Equal(ModelPath, result.ReportedModelPath);
        Assert.Equal(["/health", "/models"], requests.Select(uri => uri.AbsolutePath));
        Assert.Equal("autoload=false", requests[1].Query.TrimStart('?'));
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.StartsWith("/v1/models", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeRouter_AcceptsB10488UnloadedPresetPathFromStatusArguments()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("{\"status\":\"ok\"}")
            : Json(B10488PresetResponse(Alias, ModelPath, "unloaded")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(Endpoint, Alias, ModelPath);

        Assert.True(result.IsHealthy);
        Assert.Equal(LocalAiModelAvailabilityState.Verified, result.ModelState);
        Assert.Equal(ModelPath, result.ReportedModelPath);
    }

    [Fact]
    public async Task ProbeRouter_RejectsConflictingTopLevelAndArgumentPaths()
    {
        string response = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    id = Alias,
                    path = ModelPath,
                    status = new { value = "unloaded", args = new[] { "llama-server.exe", "--model", @"C:\outside\wrong.gguf" } },
                },
            },
        });
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("{\"status\":\"ok\"}")
            : Json(response));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(Endpoint, Alias, ModelPath);

        Assert.True(result.IsHealthy);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
    }

    [Fact]
    public async Task ProbeRouter_ReturnsNotInstalledWhenAliasIsAbsent()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("{\"status\":\"ok\"}")
            : Json(ModelResponse("another-model", ModelPath, "unloaded")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(Endpoint, Alias, ModelPath);

        Assert.True(result.IsHealthy);
        Assert.Equal(LocalAiModelAvailabilityState.NotInstalled, result.ModelState);
    }

    [Fact]
    public async Task ProbeRouter_KeepsRouterHealthyWhenModelEvidenceIsInvalid()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("{\"status\":\"ok\"}")
            : Json(ModelResponse(Alias, @"C:\outside\wrong.gguf", "loaded")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(Endpoint, Alias, ModelPath);

        Assert.True(result.IsHealthy);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
        Assert.Null(result.ReportedModelPath);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"status\":\"ok\"}")]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"loading\"}")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    public async Task ProbeRouter_FailsClosedOnUnhealthyResponse(HttpStatusCode status, string body)
    {
        using var client = Client(_ => Json(body, status));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(Endpoint, Alias, ModelPath);

        Assert.False(result.IsHealthy);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
    }

    [Fact]
    public async Task ProbeRouter_RejectsOversizedModelEvidence()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("{\"status\":\"ok\"}")
            : Json(new string('x', 1024 * 1024 + 1)));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(Endpoint, Alias, ModelPath);

        Assert.True(result.IsHealthy);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
    }

    private static LlamaServerClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHandler(responder));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string ModelResponse(string alias, string path, string state) =>
        JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = alias, path, status = new { value = state } },
            },
        });

    private static string B10488PresetResponse(string alias, string path, string state) =>
        JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    id = alias,
                    status = new { value = state, args = new[] { "llama-server.exe", "--model", path } },
                    source = "preset",
                },
            },
        });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
