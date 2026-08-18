using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

internal sealed record ProgressPageArgs(
    SetupConfig Config,
    bool ShowMilestoneOnly,
    string DataDir,
    string LocalDataDir);

public sealed partial class ProgressPage : Page
{
    private SetupConfig? _config;
    private SetupPipeline? _pipeline;
    private SetupLogger? _logger;
    private CancellationTokenSource? _runCts;
    private readonly Dictionary<string, StepRow> _rows = new();
    private int _logLineCount;
    private bool _pipelineFinished;
    private string _dataDir = null!;
    private string _localDataDir = null!;
    private StepGroup[] _stepGroups = [];
    private Uri? _tailscaleAuthorizationUri;
    private const int MaxLogLines = 200;

    internal bool IsPipelineRunning => _runCts != null && !_pipelineFinished;

    // Map pipeline step IDs to display groups (N:1).
    private static readonly StepGroup[] StandardStepGroups =
    [
        new("preflight", "Check system", ["validate-distro-path", "preflight-os", "preflight-wsl", "preflight-windows-tailscale"]),
        new("cleanup", "Removing existing gateway", ["cleanup-distro", "cleanup-gateway"]),
        new("port", "Checking gateway port", ["preflight-port"]),
        new("wsl-create", "Installing clean WSL gateway", ["wsl-create"]),
        new("wsl-configure", "Configuring instance", ["wsl-configure", "validate-wsl-lockdown"]),
        new("install-cli", "Installing OpenClaw", ["install-cli"]),
        new("tailscale-auth", "Connecting Tailscale", ["install-tailscale", "authorize-tailscale"]),
        new("configure", "Preparing gateway", ["configure-gateway", "install-service"]),
        new("start", "Starting gateway", ["start-gateway", "mint-token"]),
        new("tailscale-serve", "Publishing on Tailscale", ["finalize-tailscale-serve"]),
        new("pairing", "Pairing device", ["pair-operator", "pair-node", "verify-e2e"]),
        new("finish", "Finishing setup", ["run-wizard", "start-keepalive"]),
    ];

    private static readonly StepGroup LocalAiNetworkingGroup = new(
        "local-ai-networking",
        "Configuring WSL networking for Local AI",
        ["configure-local-ai-wsl-networking"],
        "Mirrored networking and one-time WSL shutdown");

    private static readonly StepGroup LocalAiModelGroup = new(
        "local-ai-model",
        "Downloading Qwen3.6 35B",
        ["download-local-ai-model"],
        "qwen3.6:35b-a3b-mtp-q4_K_M, ~23 GB",
        ShowsDeterminateProgress: true);

    private static readonly StepGroup LocalAiWslVerificationGroup = new(
        "local-ai-wsl-verification",
        "Verifying WSL can reach Local AI",
        ["verify-local-ai-wsl"],
        "Checking the native Ollama endpoint from the gateway");

    private static readonly StepGroup LocalAiInferenceGroup = new(
        "local-ai-inference",
        "Verifying Local AI inference on GPU",
        ["verify-local-ai-inference"],
        "One-token generation at 256K context with full GPU residency");

    private static readonly StepGroup LocalAiGatewayGroup = new(
        "local-ai-gateway",
        "Connecting OpenClaw to Local AI",
        ["configure-local-ai-gateway"],
        "Configuring the 256K provider context");

    public ProgressPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPipeline();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = e.Parameter as ProgressPageArgs;
        _config = args?.Config ?? e.Parameter as SetupConfig ?? new SetupConfig();
        _dataDir = args?.DataDir ?? SetupContext.ResolveDataDir();
        _localDataDir = args?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        var localAiEnabled = _config.LocalAi.Enabled;
        var localAiSummary = localAiEnabled
            ? SetupReviewSummaryBuilder.Build(_config, _dataDir, _localDataDir)
            : null;
        _stepGroups = BuildStepGroups(localAiEnabled, localAiSummary?.LocalAiEngineDescription);
        TitleText.Text = localAiEnabled ? "Setting up OpenClaw and Local AI" : "Setting up WSL gateway";
        SubtitleText.Text = localAiEnabled
            ? "Installing Ollama and Qwen3.6 on this PC"
            : $"Creating {_config.DistroName} WSL instance";

