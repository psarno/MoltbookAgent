namespace MoltbookAgent.Models;

/// <summary>
/// Root configuration model matching config.toml structure
/// </summary>
public class AgentConfig
{
    public LlmConfig Llm { get; set; } = new();
    public PollingConfig Polling { get; set; } = new();
    public MoltbookConfig Moltbook { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public RemindersConfig Reminders { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
}

public class LlmConfig
{
    public string Provider { get; set; } = "anthropic";
    public string Model { get; set; } = "";
    public string? Endpoint { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 1.0;
    public string? ApiKey { get; set; }
}

public class PollingConfig
{
    public int IntervalHours { get; set; } = 4;
}

public class MoltbookConfig
{
    public string ApiBase { get; set; } = "https://www.moltbook.com/api/v1";
    public string AgentName { get; set; } = "YourAgentName";
    public bool ObservationMode { get; set; } = true;
    public string? ApiKey { get; set; }
}

public class PathsConfig
{
    public string Instructions { get; set; } = "./instructions.md";
    public string State { get; set; } = "./state.toml";
    public string Memories { get; set; } = "./memories.toml";
    public string Reminders { get; set; } = "./reminders.md";
    public string? SpecialNotes { get; set; } = "./special-note.md";
}

public class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public string File { get; set; } = "./logs/moltbook-agent.log";
}

public class RemindersConfig
{
    /// <summary>
    /// How often to inject reminders (every N cycles). Set to 0 to disable reminders.
    /// </summary>
    public int IntervalCycles { get; set; } = 3;
}

public class MemoryConfig
{
    /// <summary>
    /// Maximum number of recent memories to auto-inject into each cycle.
    /// Memories are sorted by priority (high first) then by recency.
    /// </summary>
    public int MaxInject { get; set; } = 10;
}
