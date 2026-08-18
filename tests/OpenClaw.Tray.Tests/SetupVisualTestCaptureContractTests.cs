namespace OpenClaw.Tray.Tests;

public sealed class SetupVisualTestCaptureContractTests
{
    [Fact]
    public void SetupCapture_IsDebugOnlySignalDrivenAndRendersTheXamlTree()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var capture = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "XamlVisualTestCapture.cs"));
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml"));
        var hub = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "HubWindow.xaml.cs"));
        var trayAdapter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Helpers",
            "VisualTestCapture.cs"));

        Assert.StartsWith("#if DEBUG", capture);
        Assert.Contains("OPENCLAW_VISUAL_TEST", capture);
        Assert.Contains("OPENCLAW_VISUAL_TEST_DIR", capture);
        Assert.Contains("OPENCLAW_VISUAL_TEST_SIGNAL_DIR", capture);
        Assert.Contains("$\"{routeSurfaceName}.signal\"", capture);
        Assert.Contains("OPENCLAW_VISUAL_TEST_SIGNAL", capture);
        Assert.Contains("OPENCLAW_VISUAL_TEST_SURFACE", capture);
        Assert.Contains("RenderTargetBitmap", capture);
        Assert.Contains("File.Delete(signalPath)", capture);
        Assert.Contains("File.WriteAllBytesAsync(outputPath, png", capture);
        Assert.Contains("#if DEBUG", window);
        Assert.Contains("XamlVisualTestCapture.ScheduleSignalCapture(RootGrid, \"Setup\"", window);
        Assert.Contains("x:Name=\"RootGrid\"", xaml);
        Assert.Contains("VisualTestCapture.ScheduleSignalCapture(RootGrid, \"Hub\")", hub);
        Assert.Contains("#if DEBUG", trayAdapter);
        Assert.Contains("XamlVisualTestCapture.ScheduleSignalCapture(root, surfaceName)", trayAdapter);
        Assert.Contains("XamlVisualTestCapture.CaptureAsync(root, surfaceName)", trayAdapter);
    }
}
