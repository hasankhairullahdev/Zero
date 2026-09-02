using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zero.Core.LLM;

namespace Zero.Core;

/// <summary>
/// Persists conversation history across ZERO sessions.
/// - Saves history to a JSON file on each turn.
/// - Loads history on startup (last N messages, excluding system prompt).
/// - Prunes old messages automatically to prevent unbounded growth.
/// </summary>
public sealed class SessionMemory
{
    private readonly ILogger<SessionMemory> _log;

    private static readonly string MemoryDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZERO", "memory");

    private static readonly string MemoryFile = Path.Combine(MemoryDir, "history.json");

    /// <summary>Maximum number of non-system messages to retain across sessions.</summary>
    private const int MaxMessages = 40;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented       = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SessionMemory(ILogger<SessionMemory> log)
    {
        _log = log;
    }

    /// <summary>
    /// Load persisted history into <paramref name="history"/>.
    /// Inserts messages AFTER the system prompt (index 1 onward).
    /// </summary>
    public void Load(List<ChatMessage> history)
    {
        if (!File.Exists(MemoryFile)) return;

        try
        {
            var json     = File.ReadAllText(MemoryFile);
            var messages = JsonSerializer.Deserialize<List<PersistedMessage>>(json, JsonOpts);
            if (messages is null or { Count: 0 }) return;

            // Only restore user + assistant turns — skip tool/system messages
            var toRestore = messages
                .Where(m => m.Role is "user" or "assistant" && m.Content is not null)
                .TakeLast(MaxMessages)
                .ToList();

            // Insert after system prompt
            int insertAt = history.Count > 0 && history[0].Role == "system" ? 1 : 0;
            foreach (var m in toRestore)
            {
                history.Insert(insertAt++, new ChatMessage
                {
                    Role    = m.Role,
                    Content = m.Content
                });
            }

            _log.LogInformation("Session memory loaded: {Count} messages from previous session.", toRestore.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load session memory — starting fresh.");
        }
    }

    /// <summary>
    /// Persist current history to disk. Call after each completed turn.
    /// Only user and assistant messages are saved (no system/tool noise).
    /// </summary>
    public void Save(IReadOnlyList<ChatMessage> history)
    {
        try
        {
            Directory.CreateDirectory(MemoryDir);

            var toSave = history
                .Where(m => m.Role is "user" or "assistant" && m.Content is not null)
                .TakeLast(MaxMessages)
                .Select(m => new PersistedMessage { Role = m.Role, Content = m.Content! })
                .ToList();

            var json = JsonSerializer.Serialize(toSave, JsonOpts);
            File.WriteAllText(MemoryFile, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save session memory.");
        }
    }

    /// <summary>Wipe all persisted history.</summary>
    public void Clear()
    {
        try { if (File.Exists(MemoryFile)) File.Delete(MemoryFile); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to clear session memory."); }
    }

    private sealed class PersistedMessage
    {
        public string Role    { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
