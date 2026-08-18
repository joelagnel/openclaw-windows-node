using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiArtifactInstallerTests : IDisposable
{
    private static readonly LocalAiComponentIdentity Component = new(
        "native-engine",
        "1.2.3-test",
        "win-x64");

    private readonly TempDirectory _temp = new("local-ai-installer-");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task InstallAsync_VerifiesExtractsAndAtomicallyPromotesSingleArchive()
    {
        var payload = CreateArchive(("bin/native.exe", "native executable"), ("lib/runtime.dll", "runtime"));
        var archive = CreatePinnedArchive("runtime.zip", payload);
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);
        var reported = new RecordingProgress<LocalAiArtifactInstallProgress>();
        var events = new List<LocalAiArtifactInstallProgress>();
        installer.ProgressChanged += (_, value) => events.Add(value);

        var result = await installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            reported,
            CancellationToken.None);

        Assert.Equal(Component, result.Component);
        Assert.Equal(GetInstallDirectory(), result.InstallDirectory);
        Assert.Equal(result.InstallDirectory, result.Rollback.CreatedDirectory);
        Assert.Equal("native executable", await File.ReadAllTextAsync(
            Path.Combine(result.InstallDirectory, "bin", "native.exe")));
        Assert.Equal("runtime", await File.ReadAllTextAsync(
            Path.Combine(result.InstallDirectory, "lib", "runtime.dll")));
        var verified = Assert.Single(result.VerifiedArchives);
        Assert.Equal(archive.FileName, verified.FileName);
        Assert.Equal(archive.SizeBytes, verified.SizeBytes);
        Assert.Equal(archive.Sha256, verified.Sha256);
        AssertNoTransientPayload(archive);
        Assert.Equal(LocalAiArtifactInstallPhase.Complete, reported.Values[^1].Phase);
        Assert.Equal(reported.Values, events);
        Assert.Contains(reported.Values, value =>
            value.Phase == LocalAiArtifactInstallPhase.Downloading && value.Fraction == 1);
        Assert.Contains(reported.Values, value => value.Phase == LocalAiArtifactInstallPhase.Verifying);
        Assert.Contains(reported.Values, value => value.Phase == LocalAiArtifactInstallPhase.Extracting);
        Assert.Contains(reported.Values, value => value.Phase == LocalAiArtifactInstallPhase.Promoting);
    }

    [Fact]
    public async Task InstallAsync_CombinesMultiplePinnedArchivesWithoutComponentSpecificValidation()
    {
        var runtimePayload = CreateArchive(("bin/runtime.bin", "runtime"));
        var dependencyPayload = CreateArchive(("lib/dependency.bin", "dependency"));
        var runtime = CreatePinnedArchive("runtime.zip", runtimePayload);
        var dependencies = CreatePinnedArchive("dependencies.zip", dependencyPayload);
        using var httpClient = ClientReturning(
            (runtime, runtimePayload),
            (dependencies, dependencyPayload));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var result = await installer.InstallAsync(
            _temp.Path,
            Component,
            [runtime, dependencies],
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, result.VerifiedArchives.Count);
        Assert.Equal(["runtime.zip", "dependencies.zip"], result.VerifiedArchives.Select(item => item.FileName));
        Assert.Equal("runtime", await File.ReadAllTextAsync(
            Path.Combine(result.InstallDirectory, "bin", "runtime.bin")));
        Assert.Equal("dependency", await File.ReadAllTextAsync(
            Path.Combine(result.InstallDirectory, "lib", "dependency.bin")));
        AssertNoTransientPayload(runtime, dependencies);
    }

    [Fact]
    public async Task InstallAsync_ReturnsContainedRollbackMetadata()
    {
        var payload = CreateArchive(("payload.bin", "payload"));
        var archive = CreatePinnedArchive("payload.zip", payload);
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var result = await installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None);

        Assert.True(LocalAiPathPolicy.TryValidateManagedDeleteTarget(
            _temp.Path,
            result.Rollback.CreatedDirectory,
            out var rollbackDirectory,
            out var error), error);
        Assert.Equal(result.InstallDirectory, rollbackDirectory);
    }

    [Fact]
    public async Task InstallAsync_RejectsDeclaredSizeMismatchWithoutReadingBodyAndCleansStaging()
    {
        var payload = CreateArchive(("payload.bin", "payload"));
        var archive = CreatePinnedArchive("payload.zip", payload);
        var content = new ThrowOnReadContent(payload.LongLength + 1);
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains("declared", error.Message);
        Assert.False(content.WasRead);
        AssertNoTransientPayload(archive);
    }

    [Fact]
    public async Task InstallAsync_RejectsShaMismatchAndCleansTransientPayload()
    {
        var payload = CreateArchive(("payload.bin", "payload"));
        var archive = CreatePinnedArchive("payload.zip", payload) with { Sha256 = new string('0', 64) };
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains("SHA-256", error.Message);
        AssertNoTransientPayload(archive);
        Assert.False(Directory.Exists(GetInstallDirectory()));
    }

    [Fact]
    public async Task InstallAsync_ThrottlesDownloadProgressAndReportsExactTerminalBytes()
    {
        var payload = new byte[9 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var archive = CreatePinnedArchive("payload.zip", payload);
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);
        var progress = new RecordingProgress<LocalAiArtifactInstallProgress>();

        await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress,
            CancellationToken.None));

        var downloadReports = progress.Values
            .Where(value => value.Phase == LocalAiArtifactInstallPhase.Downloading)
            .ToArray();
        Assert.Equal(4, downloadReports.Length);
        Assert.Equal(0, downloadReports[0].Completed);
        Assert.Equal(4L * 1024 * 1024, downloadReports[1].Completed);
        Assert.Equal(8L * 1024 * 1024, downloadReports[2].Completed);
        Assert.Equal(payload.LongLength, downloadReports[^1].Completed);
        Assert.Equal(1, downloadReports[^1].Fraction);
        AssertNoTransientPayload(archive);
    }

    [Fact]
    public async Task InstallAsync_RejectsBodyShorterThanExpectedWhenLengthIsUnknown()
    {
        var payload = CreateArchive(("payload.bin", "payload"));
        var archive = CreatePinnedArchive("payload.zip", payload) with { SizeBytes = payload.LongLength + 1 };
        using var httpClient = ClientReturningUnknownLength(payload);
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains("contained", error.Message);
        AssertNoTransientPayload(archive);
        Assert.False(Directory.Exists(GetInstallDirectory()));
    }

    [Fact]
    public async Task InstallAsync_RejectsBodyLongerThanExpectedWhenLengthIsUnknown()
    {
        var payload = CreateArchive(("payload.bin", "payload"));
        var archive = CreatePinnedArchive("payload.zip", payload) with { SizeBytes = payload.LongLength - 1 };
        using var httpClient = ClientReturningUnknownLength(payload);
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains("exceeded", error.Message);
        AssertNoTransientPayload(archive);
        Assert.False(Directory.Exists(GetInstallDirectory()));
    }

    [Fact]
    public async Task InstallAsync_RejectsTraversalEntryAndCleansTransientPayload()
    {
        var payload = CreateArchive(("payload.bin", "payload"), ("../outside.bin", "escape"));
        var archive = CreatePinnedArchive("payload.zip", payload);
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains("unsafe path segment", error.Message);
        AssertNoTransientPayload(archive);
        Assert.False(File.Exists(_temp.Combine("LocalAI", "staging", "outside.bin")));
    }

    [Theory]
    [InlineData("payload.bin:stream")]
    [InlineData("CON")]
    [InlineData("LPT1.log")]
    [InlineData("lib//runtime.dll")]
    [InlineData("lib/../runtime.dll")]
    public async Task InstallAsync_RejectsUnsafeWindowsEntryNames(string entryName)
    {
        var payload = CreateArchive(("safe.bin", "safe"), (entryName, "unsafe"));
        var archive = CreatePinnedArchive("payload.zip", payload);
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains("unsafe path segment", error.Message);
        AssertNoTransientPayload(archive);
    }

    [Theory]
    [InlineData(unchecked((int)0xA0000000), "symbolic link")]
    [InlineData((int)FileAttributes.ReparsePoint, "reparse point")]
    [InlineData(unchecked((int)0x10000000), "unsupported file type")]
    public async Task InstallAsync_RejectsLinkLikeAndUnsupportedArchiveEntries(
        int externalAttributes,
        string expectedError)
    {
        var payload = CreateArchiveWithAttributes("payload.bin", "target", externalAttributes);
        var archive = CreatePinnedArchive("payload.zip", payload);
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains(expectedError, error.Message);
        AssertNoTransientPayload(archive);
        Assert.False(Directory.Exists(GetInstallDirectory()));
    }

    [Fact]
    public async Task InstallAsync_CancellationDuringDownloadRemovesPartialAndStaging()
    {
        var prefix = new byte[64];
        RandomNumberGenerator.Fill(prefix);
        var archive = CreatePinnedArchive("payload.zip", new byte[128]);
        var stream = new PrefixThenBlockingStream(prefix);
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            }))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var installer = new LocalAiArtifactInstaller(httpClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            cancellation.Token));

        Assert.True(stream.PrefixWasRead);
        AssertNoTransientPayload(archive);
        Assert.False(Directory.Exists(GetInstallDirectory()));
    }

    [Fact]
    public async Task InstallAsync_RefusesToReplaceExistingInstallBeforeRequest()
    {
        var payload = CreateArchive(("payload.bin", "new"));
        var archive = CreatePinnedArchive("payload.zip", payload);
        var installDirectory = GetInstallDirectory();
        Directory.CreateDirectory(installDirectory);
        var sentinel = Path.Combine(installDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        var requestSent = false;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return BinaryResponse(payload);
        }));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            progress: null,
            CancellationToken.None));

        Assert.Contains("Refusing to replace", error.Message);
        Assert.False(requestSent);
        Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task InstallAsync_RejectsCrossArchiveOverwriteAndPromotesNothing()
    {
        var firstPayload = CreateArchive(("shared.bin", "first"));
        var secondPayload = CreateArchive(("shared.bin", "second"));
        var first = CreatePinnedArchive("first.zip", firstPayload);
        var second = CreatePinnedArchive("second.zip", secondPayload);
        using var httpClient = ClientReturning((first, firstPayload), (second, secondPayload));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [first, second],
            progress: null,
            CancellationToken.None));

        Assert.Contains("overwrite", error.Message);
        AssertNoTransientPayload(first, second);
        Assert.False(Directory.Exists(GetInstallDirectory()));
    }

    [Fact]
    public async Task InstallAsync_RejectsDuplicateArchiveFileNamesBeforeRequest()
    {
        var payload = CreateArchive(("payload.bin", "payload"));
        var first = CreatePinnedArchive("payload.zip", payload);
        var second = CreatePinnedArchive("PAYLOAD.ZIP", payload);
        var requestSent = false;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return BinaryResponse(payload);
        }));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [first, second],
            progress: null,
            CancellationToken.None));

        Assert.Contains("more than once", error.Message);
        Assert.False(requestSent);
    }

    [Fact]
    public async Task InstallAsync_RejectsEmptyArchiveSetBeforeRequest()
    {
        var requestSent = false;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var installer = new LocalAiArtifactInstaller(httpClient);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallAsync(
            _temp.Path,
            Component,
            [],
            progress: null,
            CancellationToken.None));

        Assert.Contains("At least one", error.Message);
        Assert.False(requestSent);
    }

    [Fact]
    public async Task InstallAsync_ProgressObserversCannotBreakInstall()
    {
        var payload = CreateArchive(("payload.bin", "payload"));
        var archive = CreatePinnedArchive("payload.zip", payload);
        using var httpClient = ClientReturning((archive, payload));
        var installer = new LocalAiArtifactInstaller(httpClient);
        installer.ProgressChanged += (_, _) => throw new InvalidOperationException("event observer failure");

        var result = await installer.InstallAsync(
            _temp.Path,
            Component,
            [archive],
            new ThrowingProgress<LocalAiArtifactInstallProgress>(),
            CancellationToken.None);

        Assert.True(Directory.Exists(result.InstallDirectory));
    }

    private static LocalAiPinnedArchive CreatePinnedArchive(string fileName, byte[] body)
        => new(
            fileName,
            new Uri($"https://example.test/{fileName}"),
            body.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(body)));

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

    private static byte[] CreateArchiveWithAttributes(
        string name,
        string contents,
        int externalAttributes)
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

    private static HttpClient ClientReturning(
        params (LocalAiPinnedArchive Archive, byte[] Body)[] responses)
    {
        var bodies = responses.ToDictionary(
            response => response.Archive.DownloadUri,
            response => response.Body);
        return new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.NotNull(request.RequestUri);
            return BinaryResponse(bodies[request.RequestUri]);
        }));
    }

    private static HttpClient ClientReturningUnknownLength(byte[] body)
        => new(new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnknownLengthStream(body)),
            }));

    private static HttpResponseMessage BinaryResponse(byte[] body)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };

    private string GetPartialPath(LocalAiPinnedArchive archive)
        => _temp.Combine("LocalAI", "downloads", archive.FileName + ".partial");

    private string GetStagingRoot()
        => _temp.Combine("LocalAI", "staging");

    private string GetInstallDirectory()
        => _temp.Combine(
            "LocalAI",
            "engines",
            Component.Name,
            Component.Version,
            Component.RuntimeIdentifier);

    private void AssertNoTransientPayload(params LocalAiPinnedArchive[] archives)
    {
        foreach (var archive in archives)
            Assert.False(File.Exists(GetPartialPath(archive)));
        var staging = GetStagingRoot();
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class ThrowingProgress<T> : IProgress<T>
    {
        public void Report(T value) => throw new InvalidOperationException("progress observer failure");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
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
