using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.SetupEngine;

internal sealed record OllamaVersionInfo(string Version);

internal sealed record OllamaModelInfo(
    string Name,
    string Model,
    DateTimeOffset? ModifiedAt,
    long SizeBytes,
    string? Digest);

internal sealed record OllamaPullProgress(
    string Status,
    string? Digest,
    long? BlobCompletedBytes,
    long? BlobTotalBytes,
    long TransferredBytes,
    long? ExpectedBytes,
    double? Fraction,
    bool IsComplete);

internal sealed record OllamaPullResult(
    string Model,
    long TransferredBytes,
    long? ExpectedBytes);

internal sealed class OllamaApiException : Exception
{
    public OllamaApiException(string message)
        : base(message)
    {
    }

    public OllamaApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal interface IOllamaApiClient
{
    Task<OllamaVersionInfo> GetVersionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
    Task<OllamaPullResult> PullModelAsync(
        string model,
        long? expectedBytes,
        IProgress<OllamaPullProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Minimal client for the native Ollama endpoints needed during setup.
/// The supplied HttpClient controls request timeout and transport lifetime.
/// </summary>
internal sealed class OllamaApiClient : IOllamaApiClient
{
    private const int MaxErrorBodyCharacters = 4_096;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public OllamaApiClient(HttpClient httpClient, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "Ollama endpoint must be an absolute HTTP or HTTPS URL without credentials, query, or fragment.",
                nameof(endpoint));
        }

        _httpClient = httpClient;
        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath.TrimEnd('/') + "/",
            Query = "",
            Fragment = "",
        };
        _baseUri = builder.Uri;
    }

    public async Task<OllamaVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/version"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        OllamaVersionResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<OllamaVersionResponse>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new OllamaApiException("Ollama returned an invalid version response.", ex);
        }

        if (string.IsNullOrWhiteSpace(payload?.Version))
            throw new OllamaApiException("Ollama returned a version response without a version.");

        return new OllamaVersionInfo(payload.Version.Trim());
    }

    public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("api/tags"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        OllamaTagsResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new OllamaApiException("Ollama returned an invalid model list.", ex);
        }

        if (payload?.Models is not { } models)
            return Array.Empty<OllamaModelInfo>();

        var result = new List<OllamaModelInfo>(models.Count);
        foreach (var model in models)
        {
            var name = FirstNonEmpty(model.Name, model.Model);
            var canonicalModel = FirstNonEmpty(model.Model, model.Name);
            if (name is null || canonicalModel is null)
                continue;

            result.Add(new OllamaModelInfo(
                name,
                canonicalModel,
                model.ModifiedAt,
                Math.Max(0, model.Size),
                string.IsNullOrWhiteSpace(model.Digest) ? null : model.Digest));
        }

        return result;
    }

    public async Task<OllamaPullResult> PullModelAsync(
        string model,
        long? expectedBytes,
        IProgress<OllamaPullProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Contains('\r') || model.Contains('\n'))
            throw new ArgumentException("Ollama model name must be non-empty and contain no newlines.", nameof(model));
        if (expectedBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes), "Expected model size must be positive when provided.");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("api/pull"))
        {
            Content = JsonContent.Create(new OllamaPullRequest(model.Trim(), Stream: true), options: JsonOptions),
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var digests = new Dictionary<string, BlobProgress>(StringComparer.Ordinal);
        var sawResponse = false;
        var completed = false;
        var lastStatus = "starting";

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            sawResponse = true;
            OllamaPullResponse update;
            try
            {
                update = JsonSerializer.Deserialize<OllamaPullResponse>(line, JsonOptions)
                    ?? throw new JsonException("The response line was null.");
            }
            catch (JsonException ex)
            {
                throw new OllamaApiException("Ollama returned invalid pull progress.", ex);
            }

            if (!string.IsNullOrWhiteSpace(update.Error))
                throw new OllamaApiException($"Ollama could not pull model '{model}': {Flatten(update.Error)}");

            lastStatus = string.IsNullOrWhiteSpace(update.Status) ? lastStatus : update.Status.Trim();
            if (!string.IsNullOrWhiteSpace(update.Digest))
            {
                var digest = update.Digest.Trim();
                digests.TryGetValue(digest, out var previous);
                var total = NormalizeNonNegative(update.Total) ?? previous?.TotalBytes;
                var blobCompleted = NormalizeNonNegative(update.Completed) ?? previous?.CompletedBytes ?? 0;
                if (total is { } knownTotal)
                    blobCompleted = Math.Min(blobCompleted, knownTotal);
                digests[digest] = new BlobProgress(blobCompleted, total);
            }

            completed = string.Equals(lastStatus, "success", StringComparison.OrdinalIgnoreCase);
            var transferred = SaturatingSum(digests.Values.Select(value => value.CompletedBytes));
            var discoveredTotal = SaturatingSumNullable(digests.Values.Select(value => value.TotalBytes));
            var effectiveExpected = expectedBytes ?? discoveredTotal;
            var fraction = completed
                ? 1d
                : ComputeFraction(transferred, effectiveExpected);
            progress?.Report(new OllamaPullProgress(
                lastStatus,
                string.IsNullOrWhiteSpace(update.Digest) ? null : update.Digest.Trim(),
                NormalizeNonNegative(update.Completed),
                NormalizeNonNegative(update.Total),
                transferred,
                effectiveExpected,
                fraction,
                completed));
        }

        if (!sawResponse)
            throw new OllamaApiException("Ollama ended the pull response without reporting progress.");
        if (!completed)
            throw new OllamaApiException($"Ollama ended the pull before success. Last status: {Flatten(lastStatus)}.");

        return new OllamaPullResult(
            model.Trim(),
            SaturatingSum(digests.Values.Select(value => value.CompletedBytes)),
            expectedBytes ?? SaturatingSumNullable(digests.Values.Select(value => value.TotalBytes)));
    }

    private Uri BuildUri(string relativePath) => new(_baseUri, relativePath);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > MaxErrorBodyCharacters)
            body = body[..MaxErrorBodyCharacters];
        body = Flatten(body);
        var detail = string.IsNullOrWhiteSpace(body) ? "no response body" : body;
        throw new OllamaApiException(
            $"Ollama API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}");
    }

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first)
            ? first.Trim()
            : !string.IsNullOrWhiteSpace(second)
                ? second.Trim()
                : null;

    private static long? NormalizeNonNegative(long? value)
        => value is null ? null : Math.Max(0, value.Value);

    private static double? ComputeFraction(long completed, long? total)
        => total is > 0
            ? Math.Clamp((double)completed / total.Value, 0d, 1d)
            : null;

    private static long SaturatingSum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            if (value > long.MaxValue - total)
                return long.MaxValue;
            total += value;
        }

        return total;
    }

    private static long? SaturatingSumNullable(IEnumerable<long?> values)
    {
        var any = false;
        var total = 0L;
        foreach (var value in values)
        {
            if (value is not { } present)
                continue;
            any = true;
            if (present > long.MaxValue - total)
                return long.MaxValue;
            total += present;
        }

        return any ? total : null;
    }

    private static string Flatten(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed record BlobProgress(long CompletedBytes, long? TotalBytes);

    private sealed record OllamaVersionResponse(
        [property: JsonPropertyName("version")] string? Version);

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] List<OllamaModelResponse>? Models);

    private sealed record OllamaModelResponse(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);

    private sealed record OllamaPullRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record OllamaPullResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("total")] long? Total,
        [property: JsonPropertyName("completed")] long? Completed,
        [property: JsonPropertyName("error")] string? Error);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
