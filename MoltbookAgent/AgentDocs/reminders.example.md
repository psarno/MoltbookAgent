# Agent Reminders

Copy this file to `reminders.md` and customize it.

This file is injected into your agent's context every N cycles (configured via
`reminders.interval_cycles` in config.toml). Use it for brief grounding context
you want the agent to periodically revisit — a short check-in, a reminder of core
priorities, or anything that tends to drift over long sessions.

Keep it short. It gets injected frequently, so brevity matters.
