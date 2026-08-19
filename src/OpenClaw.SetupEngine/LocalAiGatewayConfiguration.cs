using System.Text;
using System.Text.Json;
using OpenClaw.Connection.LocalAi;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiGatewayPriorState(
    bool ProviderExisted,
    string? ProviderJson,
    bool PrimaryModelExisted,
    string? PrimaryModelJson);

internal static class LocalAiGatewayConfigBuilder
{
    internal const string ProviderPath = LocalAiGatewayProviderDefinition.ProviderPath;
    internal const string PrimaryModelPath = LocalAiGatewayProviderDefinition.PrimaryModelPath;

    public static string BuildBatchJson(SetupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var install = context.LocalAiResolvedInstall
            ?? throw new InvalidOperationException("The Local AI install receipt is required.");
        _ = context.LocalAiEligibility?.Plan
            ?? throw new InvalidOperationException("The qualified Local AI plan is required.");
        using JsonDocument provider = JsonDocument.Parse(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install));
        object[] operations =
        [
            new { path = ProviderPath, value = (object)provider.RootElement.Clone() },
            new { path = PrimaryModelPath, value = (object)LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) },
        ];
        return JsonSerializer.Serialize(operations);
    }

    public static string BuildRestoreBatchJson(LocalAiGatewayPriorState prior)
    {
        ArgumentNullException.ThrowIfNull(prior);
        var operations = new List<object>(2);
        if (prior.ProviderExisted)
        {
            using JsonDocument provider = JsonDocument.Parse(prior.ProviderJson!);
            operations.Add(new { path = ProviderPath, value = (object)provider.RootElement.Clone() });
        }
        if (prior.PrimaryModelExisted)
        {
            using JsonDocument primary = JsonDocument.Parse(prior.PrimaryModelJson!);
            operations.Add(new { path = PrimaryModelPath, value = (object)primary.RootElement.Clone() });
        }
        return JsonSerializer.Serialize(operations);
    }

    public static string ExpectedPrimaryModel(SetupContext context) =>
        LocalAiGatewayProviderDefinition.BuildPrimaryModel(
            context.LocalAiResolvedInstall
                ?? throw new InvalidOperationException("The Local AI install receipt is required."));
}

public sealed class ConfigureLocalAiGatewayStep : SetupStep
{
    private const string ProviderMarker = "OPENCLAW_LOCAL_AI_PROVIDER_B64=";
    private const string PrimaryMarker = "OPENCLAW_LOCAL_AI_PRIMARY_B64=";
    private const string MissingValue = "MISSING";
    private const string BatchVariable = "OPENCLAW_LOCAL_AI_BATCH_B64";
    private const int MaximumSnapshotBytes = 1024 * 1024;

