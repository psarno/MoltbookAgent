namespace MoltbookAgent.Models;

/// <summary>
/// State tracking model matching state.toml structure
/// </summary>
public class AgentState
{
    public TimestampsState Timestamps { get; set; } = new();
    public SeenState Seen { get; set; } = new();
    public StatsState Stats { get; set; } = new();
}

public class TimestampsState
{
    public string LastMoltbookCheck { get; set; } = string.Empty;
    public string LastPostCreated { get; set; } = string.Empty;

    public DateTime? GetLastCheckTime()
    {
        if (string.IsNullOrEmpty(LastMoltbookCheck))
            return null;

        if (DateTime.TryParse(LastMoltbookCheck, out var dt))
            return dt;

        return null;
    }

    public void SetLastCheckTime(DateTime dt)
    {
        LastMoltbookCheck = dt.ToUniversalTime().ToString("O");
    }

    public DateTime? GetLastPostTime()
    {
        if (string.IsNullOrEmpty(LastPostCreated))
            return null;

        if (DateTime.TryParse(LastPostCreated, out var dt))
            return dt;

        return null;
    }

    public void SetLastPostTime(DateTime dt)
    {
        LastPostCreated = dt.ToUniversalTime().ToString("O");
    }
}

public class SeenState
{
    public List<string> PostIds { get; set; } = new();
    public List<string> CommentIds { get; set; } = new();
}

public class StatsState
{
    public int TotalPostsCreated { get; set; }
    public int TotalCommentsCreated { get; set; }
    public int TotalUpvotes { get; set; }
    public int TotalDownvotes { get; set; }
    public int CycleCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
}
