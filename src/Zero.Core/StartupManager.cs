using Microsoft.Win32;

namespace Zero.Core;

/// <summary>
/// Manages Windows startup registry entry so ZERO auto-starts on boot.
/// Writes to HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin needed).
/// </summary>
public static class StartupManager
{
    private const string RegistryKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName      = "ZERO";

    /// <summary>Register ZERO to start with Windows.</summary>
    public static void Enable()
    {
        var exePath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine executable path.");

        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true)
            ?? throw new InvalidOperationException($"Cannot open registry key: {RegistryKey}");

        key.SetValue(AppName, $"\"{exePath}\"");
        Console.WriteLine($"[ZERO] Startup enabled — will launch on Windows boot.");
    }

    /// <summary>Remove ZERO from Windows startup.</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        if (key?.GetValue(AppName) is not null)
        {
            key.DeleteValue(AppName);
            Console.WriteLine("[ZERO] Startup disabled.");
        }
    }

    /// <summary>Returns true if ZERO is registered to start with Windows.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: false);
        return key?.GetValue(AppName) is not null;
    }
}
