# SLNG — Second Life Next-Gen Viewer

A next-generation client for **Second Life** and **OpenSimulator** with a modern,
PBR-based graphics engine. The protocol layer reuses the battle-tested
[LibreMetaverse](https://github.com/cinderblocks/libremetaverse) library; the
rendering runs on **Godot 4 (.NET / Vulkan)** so we get modern lighting, shadows
and post-processing without writing a renderer from scratch.

> Status: **pre-alpha / scaffolding.** Target of the first milestone (M0) is a
> login that connects to a grid and logs incoming object & chat events.

## Why this exists

The official viewer and its forks sit on a ~20-year-old OpenGL codebase. SLNG
keeps full grid compatibility but replaces the rendering and asset path with a
modern engine. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full
rationale and [docs/adr/0001-engine-and-stack.md](docs/adr/0001-engine-and-stack.md)
for the stack decision.

## Tech stack

| Concern | Choice |
|---|---|
| Engine / renderer | Godot 4, .NET (Vulkan) |
| Language | C# (.NET 8) |
| Protocol | LibreMetaverse (login, UDP message system, HTTP CAPS) |
| Physics | Godot Jolt |
| Targets | Second Life grid + OpenSim; Windows first, then Linux/macOS |

## Repository layout

```
app/    Godot 4 .NET project (scenes, rendering, UI)
src/    Engine-agnostic C# libraries (core, net, assets)
tests/  Unit + integration tests (xUnit, OpenSim test grid)
docs/   Architecture, roadmap, AI workflow, ADRs
tools/  Dev scripts and test-grid helpers
```

## Built with AI, by two assistants in parallel

This project is developed primarily by AI agents. It is set up so that
**Claude Code** and **Gemini CLI** can work the repo **at the same time** on
separate git worktrees. Read these before touching code:

- [AGENTS.md](AGENTS.md) — single source of truth (stack, conventions, rules)
- [docs/AI_WORKFLOW.md](docs/AI_WORKFLOW.md) — parallel-agent workflow & worktrees
- [docs/ROADMAP.md](docs/ROADMAP.md) — AI-sized tasks, workstreams, milestones

## Quickstart (once M0 is scaffolded)

```bash
# build the engine-agnostic libraries
dotnet build SLNG.sln

# run the Godot client (editor or headless)
godot --path app
```

## Legal

Any build that connects to the Second Life grid must comply with the Linden Lab
**Third-Party Viewer (TPV) Policy**. The permission/asset-protection model is a
hard design constraint, not an afterthought — see [AGENTS.md](AGENTS.md#non-negotiables).

We ship a small amount of Second Life viewer artwork (the terrain blend ramp and the
Windlight cloud texture) under **Creative Commons Attribution-Share Alike 3.0**. That
licence requires the notice to travel with the program, so the canonical file lives at
[`app/THIRD-PARTY-NOTICES.md`](app/THIRD-PARTY-NOTICES.md) — inside the Godot project, so
an export includes it — and the client shows it under **Preferences → Licences**.

**If you add a third-party asset, add it to that notice file**, including what you changed
about it. Identifying changes is a condition of the licence, not a courtesy.
