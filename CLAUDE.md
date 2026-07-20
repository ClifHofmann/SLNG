# CLAUDE.md

@AGENTS.md

The shared rules above are authoritative. This file only adds Claude-Code-specific
guidance.

## Subagents

Specialized agents live in `.claude/agents/`. Delegate to them rather than doing
everything in the main thread — it keeps context focused and lets work parallelize.

| Agent | Use it for |
|---|---|
| `architect` | System design, module boundaries, ADRs, cross-cutting decisions |
| `protocol-re` | Reverse-engineering SL/OpenSim protocol & asset formats; LibreMetaverse internals |
| `graphics-engineer` | Godot rendering, PBR materials, shaders, lighting, post-processing |
| `asset-pipeline` | J2K decode, mesh/LOD, material resolve, avatar bake, caching, threading |
| `ux-designer` | Viewer UX/UI: HUD, inventory, chat, world map, settings |
| `test-engineer` | Test strategy, xUnit suites, OpenSim integration harness |
| `performance-engineer` | Profiling, frame budgets, memory/VRAM, draw-call reduction |
| `code-reviewer` | Read-only review against AGENTS.md before merge |

Invoke proactively. Example: when starting a networking task, hand the spec to
`protocol-re`; when wiring it into the scene, hand off to `graphics-engineer`.

## Working agreement

- Read the task in `docs/ROADMAP.md`, set its `Owner: claude`, and work on the
  matching branch in your own worktree (see `docs/AI_WORKFLOW.md`).
- Keep `src/` free of `using Godot;`.
- Prefer test-first for protocol and asset code.
- When you finish a task, run `dotnet build` + `dotnet test`, then commit with the
  task id in the message.
- Don't explain your thought process. Don't include introductory or concluding sentences like "Here's the code" or "I'm going to perform step 1 now...". No explanations, no filler text.
