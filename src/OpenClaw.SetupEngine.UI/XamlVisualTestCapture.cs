#if DEBUG
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Debug-only XAML capture hook for current-head UI proof. Rendering the XAML tree
/// directly remains useful when desktop capture APIs cannot see a disconnected session.
/// </summary>
public static class XamlVisualTestCapture
{
    private const string EnabledVariable = "OPENCLAW_VISUAL_TEST";
    private const string DirectoryVariable = "OPENCLAW_VISUAL_TEST_DIR";
    private const string SignalDirectoryVariable = "OPENCLAW_VISUAL_TEST_SIGNAL_DIR";
    private const string LegacySignalVariable = "OPENCLAW_VISUAL_TEST_SIGNAL";
    private const string SurfaceVariable = "OPENCLAW_VISUAL_TEST_SURFACE";
    private static readonly ConditionalWeakTable<FrameworkElement, object> s_signalListeners = new();

    public static void ScheduleSignalCapture(
        FrameworkElement root,
        string defaultSurfaceName,
        CancellationToken cancellationToken = default)
    {
        if (Environment.GetEnvironmentVariable(EnabledVariable) != "1"
            || !TryResolvePath(Environment.GetEnvironmentVariable(DirectoryVariable), out var rootDirectory))
        {
            return;
        }

        var routeSurfaceName = SanitizePathSegment(defaultSurfaceName);
        string signalPath;
        if (TryResolvePath(
                Environment.GetEnvironmentVariable(SignalDirectoryVariable),
                out var signalDirectory))
        {
            Directory.CreateDirectory(signalDirectory);
            signalPath = Path.Combine(signalDirectory, $"{routeSurfaceName}.signal");
        }
        else if (!TryResolvePath(
                     Environment.GetEnvironmentVariable(LegacySignalVariable),
                     out signalPath))
        {
            return;
        }

        lock (s_signalListeners)
        {
            if (s_signalListeners.TryGetValue(root, out _))
                return;
            s_signalListeners.Add(root, new object());
        }

        var requestedSurface = Environment.GetEnvironmentVariable(SurfaceVariable);
        var surfaceName = SanitizePathSegment(
            string.IsNullOrWhiteSpace(requestedSurface)
                ? defaultSurfaceName
                : requestedSurface.Trim());
        var surfaceDirectory = Path.Combine(rootDirectory, surfaceName);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        RoutedEventHandler? onUnloaded = null;
        onUnloaded = (_, _) =>
        {
            root.Unloaded -= onUnloaded;
            lifetime.Cancel();
        };
        root.Unloaded += onUnloaded;

        _ = CaptureWhenSignaledAsync(root, signalPath, surfaceDirectory, lifetime);
    }

    public static async Task CaptureAsync(FrameworkElement root, string surfaceName)
    {
        if (Environment.GetEnvironmentVariable(EnabledVariable) != "1"
            || !TryResolvePath(Environment.GetEnvironmentVariable(DirectoryVariable), out var rootDirectory))
        {
            return;
        }

        await CaptureToDirectoryAsync(
            root,
            Path.Combine(rootDirectory, SanitizePathSegment(surfaceName)),
            CancellationToken.None);
    }

    private static async Task CaptureWhenSignaledAsync(
        FrameworkElement root,
        string signalPath,
        string surfaceDirectory,
        CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(100, lifetime.Token);
                if (!root.IsLoaded
                    || root.ActualWidth <= 0
                    || root.ActualHeight <= 0
                    || !File.Exists(signalPath)
                    || !TryConsumeSignal(signalPath))
                {
                    continue;
                }

                try
                {
                    await CaptureToDirectoryAsync(root, surfaceDirectory, lifetime.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"XAML visual capture failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Window teardown owns cancellation of the proof listener.
        }
        finally
        {
            lock (s_signalListeners)
                s_signalListeners.Remove(root);
            lifetime.Dispose();
        }
    }

    private static bool TryConsumeSignal(string signalPath)
    {
        try
        {
            File.Delete(signalPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task CaptureToDirectoryAsync(
        FrameworkElement root,
        string surfaceDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(surfaceDirectory);

        if (root.DispatcherQueue.HasThreadAccess)
        {
            await CaptureOnUiThreadAsync(root, surfaceDirectory, cancellationToken);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!root.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                async () =>
                {
                    try
                    {
                        await CaptureOnUiThreadAsync(root, surfaceDirectory, cancellationToken);
                        completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }))
        {
            throw new InvalidOperationException("The UI dispatcher rejected visual capture work.");
        }

        await completion.Task.WaitAsync(cancellationToken);
    }

    private static async Task CaptureOnUiThreadAsync(
        FrameworkElement root,
        string surfaceDirectory,
        CancellationToken cancellationToken)
    {
        if (!root.IsLoaded || root.ActualWidth <= 0 || root.ActualHeight <= 0)
            throw new InvalidOperationException("The XAML surface is not ready to capture.");

        var restoreBackground = ApplyCaptureBackground(root);
        try
        {
            var renderTarget = new RenderTargetBitmap();
            await renderTarget.RenderAsync(root);
            var pixels = await renderTarget.GetPixelsAsync();
            if (renderTarget.PixelWidth <= 0 || renderTarget.PixelHeight <= 0 || pixels.Length == 0)
                throw new InvalidOperationException("The XAML surface rendered an empty frame.");

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)renderTarget.PixelWidth,
                (uint)renderTarget.PixelHeight,
                96,
                96,
                pixels.ToArray());
            await encoder.FlushAsync();

            stream.Seek(0);
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var png = new byte[stream.Size];
            reader.ReadBytes(png);

            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = GetNextOutputPath(surfaceDirectory);
            await File.WriteAllBytesAsync(outputPath, png, cancellationToken);
        }
        finally
        {
            restoreBackground();
        }
    }

    private static Action ApplyCaptureBackground(FrameworkElement root)
    {
        if (root is not Panel panel || panel.Background is not null)
            return static () => { };

        var color = root.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
            : Microsoft.UI.Colors.White;
        panel.Background = new SolidColorBrush(color);
        return () => panel.Background = null;
    }

    private static string GetNextOutputPath(string surfaceDirectory)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var candidate = Path.Combine(surfaceDirectory, $"capture-{index:D2}.png");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"No available visual capture filename in '{surfaceDirectory}'.");
    }

    private static bool TryResolvePath(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            return false;

        try
        {
            path = Path.GetFullPath(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidCharacter, '-');
        return value;
    }
}
#endif
