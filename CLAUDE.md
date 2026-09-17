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
- Skip the preamble and the sign-off — no "Here's the code", no "I'm going to perform
  step 1 now". Findings, failures and the reasoning behind a non-obvious fix still belong
  in the reply; it is the narration around them that is unwanted.