        BuildStepRows();
        if (args?.ShowMilestoneOnly == true)
        {
            foreach (var group in _stepGroups)
            {
                var groupId = group.GroupId;
                if (_rows.TryGetValue(groupId, out var row))
                    row.SetStatus(StepStatus.Done);
            }
            ShowGatewayInstalledMilestone();
            return;
        }

        if (SetupPreview.IsActive)
        {
            if (SetupPreview.RequestedPage == "milestone")
            {
                foreach (var group in _stepGroups)
                {
                    var groupId = group.GroupId;
                    if (_rows.TryGetValue(groupId, out var row))
                        row.SetStatus(StepStatus.Done);
                }
                ShowGatewayInstalledMilestone();
                return;
            }
            if (SetupPreview.RequestedPage == "progress-local-ai")
                RenderLocalAiProgressPreview();
            else
                RenderProgressPreview();
            return;
        }
        StartPipeline();
    }

    private void RenderProgressPreview()
    {
        SubtitleText.Text = "Creating OpenClawGateway WSL instance: about 4 minutes left";
        var ids = _stepGroups.Select(g => g.GroupId).ToArray();
        for (int i = 0; i < ids.Length; i++)
        {
            var status = i < 3 ? StepStatus.Done : i == 3 ? StepStatus.Running : StepStatus.Idle;
            if (_rows.TryGetValue(ids[i], out var row))
                row.SetStatus(status);
        }
        LogText.Text =
            "[12:04:01] [info] Windows 11 26100 · WSL 2 present\n" +
            "[12:04:03] [info] port 127.0.0.1:18789 available\n" +
            "[12:04:05] [info] wsl --install -d Ubuntu-24.04 --name OpenClawGateway --no-launch\n" +
            "[12:04:38] [info] downloading distro … 142/200 MB\n" +
            "[12:04:38] [changed] created %LOCALAPPDATA%\\OpenClawTray\\wsl\\OpenClawGateway\\\n" +
            "[12:04:38] [info] next: install CLI via HTTPS, configure loopback gateway\n";
    }

    private void RenderLocalAiProgressPreview()
    {
        TitleText.Text = "Setting up OpenClaw and Local AI";
        SubtitleText.Text = "Downloading Qwen3.6 35B to this PC";
        var modelIndex = Array.FindIndex(_stepGroups, group => group.GroupId == "local-ai-model");
        for (var i = 0; i < _stepGroups.Length; i++)
        {
            var status = i < modelIndex
                ? StepStatus.Done
                : i == modelIndex ? StepStatus.Running : StepStatus.Idle;
            if (_rows.TryGetValue(_stepGroups[i].GroupId, out var row))
                row.SetStatus(status);
        }
        if (_rows.TryGetValue("local-ai-model", out var modelRow))
            modelRow.SetProgress(38);

        LogText.Text =
            "[12:04:01] [info] WSL mirrored networking enabled\n" +
            "[12:04:03] [info] Ollama v0.32.14 installed on Windows\n" +
            "[12:04:05] [info] starting fresh managed model download\n" +
            "[12:04:38] [info] qwen3.6:35b-a3b-mtp-q4_K_M download in progress\n" +
            "[12:04:38] [info] target: 256K provider context\n";
    }

    private void BuildStepRows()
    {
        foreach (var group in _stepGroups)
        {
            var row = new StepRow(
                group.GroupId,
                group.DisplayName,
                group.Detail,
                group.ShowsDeterminateProgress);
            _rows[group.GroupId] = row;
            StepsPanel.Children.Add(row.Element);
        }
    }

    private static StepGroup[] BuildStepGroups(bool localAiEnabled, string? localAiEngineDescription)
    {
        if (!localAiEnabled)
            return StandardStepGroups;

        var groups = new List<StepGroup>(StandardStepGroups.Length + 6);
        foreach (var group in StandardStepGroups)
        {
            if (group.GroupId == "configure")
            {
                groups.Add(new StepGroup("configure-gateway", "Preparing gateway", ["configure-gateway"]));
                groups.Add(LocalAiGatewayGroup);
                groups.Add(new StepGroup("install-service", "Installing gateway service", ["install-service"]));
                continue;
            }

            groups.Add(group);
            if (group.GroupId == "preflight")
            {
                groups.Add(LocalAiNetworkingGroup);
            }
            else if (group.GroupId == "install-cli")
            {
                groups.Add(new StepGroup(
                    "local-ai-engine",
                    "Preparing Ollama for Local AI",
                    ["acquire-local-ai-engine"],
                    localAiEngineDescription is null
                        ? "Uses a healthy existing engine or installs the pinned managed engine"
                        : $"Managed install when needed: {localAiEngineDescription}"));
                groups.Add(LocalAiModelGroup);
                groups.Add(LocalAiInferenceGroup);
                groups.Add(LocalAiWslVerificationGroup);
            }
        }

        return groups.ToArray();
    }

    private void StartPipeline() =>
        AsyncEventHandlerGuard.Run(
            StartPipelineAsync,
            NullLogger.Instance,
            nameof(StartPipeline));

    private async Task StartPipelineAsync()
    {
        var config = _config!;
        if (_runCts != null)
            return;

        config.LogPath ??= Path.Combine(
            _dataDir, "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        SetupContext? context = null;

        try
        {
            _logger = new SetupLogger(config.LogPath,
                Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);

            _logger.LogEmitted += OnLogEmitted;

            var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
            using var journal = new TransactionJournal(journalPath);
            var commands = new CommandRunner(_logger);
            context = new SetupContext(
                config,
                _logger,
                journal,
                commands,
                cts.Token,
                _dataDir,
                _localDataDir);
            context.ExternalAuthorizationPresenter = new ProgressAuthorizationPresenter(DispatcherQueue, ShowTailscaleAuthorization);
            context.DetailProgress += OnDetailProgress;

            var steps = BuildSteps(config);
            _pipeline = new SetupPipeline(steps);
            _pipeline.StepProgress += OnStepProgress;

            var result = await Task.Run(() => _pipeline.RunAsync(context), cts.Token);
            sw.Stop();
            _pipelineFinished = true;

            var success = result.Outcome == PipelineOutcome.Success;
            if (success)
            {
                if (!config.SkipWizard)
                {
                    if (_rows.TryGetValue("finish", out var finishRow))
                        finishRow.SetStatus(StepStatus.Done);
                    // Pause on a "Gateway installed" milestone so the user knowingly steps
                    // from install (gateway provisioning) into onboarding (the OpenClaw wizard),
                    // instead of being thrown straight into the questions.
                    ShowGatewayInstalledMilestone();
                }
                else
                    // Permissions are now surfaced inline on the capabilities screen, so
                    // the standalone permissions step is skipped — go straight to done.
                    SetupWindow.Active?.NavigateToComplete(true, sw.Elapsed, config.LogPath);
            }
            else
            {
                var errorMsg = result.Outcome == PipelineOutcome.Cancelled
                    ? "Setup was cancelled."
                    : result.FailedStepId != null
                        ? $"Step '{result.FailedStepId}' failed: {result.Message}"
                        : result.Message;
                SetupWindow.Active?.NavigateToComplete(
                    false,
                    sw.Elapsed,
                    config.LogPath,
                    errorMsg,
                    result.CompatibilityFailure);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            _pipelineFinished = true;
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, "Setup was cancelled.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _pipelineFinished = true;
            _logger?.Error($"Setup UI pipeline failed: {ex.Message}");
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, $"Setup crashed: {ex.Message}");
        }
        finally
        {
            if (context != null)
                context.DetailProgress -= OnDetailProgress;
            if (_logger != null)
                _logger.LogEmitted -= OnLogEmitted;
            if (_pipeline != null)
                _pipeline.StepProgress -= OnStepProgress;
            _logger?.Dispose();
            _logger = null;
            _pipeline = null;
            if (ReferenceEquals(_runCts, cts))
                _runCts = null;
        }
    }

    private void CancelPipeline()
    {
        if (!_pipelineFinished)
            _runCts?.Cancel();
    }

    private void OnStepProgress(object? sender, StepProgressEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Find which group this step belongs to
            var groupIndex = Array.FindIndex(_stepGroups, g => g.StepIds.Contains(e.StepId));
            if (groupIndex < 0) return;

            var group = _stepGroups[groupIndex];
            var row = _rows[group.GroupId];

            if (e.Outcome == null)
            {
                // Step started — mark all previous groups as done if still running
                for (int i = 0; i < groupIndex; i++)
                {
                    var prevRow = _rows[_stepGroups[i].GroupId];
                    if (prevRow.Status == StepStatus.Running)
                        prevRow.SetStatus(StepStatus.Done);
                }

                // Mark this group as running
                if (row.Status != StepStatus.Done)
                    row.SetStatus(StepStatus.Running);
            }
            else if (e.Outcome == StepOutcome.Failed || e.Outcome == StepOutcome.FailedTerminal)
            {
                row.SetStatus(StepStatus.Failed);
            }
            else
            {
                // Step succeeded/skipped — track it
                _completedSteps.Add(e.StepId);

                // If all steps in this group are done, mark group done
                if (group.StepIds.All(id => _completedSteps.Contains(id)))
                    row.SetStatus(StepStatus.Done);
            }
        });
    }

    private readonly HashSet<string> _completedSteps = new();

    private void OnDetailProgress(object? sender, SetupDetailProgressEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var groupId = e.Phase switch
            {
                "artifact" => "local-ai-engine",
                "model" => "local-ai-model",
                "inference" => "local-ai-inference",
                "verification" => "local-ai-wsl-verification",
                _ => null,
            };
            if (groupId is null || !_rows.TryGetValue(groupId, out var row))
                return;

            var detail = FormatDetailProgress(e);
            row.SetDetail(detail);

            if (e.Phase == "model")
            {
                if (e.Completed is { } completed && e.Total is > 0 and { } total)
                    row.SetProgress(completed * 100d / total);
                SubtitleText.Text = $"Downloading Qwen3.6 35B: {detail}";
            }
            else if (e.Phase == "artifact")
            {
                SubtitleText.Text = $"Preparing Ollama for Local AI: {detail}";
            }
            else if (e.Phase == "inference")
            {
                SubtitleText.Text = $"Verifying Local AI inference on GPU: {detail}";
            }
        });
    }

    private static string FormatDetailProgress(SetupDetailProgressEvent progress)
    {
        var status = string.IsNullOrWhiteSpace(progress.Status)
            ? "Working"
            : progress.Status.Trim().Replace('_', ' ');

        return progress.Unit switch
        {
            SetupDetailProgressUnit.Bytes when progress.Completed is { } completed && progress.Total is > 0 and { } total =>
                $"{status}: {FormatDecimalBytes(completed)} of {FormatDecimalBytes(total)}",
            SetupDetailProgressUnit.Bytes when progress.Completed is { } completed =>
                $"{status}: {FormatDecimalBytes(completed)}",
            SetupDetailProgressUnit.Entries when progress.Completed is { } completed && progress.Total is > 0 and { } total =>
                $"{status}: {completed:N0} of {total:N0} files",
            _ => status,
        };
    }

    private static string FormatDecimalBytes(long bytes)
    {
        const double megabyte = 1_000_000d;
        const double gigabyte = 1_000_000_000d;
        return bytes >= gigabyte
            ? $"{bytes / gigabyte:0.0} GB"
            : $"{bytes / megabyte:0} MB";
    }

    private void OnLogEmitted(object? sender, LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var line = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}\n";
            _logLineCount++;
            if (_logLineCount > MaxLogLines)
            {
                // Trim old lines (simple: just keep appending; reset periodically)
                if (_logLineCount % MaxLogLines == 0)
                    LogText.Text = line;
                else
                    LogText.Text += line;
            }
            else
            {
                LogText.Text += line;
            }

            // Auto-scroll
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
        });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_config?.LogPath);
    }

    private void ShowTailscaleAuthorization(ExternalAuthorizationRequest request)
    {
        _tailscaleAuthorizationUri = request.AuthorizationUri;
        TailscaleAuthorizationText.Text = request.Message;
        TailscaleAuthorizationPanel.Visibility = Visibility.Visible;
        _ = global::Windows.System.Launcher.LaunchUriAsync(request.AuthorizationUri);
    }

    private void TailscaleAuthorization_Click(object sender, RoutedEventArgs e)
    {
        if (_tailscaleAuthorizationUri is not null)
            _ = global::Windows.System.Launcher.LaunchUriAsync(_tailscaleAuthorizationUri);
    }

    // Swap the install UI for a "Gateway installed" milestone with an explicit
    // onboard CTA. The gateway keeps running (WSL keepalive), so the wizard
    // connects when the user chooses to continue.
    private void ShowGatewayInstalledMilestone()
    {
        InstallHeader.Visibility = Visibility.Collapsed;
        InstallContent.Visibility = Visibility.Collapsed;
        MilestonePanel.Visibility = Visibility.Visible;
        OnboardButton.Visibility = Visibility.Visible;
    }

    private void Onboard_Click(object sender, RoutedEventArgs e)
    {
        if (SetupWindow.Active?.TryNavigateToWizard() == true)
            return;

        MilestoneStatusText.Text = "Another setup task is still active. Wait for it to finish, then start OpenClaw onboard.";
    }

    private static List<SetupStep> BuildSteps(SetupConfig config)
        => SetupStepFactory.BuildDefaultSteps()
            .Where(step => step is not RunGatewayWizardStep)
            .Where(step => config.SkipWizard || step is not WindowsNodeBootstrapContextStep)
            .ToList();
}

