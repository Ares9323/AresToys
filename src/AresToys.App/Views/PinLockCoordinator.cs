using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace AresToys.App.Views;

/// <summary>A pinned window that can be locked. Locked pins are click-through, so the only ways
/// back to their controls are the Ctrl+Shift "peek" handled here and the tray's "Unlock all".</summary>
internal interface ILockablePin
{
    Window Window { get; }

    /// <summary>Temporarily interactive (Ctrl+Shift held over the pin): input reaches the window
    /// and the hover overlay shows, the lock rules stay in force. Idempotent.</summary>
    void SetPeek(bool peek);

    /// <summary>Unlock and persist, as if the lock button had been clicked.</summary>
    void Unlock();
}

/// <summary>Watches the locked pins for the Ctrl+Shift peek gesture. One shared 100 ms timer
/// that runs only while at least one locked pin exists, polling the key state and the cursor
/// against each pin's physical window rectangle: no global keyboard hook. UI-thread only.</summary>
internal static partial class PinLockCoordinator
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;

    private static readonly List<ILockablePin> _locked = new();
    private static DispatcherTimer? _timer;

    public static bool HasLockedPins => _locked.Count > 0;

    public static void Register(ILockablePin pin)
    {
        if (!_locked.Contains(pin)) _locked.Add(pin);
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, pin.Window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _timer.Tick += (_, _) => Tick();
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    public static void Unregister(ILockablePin pin)
    {
        _locked.Remove(pin);
        if (_locked.Count == 0) _timer?.Stop();
    }

    /// <summary>Tray fallback: unlock every locked pin.</summary>
    public static void UnlockAll()
    {
        foreach (var pin in _locked.ToList()) pin.Unlock();
    }

    private static void Tick()
    {
        var chord = IsDown(VK_CONTROL) && IsDown(VK_SHIFT);
        var hasCursor = GetCursorPos(out var pt);
        foreach (var pin in _locked.ToList())
        {
            var inside = chord && hasCursor
                && PinWindowPlacement.GetPhysicalBounds(pin.Window) is { } r
                && pt.X >= r.X && pt.X < r.Right && pt.Y >= r.Y && pt.Y < r.Bottom;
            pin.SetPeek(inside);
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);
}
