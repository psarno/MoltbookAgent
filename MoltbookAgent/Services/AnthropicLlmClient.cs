using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoltbookAgent.Services;

/// <summary>
/// Anthropic Claude implementation of ILlmClient using the native /v1/messages HTTP API.
/// Tool definitions and dispatch use the provider-agnostic AgentTool type.
/// </summary>
public class AnthropicLlmClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly ILogger _logger;

    public AnthropicLlmClient(string model, int maxTokens, double temperature, ILogger logger, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "Anthropic API key not configured. Set api_key in config.toml [llm], " +
                "or set the LLM_API_KEY or ANTHROPIC_API_KEY environment variable.");

        _httpClient = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com/") };
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;
        _logger = logger;
    }

    public async Task<AgentLoopResult> RunAgentLoopAsync(
        string systemPrompt,
        string initialMessage,
        IReadOnlyList<AgentTool> tools,
        int maxTurns,
        Action<int, string>? onTurnText = null,
        CancellationToken cancellationToken = default)
    {
        // Anthropic tool format: { name, description, input_schema }
        // input_schema is the JSON schema object from AgentTool.Parameters
        var toolDefs = tools.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            input_schema = t.Parameters
        }).ToList();

        // Conversation history — List<object> with anonymous types; STJ serializes via runtime type
        var history = new List<object>
        {
            new { role = "user", content = initialMessage }
        };

        var conversation = new List<ConversationEntry>
        {
            new() { Role = "user", Text = initialMessage }
        };

        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        string finalResponse = string.Empty;
        bool quitEarly = false;
        int turnsUsed = 0;

        for (int turn = 0; turn < maxTurns; turn++)
        {
            turnsUsed = turn + 1;
            _logger.LogInformation("Turn {Turn}/{Max}", turn + 1, maxTurns);

            var request = new
            {
                model = _model,
                max_tokens = _maxTokens,
                temperature = _temperature,
                system = systemPrompt,
                tools = toolDefs,
                messages = history
            };

            var response = await _httpClient.PostAsJsonAsync("v1/messages", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Empty response from Anthropic API");

            totalInputTokens  += result.Usage?.InputTokens  ?? 0;
            totalOutputTokens += result.Usage?.OutputTokens ?? 0;
            _logger.LogDebug("Turn {Turn}: {In} input tokens, {Out} output tokens",
                turn + 1, result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0);

            // Extract any text the model produced this turn
            var textBlock = result.Content?.FirstOrDefault(b => b.Type == "text");
            if (textBlock?.Text is string text && !string.IsNullOrWhiteSpace(text))
            {
                onTurnText?.Invoke(turn + 1, text);
                finalResponse = text;
            }

            // Rebuild the assistant message for history — must include all content blocks
            var assistantBlocks = (result.Content ?? []).Select(b => b.Type switch
            {
                "text"     => (object)new { type = "text", text = b.Text },
                "tool_use" => (object)new { type = "tool_use", id = b.Id, name = b.Name, input = b.Input },
                _          => (object)new { type = b.Type }
            }).ToArray();
            history.Add(new { role = "assistant", content = assistantBlocks });

            // Track tool calls for the conversation log
            var toolCallEntries = (result.Content ?? [])
                .Where(b => b.Type == "tool_use")
                .Select(b => new ToolCallEntry { Name = b.Name!, Input = b.Input ?? default })
                .ToList();

            conversation.Add(new ConversationEntry
            {
                Role = "assistant",
                Text = textBlock?.Text,
                ToolCalls = toolCallEntries.Count > 0 ? toolCallEntries : null
            });

            // No tool calls → model is done
            if (result.StopReason == "end_turn" || toolCallEntries.Count == 0)
            {
                _logger.LogInformation("Agent finished after {Turns} turn(s)", turn + 1);
                break;
            }

            // Invoke tools and collect results
            var toolResultBlocks = new List<object>();
            bool shouldQuit = false;

            foreach (var block in result.Content!.Where(b => b.Type == "tool_use"))
            {
                _logger.LogInformation("  Tool: {Name}", block.Name);
                var tool = tools.FirstOrDefault(t => t.Name == block.Name);
                string toolResult;

                if (tool is null)
                {
                    toolResult = JsonSerializer.Serialize(new { error = $"Unknown tool: {block.Name}" });
                }
                else
                {
                    try
                    {
                        toolResult = await tool.Handler(block.Input ?? default);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error invoking tool {Name}", block.Name);
                        toolResult = JsonSerializer.Serialize(new { error = ex.Message });
                    }
                }

                conversation.Add(new ConversationEntry
                {
                    Role = "tool_result",
                    ToolResult = new ToolResultEntry { ToolName = block.Name!, Content = toolResult }
                });

                toolResultBlocks.Add(new
                {
                    type = "tool_result",
                    tool_use_id = block.Id,
                    content = toolResult
                });

                if (toolResult.Contains("\"quit_signal\":true") || toolResult.Contains("\"quit_signal\": true"))
                {
                    _logger.LogInformation("Agent requested early exit");
                    shouldQuit = true;
                }
            }

            // Tool results go back as a user message with content blocks
            history.Add(new { role = "user", content = toolResultBlocks.ToArray() });

            if (shouldQuit)
            {
                quitEarly = true;
                break;
            }
        }

        if (turnsUsed >= maxTurns)
            _logger.LogWarning("Reached maximum turns ({Max})", maxTurns);

        return new AgentLoopResult
        {
            FinalResponse     = finalResponse,
            TurnsUsed         = turnsUsed,
            InputTokens       = totalInputTokens,
            OutputTokens      = totalOutputTokens,
            QuitEarly         = quitEarly,
            Conversation      = conversation
        };
    }

    // ── Response DTOs ──────────────────────────────────────────────────────────

    private sealed class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public AnthropicBlock[]? Content { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }

        [JsonPropertyName("usage")]
        public AnthropicUsage? Usage { get; set; }
    }

    private sealed class AnthropicBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        // tool_use input arrives as an arbitrary JSON object
        [JsonPropertyName("input")]
        public JsonElement? Input { get; set; }
    }

    private sealed class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }
}
