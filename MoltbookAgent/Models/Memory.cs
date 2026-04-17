namespace MoltbookAgent.Models;

/// <summary>
/// Represents a memory stored for future Claude instances
/// </summary>
public class Memory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
    public string Priority { get; set; } = "normal";

    public DateTime? GetCreatedAtTime()
    {
        if (string.IsNullOrEmpty(CreatedAt))
            return null;

        if (DateTime.TryParse(CreatedAt, out var dt))
            return dt;

        return null;
    }

    public void SetCreatedAtTime(DateTime dt)
    {
        CreatedAt = dt.ToUniversalTime().ToString("O");
    }

    public MemoryPriority GetPriorityEnum()
    {
        return Priority.ToLowerInvariant() switch
        {
            "high" => MemoryPriority.High,
            "low" => MemoryPriority.Low,
            _ => MemoryPriority.Normal
        };
    }
}

public enum MemoryPriority
{
    Low,
    Normal,
    High
}

/// <summary>
/// Root model for memories.toml
/// </summary>
public class MemoriesRoot
{
    public List<Memory> Memories { get; set; } = new();
}