    public override string Id => "configure-local-ai-gateway";
    public override string DisplayName => "Connect gateway to Local AI";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiResolvedInstall is null || ctx.LocalAiEligibility?.Plan is null)
            return StepResult.Terminal("Local AI gateway configuration requires a qualified install receipt.");

        CommandResult snapshotResult = await CaptureStateAsync(ctx, ct);
        if (snapshotResult.ExitCode != 0 || snapshotResult.TimedOut)
            return StepResult.Fail("Could not safely snapshot the existing Local AI gateway configuration.");

        try
        {
            ctx.LocalAiGatewayPriorState = ParseSnapshot(snapshotResult.Stdout);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            return StepResult.Fail("The existing Local AI gateway configuration could not be validated.", ex);
        }

        string batchJson = LocalAiGatewayConfigBuilder.BuildBatchJson(ctx);
        CommandResult result = await ApplyBatchAsync(ctx, batchJson, "LOCAL_AI_GATEWAY_CONFIGURED", ct);
        if (result.ExitCode != 0 || result.TimedOut ||
            !result.Stdout.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal))
        {
            return StepResult.Fail(result.TimedOut
                ? "Local AI gateway configuration timed out."
                : $"Local AI gateway configuration failed (exit {result.ExitCode}).");
        }

        return StepResult.Ok("Gateway configured to use the managed llama-server provider");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiGatewayPriorState is not { } prior)
            return;

        CommandResult currentResult = await CaptureStateAsync(ctx, ct);
        if (currentResult.ExitCode != 0 || currentResult.TimedOut)
        {
            ctx.Logger.Warn("Could not inspect the Local AI gateway configuration during rollback; preserving it.");
            return;
        }

        LocalAiGatewayPriorState current;
        try
        {
            current = ParseSnapshot(currentResult.Stdout);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            ctx.Logger.Warn($"Could not validate Local AI gateway rollback state; preserving it ({ex.GetType().Name}).");
            return;
        }

        string expectedProvider = ExtractOperationValue(LocalAiGatewayConfigBuilder.BuildBatchJson(ctx), 0);
        string expectedPrimary = JsonSerializer.Serialize(LocalAiGatewayConfigBuilder.ExpectedPrimaryModel(ctx));
        if (!current.ProviderExisted || !current.PrimaryModelExisted ||
            !JsonEquals(current.ProviderJson!, expectedProvider) ||
            !JsonEquals(current.PrimaryModelJson!, expectedPrimary))
        {
            ctx.Logger.Warn("Local AI gateway settings changed after setup; preserving the newer values.");
            return;
        }

        string restoreBatch = LocalAiGatewayConfigBuilder.BuildRestoreBatchJson(prior);
        if (restoreBatch != "[]")
        {
            CommandResult restore = await ApplyBatchAsync(ctx, restoreBatch, "LOCAL_AI_GATEWAY_RESTORED", ct);
            if (restore.ExitCode != 0 || restore.TimedOut)
                ctx.Logger.Warn("Restoring the previous Local AI gateway settings failed.");
        }

        var unset = new List<string>(2);
        if (!prior.PrimaryModelExisted)
            unset.Add($"openclaw config unset {LocalAiGatewayConfigBuilder.PrimaryModelPath}");
        if (!prior.ProviderExisted)
            unset.Add($"openclaw config unset {LocalAiGatewayConfigBuilder.ProviderPath}");
        if (unset.Count > 0)
        {
            string script = $"set -e\n{ctx.WslPathPrefix}\n{string.Join("\n", unset)}\necho LOCAL_AI_GATEWAY_UNSET";
            CommandResult result = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!, script, TimeSpan.FromMinutes(2), ct: ct,
                user: ctx.Config.Wsl.User, inputViaStdin: true);
            if (result.ExitCode != 0 || result.TimedOut)
                ctx.Logger.Warn("Removing setup-created Local AI gateway settings failed.");
        }
    }

    private static Task<CommandResult> CaptureStateAsync(SetupContext ctx, CancellationToken ct)
    {
        string script = $$"""
            set -u
            {{ctx.WslPathPrefix}}
            capture_value() {
              key="$1"
              marker="$2"
              temp_file="$(mktemp)"
              if openclaw config get "$key" --json >"$temp_file" 2>/dev/null; then
                printf '%s%s\n' "$marker" "$(base64 -w0 <"$temp_file")"
              else
                printf '%s{{MissingValue}}\n' "$marker"
              fi
              rm -f "$temp_file"
            }
            capture_value '{{LocalAiGatewayConfigBuilder.ProviderPath}}' '{{ProviderMarker}}'
            capture_value '{{LocalAiGatewayConfigBuilder.PrimaryModelPath}}' '{{PrimaryMarker}}'
            """;
        return ctx.Commands.RunInWslAsync(
            ctx.DistroName!, script, TimeSpan.FromMinutes(1), ct: ct,
            user: ctx.Config.Wsl.User, inputViaStdin: true);
    }

    private static Task<CommandResult> ApplyBatchAsync(
        SetupContext ctx,
        string batchJson,
        string successMarker,
        CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            [BatchVariable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(batchJson)),
        };
        string script = $$"""
            set -e
            {{ctx.WslPathPrefix}}
            batch_file="$(mktemp)"
            trap 'rm -f "$batch_file"' EXIT
            printf '%s' "$OPENCLAW_LOCAL_AI_BATCH_B64" | base64 -d > "$batch_file"
            openclaw config set --batch-file "$batch_file" --dry-run
            openclaw config set --batch-file "$batch_file"
            echo {{successMarker}}
            """;
        return ctx.Commands.RunInWslAsync(
            ctx.DistroName!, script, TimeSpan.FromMinutes(2), environment, ct,
            user: ctx.Config.Wsl.User, inputViaStdin: true);
    }

    private static LocalAiGatewayPriorState ParseSnapshot(string stdout)
    {
        (bool providerExists, string? provider) = ParseMarker(stdout, ProviderMarker);
        (bool primaryExists, string? primary) = ParseMarker(stdout, PrimaryMarker);
        return new(providerExists, provider, primaryExists, primary);
    }

    private static (bool Exists, string? Json) ParseMarker(string stdout, string marker)
    {
        string? value = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal))?[marker.Length..];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Missing configuration marker '{marker}'.");
        if (string.Equals(value, MissingValue, StringComparison.Ordinal))
            return (false, null);
        if (value.Length > MaximumSnapshotBytes * 2)
            throw new InvalidDataException("The configuration snapshot is too large.");

        byte[] bytes = Convert.FromBase64String(value);
        if (bytes.Length > MaximumSnapshotBytes)
            throw new InvalidDataException("The configuration snapshot is too large.");
        string json = Encoding.UTF8.GetString(bytes);
        using JsonDocument _ = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        return (true, json);
    }

    private static string ExtractOperationValue(string batchJson, int index)
    {
        using JsonDocument document = JsonDocument.Parse(batchJson);
        return document.RootElement[index].GetProperty("value").GetRawText();
    }

    private static bool JsonEquals(string left, string right)
    {
        using JsonDocument leftDocument = JsonDocument.Parse(left);
        using JsonDocument rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }
}
