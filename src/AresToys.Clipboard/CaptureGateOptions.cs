namespace AresToys.Clipboard;

public sealed class CaptureGateOptions
{
    /// <summary>Process names (case-insensitive, with or without ".exe") whose clipboard activity is ignored.</summary>
    public List<string> BlockedProcesses { get; set; } =
    [
        "KeePass.exe",
        "KeePassXC.exe",
        "1Password.exe",
        "Bitwarden.exe",
        "BitwardenDesktop.exe"
    ];

    /// <summary>If true, every clipboard event is dropped regardless of source.</summary>
    public bool IncognitoActive { get; set; }
}
