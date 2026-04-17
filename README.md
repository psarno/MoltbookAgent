# MoltbookAgent

An autonomous AI agent harness for [Moltbook](https://www.moltbook.com) — a platform launched in January 2026 that is exclusively for AI agents. Humans can read, but not post. The premise is exactly as weird as it sounds, and the results of dropping Claude into it unsupervised were genuinely strange. Good strange. Hence this repo.

The agent runs on a configurable polling interval, wakes up, pulls its feed, and runs a full agentic loop using native tool calling — exploring posts, reading context, leaving comments, upvoting, following other agents. It does this autonomously, guided only by the instructions you give it in plain markdown. No hand-holding between cycles; it builds its own memory. Then it goes back to sleep.

I built this to see what would happen. What happened was interesting enough to share.

---

<img width="1565" height="687" alt="image" src="https://github.com/user-attachments/assets/afcd1ce7-f8f7-4469-b219-c012ba3bce27" />

---
## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A [Moltbook](https://www.moltbook.com) account (claim it for your agent)
- An API key for your chosen LLM provider (Anthropic, OpenAI, OpenRouter, or a local model)

---

## Setup

**1. Clone and configure**

```bash
git clone https://github.com/psarno/MoltbookAgent.git
cd MoltbookAgent/MoltbookAgent
cp config.toml.example config.toml
```

Edit `config.toml`. At minimum:

```toml
[llm]
provider = "anthropic"          # or "openai-compatible"
api_key  = ""                   # or set ANTHROPIC_API_KEY / LLM_API_KEY env var
model    = "claude-sonnet-4-6"

[moltbook]
agent_name = "your-agent-username"
observation_mode = true         # keep this true until you've reviewed a few cycles
```

Full config reference is in `config.toml.example` — every option is documented inline.

**2. Set up AgentDocs**

```bash
cp AgentDocs/instructions.example.md AgentDocs/instructions.md
cp AgentDocs/reminders.example.md AgentDocs/reminders.md
cp AgentDocs/special-note.example.md AgentDocs/special-note.md
```

`instructions.md` is the most important file you'll edit. It's your agent's persona, values, and operating principles — loaded into every cycle's system prompt. The example file explains what to put there. Spend time on it; it's the difference between an agent that posts like a chatbot and one that actually has a voice.

`reminders.md` is a shorter document injected periodically (configurable interval) as a grounding check. `special-note.md` is for temporary context you want the agent aware of right now — site issues, things to avoid this week, etc.

---

## Running

**Direct (foreground, good for initial testing)**

```bash
cd MoltbookAgent
dotnet run
```

Each cycle's turn-by-turn output prints to stdout. Conversations are logged as JSONL to `logs/`.

**Windows Service**

```powershell
# Run as Administrator
.\install-service.ps1 -Action install

# Other actions: update, uninstall, status, start, stop
.\install-service.ps1 -Action update
```

**Linux (systemd)**

```bash
dotnet publish -c Release -o /opt/moltbook-agent

# Create /etc/systemd/system/moltbook-agent.service:
[Unit]
Description=MoltbookAgent
After=network.target

[Service]
WorkingDirectory=/opt/moltbook-agent
ExecStart=/opt/moltbook-agent/MoltbookAgent
Restart=always
RestartSec=30
Environment=ANTHROPIC_API_KEY=your-key-here

[Install]
WantedBy=multi-user.target
```

```bash
systemctl enable --now moltbook-agent
```

---

## Observation Mode

`observation_mode = true` in `config.toml` is the safe default and the right place to start. In this mode, action tools (`create_comment`, `create_post`, `upvote_post`, etc.) are intercepted — the agent goes through the full agentic loop, makes decisions, reaches for tools, but nothing actually posts. Everything is logged. You get to read what it *would* have done before you let it loose.

Run a few cycles in observation mode. Read the conversation logs. Tweak `instructions.md`. When the behavior looks right, flip `observation_mode = false`.

---

## LLM Providers

The harness has no vendor SDK dependencies. It speaks directly to provider HTTP APIs and routes through a single `ILlmClient` interface. Switching providers is a config change.

| Provider | Config |
|---|---|
| Anthropic Claude | `provider = "anthropic"` — `ANTHROPIC_API_KEY` or `LLM_API_KEY` |
| OpenAI | `provider = "openai-compatible"`, `endpoint = "https://api.openai.com/v1"` — `OPENAI_API_KEY` or `LLM_API_KEY` |
| OpenRouter | `provider = "openai-compatible"`, `endpoint = "https://openrouter.ai/api/v1"` |
| Local (Ollama, LM Studio, etc.) | `provider = "openai-compatible"`, `endpoint = "http://localhost:1234/v1"`, `api_key = ""` |

API key resolution order: `config.toml [llm] api_key` → `LLM_API_KEY` env var → provider-specific fallback (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`).

---

## Tools

The agent has 14 tools available each cycle:

**Exploration** — `search`, `get_post`, `list_submolts`, `get_profile`  
**Actions** — `create_comment`, `create_post`, `upvote_post`, `upvote_comment`, `follow_agent`, `subscribe_submolt`, `solve_verification`  
**Memory** — `add_memory`, `remove_memory`  
**Control** — `quit` (exits early if there's nothing worth doing)

The agentic loop runs up to 15 turns per cycle; in practice it's usually 3–7. Each turn's text and tool calls print to stdout and are persisted to a timestamped JSONL file.

---

## Project Structure

```
MoltbookAgent/
├── config.toml.example       # Config template — copy to config.toml
├── install-service.ps1       # Windows Service management
├── Worker.cs                 # BackgroundService — polling loop, prompt assembly
├── Services/
│   ├── ILlmClient.cs         # Provider-agnostic interface + factory
│   ├── AnthropicLlmClient.cs # Native /v1/messages tool-calling loop
│   ├── OpenAICompatibleClient.cs  # /chat/completions tool-calling loop
│   ├── MoltbookTools.cs      # All 14 tools with schemas + handlers
│   ├── MoltbookClient.cs     # Moltbook HTTP API client
│   ├── MemoryManager.cs      # Persistent cross-cycle memory (memories.toml)
│   └── StateManager.cs       # Cycle state + stats (state.toml)
├── Models/                   # AgentConfig, AgentState, Memory, etc.
└── AgentDocs/
    ├── instructions.example.md
    ├── reminders.example.md
    └── special-note.example.md
```

---

## License

MIT
