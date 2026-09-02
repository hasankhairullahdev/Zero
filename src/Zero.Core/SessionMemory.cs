using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zero.Core.LLM;

namespace Zero.Core;

/// <summary>
/// Smart session memory with two tiers:
///
/// 1. SHORT-TERM  — current session messages, kept verbatim in context (last 20 turns).
///    Trimmed automatically when in-session history grows beyond the limit.
///
/// 2. LONG-TERM   — a brief plain-text summary of past sessions, injected as a single
///    "memory" system message just after the main system prompt. This gives ZERO
///    continuity without polluting the context with stale raw messages.
///
/// On startup: long-term summary injected → today's prior turns loaded (if any).
/// After each turn: short-term trimmed → summary updated if session ended.
/// </summary>
public sealed class SessionMemory
{
    private readonly ILogger<SessionMemory> _log;

    // ── Paths ─────────────────────────────────────────────────────────────────
    private static readonly string MemoryDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZERO", "memory");

    private static readonly string ShortTermFile = Path.Combine(MemoryDir, "session.json");
    private static readonly string LongTermFile  = Path.Combine(MemoryDir, "summary.txt");

    // ── Limits ────────────────────────────────────────────────────────────────
    /// <summary>Max user+assistant turns kept verbatim in context (each turn = 2 msgs).</summary>
    private const int MaxShortTermTurns = 12;   // 24 messages max in context
    /// <summary>Max chars for the long-term summary injected into system context.</summary>
    private const int MaxSummaryChars   = 600;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SessionMemory(ILogger<SessionMemory> log)
    {
        _log = log;
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inject memory into <paramref name="history"/> right after the system prompt.
    /// Order: [system] → [memory injection] → [short-term turns]
    /// </summary>
    public void Load(List<ChatMessage> history)
    {
        Directory.CreateDirectory(MemoryDir);

        int insertAt = history.Count > 0 && history[0].Role == "system" ? 1 : 0;

        // 1. Inject long-term summary as a system message
        var summary = LoadSummary();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            history.Insert(insertAt++, new ChatMessage
            {
                Role    = "system",
                Content = $"[ZERO Memory — past sessions]\n{summary}"
            });
            _log.LogInformation("Long-term memory injected ({Chars} chars).", summary.Length);
        }

        // 2. Load short-term (today's session turns)
        var shortTerm = LoadShortTerm();
        if (shortTerm.Count > 0)
        {
            foreach (var m in shortTerm)
                history.Insert(insertAt++, m);
            _log.LogInformation("Short-term memory loaded: {Count} messages.", shortTerm.Count);
        }
    }

    // ── Save (called after every turn) ───────────────────────────────────────

    /// <summary>
    /// Persist the current session's short-term history.
    /// Keeps only the most recent <see cref="MaxShortTermTurns"/> user+assistant pairs.
    /// Also trims <paramref name="history"/> in-place so the live context stays lean.
    /// </summary>
    public void Save(List<ChatMessage> history)
    {
        try
        {
            // Extract user+assistant messages from live history (skip system messages)
            var turns = history
                .Where(m => m.Role is "user" or "assistant" && m.Content is not null)
                .ToList();

            // Trim to max turns
            if (turns.Count > MaxShortTermTurns * 2)
            {
                var excess = turns.Count - MaxShortTermTurns * 2;
                // Remove oldest turns from live history too
                var toRemove = history
                    .Where(m => m.Role is "user" or "assistant" && m.Content is not null)
                    .Take(excess)
                    .ToList();
                foreach (var m in toRemove) history.Remove(m);

                turns = turns.Skip(excess).ToList();
            }

            // Persist trimmed short-term
            var toSave = turns.Select(m => new PersistedMessage
            {
                Role    = m.Role,
                Content = m.Content!
            }).ToList();

            File.WriteAllText(ShortTermFile,
                JsonSerializer.Serialize(toSave, JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save short-term memory.");
        }
    }

    /// <summary>
    /// Update long-term summary from the current session.
    /// Appends a brief bullet of what happened today to the summary file.
    /// Call this when the app is shutting down or periodically.
    /// </summary>
    public void UpdateLongTermSummary(IReadOnlyList<ChatMessage> history)
    {
        try
        {
            var turns = history
                .Where(m => m.Role is "user" or "assistant" && m.Content is not null)
                .ToList();

            if (turns.Count < 2) return;

            // Build a compact bullet summary of this session
            var today    = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var lines    = new List<string> { $"[{today}]" };
            int maxBullets = 5;
            int bullets    = 0;

            for (int i = 0; i + 1 < turns.Count && bullets < maxBullets; i += 2)
            {
                var userMsg = turns[i].Content!.Trim();
                if (userMsg.Length > 80) userMsg = userMsg[..80] + "…";
                lines.Add($"• {userMsg}");
                bullets++;
            }

            var sessionBlurb = string.Join("\n", lines);

            // Append to summary, then trim to MaxSummaryChars from the END
            // (keep most recent context, drop oldest)
            var existing = LoadSummary();
            var combined = string.IsNullOrWhiteSpace(existing)
                ? sessionBlurb
                : existing + "\n\n" + sessionBlurb;

            if (combined.Length > MaxSummaryChars)
                combined = "…" + combined[^(MaxSummaryChars - 1)..];

            File.WriteAllText(LongTermFile, combined);
            _log.LogInformation("Long-term summary updated ({Chars} chars).", combined.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to update long-term summary.");
        }
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    public void Clear()
    {
        try
        {
            if (File.Exists(ShortTermFile)) File.Delete(ShortTermFile);
            if (File.Exists(LongTermFile))  File.Delete(LongTermFile);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to clear memory."); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<ChatMessage> LoadShortTerm()
    {
        if (!File.Exists(ShortTermFile)) return [];
        try
        {
            var json = File.ReadAllText(ShortTermFile);
            var msgs = JsonSerializer.Deserialize<List<PersistedMessage>>(json, JsonOpts);
            return msgs?
                .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => new ChatMessage { Role = m.Role, Content = m.Content })
                .ToList() ?? [];
        }
        catch { return []; }
    }

    private string LoadSummary()
    {
        if (!File.Exists(LongTermFile)) return string.Empty;
        try { return File.ReadAllText(LongTermFile).Trim(); }
        catch { return string.Empty; }
    }

    private sealed class PersistedMessage
    {
        public string Role    { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
