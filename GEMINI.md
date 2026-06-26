# GEMINI.md

@AGENTS.md

The shared rules above are authoritative. This file only adds Gemini-CLI-specific
guidance so that Gemini and Claude Code can develop SLNG in parallel.

## Working agreement (same as Claude)

- Pick a task from `docs/ROADMAP.md`, set its `Owner: gemini`, and work the matching
  branch in your **own git worktree** — never share a working tree with another agent.
  See `docs/AI_WORKFLOW.md`.
- Respect the layering rule: no `using Godot;` in `src/`.
- Conventional Commits with the task id, e.g. `feat(assets): j2c decoder (M2-3)`.
- Run `dotnet build` and `dotnet test` before committing.

## Using the role definitions

Gemini CLI has no native subagents, but the role prompts in `.claude/agents/` are
plain Markdown and tool-agnostic. To adopt a role for a session, load it as context,
e.g.:

```
@.claude/agents/protocol-re.md   help me map the ObjectUpdate packet to our ECS
@.claude/agents/graphics-engineer.md   wire J2K textures into a Godot PBR material
```

This keeps one shared set of role definitions for both tools.

## Coordination

- Gemini and Claude pick **different workstreams** by default (e.g. Gemini on
  `assets`, Claude on `net`) so they rarely touch the same files.
- Integrate through small PRs into `main`; rebase often.
- The roadmap's `Suggested tool` column is a hint, not a rule — whoever is free
  takes the next unblocked task and marks ownership.
