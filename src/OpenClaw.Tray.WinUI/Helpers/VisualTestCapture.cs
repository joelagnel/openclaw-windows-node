using Microsoft.UI.Xaml;
using OpenClaw.SetupEngine.UI;

namespace OpenClawTray.Helpers;

internal static class VisualTestCapture
{
    public static void ScheduleSignalCapture(
        FrameworkElement root,
        string surfaceName = "Chat")
    {
#if DEBUG
        XamlVisualTestCapture.ScheduleSignalCapture(root, surfaceName);
#endif
    }

    public static Task CaptureAsync(FrameworkElement root, string surfaceName)
    {
#if DEBUG
        return XamlVisualTestCapture.CaptureAsync(root, surfaceName);
#else
        return Task.CompletedTask;
#endif
    }
}