internal sealed class ProgressAuthorizationPresenter(
    DispatcherQueue dispatcherQueue,
    Action<ExternalAuthorizationRequest> present) : IExternalAuthorizationPresenter
{
    public Task PresentAsync(ExternalAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dispatcherQueue.TryEnqueue(() => present(request)))
            throw new InvalidOperationException("Setup UI closed before the Tailscale authorization link could be shown.");
        return Task.CompletedTask;
    }
}

// ─── Step Row UI Element ───

internal sealed record StepGroup(
    string GroupId,
    string DisplayName,
    string[] StepIds,
    string? Detail = null,
    bool ShowsDeterminateProgress = false);

internal enum StepStatus { Idle, Running, Done, Failed }

internal sealed class StepRow
{
    public FrameworkElement Element { get; }
    public StepStatus Status { get; private set; }

    private readonly TextBlock _label;
    private readonly TextBlock? _detail;
    private readonly string _displayName;
    private readonly ProgressBar? _determinateProgress;
    private readonly ProgressRing _spinner;
    private readonly Border _idleBadge;
    private readonly Border _checkBadge;
    private readonly Border _errorBadge;
    private readonly Border _rowBorder;

    public StepRow(string groupId, string displayName, string? detail, bool showsDeterminateProgress)
    {
        _displayName = displayName;
        _label = new TextBlock
        {
            Text = displayName,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel textContent;
        if (string.IsNullOrWhiteSpace(detail))
        {
            _detail = null;
            textContent = new StackPanel { Children = { _label } };
        }
        else
        {
            _detail = new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            };
            textContent = new StackPanel
            {
                Spacing = 1,
                Children = { _label, _detail },
            };
        }

        if (showsDeterminateProgress)
        {
            _determinateProgress = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 4,
                Margin = new Thickness(0, 4, 0, 0),
                IsIndeterminate = true,
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetAutomationId(
                _determinateProgress,
                $"SetupProgress.{groupId}.Download");
            AutomationProperties.SetName(_determinateProgress, $"{displayName} download progress");
            textContent.Children.Add(_determinateProgress);
        }
        else
        {
            _determinateProgress = null;
        }

        // Bare Windows spinner (no filled disc) — theme-neutral so it reads white
        // on the dark active row and dark on light, like a standard ProgressRing.
        _spinner = new ProgressRing
        {
            Width = 20, Height = 20,
            MinWidth = 20, MinHeight = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var spinnerFg) && spinnerFg is Brush spinnerBrush)
            _spinner.Foreground = spinnerBrush;

