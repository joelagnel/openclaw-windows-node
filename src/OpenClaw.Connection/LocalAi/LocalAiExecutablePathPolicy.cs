using System.Reflection.PortableExecutable;

namespace OpenClaw.Connection.LocalAi;

internal sealed record LlamaServerExecutableSelection(
    string ExecutablePath,
    string RuntimeDirectory,
    bool IsCustom);

internal static class LocalAiExecutablePathPolicy
{
    private const string ExpectedFileName = "llama-server.exe";
    private const string RequiredImplementationFileName = "llama-server-impl.dll";

    public static LlamaServerExecutableSelection Resolve(
        string? customExecutablePath,
        LocalAiResolvedInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);

        if (string.IsNullOrWhiteSpace(customExecutablePath))
        {
            string installedDirectory = Path.GetDirectoryName(install.ExecutablePath)
                ?? throw new InvalidDataException("The managed llama-server runtime directory is invalid.");
            return new LlamaServerExecutableSelection(
                install.ExecutablePath,
                installedDirectory,
                IsCustom: false);
        }

        string fullPath;
        try
        {
            string candidate = customExecutablePath.Trim();
            if (!IsLocalDriveQualifiedPath(candidate))
            {
                throw new InvalidDataException(
                    "The custom llama-server executable path must use an absolute local drive path.");
            }
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The custom llama-server executable path is invalid.", ex);
        }

        if (!string.Equals(Path.GetFileName(fullPath), ExpectedFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The custom local AI executable must be named llama-server.exe.");
        if (!File.Exists(fullPath))
            throw new InvalidDataException("The custom llama-server executable does not exist.");

        string runtimeDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The custom llama-server runtime directory is invalid.");
        if (!Directory.Exists(runtimeDirectory))
            throw new InvalidDataException("The custom llama-server runtime directory does not exist.");
        if (!File.Exists(Path.Combine(runtimeDirectory, RequiredImplementationFileName)))
        {
            throw new InvalidDataException(
                $"The custom llama-server runtime directory is missing {RequiredImplementationFileName}.");
        }

        ValidatePortableExecutable(fullPath, install.Manifest.Architecture);
        return new LlamaServerExecutableSelection(fullPath, runtimeDirectory, IsCustom: true);
    }

    private static bool IsLocalDriveQualifiedPath(string path) =>
        Path.IsPathFullyQualified(path) &&
        path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) &&
        path[1] == Path.VolumeSeparatorChar &&
        (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar);

    private static void ValidatePortableExecutable(string executablePath, string architecture)
    {
        Machine machine;
        Characteristics characteristics;
        bool hasPeHeader;
        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var reader = new PEReader(stream);
            machine = reader.PEHeaders.CoffHeader.Machine;
            characteristics = reader.PEHeaders.CoffHeader.Characteristics;
            hasPeHeader = reader.PEHeaders.PEHeader is not null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The custom llama-server executable is not a readable Windows PE image.", ex);
        }

        if (!hasPeHeader ||
            !characteristics.HasFlag(Characteristics.ExecutableImage) ||
            characteristics.HasFlag(Characteristics.Dll))
        {
            throw new InvalidDataException("The custom llama-server file is not a Windows executable image.");
        }

        Machine expectedMachine = architecture switch
        {
            "x64" => Machine.Amd64,
            "arm64" => Machine.Arm64,
            _ => throw new InvalidDataException("The managed local AI architecture is invalid."),
        };
        if (machine != expectedMachine)
        {
            throw new InvalidDataException(
                $"The custom llama-server executable architecture ({machine}) does not match the managed local AI architecture ({architecture}).");
        }
    }
}
