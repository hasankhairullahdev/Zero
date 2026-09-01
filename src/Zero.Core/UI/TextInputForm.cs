using System.Drawing;
using System.Windows.Forms;

namespace Zero.Core.UI;

/// <summary>
/// A minimal always-on-top text input popup that lets the user send
/// a command to ZERO without focusing the console window.
/// Triggered by Ctrl+Shift+Z. Submits on Enter, closes on Escape.
/// </summary>
public sealed class TextInputForm : Form
{
    private readonly TextBox  _input;
    private readonly Button   _sendBtn;

    /// <summary>Fired when the user submits a non-empty command.</summary>
    public event EventHandler<string>? CommandSubmitted;

    public TextInputForm()
    {
        // ── Form ───────────────────────────────────────────────────────────────
        Text            = "ZERO";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        ClientSize      = new Size(480, 44);
        BackColor       = Color.FromArgb(0x1e, 0x1e, 0x2e);
        ForeColor       = Color.FromArgb(0xcd, 0xd6, 0xf4);
        Font            = new Font("Segoe UI", 11f, FontStyle.Regular);

        // Centre horizontally at top of primary screen
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(
            screen.Left + (screen.Width - ClientSize.Width) / 2,
            screen.Top  + 60);

        // ── TextBox ────────────────────────────────────────────────────────────
        _input = new TextBox
        {
            Dock          = DockStyle.None,
            Location      = new Point(12, 10),
            Size          = new Size(380, 24),
            BackColor     = Color.FromArgb(0x31, 0x32, 0x44),
            ForeColor     = Color.FromArgb(0xcd, 0xd6, 0xf4),
            BorderStyle   = BorderStyle.FixedSingle,
            Font          = new Font("Segoe UI", 11f),
            PlaceholderText = "Ask ZERO anything…"
        };
        _input.KeyDown += OnInputKeyDown;

        // ── Send button ────────────────────────────────────────────────────────
        _sendBtn = new Button
        {
            Text      = "Send",
            Location  = new Point(400, 8),
            Size      = new Size(68, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x89, 0xb4, 0xfa),
            ForeColor = Color.FromArgb(0x1e, 0x1e, 0x2e),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor    = Cursors.Hand
        };
        _sendBtn.FlatAppearance.BorderSize = 0;
        _sendBtn.Click += OnSend;

        Controls.Add(_input);
        Controls.Add(_sendBtn);

        // Focus input on show
        Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };

        // Close on deactivate (user clicks elsewhere)
        Deactivate += (_, _) => HideForm();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            OnSend(sender, e);
        }
        else if (e.KeyCode == Keys.Escape)
        {
            HideForm();
        }
    }

    private void OnSend(object? sender, EventArgs e)
    {
        var text = _input.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _input.Clear();
        HideForm();
        CommandSubmitted?.Invoke(this, text);
    }

    /// <summary>Show the form and pre-fill with existing text (if any).</summary>
    public void ShowAndFocus(string? prefill = null)
    {
        if (prefill is not null)
        {
            _input.Text = prefill;
            _input.SelectAll();
        }

        // Re-centre in case display resolution changed
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(
            screen.Left + (screen.Width - ClientSize.Width) / 2,
            screen.Top  + 60);

        Show();
        BringToFront();
        Activate();
        _input.Focus();
    }

    private void HideForm()
    {
        if (Visible) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Never destroy — just hide so it can be reused
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideForm();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }
}
