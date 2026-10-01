# CLAUDE.md

@AGENTS.md

The shared rules above are authoritative. This file only adds Claude-Code-specific
guidance.

## Subagents

Specialized agents live in `.claude/agents/`; each one's own `description` says what it
covers. Delegate a task that is a phase of its own — it keeps context focused and lets
work parallelize. Example: hand a networking spec to `protocol-re`, then hand the
scene-wiring to `graphics-engineer`.

## Delegation — cost first

The main session is the expensive model. Use it to understand, decide and review; hand
the typing to a cheaper one. Pass `model` on the `Agent` call to override the agent's
default.

| Work | Who |
|---|---|
| Mechanical, fully specified (renames, applying a stated change to N files, boilerplate, `AppVersion`/ROADMAP edits, log/cache greps, running verify and summarising) | `mechanic` (haiku) |
| Implementing a task whose approach is already decided; tests; UI layouts | the matching specialist, `model: sonnet` |
| Reading protocol/LMV source, checking behaviour against the real viewer source | `protocol-re` / `viewer-parity` (sonnet) — they must cite the source, which is the check on their output |
| Design, cross-layer decisions, root-causing a bug, reviewing a diff | main session, or `architect` (opus) |

- Write the prompt so the delegate needs no more context: files, the exact change, the
  acceptance check. A vague prompt costs more than doing it yourself.
- Do not implement a decided change in the main session when a delegate can. Read the
  delegate's diff before reporting done — you own the result.
- Do not delegate what takes less effort to do than to specify (a one-line edit).

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
