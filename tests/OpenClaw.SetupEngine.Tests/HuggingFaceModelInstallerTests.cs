using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OpenClaw.SetupEngine.Tests;

public sealed class HuggingFaceModelInstallerTests
{
    private static readonly LocalAiComponentIdentity Component = new("llama-server", "b10488", "win-arm64");

    [Fact]
    public async Task Install_DownloadsVerifiesAndPromotesImmutableModel()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "verified model"u8.ToArray();
        LocalModelInfo model = Model(payload);
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(model.Weights.DownloadUri, request.RequestUri);
            Assert.Null(request.Headers.Range);
            return Response(HttpStatusCode.OK, payload);
        });
        using var client = new HttpClient(handler);
        var progress = new List<HuggingFaceModelInstallProgress>();

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            Component,
            model,
            new Progress<HuggingFaceModelInstallProgress>(progress.Add),
            CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.Downloaded, result.Disposition);
        Assert.True(result.CreatedThisRun);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.ModelPath));
        Assert.False(File.Exists(result.ModelPath + ".partial"));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Install_ResumesValidatedPartialWithRangeRequest()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "resumable model payload"u8.ToArray();
        LocalModelInfo model = Model(payload);
        (string modelPath, string partialPath) = Paths(temp.Path, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, payload[..5]);
        var handler = new RecordingHandler((request, _) =>
        {
            RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(5, range.From);
            var response = Response(HttpStatusCode.PartialContent, payload[5..]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, payload.Length - 1, payload.Length);
            return response;
        });
        using var client = new HttpClient(handler);

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            Component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(modelPath, result.ModelPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(modelPath));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Install_ResumesAfterResponseEndsPrematurely()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "network interruption must resume"u8.ToArray();
        LocalModelInfo model = Model(payload);
        var calls = 0;
        var observedDelays = new List<TimeSpan>();
        var handler = new RecordingHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
                return InterruptingResponse(payload, bytesBeforeFailure: 7);

            RangeItemHeaderValue range = Assert.Single(request.Headers.Range!.Ranges);
            Assert.Equal(7, range.From);
            var response = Response(HttpStatusCode.PartialContent, payload[7..]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(7, payload.Length - 1, payload.Length);
            return response;
        });
        using var client = new HttpClient(handler);
        var installer = new HuggingFaceModelInstaller(
            client,
            (delay, _) =>
            {
                observedDelays.Add(delay);
                return Task.CompletedTask;
            });

        HuggingFaceModelInstallResult result = await installer.InstallAsync(
            temp.Path,
            Component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.ModelPath));
        Assert.Equal(2, handler.CallCount);
        Assert.Equal([TimeSpan.FromSeconds(1)], observedDelays);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Install_RetriesTransientHttpStatus(HttpStatusCode transientStatus)
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "retry status"u8.ToArray();
        LocalModelInfo model = Model(payload);
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
            ++calls == 1 ? Response(transientStatus, []) : Response(HttpStatusCode.OK, payload));
        using var client = new HttpClient(handler);
        var installer = new HuggingFaceModelInstaller(client, (_, _) => Task.CompletedTask);

        HuggingFaceModelInstallResult result = await installer.InstallAsync(
            temp.Path,
            Component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.ModelPath));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Install_ReusesOnlyExactExistingModelWithoutNetwork()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "existing model"u8.ToArray();
        LocalModelInfo model = Model(payload);
        (string modelPath, _) = Paths(temp.Path, model);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, payload);
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("network must not be used"));
        using var client = new HttpClient(handler);

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            Component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, result.Disposition);
        Assert.False(result.CreatedThisRun);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Install_PreservesWrongExistingModelAndDoesNotUseNetwork()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "expected model"u8.ToArray();
        byte[] existing = "wrong existing"u8.ToArray();
        LocalModelInfo model = Model(payload);
        (string modelPath, _) = Paths(temp.Path, model);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, existing);
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("network must not be used"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            new HuggingFaceModelInstaller(client).InstallAsync(
                temp.Path,
                Component,
                model,
                progress: null,
                CancellationToken.None));

        Assert.Equal(existing, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Install_RejectsHashMismatchAndCleansPartial()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] expected = "expected model"u8.ToArray();
        byte[] corrupt = expected.ToArray();
        corrupt[0] ^= 0xff;
        LocalModelInfo model = Model(expected);
        using var client = new HttpClient(new RecordingHandler((_, _) => Response(HttpStatusCode.OK, corrupt)));
        (string modelPath, string partialPath) = Paths(temp.Path, model);

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            new HuggingFaceModelInstaller(client).InstallAsync(
                temp.Path,
                Component,
                model,
                progress: null,
                CancellationToken.None));

        Assert.False(File.Exists(modelPath));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Install_RejectsUntrustedRedirectAndCleansPartial()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "redirect model"u8.ToArray();
        LocalModelInfo model = Model(payload);
        var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/model.gguf") },
        };
        using var client = new HttpClient(new RecordingHandler((_, _) => response));
        (string modelPath, string partialPath) = Paths(temp.Path, model);

        HuggingFaceModelInstallException error = await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            new HuggingFaceModelInstaller(client).InstallAsync(
                temp.Path,
                Component,
                model,
                progress: null,
                CancellationToken.None));

        Assert.Contains("untrusted host", error.Message);
        Assert.False(File.Exists(modelPath));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task Install_FollowsBoundedHuggingFaceCdnRedirect()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "cdn model"u8.ToArray();
        LocalModelInfo model = Model(payload);
        var calls = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("https://cdn-lfs-us-1.hf.co/model.gguf?signature=test") },
                };
            }

            Assert.Equal("cdn-lfs-us-1.hf.co", request.RequestUri?.Host);
            return Response(HttpStatusCode.OK, payload);
        });
        using var client = new HttpClient(handler);

        HuggingFaceModelInstallResult result = await new HuggingFaceModelInstaller(client).InstallAsync(
            temp.Path,
            Component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.ModelPath));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Install_CancellationCleansPreexistingPartial()
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "cancel model"u8.ToArray();
        LocalModelInfo model = Model(payload);
        (_, string partialPath) = Paths(temp.Path, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, payload[..3]);
        using var client = new HttpClient(new RecordingHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Response(HttpStatusCode.PartialContent, payload[3..]);
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HuggingFaceModelInstaller(client).InstallAsync(
                temp.Path,
                Component,
                model,
                progress: null,
                cancellation.Token));

        Assert.False(File.Exists(partialPath));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RemoveInstalledModel_DeletesOnlyFileCreatedThisRun(
        bool createdThisRun,
        bool expectedToRemain)
    {
        using var temp = new TempDirectory("hf-model-");
        byte[] payload = "owned model"u8.ToArray();
        LocalModelInfo model = Model(payload);
        (string modelPath, _) = Paths(temp.Path, model);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, payload);
        using var client = new HttpClient(new RecordingHandler((_, _) =>
            throw new InvalidOperationException("network must not be used")));
        var installer = new HuggingFaceModelInstaller(client);

        installer.RemoveInstalledModel(
            temp.Path,
            new HuggingFaceModelInstallResult(
                modelPath,
                createdThisRun
                    ? HuggingFaceModelInstallDisposition.Downloaded
                    : HuggingFaceModelInstallDisposition.ReusedVerified,
                createdThisRun));

        Assert.Equal(expectedToRemain, File.Exists(modelPath));
    }

    private static LocalModelInfo Model(byte[] payload)
    {
        var source = new HuggingFaceRevisionSource(
            "example/model",
            "0123456789abcdef0123456789abcdef01234567");
        var artifact = new PinnedArtifact(
            "test-model",
            ArtifactRole.ModelWeights,
            source,
            "model.gguf",
            payload.Length,
            new Sha256Digest(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()));
        return LocalModelCatalog.Default with
        {
            Id = "test-model",
            Weights = artifact,
        };
    }

    private static (string ModelPath, string PartialPath) Paths(string localDataDirectory, LocalModelInfo model)
    {
        Assert.True(LocalAiPathPolicy.TryResolve(localDataDirectory, Component, out var paths, out var resolveError), resolveError);
        var source = Assert.IsType<HuggingFaceRevisionSource>(model.Weights.Source);
        Assert.True(LocalAiPathPolicy.TryGetModelPaths(
            paths,
            source.RepositoryId,
            source.RevisionSha,
            model.Weights.RelativePath,
            out var modelPath,
            out var partialPath,
            out var modelError), modelError);
        return (modelPath, partialPath);
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, byte[] content)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(content),
        };
        response.Content.Headers.ContentLength = content.Length;
        return response;
    }

    private static HttpResponseMessage InterruptingResponse(byte[] content, int bytesBeforeFailure)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new InterruptingStream(content, bytesBeforeFailure)),
        };
        response.Content.Headers.ContentLength = content.Length;
        return response;
    }

    private sealed class InterruptingStream(byte[] content, int bytesBeforeFailure) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);
        private bool _failed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failed)
                throw new IOException("response ended prematurely");

            int remainingBeforeFailure = bytesBeforeFailure - (int)_inner.Position;
            if (remainingBeforeFailure <= 0)
            {
                _failed = true;
                throw new IOException("response ended prematurely");
            }

            int count = Math.Min(buffer.Length, remainingBeforeFailure);
            return _inner.ReadAsync(buffer[..count], cancellationToken);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(callback(request, cancellationToken));
        }
    }
}
