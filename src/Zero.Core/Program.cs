using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zero.Core;
using Zero.Core.LLM;
using Zero.Core.Tray;
using Zero.Core.VoiceEngine;

// ── File log (always active — survives WinExe/no-console mode) ───────────────
var logPath   = Path.Combine(AppContext.BaseDirectory, "zero.log");
var logWriter = new StreamWriter(logPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
logWriter.WriteLine($"\n========== ZERO started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");

// ── Global unhandled exception handlers ──────────────────────────────────────
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    logWriter.WriteLine($"{DateTime.Now:HH:mm:ss} [FATAL] {e.ExceptionObject}");

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    logWriter.WriteLine($"{DateTime.Now:HH:mm:ss} [ERROR] Unobserved: {e.Exception.GetBaseException().Message}");
    e.SetObserved();
};

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("config/zero.config.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.Configure<ZeroConfig>(builder.Configuration);

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o =>
{
    o.FormatterName = "simple";
    o.LogToStandardErrorThreshold = LogLevel.Warning;
});
builder.Logging.AddProvider(new FileLoggerProvider(logWriter));
builder.Logging.SetMinimumLevel(LogLevel.Information);
// Reduce noise from framework internals
builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Extensions", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

// ── HTTP Client for Ollama ─────────────────────────────────────────────────────
builder.Services.AddHttpClient<OllamaClient>((sp, client) =>
{
    var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ZeroConfig>>().Value;
    client.BaseAddress = new Uri(cfg.OllamaBaseUrl);
    client.Timeout     = TimeSpan.FromMinutes(5);
});

// ── Core services ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<McpClientManager>();
builder.Services.AddSingleton<ToolCallRouter>();
builder.Services.AddSingleton<HotkeyListener>();
builder.Services.AddSingleton<SpeechRecognizer>();
builder.Services.AddSingleton<KokoroTtsService>();
builder.Services.AddSingleton<TrayManager>();

// ── Background services ───────────────────────────────────────────────────────
builder.Services.AddHostedService<HotkeyListener>(sp => sp.GetRequiredService<HotkeyListener>());
builder.Services.AddHostedService<ZeroHost>();

await builder.Build().RunAsync();
logWriter.Dispose();
