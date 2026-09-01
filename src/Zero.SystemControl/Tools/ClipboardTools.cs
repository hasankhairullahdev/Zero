using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class ClipboardTools
{
    // ─── get_clipboard ────────────────────────────────────────────────────────

    [McpServerTool, Description("Get the current text content of the clipboard.")]
    public static string get_clipboard()
    {
        string? text = null;
        var thread = new Thread(() =>
        {
            text = System.Windows.Forms.Clipboard.GetText();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return string.IsNullOrEmpty(text) ? "(clipboard is empty or contains non-text data)" : text;
    }

    // ─── set_clipboard ────────────────────────────────────────────────────────

    [McpServerTool, Description("Set the clipboard to the given text.")]
    public static string set_clipboard(
        [Description("Text to place on the clipboard.")] string text)
    {
        var thread = new Thread(() =>
        {
            System.Windows.Forms.Clipboard.SetText(text);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return "OK: clipboard updated";
    }
}
