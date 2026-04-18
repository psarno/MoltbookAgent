# MoltbookAgent

An autonomous AI agent harness for [Moltbook](https://www.moltbook.com) - a platform launched in January 2026 that is exclusively for AI agents. Humans can read, but not post. Yes, it's as weird as it sounds. I dropped Claude into it unsupervised and the results were interesting enough to turn into a proper repo.

The agent runs on a configurable polling interval, wakes up, pulls its feed, and runs a full agentic loop using native tool calling (exploring posts, reading context, leaving comments, upvoting, following other agents). It does this autonomously, guided only by the instructions you give it in plain markdown. No hand-holding between cycles - it builds its own memory and goes back to sleep.

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

> [!IMPORTANT]
> Full config reference is in `config.toml.example` - every option is documented inline.

> [!TIP]
> `[paths] logs` lets you redirect conversation logs to a fixed directory (useful if you have a separate log viewer).

**2. Set up AgentDocs**

```bash
cp AgentDocs/instructions.example.md AgentDocs/instructions.md
cp AgentDocs/reminders.example.md AgentDocs/reminders.md
cp AgentDocs/special-note.example.md AgentDocs/special-note.md
```

> [!IMPORTANT]
> `instructions.md` is the most important file you'll edit. It's your agent's persona, values, and operating principles, loaded into every cycle's system prompt. The example file explains what to put there. Spend time on it - it's the difference between an agent that posts like a chatbot and one that actually has a voice.

`reminders.md` is a shorter document injected periodically (configurable interval) as a grounding check. `special-note.md` is for temporary context you want the agent aware of right now - site issues, things to avoid this week, etc.

---

## Running

**Direct (foreground, good for initial testing)**

```bash
cd MoltbookAgent
dotnet run
```

Each cycle's turn-by-turn output prints to stdout. Conversations are logged as JSONL to a `logs/` directory alongside `config.toml`. To write logs elsewhere, set `[paths] logs` in `config.toml` (absolute paths and `~` are supported).

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

> [!IMPORTANT]
> `observation_mode = true` in `config.toml` is the safe default and the right place to start. In this mode, action tools (`create_comment`, `create_post`, `upvote_post`, etc.) are intercepted - the agent goes through the full agentic loop, makes decisions, reaches for tools, but nothing actually posts. Everything is logged. You get to read what it *would* have done before you let it loose.

Run a few cycles in observation mode. Read the conversation logs. Tweak `instructions.md`.

> [!WARNING]
> When the behavior looks right, flip `observation_mode = false`. From that point forward, the agent will take real actions on Moltbook. Verify carefully before enabling.

---

## LLM Providers

The harness has no vendor SDK dependencies. It speaks directly to provider HTTP APIs and routes through a single `ILlmClient` interface. Switching providers is a config change.

| Provider | Config |
|---|---|
| Anthropic Claude | `provider = "anthropic"` - `ANTHROPIC_API_KEY` or `LLM_API_KEY` |
| OpenAI | `provider = "openai-compatible"`, `endpoint = "https://api.openai.com/v1"` - `OPENAI_API_KEY` or `LLM_API_KEY` |
| OpenRouter | `provider = "openai-compatible"`, `endpoint = "https://openrouter.ai/api/v1"` |
| Local (Ollama, LM Studio, etc.) | `provider = "openai-compatible"`, `endpoint = "http://localhost:1234/v1"`, `api_key = ""` |

API key resolution order: `config.toml [llm] api_key` > `LLM_API_KEY` env var > provider-specific fallback (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`).

---

## Tools

The agent has 14 tools available each cycle:

**Exploration** - `search`, `get_post`, `list_submolts`, `get_profile`  
**Actions** - `create_comment`, `create_post`, `upvote_post`, `upvote_comment`, `follow_agent`, `subscribe_submolt`, `solve_verification`  
**Memory** - `add_memory`, `remove_memory`  
**Control** - `quit` (exits early if there's nothing worth doing)

The agentic loop runs up to 15 turns per cycle; in practice it's usually 3–7. Each turn's text and tool calls print to stdout and are persisted to a timestamped JSONL file.

---

## How It Works

**The "Harness" Concept**

In software testing and automation, a "harness" is a framework that wraps and controls a system under test, automating interactions and collecting results. MoltbookAgent is a harness for Claude - it provides the scaffolding (config, polling, logging, tool definitions) while Claude makes all the decisions. The harness doesn't tell Claude what to do; it lets Claude decide, and provides the machinery to execute those decisions safely.

**The Agentic Loop**

Each polling cycle is a multi-turn conversation between Claude and the harness:

1. Claude reads its system prompt (your `instructions.md`), its memory, and the current Moltbook feed
2. Claude reasons about what to do next
3. Claude calls a tool (e.g., `get_post`, `create_comment`, `upvote_post`)
4. The harness executes the tool and returns the result
5. Repeat until Claude stops or hits the 15-turn limit

Think of it like checking email - open a message, decide what to do, take action, move to the next one.

**Tool Calling**

Claude doesn't hit the Moltbook API directly. It requests tools by name with parameters, the harness validates and executes them, and returns structured results. In observation mode, tool calls are logged but not executed, so you can see exactly what would have happened. This also forces Claude to be deliberate about stating its intent, which makes behavior easier to predict and debug.

**Memory Across Cycles**

> [!NOTE]
> Between cycles, Claude retains no conversation history - it starts fresh each time. The harness persists a `memories.toml` file that Claude can read and write to using `add_memory` and `remove_memory` tools. This is how the agent builds persistent context about users, ongoing discussions, or lessons learned. Basically a notebook.

**How this differs from scripted automation**

With traditional API automation, you script each step. With an agentic harness, you write principles and instructions, and the LLM figures out how to apply them. The upside is adaptability - the agent handles new situations without code changes. The downside is reduced predictability, which is why observation mode exists.

---

## Project Structure

```
MoltbookAgent/
├── config.toml.example       # Config template - copy to config.toml
├── install-service.ps1       # Windows Service management
├── Worker.cs                 # BackgroundService - polling loop, prompt assembly
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
