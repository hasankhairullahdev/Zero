using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class AppControlTools
{
    // Loaded once on first use — key: alias (lowercase), value: exe path/name
    private static Dictionary<string, string>? _aliases;
    private static readonly object _aliasLock = new();

    private static Dictionary<string, string> GetAliases()
    {
        if (_aliases is not null) return _aliases;
        lock (_aliasLock)
        {
            if (_aliases is not null) return _aliases;

            // Resolve config path relative to the executing assembly location
            var baseDir    = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "config", "app-aliases.json"),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "config", "app-aliases.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "config", "app-aliases.json"),
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                var raw  = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (raw is not null)
                {
                    _aliases = new Dictionary<string, string>(
                        raw.Select(kv => new KeyValuePair<string, string>(kv.Key.ToLowerInvariant(), kv.Value)),
                        StringComparer.OrdinalIgnoreCase);
                    return _aliases;
                }
            }

            _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return _aliases;
        }
    }

    /// <summary>Resolve an app name via aliases, falling back to the raw value.</summary>
    private static string ResolveAppName(string appName)
    {
        var aliases = GetAliases();
        return aliases.TryGetValue(appName.Trim(), out var resolved) ? resolved : appName;
    }

    // ─── open_url ─────────────────────────────────────────────────────────────

    [McpServerTool, Description("Open a URL in the default browser (e.g. 'https://youtube.com'). Always use this tool when the user wants to visit a website.")]
    public static string open_url(
        [Description("Full URL to open, including scheme (e.g. 'https://youtube.com').")] string url)
    {
        // Prepend https:// if no scheme provided
        if (!url.Contains("://"))
            url = "https://" + url;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true
            });
            return $"OK: opened '{url}' in default browser";
        }
        catch (Exception ex)
        {
            return $"Error: could not open '{url}' — {ex.Message}";
        }
    }

    // ─── launch_app ───────────────────────────────────────────────────────────

    [McpServerTool, Description("Launch an application by name or alias (e.g. 'chrome', 'vscode') or full executable path.")]
    public static string launch_app(
        [Description("Application name or alias (e.g. 'chrome', 'notepad') or full path to executable.")] string appName)
    {
        var target = ResolveAppName(appName);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName       = target,
                UseShellExecute = true
            });
            return target == appName
                ? $"OK: launched '{appName}'"
                : $"OK: launched '{appName}' → '{target}'";
        }
        catch (Exception ex)
        {
            return $"Error: could not launch '{appName}' (resolved: '{target}') — {ex.Message}";
        }
    }

    // ─── close_app ────────────────────────────────────────────────────────────

    [McpServerTool, Description("Close all running processes matching the given name or alias.")]
    public static string close_app(
        [Description("Process name or alias without extension (e.g. 'chrome', 'notepad').")] string appName)
    {
        // Resolve alias and strip .exe suffix for GetProcessesByName
        var target      = ResolveAppName(appName);
        var processName = Path.GetFileNameWithoutExtension(target);
        var processes   = Process.GetProcessesByName(processName);

        if (processes.Length == 0)
            return $"Error: no running process found with name '{processName}'";

        foreach (var p in processes)
        {
            p.CloseMainWindow();
            if (!p.WaitForExit(3000))
                p.Kill();
            p.Dispose();
        }

        return $"OK: closed {processes.Length} process(es) matching '{processName}'";
    }

    // ─── list_running_apps ────────────────────────────────────────────────────

    [McpServerTool, Description("List all currently running processes.")]
    public static string list_running_apps()
    {
        var sb = new StringBuilder();
        foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            try { sb.AppendLine($"{p.Id,6}  {p.ProcessName}"); }
            catch { /* access denied for some system processes */ }
        }
        return sb.ToString();
    }

    // ─── focus_window ─────────────────────────────────────────────────────────

    [McpServerTool, Description("Bring a window with the given title to the foreground.")]
    public static string focus_window(
        [Description("Partial or full window title to match.")] string windowTitle)
    {
        var process = Process.GetProcesses()
            .FirstOrDefault(p =>
            {
                try { return p.MainWindowTitle.Contains(windowTitle, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });

        if (process == null)
            return $"Error: no window found with title containing '{windowTitle}'";

        var hwnd = process.MainWindowHandle;
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        return $"OK: focused window '{process.MainWindowTitle}'";
    }

    // ─── minimize_window ──────────────────────────────────────────────────────

    [McpServerTool, Description("Minimize a window with the given title.")]
    public static string minimize_window(
        [Description("Partial or full window title to match.")] string windowTitle)
    {
        var process = Process.GetProcesses()
            .FirstOrDefault(p =>
            {
                try { return p.MainWindowTitle.Contains(windowTitle, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });

        if (process == null)
            return $"Error: no window found with title containing '{windowTitle}'";

        NativeMethods.ShowWindow(process.MainWindowHandle, NativeMethods.SW_MINIMIZE);
        return $"OK: minimized window '{process.MainWindowTitle}'";
    }
}

internal static partial class NativeMethods
{
    internal const int SW_MINIMIZE = 6;
    internal const int SW_RESTORE  = 9;

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
