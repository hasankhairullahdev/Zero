using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class NotificationTools
{
    // ─── send_notification ────────────────────────────────────────────────────

    [McpServerTool, Description("Send a Windows balloon (tray) notification.")]
    public static string send_notification(
        [Description("Notification title.")] string title,
        [Description("Notification message body.")] string message)
    {
        // Use a hidden NotifyIcon to show balloon tip — no toast SDK required
        var thread = new Thread(() =>
        {
            using var icon = new System.Windows.Forms.NotifyIcon
            {
                Icon    = System.Drawing.SystemIcons.Information,
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText  = message,
                BalloonTipIcon  = System.Windows.Forms.ToolTipIcon.Info
            };
            icon.ShowBalloonTip(5000);
            Thread.Sleep(5500); // keep alive long enough to display
            icon.Visible = false;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return $"OK: notification sent — '{title}'";
    }
}
