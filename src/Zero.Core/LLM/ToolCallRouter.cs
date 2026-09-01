using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Zero.Core.LLM;

/// <summary>
/// Routes tool calls from the LLM to the appropriate MCP server and
/// returns their results as ChatMessages ready to send back to Ollama.
/// </summary>
public sealed class ToolCallRouter
{
    private readonly McpClientManager          _mcp;
    private readonly ILogger<ToolCallRouter>   _log;

    public ToolCallRouter(McpClientManager mcp, ILogger<ToolCallRouter> log)
    {
        _mcp = mcp;
        _log = log;
    }

    /// <summary>
    /// Execute all tool calls contained in an assistant message.
    /// Returns one ChatMessage per tool call (role = "tool").
    /// </summary>
    public async Task<List<ChatMessage>> ExecuteAsync(
        ChatMessage          assistantMessage,
        CancellationToken    ct = default)
    {
        var results = new List<ChatMessage>();

        if (assistantMessage.ToolCalls is not { Count: > 0 })
            return results;

        foreach (var call in assistantMessage.ToolCalls)
        {
            var toolName = call.Function.Name;
            var callId   = call.Id ?? Guid.NewGuid().ToString("N")[..8];

            _log.LogInformation("Executing tool '{Tool}' (id={Id})", toolName, callId);

            string resultText;
            try
            {
                // Serialise arguments back to a dictionary for MCP
                var argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    call.Function.Arguments.GetRawText())
                    ?? [];

                var mcpResult = await _mcp.CallToolAsync(toolName, argsDict, ct);
                resultText    = mcpResult;
                _log.LogDebug("Tool '{Tool}' returned: {Result}", toolName, resultText);
            }
            catch (Exception ex)
            {
                resultText = $"Error executing tool '{toolName}': {ex.Message}";
                _log.LogWarning(ex, "Tool '{Tool}' failed", toolName);
            }

            results.Add(new ChatMessage
            {
                Role        = "tool",
                Content     = resultText,
                ToolCallId  = callId
            });
        }

        return results;
    }
}
