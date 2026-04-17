using System.Text.Json;

namespace MoltbookAgent.Services;

/// <summary>
/// Provider-agnostic tool definition passed to ILlmClient.RunAgentLoopAsync.
/// </summary>
public class AgentTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON schema object (e.g. anonymous type) describing the tool's parameters.</summary>
    public required object Parameters { get; init; }
    public required Func<JsonElement, Task<string>> Handler { get; init; }
}

/// <summary>
/// Result returned by a completed agentic loop run.
/// </summary>
public class AgentLoopResult
{
    public string FinalResponse { get; init; } = string.Empty;
    public int TurnsUsed { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public bool QuitEarly { get; init; }
    public IReadOnlyList<ConversationEntry> Conversation { get; init; } = [];
}

public class ConversationEntry
{
    public required string Role { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ToolCallEntry>? ToolCalls { get; init; }
    public ToolResultEntry? ToolResult { get; init; }
}

public class ToolCallEntry
{
    public required string Name { get; init; }
    public JsonElement Input { get; init; }
}

public class ToolResultEntry
{
    public required string ToolName { get; init; }
    public required string Content { get; init; }
}
