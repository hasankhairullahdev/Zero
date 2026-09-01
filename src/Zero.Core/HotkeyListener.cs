using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zero.Core;

/// <summary>
/// Registers global hotkeys and fires events when they are pressed.
/// Hotkeys:
///   Ctrl+Shift+Space  — toggle voice push-to-talk
///   Ctrl+Shift+Z      — open text input popup
///   Ctrl+Shift+X      — cancel / interrupt active response
/// Runs a hidden Win32 message loop on a dedicated thread.
/// </summary>
public sealed class HotkeyListener : IHostedService, IDisposable
{
    public event EventHandler? HotkeyPressed;       // Ctrl+Shift+Space
    public event EventHandler? TextInputRequested;  // Ctrl+Shift+Z
    public event EventHandler? CancelRequested;     // Ctrl+Shift+X

    private readonly ZeroConfig              _cfg;
    private readonly ILogger<HotkeyListener> _log;
    private Thread?  _msgThread;
    private bool     _running;

    private const int WM_HOTKEY = 0x0312;

    // Hotkey IDs
    private const int ID_VOICE  = 1;  // Ctrl+Shift+Space
    private const int ID_TEXT   = 2;  // Ctrl+Shift+Z
    private const int ID_CANCEL = 3;  // Ctrl+Shift+X

    private const uint MOD_CTRL_SHIFT = 0x0002 | 0x0004; // MOD_CTRL | MOD_SHIFT
    private const uint VK_Z     = 0x5A;
    private const uint VK_X     = 0x58;

    public HotkeyListener(IOptions<ZeroConfig> cfg, ILogger<HotkeyListener> log)
    {
        _cfg = cfg.Value;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _running   = true;
        _msgThread = new Thread(MessageLoop) { IsBackground = true, Name = "HotkeyMsgLoop" };
        _msgThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _running = false;
        NativeMethods.PostThreadMessage((uint)_msgThread!.ManagedThreadId, 0x0012 /*WM_QUIT*/, 0, 0);
        return Task.CompletedTask;
    }

    private void MessageLoop()
    {
        Register(ID_VOICE,  (uint)_cfg.HotkeyMod, (uint)_cfg.HotkeyVk, "Ctrl+Shift+Space (voice)");
        Register(ID_TEXT,   MOD_CTRL_SHIFT, VK_Z,  "Ctrl+Shift+Z (text input)");
        Register(ID_CANCEL, MOD_CTRL_SHIFT, VK_X,  "Ctrl+Shift+X (cancel)");

        while (_running && NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                switch (msg.wParam.ToInt32())
                {
                    case ID_VOICE:
                        _log.LogDebug("Voice hotkey triggered.");
                        HotkeyPressed?.Invoke(this, EventArgs.Empty);
                        break;
                    case ID_TEXT:
                        _log.LogDebug("Text input hotkey triggered.");
                        TextInputRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case ID_CANCEL:
                        _log.LogDebug("Cancel hotkey triggered.");
                        CancelRequested?.Invoke(this, EventArgs.Empty);
                        break;
                }
            }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnregisterHotKey(IntPtr.Zero, ID_VOICE);
        NativeMethods.UnregisterHotKey(IntPtr.Zero, ID_TEXT);
        NativeMethods.UnregisterHotKey(IntPtr.Zero, ID_CANCEL);
    }

    private void Register(int id, uint mod, uint vk, string name)
    {
        bool ok = NativeMethods.RegisterHotKey(IntPtr.Zero, id, mod, vk);
        if (!ok)
            _log.LogWarning("Could not register hotkey {Name}. Another app may own it.", name);
        else
            _log.LogInformation("Hotkey registered: {Name}", name);
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private static class NativeMethods
    {
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] internal static extern int  GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref MSG lpmsg);
        [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            internal IntPtr hwnd;
            internal uint   message;
            internal IntPtr wParam;
            internal IntPtr lParam;
            internal uint   time;
            internal System.Drawing.Point pt;
        }
    }
}
