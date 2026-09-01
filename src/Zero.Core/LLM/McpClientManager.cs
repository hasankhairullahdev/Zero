using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Zero.Core.LLM;

/// <summary>
/// Manages MCP client connections to Zero.FileManager and Zero.SystemControl.
/// Spawns each server as a child process via stdio transport.
/// </summary>
public sealed class McpClientManager : IAsyncDisposable
{
    private readonly ZeroConfig               _cfg;
    private readonly ILogger<McpClientManager> _log;

    private IMcpClient? _fileManager;
    private IMcpClient? _systemControl;
    private IMcpClient? _webAccess;

    // Cached tool list exposed to Ollama
    private List<OllamaTool>? _toolCache;

    public McpClientManager(IOptions<ZeroConfig> cfg, ILogger<McpClientManager> log)
    {
        _cfg = cfg.Value;
        _log = log;
    }

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Initialising MCP clients...");

        _fileManager = await McpClientFactory.CreateAsync(
            new StdioClientTransport(BuildTransportOptions("Zero.FileManager", _cfg.FileManagerProjectPath)),
            cancellationToken: ct);

        _systemControl = await McpClientFactory.CreateAsync(
            new StdioClientTransport(BuildTransportOptions("Zero.SystemControl", _cfg.SystemControlProjectPath)),
            cancellationToken: ct);

        _webAccess = await McpClientFactory.CreateAsync(
            new StdioClientTransport(BuildTransportOptions("Zero.WebAccess", _cfg.WebAccessProjectPath)),
            cancellationToken: ct);

        _log.LogInformation("MCP clients ready.");
    }

    /// <summary>
    /// If a published exe exists next to Zero.Core.exe, spawn it directly (fast).
    /// Otherwise fall back to 'dotnet run' for dev mode.
    /// </summary>
    private static StdioClientTransportOptions BuildTransportOptions(string serverName, string projectPath)
    {
        // Look for published exe in same directory as the running executable
        var exeDir  = AppContext.BaseDirectory;
        var exePath = Path.Combine(exeDir, serverName + ".exe");

        if (File.Exists(exePath))
        {
            return new StdioClientTransportOptions
            {
                Command   = exePath,
                Arguments = [],
                Name      = serverName
            };
        }

        // Dev fallback: dotnet run
        return new StdioClientTransportOptions
        {
            Command   = "dotnet",
            Arguments = ["run", "--project", projectPath, "--configuration", "Release"],
            Name      = serverName
        };
    }

    /// <summary>Returns all available tools from both MCP servers as Ollama tool definitions.</summary>
    public async Task<List<OllamaTool>> GetToolsAsync(CancellationToken ct = default)
    {
        if (_toolCache is not null)
            return _toolCache;

        var tools = new List<OllamaTool>();

        foreach (var client in new[] { _fileManager, _systemControl, _webAccess })
        {
            if (client is null) continue;
            var mcpTools = await client.ListToolsAsync(cancellationToken: ct);
            foreach (var t in mcpTools)
            {
                var props    = new Dictionary<string, OllamaProperty>();
                var required = new List<string>();

                if (t.JsonSchema.TryGetProperty("properties", out var propsEl))
                    foreach (var prop in propsEl.EnumerateObject())
                    {
                        var desc  = prop.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var type  = prop.Value.TryGetProperty("type",        out var tp) ? tp.GetString() ?? "string" : "string";
                        props[prop.Name] = new OllamaProperty { Type = type, Description = desc };
                    }

                if (t.JsonSchema.TryGetProperty("required", out var reqEl))
                    foreach (var r in reqEl.EnumerateArray())
                        if (r.GetString() is { } name)
                            required.Add(name);

                tools.Add(new OllamaTool
                {
                    Function = new OllamaFunction
                    {
                        Name        = t.Name,
                        Description = t.Description ?? "",
                        Parameters  = new OllamaParameters
                        {
                            Properties = props,
                            Required   = required
                        }
                    }
                });
            }
        }

        _toolCache = tools;
        _log.LogInformation("Loaded {Count} tools from MCP servers.", tools.Count);
        return tools;
    }

    /// <summary>Call a named tool on whichever MCP server owns it.</summary>
    public async Task<string> CallToolAsync(
        string                          toolName,
        Dictionary<string, object?>     arguments,
        CancellationToken               ct = default)
    {
        foreach (var client in new[] { _fileManager, _systemControl, _webAccess })
        {
            if (client is null) continue;

            var mcpTools = await client.ListToolsAsync(cancellationToken: ct);
            if (!mcpTools.Any(t => t.Name == toolName))
                continue;

            var readOnly = (IReadOnlyDictionary<string, object?>)arguments;
            var result   = await client.CallToolAsync(toolName, readOnly, cancellationToken: ct);

            // Concatenate all text content blocks
            var sb = new System.Text.StringBuilder();
            foreach (var content in result.Content)
                if (content is TextContentBlock tcb)
                    sb.AppendLine(tcb.Text);

            return sb.ToString().TrimEnd();
        }

        return $"Error: no MCP server found for tool '{toolName}'";
    }

    public async ValueTask DisposeAsync()
    {
        if (_fileManager   is IAsyncDisposable fmD) await fmD.DisposeAsync();
        if (_systemControl is IAsyncDisposable scD) await scD.DisposeAsync();
        if (_webAccess     is IAsyncDisposable waD) await waD.DisposeAsync();
    }
}
