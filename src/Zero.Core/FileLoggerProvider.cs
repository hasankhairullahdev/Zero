using Microsoft.Extensions.Logging;

namespace Zero.Core;

/// <summary>
/// Simple ILoggerProvider that writes structured log lines to a shared StreamWriter.
/// Thread-safe via lock on the writer instance.
/// </summary>
public sealed class FileLoggerProvider(StreamWriter writer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, writer);
    public void Dispose() { }
}

file sealed class FileLogger(string category, StreamWriter writer) : ILogger
{
    // Shorten category: "Zero.Core.LLM.OllamaClient" -> "OllamaClient"
    private readonly string _name = category.Contains('.')
        ? category[(category.LastIndexOf('.') + 1)..]
        : category;

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(
        LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;

        var prefix = level switch
        {
            LogLevel.Information => "INFO ",
            LogLevel.Warning     => "WARN ",
            LogLevel.Error       => "ERROR",
            LogLevel.Critical    => "CRIT ",
            _                    => "DEBUG"
        };

        var msg = formatter(state, exception);
        var line = $"{DateTime.Now:HH:mm:ss} [{prefix}] {_name}: {msg}";
        if (exception is not null)
            line += $"\n  -> {exception.GetType().Name}: {exception.Message}";

        lock (writer)
        {
            try { writer.WriteLine(line); }
            catch { /* ignore write errors */ }
        }
    }
}
