using OpenClaw.Connection.LocalAi;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerInferenceClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:18803/v1");
    private const string Alias = "qwen3.6-35b-a3b-mtp-q4-k-m";

    [Fact]
    public async Task Verify_UsesOpenAiChatEndpointAndReturnsOnlyOperationalEvidence()
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:18803/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal(Alias, body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            return JsonResponse(ValidResponse());
        });
        using var client = new LlamaServerInferenceClient(handler);

        LlamaServerInferenceVerification result = await client.VerifyAsync(Endpoint, Alias);

        Assert.Equal(Alias, result.ModelId);
        Assert.Equal(12, result.PromptTokens);
        Assert.Equal(7, result.CompletionTokens);
        Assert.Equal(45.5, result.PromptMilliseconds);
        Assert.Equal(125.25, result.CompletionMilliseconds);
    }

    [Fact]
    public async Task Verify_AcceptsBoundedReasoningOutputBeforeFinalContent()
    {
        using var client = new LlamaServerInferenceClient(new RecordingHandler(_ =>
            Task.FromResult(JsonResponse(ValidResponse(content: null, reasoningContent: "local inference is ready")))));

        LlamaServerInferenceVerification result = await client.VerifyAsync(Endpoint, Alias);

        Assert.Equal(7, result.CompletionTokens);
    }

    [Theory]
    [InlineData("wrong-model", "ready", 7, true)]
    [InlineData(Alias, "", 7, true)]
    [InlineData(Alias, "ready", 0, true)]
    [InlineData(Alias, "ready", 7, false)]
    public async Task Verify_RejectsIncompleteOrWrongEvidence(
        string model,
        string? content,
        int completionTokens,
        bool includeTimings)
    {
        using var client = new LlamaServerInferenceClient(new RecordingHandler(_ =>
            Task.FromResult(JsonResponse(ValidResponse(model, content, completionTokens, includeTimings)))));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.VerifyAsync(Endpoint, Alias));
    }

    [Fact]
    public async Task Verify_RejectsOversizedResponseBeforeReadingBody()
    {
        using var client = new LlamaServerInferenceClient(new RecordingHandler(_ =>
        {
            var response = JsonResponse(ValidResponse());
            response.Content.Headers.ContentLength = 2 * 1024 * 1024;
            return Task.FromResult(response);
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.VerifyAsync(Endpoint, Alias));
    }

    [Theory]
    [InlineData("http://localhost:18803/v1")]
    [InlineData("https://127.0.0.1:18803/v1")]
    [InlineData("http://127.0.0.1:18803")]
    public async Task Verify_RejectsEndpointOutsideManagedBoundary(string endpoint)
    {
        using var client = new LlamaServerInferenceClient(new RecordingHandler(_ =>
            throw new InvalidOperationException("network must not be used")));

        await Assert.ThrowsAsync<ArgumentException>(() => client.VerifyAsync(new Uri(endpoint), Alias));
    }

    private static string ValidResponse(
        string model = Alias,
        string? content = "ready",
        int completionTokens = 7,
        bool includeTimings = true,
        string? reasoningContent = null)
    {
        var response = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["choices"] = new[]
            {
                new { message = new { role = "assistant", content, reasoning_content = reasoningContent } },
            },
            ["usage"] = new { prompt_tokens = 12, completion_tokens = completionTokens },
        };
        if (includeTimings)
            response["timings"] = new { prompt_ms = 45.5, predicted_ms = 125.25 };
        return JsonSerializer.Serialize(response);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return callback(request);
        }
    }
}
