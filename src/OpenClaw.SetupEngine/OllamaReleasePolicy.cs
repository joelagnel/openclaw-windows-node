using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

public sealed record OllamaReleaseArtifact(
    string Version,
    Architecture Architecture,
    string RuntimeIdentifier,
    string FileName,
    Uri DownloadUri,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Embedded allowlist for native Windows Ollama artifacts qualified for setup.
/// Runtime discovery never selects an unlisted release or download location.
/// </summary>
public static partial class OllamaReleasePolicy
{
    public const string RecommendedVersion = "0.32.14";

    private static readonly IReadOnlyDictionary<Architecture, OllamaReleaseArtifact> s_artifacts =
        new ReadOnlyDictionary<Architecture, OllamaReleaseArtifact>(
            new Dictionary<Architecture, OllamaReleaseArtifact>
            {
                [Architecture.X64] = new(
                    RecommendedVersion,
                    Architecture.X64,
                    "win-x64",
                    "ollama-windows-amd64.zip",
                    new Uri("https://github.com/ollama/ollama/releases/download/v0.32.14/ollama-windows-amd64.zip"),
                    1_459_874_325,
                    "5ae5bca5f0d297f5e35665e01db399a69a8eac3f8fad89cd9d2531fd495c9457"),
                [Architecture.Arm64] = new(
                    RecommendedVersion,
                    Architecture.Arm64,
                    "win-arm64",
                    "ollama-windows-arm64.zip",
                    new Uri("https://github.com/ollama/ollama/releases/download/v0.32.14/ollama-windows-arm64.zip"),
                    209_894_691,
                    "821cdc689f3bb750ab3192fa96189676f8db0eda51e8d01b837ea7581474e1de"),
            });

    public static IReadOnlyDictionary<Architecture, OllamaReleaseArtifact> Artifacts => s_artifacts;

    public static OllamaReleaseArtifact ResolveForCurrentOperatingSystem()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native Ollama setup is supported only on Windows.");

        return Resolve(RuntimeInformation.OSArchitecture);
    }

    public static OllamaReleaseArtifact Resolve(Architecture architecture)
        => s_artifacts.TryGetValue(architecture, out var artifact)
            ? artifact
            : throw new PlatformNotSupportedException(
                $"Native Ollama setup does not support Windows architecture '{architecture}'.");

    public static IReadOnlyList<string> ValidateEmbeddedPolicy()
    {
        var errors = new List<string>();
        if (!Version.TryParse(RecommendedVersion, out _))
            errors.Add("The recommended Ollama version is not an exact numeric version.");

        foreach (var requiredArchitecture in new[] { Architecture.X64, Architecture.Arm64 })
        {
            if (!s_artifacts.TryGetValue(requiredArchitecture, out var artifact))
            {
                errors.Add($"No Ollama artifact is pinned for {requiredArchitecture}.");
                continue;
            }

            ValidateArtifact(artifact, errors);
        }

        return errors;
    }

    private static void ValidateArtifact(OllamaReleaseArtifact artifact, List<string> errors)
    {
        if (!string.Equals(artifact.Version, RecommendedVersion, StringComparison.Ordinal))
            errors.Add($"The {artifact.Architecture} artifact version does not match {RecommendedVersion}.");
        if (artifact.SizeBytes <= 0)
            errors.Add($"The {artifact.Architecture} artifact has no positive expected size.");
        if (!Sha256Pattern().IsMatch(artifact.Sha256))
            errors.Add($"The {artifact.Architecture} artifact has an invalid SHA-256 digest.");
        if (!artifact.DownloadUri.IsAbsoluteUri ||
            !string.Equals(artifact.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.DownloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"The {artifact.Architecture} artifact must use an HTTPS github.com URL.");
        }

        if (!string.Equals(Path.GetFileName(artifact.DownloadUri.AbsolutePath), artifact.FileName, StringComparison.Ordinal))
            errors.Add($"The {artifact.Architecture} artifact file name does not match its URL.");
        if (!artifact.DownloadUri.AbsolutePath.Contains(
                $"/releases/download/v{RecommendedVersion}/",
                StringComparison.Ordinal))
        {
            errors.Add($"The {artifact.Architecture} artifact URL does not identify release v{RecommendedVersion}.");
        }

        var expectedRuntimeIdentifier = artifact.Architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null,
        };
        if (!string.Equals(artifact.RuntimeIdentifier, expectedRuntimeIdentifier, StringComparison.Ordinal))
            errors.Add($"The {artifact.Architecture} artifact has an invalid runtime identifier.");
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
