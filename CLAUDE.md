# CLAUDE.md

@AGENTS.md

The shared rules above are authoritative. This file only adds Claude-Code-specific
guidance.

## Subagents

Specialized agents live in `.claude/agents/`; each one's own `description` says what it
covers. Delegate a task that is a phase of its own — it keeps context focused and lets
work parallelize. Example: hand a networking spec to `protocol-re`, then hand the
scene-wiring to `graphics-engineer`.

## Working agreement

- Read the task in `docs/ROADMAP.md`, set its `Owner` to `claude`, and work on the
  matching branch in `E:/Git/SLNG` (see `docs/AI_WORKFLOW.md`). Branch in place — no
  `git worktree`.
- Keep `src/` free of `using Godot;`.
- Prefer test-first for protocol and asset code.
- When you finish a task, run `/slng-verify` (it covers the steps `dotnet build`
  alone misses), then commit with the task id in the message.
- Keep replies short and plain. No preamble, no sign-off, no restating what was asked.
  A finding or a fix gets 1–3 sentences: what's wrong/what changed, why, and the file. Skip
  the rest unless asked.
