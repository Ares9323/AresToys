using System.Runtime.InteropServices;

namespace AresToys.Capture.Native;

internal static partial class CaptureNativeMethods
{
    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);
}
