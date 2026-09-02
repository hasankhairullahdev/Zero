using System.ComponentModel;
using System.Runtime.InteropServices;
using ModelContextProtocol.Server;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class InputTools
{
    // ─── type_text ────────────────────────────────────────────────────────────

    [McpServerTool, Description("Type text into the currently active window as keyboard input.")]
    public static string type_text(
        [Description("Text to type.")] string text)
    {
        System.Windows.Forms.SendKeys.SendWait(EscapeSendKeys(text));
        return $"OK: typed {text.Length} characters";
    }

    // ─── press_key ────────────────────────────────────────────────────────────

    [McpServerTool, Description("Press a key or key combination (e.g. 'ctrl+c', 'alt+f4', 'enter').")]
    public static string press_key(
        [Description("Key combination string. Modifiers: ctrl, alt, shift. Example: 'ctrl+c', 'win+d', 'f5'.")] string keys)
    {
        var sendKeysStr = ConvertToSendKeys(keys);
        System.Windows.Forms.SendKeys.SendWait(sendKeysStr);
        return $"OK: pressed '{keys}'";
    }

    // ─── mouse_move ───────────────────────────────────────────────────────────

    [McpServerTool, Description("Move the mouse cursor to absolute screen coordinates.")]
    public static string mouse_move(
        [Description("X coordinate (pixels from left edge of screen).")] int x,
        [Description("Y coordinate (pixels from top edge of screen).")] int y)
    {
        SetCursorPos(x, y);
        return $"OK: mouse moved to ({x},{y})";
    }

    // ─── mouse_click ──────────────────────────────────────────────────────────

    [McpServerTool, Description("Click the mouse at the given screen coordinates. Moves cursor then clicks.")]
    public static string mouse_click(
        [Description("X coordinate.")] int x,
        [Description("Y coordinate.")] int y,
        [Description("Button to click: 'left' (default), 'right', or 'double'.")] string button = "left")
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50); // let the OS register the move

        var inputs = button switch
        {
            "right"  => new[] { MakeMouseInput(MOUSEEVENTF_RIGHTDOWN), MakeMouseInput(MOUSEEVENTF_RIGHTUP) },
            "double" => new[]
            {
                MakeMouseInput(MOUSEEVENTF_LEFTDOWN), MakeMouseInput(MOUSEEVENTF_LEFTUP),
                MakeMouseInput(MOUSEEVENTF_LEFTDOWN), MakeMouseInput(MOUSEEVENTF_LEFTUP),
            },
            _        => new[] { MakeMouseInput(MOUSEEVENTF_LEFTDOWN), MakeMouseInput(MOUSEEVENTF_LEFTUP) },
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return $"OK: {button} click at ({x},{y})";
    }

    // ─── mouse_scroll ─────────────────────────────────────────────────────────

    [McpServerTool, Description("Scroll the mouse wheel at the current cursor position.")]
    public static string mouse_scroll(
        [Description("Number of scroll notches. Positive = scroll up, negative = scroll down.")] int amount)
    {
        var input = MakeMouseInput(MOUSEEVENTF_WHEEL);
        input.union.mi.mouseData = (uint)(amount * 120); // 120 = one notch (WHEEL_DELTA)
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
        return $"OK: scrolled {amount} notches";
    }

    // ─── Win32 P/Invoke ───────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP    = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP   = 0x0010;
    private const uint MOUSEEVENTF_WHEEL     = 0x0800;

    private static INPUT MakeMouseInput(uint flags)
    {
        var i = new INPUT { type = 0 }; // INPUT_MOUSE = 0
        i.union.mi.dwFlags = flags;
        return i;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public UNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int  dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Escape special SendKeys characters in plain text.</summary>
    private static string EscapeSendKeys(string text)
    {
        // Characters with special meaning in SendKeys syntax
        const string specials = "+-^%~(){}[]";
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (specials.Contains(c))
                sb.Append('{').Append(c).Append('}');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Convert human-readable key string (e.g. "ctrl+c") to SendKeys syntax.</summary>
    private static string ConvertToSendKeys(string keys)
    {
        // Normalise
        var parts = keys.ToLowerInvariant().Split('+');
        var main  = parts[^1].Trim();
        var mods  = parts[..^1];

        var prefix = new System.Text.StringBuilder();
        foreach (var mod in mods)
        {
            prefix.Append(mod.Trim() switch
            {
                "ctrl"  => "^",
                "alt"   => "%",
                "shift" => "+",
                "win"   => "^%", // approximation: Win key not natively supported via SendKeys
                _       => ""
            });
        }

        var key = main switch
        {
            "enter"  => "{ENTER}",
            "tab"    => "{TAB}",
            "esc"    => "{ESC}",
            "escape" => "{ESC}",
            "space"  => " ",
            "backspace" => "{BACKSPACE}",
            "delete" => "{DELETE}",
            "del"    => "{DELETE}",
            "home"   => "{HOME}",
            "end"    => "{END}",
            "pgup"   => "{PGUP}",
            "pgdn"   => "{PGDN}",
            "up"     => "{UP}",
            "down"   => "{DOWN}",
            "left"   => "{LEFT}",
            "right"  => "{RIGHT}",
            "f1"     => "{F1}",  "f2"  => "{F2}",  "f3"  => "{F3}",  "f4"  => "{F4}",
            "f5"     => "{F5}",  "f6"  => "{F6}",  "f7"  => "{F7}",  "f8"  => "{F8}",
            "f9"     => "{F9}",  "f10" => "{F10}", "f11" => "{F11}", "f12" => "{F12}",
            _        => main   // single char or already formatted
        };

        return prefix.ToString() + key;
    }
}
