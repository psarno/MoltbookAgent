using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoltbookAgent.Services;

/// <summary>
/// OpenAI-compatible API client supporting tool calling.
/// Works with OpenAI, OpenRouter, and self-hosted models (Ollama, LM Studio, etc.)
/// that implement the /chat/completions endpoint.
/// </summary>
public class OpenAICompatibleClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly ILogger _logger;

    public OpenAICompatibleClient(
        string endpoint, string model, int maxTokens, double temperature,
        ILogger logger, string apiKey)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentException(
                "endpoint is required for openai-compatible provider. " +
                "Example: https://openrouter.ai/api/v1", nameof(endpoint));

        // Normalise: strip trailing slash so relative path concatenation is predictable
        _httpClient = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };

        // Local models may not need auth; skip header if key is empty
        if (!string.IsNullOrEmpty(apiKey))
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

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
        // OpenAI tool format: { type: "function", function: { name, description, parameters } }
        var toolDefs = tools.Select(t => (object)new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.Parameters
            }
        }).ToList();

        // System prompt is sent as the first message with role "system"
        var history = new List<object>
        {
            new { role = "system",  content = systemPrompt },
            new { role = "user",    content = initialMessage }
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
                tools = toolDefs,
                messages = history
            };

            var response = await _httpClient.PostAsJsonAsync("chat/completions", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OpenAIResponse>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Empty response from OpenAI-compatible API");

            var choice = result.Choices?.FirstOrDefault()
                ?? throw new InvalidOperationException("No choices in response");

            totalInputTokens  += result.Usage?.PromptTokens     ?? 0;
            totalOutputTokens += result.Usage?.CompletionTokens ?? 0;
            _logger.LogDebug("Turn {Turn}: {In} input tokens, {Out} output tokens",
                turn + 1, result.Usage?.PromptTokens ?? 0, result.Usage?.CompletionTokens ?? 0);

            var message = choice.Message;

            // Emit text content for this turn
            if (!string.IsNullOrWhiteSpace(message?.Content))
            {
                onTurnText?.Invoke(turn + 1, message.Content);
                finalResponse = message.Content;
            }

            // Build the assistant history entry
            var toolCallEntries = new List<ToolCallEntry>();

            if (message?.ToolCalls?.Length > 0)
            {
                // Tool calls present — add them to history in the format the API expects back
                var tcForHistory = message.ToolCalls.Select(tc => (object)new
                {
                    id   = tc.Id,
                    type = "function",
                    function = new { name = tc.Function?.Name, arguments = tc.Function?.Arguments }
                }).ToArray();

                history.Add(new { role = "assistant", content = message.Content, tool_calls = tcForHistory });

                foreach (var tc in message.ToolCalls)
                {
                    try
                    {
                        var argStr = tc.Function?.Arguments ?? "{}";
                        var inputEl = JsonSerializer.Deserialize<JsonElement>(argStr);
                        toolCallEntries.Add(new ToolCallEntry { Name = tc.Function?.Name ?? "", Input = inputEl });
                    }
                    catch
                    {
                        toolCallEntries.Add(new ToolCallEntry { Name = tc.Function?.Name ?? "" });
                    }
                }
            }
            else
            {
                history.Add(new { role = "assistant", content = message?.Content });
            }

            conversation.Add(new ConversationEntry
            {
                Role = "assistant",
                Text = message?.Content,
                ToolCalls = toolCallEntries.Count > 0 ? toolCallEntries : null
            });

            // No tool calls → model is done
            if (choice.FinishReason == "stop" || choice.FinishReason == "length" ||
                message?.ToolCalls == null || message.ToolCalls.Length == 0)
            {
                _logger.LogInformation("Agent finished after {Turns} turn(s)", turn + 1);
                break;
            }

            // Invoke tools
            bool shouldQuit = false;

            foreach (var tc in message.ToolCalls!)
            {
                _logger.LogInformation("  Tool: {Name}", tc.Function?.Name);
                var toolName = tc.Function?.Name ?? string.Empty;
                var tool = tools.FirstOrDefault(t => t.Name == toolName);
                string toolResult;

                if (tool is null)
                {
                    toolResult = JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" });
                }
                else
                {
                    try
                    {
                        var argStr  = tc.Function?.Arguments ?? "{}";
                        var inputEl = JsonSerializer.Deserialize<JsonElement>(argStr);
                        toolResult  = await tool.Handler(inputEl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error invoking tool {Name}", toolName);
                        toolResult = JsonSerializer.Serialize(new { error = ex.Message });
                    }
                }

                conversation.Add(new ConversationEntry
                {
                    Role = "tool_result",
                    ToolResult = new ToolResultEntry { ToolName = toolName, Content = toolResult }
                });

                // OpenAI tool result: role "tool" with matching tool_call_id
                history.Add(new { role = "tool", tool_call_id = tc.Id, content = toolResult });

                if (toolResult.Contains("\"quit_signal\":true") || toolResult.Contains("\"quit_signal\": true"))
                {
                    _logger.LogInformation("Agent requested early exit");
                    shouldQuit = true;
                }
            }

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
            FinalResponse = finalResponse,
            TurnsUsed     = turnsUsed,
            InputTokens   = totalInputTokens,
            OutputTokens  = totalOutputTokens,
            QuitEarly     = quitEarly,
            Conversation  = conversation
        };
    }

    // ── Response DTOs ──────────────────────────────────────────────────────────

    private sealed class OpenAIResponse
    {
        [JsonPropertyName("choices")]
        public OpenAIChoice[]? Choices { get; set; }

        [JsonPropertyName("usage")]
        public OpenAIUsage? Usage { get; set; }
    }

    private sealed class OpenAIChoice
    {
        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }

        [JsonPropertyName("message")]
        public OpenAIMessage? Message { get; set; }
    }

    private sealed class OpenAIMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public OpenAIToolCall[]? ToolCalls { get; set; }
    }

    private sealed class OpenAIToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("function")]
        public OpenAIFunction? Function { get; set; }
    }

    private sealed class OpenAIFunction
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        // arguments is a JSON-encoded string, not an object
        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }

    private sealed class OpenAIUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }
    }
}
