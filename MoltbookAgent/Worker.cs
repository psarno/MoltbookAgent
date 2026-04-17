using MoltbookAgent.Models;
using MoltbookAgent.Services;
using Tomlyn;
using System.Text.Json;

namespace MoltbookAgent;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private AgentConfig _config = null!;
    private StateManager _stateManager = null!;
    private MemoryManager _memoryManager = null!;
    private ILlmClient _llmClient = null!;
    private MoltbookClient _moltbookClient = null!;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MoltbookAgent starting...");

        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config.toml");
            _logger.LogInformation("Loading configuration from {Path}", configPath);
            _config = Toml.ToModel<AgentConfig>(await File.ReadAllTextAsync(configPath, cancellationToken));

            var statePath = Resolve(_config.Paths.State);
            _stateManager = new StateManager(statePath, _logger);

            var memoriesPath = Resolve(_config.Paths.Memories);
            _memoryManager = new MemoryManager(memoriesPath, _logger);

            _llmClient      = LlmClientFactory.Create(_config.Llm, _logger);
            var moltbookApiKey = !string.IsNullOrEmpty(_config.Moltbook.ApiKey)
                ? _config.Moltbook.ApiKey
                : Environment.GetEnvironmentVariable("MOLTBOOK_API_KEY")
                  ?? throw new InvalidOperationException(
                      "Moltbook API key not set. Add api_key to [moltbook] in config.toml or set MOLTBOOK_API_KEY env var.");
            _moltbookClient = new MoltbookClient(_config.Moltbook.ApiBase, moltbookApiKey, _logger);

            _logger.LogInformation("MoltbookAgent initialized — provider: {Provider}, model: {Model}",
                _config.Llm.Provider, _config.Llm.Model);
            _logger.LogInformation("Polling interval: {Hours} hours", _config.Polling.IntervalHours);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize MoltbookAgent");
            throw;
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MoltbookAgent worker started");

        await PerformHeartbeatAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(_config.Polling.IntervalHours);
            _logger.LogInformation("Next heartbeat in {Delay}", delay);
            await Task.Delay(delay, stoppingToken);

            if (!stoppingToken.IsCancellationRequested)
                await PerformHeartbeatAsync(stoppingToken);
        }
    }

    private async Task PerformHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("=== Heartbeat starting ===");

            var state = await _stateManager.LoadAsync();
            state.Stats.CycleCount++;

            // Check agent claim status
            var status = await RetryApiCallAsync(() => _moltbookClient.GetStatusAsync(ct), "GetStatus", ct);
            if (!status.Success)
            {
                _logger.LogWarning("Failed to check agent status after retries — skipping this cycle");
                return;
            }
            if (status.Data?.Status == "pending_claim")
            {
                _logger.LogWarning("Agent not yet claimed. Waiting for human to claim the account.");
                return;
            }
            _logger.LogInformation("Agent status: {Status}", status.Data?.Status);

            // Check DMs
            var dmCheck = await RetryApiCallAsync(() => _moltbookClient.CheckDmsAsync(ct), "CheckDMs", ct);
            if (dmCheck.Success && dmCheck.Data?.HasActivity == true)
                _logger.LogInformation("DM Activity: {Summary}", dmCheck.Data.Summary);

            // Get feed
            var feed  = await RetryApiCallAsync(() => _moltbookClient.GetFeedAsync("hot", 10, ct), "GetFeed", ct);
            var posts = feed.Data?.Posts ?? [];
            if (posts.Length == 0)
            {
                _logger.LogInformation("Personalized feed empty, fetching global posts");
                var global = await RetryApiCallAsync(() => _moltbookClient.GetPostsAsync("hot", 10, ct), "GetPosts", ct);
                posts = global.Data?.Posts ?? [];
            }
            _logger.LogInformation("Retrieved {Count} posts from feed", posts.Length);

            // Load instructions
            var instructions = await File.ReadAllTextAsync(Resolve(_config.Paths.Instructions), ct);

            // Load optional special notes
            string? specialNotes = null;
            if (!string.IsNullOrEmpty(_config.Paths.SpecialNotes))
            {
                var notesPath = Resolve(_config.Paths.SpecialNotes);
                if (File.Exists(notesPath))
                {
                    specialNotes = await File.ReadAllTextAsync(notesPath, ct);
                    _logger.LogInformation("Loaded special notes from {Path}", notesPath);
                }
            }

            // Load reminders
            string? reminders = null;
            var remindersPath = Resolve(_config.Paths.Reminders);
            if (_config.Reminders.IntervalCycles > 0 && File.Exists(remindersPath))
                reminders = await File.ReadAllTextAsync(remindersPath, ct);

            var recentMemories = await _memoryManager.GetRecentMemoriesAsync(_config.Memory.MaxInject);

            var initialContext = BuildContext(posts, dmCheck.Data, state, reminders, _config.Reminders.IntervalCycles);
            var systemPrompt   = BuildSystemPrompt(instructions, specialNotes, recentMemories,
                                                   _config.Moltbook.ObservationMode, _config.Moltbook.AgentName);

            var moltbookTools = new MoltbookTools(
                _moltbookClient, _logger, _config.Moltbook.ObservationMode, _stateManager, _memoryManager);

            _logger.LogInformation("Starting agentic exploration with {ToolCount} tools", moltbookTools.GetTools().Count);

            var loopResult = await _llmClient.RunAgentLoopAsync(
                systemPrompt,
                initialContext,
                moltbookTools.GetTools(),
                maxTurns: 15,
                onTurnText: (turn, text) =>
                {
                    Console.WriteLine();
                    Console.WriteLine($"[Turn {turn}]");
                    Console.WriteLine(text);
                    Console.WriteLine();
                },
                ct);

            Console.WriteLine();
            Console.WriteLine("=== Final Summary ===");
            Console.WriteLine(loopResult.FinalResponse);
            Console.WriteLine("====================");
            Console.WriteLine();

            _logger.LogInformation("Cycle complete — {Turns} turns, {In} input / {Out} output tokens",
                loopResult.TurnsUsed, loopResult.InputTokens, loopResult.OutputTokens);

            await LogConversationAsync(loopResult, posts, ct);

            state.Stats.TotalInputTokens  += loopResult.InputTokens;
            state.Stats.TotalOutputTokens += loopResult.OutputTokens;
            state.Timestamps.SetLastCheckTime(DateTime.UtcNow);
            await _stateManager.SaveAsync(state);

            _logger.LogInformation("=== Heartbeat complete ===");
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("overloaded_error") || ex.Message.Contains("Overloaded"))
        {
            _logger.LogError("API servers overloaded — skipping this cycle");
            Console.WriteLine("\nAPI servers overloaded — skipping this cycle\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during heartbeat");
        }
    }

    private string BuildSystemPrompt(
        string instructions, string? specialNotes, List<Memory> recentMemories,
        bool observationMode, string agentName)
    {
        var modeDescription = observationMode
            ? "**Action Tools** (currently in observation mode — actions are logged but not executed):"
            : "**Action Tools** (action mode enabled — these will execute real actions):";

        var specialNotesSection = !string.IsNullOrEmpty(specialNotes)
            ? $"\n---\n⚠️ **SPECIAL NOTES** ⚠️\n\n{specialNotes}\n\n---\n\n"
            : "";

        var memoriesSection = "";
        if (recentMemories.Count > 0)
        {
            memoriesSection = "\n---\n\n## 📝 Memories from Previous Cycles\n\n" +
                              "These are notes from previous instances. Use them to maintain context:\n\n";
            foreach (var memory in recentMemories)
            {
                var icon = memory.GetPriorityEnum() switch
                {
                    MemoryPriority.High => "🔴",
                    MemoryPriority.Low  => "🟢",
                    _                  => "🟡"
                };
                var createdAt = memory.GetCreatedAtTime();
                var timeAgo   = createdAt.HasValue ? $"({GetTimeAgo(createdAt.Value)})" : "";
                memoriesSection += $"- {icon} **[{memory.Priority.ToUpper()}]** {memory.Content} {timeAgo}\n";
                memoriesSection += $"  *ID: {memory.Id}*\n\n";
            }
            memoriesSection += "Use `add_memory()` to store new notes, `remove_memory(id)` when resolved.\n\n---\n\n";
        }

        var memoryToolsSection =
            "\n**Memory Tools** (maintain context across cycles):\n" +
            "- `add_memory(content, priority)` — Store a note for future instances.\n" +
            "- `remove_memory(memory_id)` — Delete a memory when resolved or no longer relevant.\n";

        return $@"{specialNotesSection}{instructions}{memoriesSection}

# Exploring Moltbook

You're participating on Moltbook under the username ""{agentName}"". You have tools available to explore and engage with the platform.

## Available Tools

**Exploration Tools** (use these to gather context before deciding):
- `search(query, type, limit)` — Semantic search for posts and comments. Use natural language.
- `get_post(post_id)` — Get full post with all comments. Read before engaging.
- `list_submolts()` — See all communities (submolts) on Moltbook.
- `get_profile(agent_name)` — Check an agent's profile and posting history.
{memoryToolsSection}
{modeDescription}
- `create_comment(post_id, content, parent_id)` — Add a comment to a post.
- `create_post(submolt, title, content, url)` — Create a new post (1 per 30 min limit).
- `upvote_post(post_id)` — Upvote a post.
- `upvote_comment(comment_id)` — Upvote a comment.
- `follow_agent(agent_name)` — Follow an agent (be very selective).
- `subscribe_submolt(submolt_name)` — Subscribe to a community.

**Control**:
- `quit(reason)` — End the cycle early if there is nothing worth doing.

## How to Use Tools

1. **Explore first** — Use search and get_post to understand what's happening
2. **Read full context** — Don't comment based on titles alone
3. **Check profiles** — Before following, see their history
4. **Be selective** — Quality over quantity

When you're done exploring, provide a final text response describing what you observed, any interesting patterns, and what actions you took (if any) and why.

Remember: You're Claude. Be yourself. Explore thoughtfully.";
    }

    private string BuildContext(
        MoltbookClient.Post[] posts, MoltbookClient.DmCheck? dmCheck,
        AgentState state, string? reminders, int reminderInterval)
    {
        var context = "# Current Moltbook Context\n\n";

        if (!string.IsNullOrEmpty(reminders) && reminderInterval > 0 && state.Stats.CycleCount % reminderInterval == 0)
        {
            context += "---\n\n# CORE REMINDER\n\n" + reminders + "\n\n---\n\n";
            _logger.LogInformation("Injected core reminders (cycle {CycleCount}, interval {Interval})",
                state.Stats.CycleCount, reminderInterval);
        }

        if (dmCheck?.HasActivity == true)
        {
            context += $"## DM Activity\n\n{dmCheck.Summary}\n\n";
            if (dmCheck.Requests?.Count > 0)
                context += $"- {dmCheck.Requests.Count} pending DM request(s) (require human approval)\n";
            if (dmCheck.Messages?.TotalUnread > 0)
                context += $"- {dmCheck.Messages.TotalUnread} unread message(s)\n";
            context += "\n";
        }

        context += "## Recent Posts\n\n";
        if (posts.Length == 0)
        {
            context += "No new posts in feed.\n";
        }
        else
        {
            foreach (var post in posts.Take(5))
            {
                context += $"### [{post.Submolt?.DisplayName ?? post.Submolt?.Name}] {post.Title}\n";
                context += $"By: {post.Author?.Name} | ↑{post.Upvotes} | {post.CommentCount} comments | ID: {post.Id}\n";
                if (!string.IsNullOrEmpty(post.Content))
                {
                    var preview = post.Content.Length > 100 ? post.Content[..100] + "..." : post.Content;
                    context += $"{preview}\n";
                }
                context += "\n";
            }
            if (posts.Length > 5)
                context += $"... and {posts.Length - 5} more posts (use search to explore)\n\n";
        }

        context += "## Your Stats\n\n";
        context += $"- Posts created: {state.Stats.TotalPostsCreated}\n";
        context += $"- Comments created: {state.Stats.TotalCommentsCreated}\n";
        context += $"- Upvotes given: {state.Stats.TotalUpvotes}\n";
        context += $"- Cycles run: {state.Stats.CycleCount}\n";
        context += $"- Total tokens used: {state.Stats.TotalInputTokens + state.Stats.TotalOutputTokens:N0} " +
                   $"({state.Stats.TotalInputTokens:N0} input, {state.Stats.TotalOutputTokens:N0} output)\n";
        context += $"- Last check: {state.Timestamps.LastMoltbookCheck}\n";

        return context;
    }

    private async Task LogConversationAsync(
        AgentLoopResult loopResult, MoltbookClient.Post[] posts, CancellationToken ct)
    {
        try
        {
            var timestamp  = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var logDir     = Resolve(_config.Paths.Logs ?? "./logs");
            var logPath    = Path.Combine(logDir, $"conversation-{timestamp}.jsonl");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

            var toolCallSummary = loopResult.Conversation
                .Where(e => e.ToolCalls is { Count: > 0 })
                .SelectMany(e => e.ToolCalls!)
                .Select(tc => new { tool = tc.Name, input = tc.Input })
                .ToArray();

            var logEntry = new
            {
                timestamp       = DateTime.UtcNow,
                model           = _config.Llm.Model,
                provider        = _config.Llm.Provider,
                turns_used      = loopResult.TurnsUsed,
                quit_early      = loopResult.QuitEarly,
                usage = new
                {
                    input_tokens  = loopResult.InputTokens,
                    output_tokens = loopResult.OutputTokens,
                    total_tokens  = loopResult.InputTokens + loopResult.OutputTokens
                },
                tool_calls      = toolCallSummary,
                final_response  = loopResult.FinalResponse,
                context_summary = new
                {
                    post_count = posts.Length,
                    post_ids   = posts.Take(10).Select(p => p.Id).ToArray(),
                    post_urls  = posts.Take(10).Select(p => $"https://www.moltbook.com/post/{p.Id}").ToArray()
                },
                full_conversation = loopResult.Conversation.Select(e => new
                {
                    role       = e.Role,
                    text       = e.Text,
                    tool_calls = e.ToolCalls?.Select(tc => new { tool = tc.Name, input = tc.Input }).ToArray(),
                    tool_result = e.ToolResult != null
                        ? new { tool = e.ToolResult.ToolName, content = e.ToolResult.Content }
                        : (object?)null
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(logEntry, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(logPath, json + Environment.NewLine, ct);
            _logger.LogInformation("Logged conversation to {File}", Path.GetFileName(logPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log conversation");
        }
    }

    private async Task<T> RetryApiCallAsync<T>(
        Func<Task<T>> apiCall, string callName, CancellationToken ct, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await apiCall();
            }
            catch (TaskCanceledException) when (attempt < maxRetries)
            {
                _logger.LogWarning("{CallName} timed out (attempt {Attempt}/{Max}). Waiting 30 s...",
                    callName, attempt, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (TaskCanceledException) when (attempt == maxRetries)
            {
                _logger.LogError("{CallName} timed out after {Max} attempts.", callName, maxRetries);
                throw;
            }
        }
        throw new InvalidOperationException("Should not reach here");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MoltbookAgent stopping...");
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves a path to an absolute path.
    /// Supports ~ for the user home directory.
    /// Relative paths are resolved against the current working directory.
    /// </summary>
    private static string Resolve(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.Length > 2 ? path[2..] : string.Empty);
        return Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
    }

    private static string GetTimeAgo(DateTime time)
    {
        var span = DateTime.UtcNow - time;
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours   < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays    < 7)  return $"{(int)span.TotalDays}d ago";
        return time.ToString("MMM d");
    }
}
