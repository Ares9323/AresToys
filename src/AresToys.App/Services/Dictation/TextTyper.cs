using System.Runtime.InteropServices;

namespace AresToys.App.Services.Dictation;

/// <summary>Types text into the window that has the focus, one Unicode character at a time through SendInput, so it
/// never touches the clipboard (nothing to restore, nothing lands in the history). Windows blocks input into
/// elevated windows unless AresToys is elevated too.</summary>
internal static class TextTyper
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventfUnicode = 0x0004;
    private const uint KeyEventfKeyUp = 0x0002;

    public static void Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // A held modifier would turn the characters into shortcuts: let go of them first.
        Native.KeyInjector.ReleaseStickyModifiers();

        var inputs = new List<Input>(text.Length * 2);
        foreach (var unit in text)
        {
            inputs.Add(Make(unit, up: false));
            inputs.Add(Make(unit, up: true));
        }

        // Sent in slices so a long take does not flood the target's input queue in one call.
        const int slice = 200;
        for (var i = 0; i < inputs.Count; i += slice)
        {
            var part = inputs.Skip(i).Take(slice).ToArray();
            _ = SendInput((uint)part.Length, part, Marshal.SizeOf<Input>());
        }
    }

    private static Input Make(char unit, bool up) => new()
    {
        type = InputKeyboard,
        u = new InputUnion { ki = new KeybdInput { wScan = unit, dwFlags = KeyEventfUnicode | (up ? KeyEventfKeyUp : 0) } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput ki;
        [FieldOffset(0)] public MouseInputPadding mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Only here so the union has the size of the largest member (MOUSEINPUT), which SendInput checks.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputPadding
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
}
