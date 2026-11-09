using AresToys.Capture.Native;

namespace AresToys.Capture;

public static class VirtualScreen
{
    public static (int Left, int Top, int Width, int Height) GetBounds()
    {
        var left = CaptureNativeMethods.GetSystemMetrics(CaptureNativeMethods.SmXVirtualScreen);
        var top = CaptureNativeMethods.GetSystemMetrics(CaptureNativeMethods.SmYVirtualScreen);
        var width = CaptureNativeMethods.GetSystemMetrics(CaptureNativeMethods.SmCxVirtualScreen);
        var height = CaptureNativeMethods.GetSystemMetrics(CaptureNativeMethods.SmCyVirtualScreen);
        return (left, top, width, height);
    }
}
