using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ModelContextProtocol.Server;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class ScreenTools
{
    // ─── take_screenshot ──────────────────────────────────────────────────────

    [McpServerTool, Description("Take a screenshot of the primary screen and save it to a file.")]
    public static string take_screenshot(
        [Description("Absolute path to save the PNG file. Defaults to Desktop if not specified.")] string savePath = "")
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            savePath = Path.Combine(desktop, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        }

        var bounds = GetPrimaryScreenBounds();
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        bitmap.Save(savePath, ImageFormat.Png);
        return $"OK: screenshot saved — {savePath}";
    }

    // ─── get_screen_info ──────────────────────────────────────────────────────

    [McpServerTool, Description("Get information about the screen resolution and monitor count.")]
    public static string get_screen_info()
    {
        var monitors = GetMonitorInfos();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Monitor count: {monitors.Count}");
        for (int i = 0; i < monitors.Count; i++)
            sb.AppendLine($"  Monitor {i + 1}: {monitors[i]}");
        return sb.ToString();
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static Rectangle GetPrimaryScreenBounds()
    {
        int width  = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return new Rectangle(0, 0, width, height);
    }

    private static List<string> GetMonitorInfos()
    {
        var results = new List<string>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                results.Add($"{r.right - r.left}x{r.bottom - r.top} at ({r.left},{r.top}){(info.dwFlags == 1 ? " [PRIMARY]" : "")}");
            }
            return true;
        }, IntPtr.Zero);
        return results;
    }
}

internal static partial class NativeMethods
{
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);

    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { internal int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MONITORINFO
    {
        internal uint cbSize;
        internal RECT rcMonitor;
        internal RECT rcWork;
        internal uint dwFlags;
    }
}