        _idleBadge = CreateEmptyBadge();

        _checkBadge = CreateIconBadge("\uE73E", ResolveColor("SystemFillColorSuccess", Color.FromArgb(255, 0x2B, 0xC3, 0x6F)), Colors.White);
        _checkBadge.Visibility = Visibility.Collapsed;

        _errorBadge = CreateIconBadge("\uE711", ResolveColor("SystemFillColorCritical", Color.FromArgb(255, 0xE8, 0x11, 0x23)), Colors.White);
        _errorBadge.Visibility = Visibility.Collapsed;

        var badgeContainer = new Grid
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeContainer.Children.Add(_idleBadge);
        badgeContainer.Children.Add(_spinner);
        badgeContainer.Children.Add(_checkBadge);
        badgeContainer.Children.Add(_errorBadge);

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        Grid.SetColumn(textContent, 0);
        Grid.SetColumn(badgeContainer, 1);
        grid.Children.Add(textContent);
        grid.Children.Add(badgeContainer);

        _rowBorder = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 5, 12, 5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetAutomationId(_rowBorder, $"SetupProgress.{groupId}");
        AutomationProperties.SetName(
            _rowBorder,
            string.IsNullOrWhiteSpace(detail) ? displayName : $"{displayName}. {detail}");
        if (showsDeterminateProgress)
            AutomationProperties.SetLiveSetting(
                _rowBorder,
                Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        Element = _rowBorder;
    }

