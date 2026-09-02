using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace Zero.Core.Tray;

/// <summary>
/// Manages the Windows system tray icon for ZERO.
/// - Idle:      solid blue circle
/// - Listening: animated pulsing green circle (wake word or hotkey active)
/// - Processing: animated spinning amber arc
/// Provides right-click context menu and double-click to open text input.
/// </summary>
public sealed class TrayManager : IDisposable
{
    public enum TrayState { Idle, Listening, Processing }

    private readonly ILogger<TrayManager> _log;
    private NotifyIcon?             _icon;
    private Thread?                 _staThread;
    private SynchronizationContext? _staSyncCtx;

    // Animation
    private System.Windows.Forms.Timer? _animTimer;
    private TrayState                   _currentState = TrayState.Idle;
    private int                         _animFrame;
    private const int AnimIntervalMs = 80;

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
    /// Safe to call from any thread.
    /// </summary>
    public void PostToStaThread(Action action)
    {
        _staSyncCtx?.Post(_ => action(), null);
    }

    /// <summary>Update the tray icon state. Starts/stops animation as needed.</summary>
    public void SetState(TrayState state)
    {
        if (_icon is null || _staSyncCtx is null) return;

        _staSyncCtx.Post(_ =>
        {
            if (_icon is null) return;
            _currentState = state;
            _animFrame    = 0;

            _icon.Text = state switch
            {
                TrayState.Listening  => "ZERO — Listening...",
                TrayState.Processing => "ZERO — Processing...",
                _                   => "ZERO — Ready",
            };

            if (state == TrayState.Idle)
            {
                _animTimer?.Stop();
                UpdateIcon(); // draw static idle icon immediately
            }
            else
            {
                _animTimer?.Start();
                UpdateIcon();
            }
        }, null);
    }

    /// <summary>Show a balloon tooltip notification from the tray icon.</summary>
    public void ShowNotification(string title, string message, int timeoutMs = 4000)
    {
        if (_icon is null || _staSyncCtx is null) return;

        _staSyncCtx.Post(_ =>
        {
            if (_icon is null) return;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText  = message;
            _icon.BalloonTipIcon  = ToolTipIcon.None;
            _icon.ShowBalloonTip(timeoutMs);
        }, null);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void RunMessageLoop()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        _staSyncCtx = SynchronizationContext.Current
                      ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_staSyncCtx);

        _icon = new NotifyIcon
        {
            Icon    = DrawIdleIcon(),
            Text    = "ZERO — Ready",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };
        _icon.DoubleClick += (_, _) => OpenTextInputRequested?.Invoke(this, EventArgs.Empty);

        // Animation timer — fires on STA thread
        _animTimer = new System.Windows.Forms.Timer { Interval = AnimIntervalMs };
        _animTimer.Tick += (_, _) =>
        {
            _animFrame++;
            UpdateIcon();
        };

        _log.LogInformation("Tray icon started.");
        Application.Run();
    }

    private void UpdateIcon()
    {
        if (_icon is null) return;
        var newIcon = _currentState switch
        {
            TrayState.Listening  => DrawListeningIcon(_animFrame),
            TrayState.Processing => DrawProcessingIcon(_animFrame),
            _                   => DrawIdleIcon(),
        };
        var old = _icon.Icon;
        _icon.Icon = newIcon;
        old?.Dispose();
    }

    // ── Icon drawing ──────────────────────────────────────────────────────────

    /// <summary>Solid blue circle — idle.</summary>
    private static Icon DrawIdleIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(0x3b, 0x82, 0xd4));
        g.FillEllipse(brush, 2, 2, 12, 12);
        using var pen = new Pen(Color.FromArgb(180, Color.White), 1f);
        g.DrawEllipse(pen, 2, 2, 12, 12);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Pulsing green circle — listening.</summary>
    private static Icon DrawListeningIcon(int frame)
    {
        // Pulse: radius oscillates between 4 and 7
        double phase  = (frame % 20) / 20.0 * Math.PI * 2;
        float  radius = 4f + (float)(Math.Sin(phase) * 3f);
        float  cx = 8f, cy = 8f;

        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Outer glow ring (faint)
        int alpha = (int)(80 + Math.Sin(phase) * 60);
        using var glowBrush = new SolidBrush(Color.FromArgb(alpha, 0x22, 0xc5, 0x5e));
        g.FillEllipse(glowBrush, cx - radius - 2, cy - radius - 2, (radius + 2) * 2, (radius + 2) * 2);

        // Core circle
        using var coreBrush = new SolidBrush(Color.FromArgb(0x22, 0xc5, 0x5e));
        g.FillEllipse(coreBrush, cx - radius, cy - radius, radius * 2, radius * 2);

        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Spinning amber arc — processing.</summary>
    private static Icon DrawProcessingIcon(int frame)
    {
        float startAngle = (frame * 18f) % 360f; // 18°/frame = ~4.4 rotations/sec at 80ms
        const float sweepAngle = 260f;

        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Background ring (dark)
        using var bgPen = new Pen(Color.FromArgb(60, 0xf5, 0xa6, 0x23), 2.5f);
        g.DrawEllipse(bgPen, 2, 2, 12, 12);

        // Spinning arc
        using var arcPen = new Pen(Color.FromArgb(0xf5, 0xa6, 0x23), 2.5f)
        {
            StartCap = LineCap.Round,
            EndCap   = LineCap.Round
        };
        g.DrawArc(arcPen, 2, 2, 12, 12, startAngle, sweepAngle);

        return Icon.FromHandle(bmp.GetHicon());
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

    public void Stop()
    {
        _staSyncCtx?.Post(_ =>
        {
            _animTimer?.Stop();
            if (_icon is not null) _icon.Visible = false;
            Application.Exit();
        }, null);
    }

    public void Dispose()
    {
        Stop();
        _icon?.Dispose();
        _animTimer?.Dispose();
    }
}
