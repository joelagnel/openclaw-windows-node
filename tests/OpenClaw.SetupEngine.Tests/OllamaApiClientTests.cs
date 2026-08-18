using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class OllamaApiClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:11434");

    [Fact]
    public async Task GetVersionAsync_UsesNativeVersionEndpoint()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1:11434/api/version", request.RequestUri?.AbsoluteUri);
            return JsonResponse("""{"version":"0.32.14"}""");
        });
        using var httpClient = new HttpClient(handler);
        var client = new OllamaApiClient(httpClient, Endpoint);

        var result = await client.GetVersionAsync(CancellationToken.None);

        Assert.Equal("0.32.14", result.Version);
    }

    [Fact]
    public async Task ListModelsAsync_ParsesTagsAndSkipsNamelessEntries()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal("/api/tags", request.RequestUri?.AbsolutePath);
            return JsonResponse("""
                {
                  "models": [
                    {
                      "name": "qwen3.6:35b-a3b-mtp-q4_K_M",
                      "model": "qwen3.6:35b-a3b-mtp-q4_K_M",
                      "modified_at": "2026-08-17T12:34:56Z",
                      "size": 22621302688,
                      "digest": "sha256:abc"
                    },
                    { "size": -10 }
                  ]
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var client = new OllamaApiClient(httpClient, Endpoint);

        var models = await client.ListModelsAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal(LocalAiConfig.DefaultModel, model.Name);
        Assert.Equal(22_621_302_688, model.SizeBytes);
        Assert.Equal("sha256:abc", model.Digest);
        Assert.Equal(DateTimeOffset.Parse("2026-08-17T12:34:56Z"), model.ModifiedAt);
    }

    [Fact]
    public async Task DeleteModelAsync_UsesExactNativeDeleteEndpoint()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/api/delete", request.RequestUri?.AbsolutePath);
            var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var requestDocument = JsonDocument.Parse(requestJson);
            Assert.Equal(LocalAiConfig.DefaultModel, requestDocument.RootElement.GetProperty("model").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var client = new OllamaApiClient(httpClient, Endpoint);

        await client.DeleteModelAsync(LocalAiConfig.DefaultModel, CancellationToken.None);
    }

    [Fact]
    public async Task PullModelAsync_StreamsAndAggregatesPerDigestProgress()
    {
        const string progressBody = """
            {"status":"pulling layer","digest":"sha256:a","total":100,"completed":25}
            {"status":"pulling layer","digest":"sha256:a","total":100,"completed":100}
            {"status":"pulling layer","digest":"sha256:b","total":50,"completed":10}
            {"status":"pulling layer","digest":"sha256:b","total":50,"completed":50}
            {"status":"success"}
            """;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/pull", request.RequestUri?.AbsolutePath);
            var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var requestDocument = JsonDocument.Parse(requestJson);
            Assert.Equal(LocalAiConfig.DefaultModel, requestDocument.RootElement.GetProperty("model").GetString());
            Assert.True(requestDocument.RootElement.GetProperty("stream").GetBoolean());
            return JsonResponse(progressBody, mediaType: "application/x-ndjson");
        });
        using var httpClient = new HttpClient(handler);
        var client = new OllamaApiClient(httpClient, Endpoint);
        var progress = new RecordingProgress<OllamaPullProgress>();

        var result = await client.PullModelAsync(
            LocalAiConfig.DefaultModel,
            expectedBytes: 150,
            progress,
            CancellationToken.None);

        Assert.Equal(LocalAiConfig.DefaultModel, result.Model);
        Assert.Equal(150, result.TransferredBytes);
        Assert.Equal(150, result.ExpectedBytes);
        Assert.Equal([25, 100, 110, 150, 150], progress.Values.Select(value => value.TransferredBytes).ToArray());
        Assert.True(progress.Values[^1].IsComplete);
        Assert.Equal(1d, progress.Values[^1].Fraction);
    }

    [Fact]
    public async Task PullModelAsync_RequiresTerminalSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """{"status":"pulling layer","digest":"sha256:a","total":100,"completed":100}""",
            mediaType: "application/x-ndjson"));
        using var httpClient = new HttpClient(handler);
        var client = new OllamaApiClient(httpClient, Endpoint);

        var error = await Assert.ThrowsAsync<OllamaApiException>(() => client.PullModelAsync(
            LocalAiConfig.DefaultModel,
            expectedBytes: 100,
            progress: null,
            CancellationToken.None));

        Assert.Contains("before success", error.Message);
    }

    [Fact]
    public async Task PullModelAsync_PropagatesStructuredServerError()
    {
        var handler = new StubHttpMessageHandler((_, _) => JsonResponse(
            """{"error":"model was not found\nretry later"}""",
            mediaType: "application/x-ndjson"));
        using var httpClient = new HttpClient(handler);
        var client = new OllamaApiClient(httpClient, Endpoint);

        var error = await Assert.ThrowsAsync<OllamaApiException>(() => client.PullModelAsync(
            "missing:model",
            expectedBytes: null,
            progress: null,
            CancellationToken.None));

        Assert.Contains("model was not found retry later", error.Message);
        Assert.DoesNotContain('\n', error.Message);
    }

    [Fact]
    public async Task PullModelAsync_ObservesCancellationWhileReadingStream()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellationBlockingStream()),
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new OllamaApiClient(httpClient, Endpoint);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PullModelAsync(
            LocalAiConfig.DefaultModel,
            expectedBytes: LocalAiConfig.DefaultModelDownloadSizeBytes,
            progress: null,
            cancellation.Token));
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:11434")]
    [InlineData("http://user:password@127.0.0.1:11434")]
    [InlineData("http://127.0.0.1:11434?token=value")]
    public void Constructor_RejectsUnsafeEndpointShape(string endpoint)
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => JsonResponse("{}")));

        Assert.Throws<ArgumentException>(() => new OllamaApiClient(httpClient, new Uri(endpoint)));
    }

    private static HttpResponseMessage JsonResponse(string json, string mediaType = "application/json")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType),
        };

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private sealed class CancellationBlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
