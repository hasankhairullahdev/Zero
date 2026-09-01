using System.ComponentModel;
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
