using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiArtifactInstallerTests : IDisposable
{
    private readonly TempDirectory _tempDirectory = new("local-ai-installer-");

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public async Task InstallAsync_VerifiesExtractsAndAtomicallyPromotesArtifact()
    {
        var archive = CreateArchive(("ollama.exe", "native executable"), ("lib/cuda.dll", "cuda"));
        var artifact = CreateArtifact(archive);
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(artifact.DownloadUri, request.RequestUri);
            return BinaryResponse(archive);
        });
        using var httpClient = new HttpClient(handler);
        var installer = new LocalAiArtifactInstaller(httpClient);
        var reported = new RecordingProgress<LocalAiArtifactInstallProgress>();
        var events = new List<LocalAiArtifactInstallProgress>();
        installer.ProgressChanged += (_, value) => events.Add(value);

        var result = await installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            reported,
            CancellationToken.None);

        Assert.True(result.CreatedEngineDirectory);
        Assert.Equal(result.EngineDirectory, result.RollbackDirectory);
        Assert.Equal(archive.Length, result.VerifiedArchiveSizeBytes);
        Assert.Equal(artifact.Sha256, result.VerifiedArchiveSha256);
        Assert.Equal("native executable", await File.ReadAllTextAsync(result.EngineExecutablePath));
        Assert.Equal("cuda", await File.ReadAllTextAsync(Path.Combine(result.EngineDirectory, "lib", "cuda.dll")));
        Assert.False(File.Exists(GetPartialPath(artifact)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(GetStagingRoot()));
        Assert.Equal(LocalAiArtifactInstallPhase.Complete, reported.Values[^1].Phase);
        Assert.Equal(reported.Values, events);
        Assert.Contains(reported.Values, value =>
            value.Phase == LocalAiArtifactInstallPhase.Downloading && value.Fraction == 1);
        Assert.Contains(reported.Values, value => value.Phase == LocalAiArtifactInstallPhase.Verifying);
        Assert.Contains(reported.Values, value => value.Phase == LocalAiArtifactInstallPhase.Extracting);
        Assert.Contains(reported.Values, value => value.Phase == LocalAiArtifactInstallPhase.Promoting);
    }

    [Fact]
    public async Task InstallAsync_RejectsDeclaredSizeMismatchWithoutReadingBodyAndCleansStaging()
    {
        var archive = CreateArchive(("ollama.exe", "executable"));
        var artifact = CreateArtifact(archive);
        var content = new ThrowOnReadContent(archive.Length + 1);
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains("declared", error.Message);
        Assert.False(content.WasRead);
        AssertNoTransientPayload(artifact);
    }

    [Fact]
    public async Task InstallAsync_RejectsShaMismatchAndCleansTransientPayload()
    {
        var archive = CreateArchive(("ollama.exe", "executable"));
        var artifact = CreateArtifact(archive) with { Sha256 = new string('0', 64) };
        using var httpClient = ClientReturning(archive);
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains("SHA-256", error.Message);
        AssertNoTransientPayload(artifact);
        Assert.False(Directory.Exists(GetEngineDirectory(artifact)));
    }

    [Fact]
    public async Task InstallAsync_ThrottlesDownloadProgressAndReportsExactFinalBytes()
    {
        var body = new byte[9 * 1024 * 1024];
        RandomNumberGenerator.Fill(body);
        var artifact = CreateArtifact(body);
        using var httpClient = ClientReturning(body);
        var installer = new LocalAiArtifactInstaller(httpClient);
        var progress = new RecordingProgress<LocalAiArtifactInstallProgress>();

        await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress,
            CancellationToken.None));

        var downloadReports = progress.Values
            .Where(value => value.Phase == LocalAiArtifactInstallPhase.Downloading)
            .ToArray();
        Assert.Equal(4, downloadReports.Length);
        Assert.Equal(0, downloadReports[0].Completed);
        Assert.Equal(body.LongLength, downloadReports[^1].Completed);
        Assert.Equal(1, downloadReports[^1].Fraction);
        AssertNoTransientPayload(artifact);
    }

    [Fact]
    public async Task InstallAsync_RejectsBodyShorterThanExpectedWhenLengthIsUnknown()
    {
        var archive = CreateArchive(("ollama.exe", "executable"));
        var artifact = CreateArtifact(archive) with { SizeBytes = archive.LongLength + 1 };
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnknownLengthStream(archive)),
        }));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains("contained", error.Message);
        AssertNoTransientPayload(artifact);
        Assert.False(Directory.Exists(GetEngineDirectory(artifact)));
    }

    [Fact]
    public async Task InstallAsync_RejectsTraversalEntryAndCleansTransientPayload()
    {
        var archive = CreateArchive(("ollama.exe", "executable"), ("../outside.exe", "escape"));
        var artifact = CreateArtifact(archive);
        using var httpClient = ClientReturning(archive);
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains("unsafe path segment", error.Message);
        AssertNoTransientPayload(artifact);
        Assert.False(File.Exists(_tempDirectory.Combine("LocalAI", "staging", "outside.exe")));
    }

    [Theory]
    [InlineData("ollama.exe:payload")]
    [InlineData("CON")]
    [InlineData("lib//cuda.dll")]
    public async Task InstallAsync_RejectsUnsafeWindowsEntryNames(string entryName)
    {
        var archive = CreateArchive(("ollama.exe", "executable"), (entryName, "unsafe"));
        var artifact = CreateArtifact(archive);
        using var httpClient = ClientReturning(archive);
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains("unsafe path segment", error.Message);
        AssertNoTransientPayload(artifact);
    }

    [Theory]
    [InlineData(unchecked((int)0xA0000000), "symbolic link")]
    [InlineData((int)FileAttributes.ReparsePoint, "reparse point")]
    public async Task InstallAsync_RejectsLinkLikeArchiveEntries(int externalAttributes, string expectedError)
    {
        var archive = CreateArchiveWithAttributes("ollama.exe", "target", externalAttributes);
        var artifact = CreateArtifact(archive);
        using var httpClient = ClientReturning(archive);
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains(expectedError, error.Message);
        AssertNoTransientPayload(artifact);
        Assert.False(Directory.Exists(GetEngineDirectory(artifact)));
    }

    [Fact]
    public async Task InstallAsync_CancellationDuringDownloadRemovesPartialAndStaging()
    {
        var prefix = new byte[64];
        RandomNumberGenerator.Fill(prefix);
        var artifact = CreateArtifact(new byte[128]);
        var stream = new PrefixThenBlockingStream(prefix);
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var installer = new LocalAiArtifactInstaller(httpClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            cancellation.Token));

        Assert.True(stream.PrefixWasRead);
        AssertNoTransientPayload(artifact);
        Assert.False(Directory.Exists(GetEngineDirectory(artifact)));
    }

    [Fact]
    public async Task InstallAsync_RefusesToReplaceExistingEngineDirectory()
    {
        var archive = CreateArchive(("ollama.exe", "new"));
        var artifact = CreateArtifact(archive);
        var engineDirectory = GetEngineDirectory(artifact);
        Directory.CreateDirectory(engineDirectory);
        var sentinel = Path.Combine(engineDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        var requestSent = false;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return BinaryResponse(archive);
        }));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains("Refusing to replace", error.Message);
        Assert.False(requestSent);
        Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task InstallAsync_RequiresRootOllamaExecutable()
    {
        var archive = CreateArchive(("bin/ollama.exe", "nested"));
        var artifact = CreateArtifact(archive);
        using var httpClient = ClientReturning(archive);
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _tempDirectory.Path,
            artifact,
            progress: null,
            CancellationToken.None));

        Assert.Contains("at its root", error.Message);
        AssertNoTransientPayload(artifact);
        Assert.False(Directory.Exists(GetEngineDirectory(artifact)));
    }

    private OllamaReleaseArtifact CreateArtifact(byte[] body)
        => new(
            "0.32.14-test",
            Architecture.X64,
            "win-x64",
            "ollama-fixture.zip",
            new Uri("https://example.test/ollama-fixture.zip"),
            body.LongLength,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

    private static byte[] CreateArchive(params (string Name, string Contents)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(contents);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateArchiveWithAttributes(string name, string contents, int externalAttributes)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            entry.ExternalAttributes = externalAttributes;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(contents);
        }

        return stream.ToArray();
    }

    private static HttpClient ClientReturning(byte[] body)
        => new(new StubHttpMessageHandler((_, _) => BinaryResponse(body)));

    private static HttpResponseMessage BinaryResponse(byte[] body)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };

    private string GetPartialPath(OllamaReleaseArtifact artifact)
        => _tempDirectory.Combine("LocalAI", "downloads", artifact.FileName + ".partial");

    private string GetStagingRoot()
        => _tempDirectory.Combine("LocalAI", "staging");

    private string GetEngineDirectory(OllamaReleaseArtifact artifact)
        => Path.Combine(
            _tempDirectory.Path,
            "LocalAI",
            "engines",
            "ollama",
            artifact.Version,
            artifact.RuntimeIdentifier);

    private void AssertNoTransientPayload(OllamaReleaseArtifact artifact)
    {
        Assert.False(File.Exists(GetPartialPath(artifact)));
        var staging = GetStagingRoot();
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_handler(request, cancellationToken));
    }

    private sealed class ThrowOnReadContent : HttpContent
    {
        public ThrowOnReadContent(long contentLength)
            => Headers.ContentLength = contentLength;

        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("Response body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength!.Value;
            return true;
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            WasRead = true;
            throw new InvalidOperationException("Response body must not be read.");
        }
    }

    private sealed class PrefixThenBlockingStream(byte[] prefix) : Stream
    {
        private bool _returnedPrefix;

        public bool PrefixWasRead => _returnedPrefix;
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
            if (!_returnedPrefix)
            {
                _returnedPrefix = true;
                prefix.CopyTo(buffer);
                return prefix.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class UnknownLengthStream(byte[] body) : Stream
    {
        private readonly MemoryStream _inner = new(body);

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
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
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
}
