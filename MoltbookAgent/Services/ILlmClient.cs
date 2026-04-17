using MoltbookAgent.Models;

namespace MoltbookAgent.Services;

public interface ILlmClient
{
    /// <summary>
    /// Run a full agentic loop: send initial message, invoke tools as requested,
    /// and continue until the model finishes or maxTurns is reached.
    /// </summary>
    Task<AgentLoopResult> RunAgentLoopAsync(
        string systemPrompt,
        string initialMessage,
        IReadOnlyList<AgentTool> tools,
        int maxTurns,
        Action<int, string>? onTurnText = null,
        CancellationToken cancellationToken = default);
}

public static class LlmClientFactory
{
    public static ILlmClient Create(LlmConfig config, ILogger logger)
    {
        if (string.IsNullOrEmpty(config.Model))
            throw new InvalidOperationException(
                "LLM model not configured. Set model in config.toml [llm] section. " +
                "Example: model = \"claude-sonnet-4-6\"");

        var apiKey = ResolveApiKey(config);
        return config.Provider.ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicLlmClient(
                config.Model, config.MaxTokens, config.Temperature, logger, apiKey),
            "openai-compatible" => new OpenAICompatibleClient(
                config.Endpoint!, config.Model, config.MaxTokens, config.Temperature, logger, apiKey),
            _ => throw new ArgumentException($"Unknown LLM provider: {config.Provider}. Valid values: anthropic, openai-compatible")
        };
    }

    /// <summary>
    /// API key resolution order:
    ///   1. config.toml [llm] api_key
    ///   2. LLM_API_KEY env var (works for any provider)
    ///   3. Provider-specific fallback: ANTHROPIC_API_KEY or OPENAI_API_KEY
    /// Local models that need no auth will receive an empty string (client skips the auth header).
    /// </summary>
    private static string ResolveApiKey(LlmConfig config)
    {
        if (!string.IsNullOrEmpty(config.ApiKey))
            return config.ApiKey;

        var generic = Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (!string.IsNullOrEmpty(generic))
            return generic;

        var providerSpecific = config.Provider.ToLowerInvariant() switch
        {
            "anthropic"          => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            "openai-compatible"  => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            _                    => null
        };

        return providerSpecific ?? string.Empty;
    }
}