    public void SetStatus(StepStatus status)
    {
        Status = status;
        _spinner.IsActive = status == StepStatus.Running;
        _spinner.Visibility = status == StepStatus.Running ? Visibility.Visible : Visibility.Collapsed;
        _idleBadge.Visibility = status == StepStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
        _checkBadge.Visibility = status == StepStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        _errorBadge.Visibility = status == StepStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _label.Opacity = status == StepStatus.Idle ? 0.72 : 1.0;
        if (_detail is not null)
            _detail.Opacity = status == StepStatus.Idle ? 0.5 : 0.65;
        if (_determinateProgress is not null)
        {
            _determinateProgress.Visibility = status == StepStatus.Running
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        _label.FontWeight = status == StepStatus.Running
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

        // Highlight the active step with the setup accent while it is running.
        if (status == StepStatus.Running
            && Application.Current.Resources.TryGetValue("SetupIndicatorAccentBrush", out var accent)
            && accent is SolidColorBrush accentBrush)
        {
            var c = accentBrush.Color;
            _rowBorder.Background = new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B));
            _rowBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B));
        }
        else
        {
            _rowBorder.Background = new SolidColorBrush(Colors.Transparent);
            _rowBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
        }
    }

    public void SetProgress(double percent)
    {
        if (_determinateProgress is null)
            return;

        _determinateProgress.IsIndeterminate = false;
        _determinateProgress.Value = Math.Clamp(percent, 0, 100);
        _determinateProgress.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            _determinateProgress,
            $"{_displayName} download progress, {_determinateProgress.Value:0}%");
    }

    public void SetDetail(string detail)
    {
        if (_detail is null || string.IsNullOrWhiteSpace(detail))
            return;

        _detail.Text = detail;
        AutomationProperties.SetName(_rowBorder, $"{_displayName}. {detail}");
    }

    private static Border CreateEmptyBadge()
    {
        // Use a theme-aware stroke so the pending-step ring stays visible in every theme.
        var border = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };

        if (Application.Current.Resources.TryGetValue("ControlStrongStrokeColorDefaultBrush", out var brush)
            && brush is Brush themed)
        {
            border.BorderBrush = themed;
        }
        else
        {
            border.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 128, 128, 128));
        }

        return border;
    }

    private static Border CreateIconBadge(string glyph, Color background, Color foreground)
    {
        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(background),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 11,
                FontFamily = IconFonts.SymbolThemeFontFamily,
                Foreground = new SolidColorBrush(foreground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }

    // Resolve a native Color theme resource (e.g. SystemFillColorSuccess) with a fallback.
    private static Color ResolveColor(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : fallback;
}
