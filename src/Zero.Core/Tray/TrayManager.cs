using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace Zero.Core.Tray;

/// <summary>
/// Manages the Windows system tray icon for ZERO.
/// Shows status via icon colour and provides a right-click context menu.
/// Must be run on an STA thread.
/// </summary>
public sealed class TrayManager : IDisposable
{
    public enum TrayState { Idle, Listening, Processing }

    private readonly ILogger<TrayManager> _log;
    private NotifyIcon?             _icon;
    private Thread?                 _staThread;
    private SynchronizationContext? _staSyncCtx;

    // Events wired to menu items
    public event EventHandler? ExitRequested;
    public event EventHandler? OpenTextInputRequested;

    public TrayManager(ILogger<TrayManager> log)
    {
        _log = log;
    }

    public void Start()
    {
        _staThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name         = "TrayMessageLoop"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    /// <summary>
    /// Post an action to the STA message-loop thread (where WinForms runs).
    /// Safe to call from any thread. No-op if the tray hasn't started yet.
    /// </summary>
    public void PostToStaThread(Action action)
    {
        _staSyncCtx?.Post(_ => action(), null);
    }

    /// <summary>Update the tray icon to reflect current ZERO state.</summary>
    public void SetState(TrayState state)
    {
        if (_icon is null || _staSyncCtx is null) return;

        var (tooltip, color) = state switch
        {
            TrayState.Listening  => ("ZERO — Listening...",  Color.FromArgb(0x22, 0xc5, 0x5e)),
            TrayState.Processing => ("ZERO — Processing...", Color.FromArgb(0xf5, 0xa6, 0x23)),
            _                   => ("ZERO — Ready",          Color.FromArgb(0x3b, 0x82, 0xd4)),
        };

        _staSyncCtx.Post(_ =>
        {
            if (_icon is null) return;
            _icon.Text = tooltip;
            _icon.Icon?.Dispose();
            _icon.Icon = CreateColorIcon(color);
        }, null);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void RunMessageLoop()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        // Capture STA sync context so SetState can marshal back
        _staSyncCtx = SynchronizationContext.Current
                      ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_staSyncCtx);

        _icon = new NotifyIcon
        {
            Icon    = CreateColorIcon(Color.FromArgb(0x3b, 0x82, 0xd4)),
            Text    = "ZERO — Ready",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _icon.DoubleClick += (_, _) => OpenTextInputRequested?.Invoke(this, EventArgs.Empty);

        _log.LogInformation("Tray icon started.");
        Application.Run();   // blocks until Application.Exit()
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var titleItem = new ToolStripLabel("ZERO") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        var textInput = new ToolStripMenuItem("Open text input\tCtrl+Shift+Z");
        textInput.Click += (_, _) => OpenTextInputRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(textInput);

        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked      = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        startupItem.Click += (_, _) =>
        {
            if (startupItem.Checked) StartupManager.Enable();
            else                     StartupManager.Disable();
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit ZERO");
        exitItem.Click += (_, _) =>
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            Stop();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    /// <summary>Create a small 16×16 solid-colour icon programmatically.</summary>
    private static Icon CreateColorIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);

        g.Clear(Color.Transparent);

        // Draw a filled circle
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 1, 1, 13, 13);

        // Thin white border for visibility on both light and dark taskbars
        using var pen = new Pen(Color.FromArgb(180, Color.White), 1f);
        g.DrawEllipse(pen, 1, 1, 13, 13);

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Stop()
    {
        _staSyncCtx?.Post(_ =>
        {
            if (_icon is not null) _icon.Visible = false;
            Application.Exit();
        }, null);
    }

    public void Dispose()
    {
        Stop();
        _icon?.Dispose();
    }
}
