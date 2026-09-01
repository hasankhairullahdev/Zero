using System.ComponentModel;
using System.Runtime.InteropServices;
using ModelContextProtocol.Server;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class PowerTools
{
    // ─── lock_screen ──────────────────────────────────────────────────────────

    [McpServerTool, Description("Lock the Windows screen immediately.")]
    public static string lock_screen()
    {
        NativeMethods.LockWorkStationNative();
        return "OK: screen locked";
    }

    // ─── shutdown ─────────────────────────────────────────────────────────────

    [McpServerTool, Description("Shutdown Windows after an optional delay.")]
    public static string shutdown(
        [Description("Delay in seconds before shutdown. Default: 0 (immediate).")] int delaySeconds = 0)
    {
        System.Diagnostics.Process.Start("shutdown", $"/s /t {delaySeconds}");
        return delaySeconds == 0
            ? "OK: shutting down now"
            : $"OK: shutdown scheduled in {delaySeconds} seconds";
    }

    // ─── restart ──────────────────────────────────────────────────────────────

    [McpServerTool, Description("Restart Windows after an optional delay.")]
    public static string restart(
        [Description("Delay in seconds before restart. Default: 0 (immediate).")] int delaySeconds = 0)
    {
        System.Diagnostics.Process.Start("shutdown", $"/r /t {delaySeconds}");
        return delaySeconds == 0
            ? "OK: restarting now"
            : $"OK: restart scheduled in {delaySeconds} seconds";
    }

    // ─── sleep ────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Put the system into sleep mode.")]
    public static string sleep()
    {
        System.Windows.Forms.Application.SetSuspendState(
            System.Windows.Forms.PowerState.Suspend, false, false);
        return "OK: going to sleep";
    }

    // ─── cancel_shutdown ──────────────────────────────────────────────────────

    [McpServerTool, Description("Cancel a pending scheduled shutdown or restart.")]
    public static string cancel_shutdown()
    {
        System.Diagnostics.Process.Start("shutdown", "/a");
        return "OK: scheduled shutdown/restart cancelled";
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll", EntryPoint = "LockWorkStation")]
    internal static extern bool LockWorkStationNative();
}
