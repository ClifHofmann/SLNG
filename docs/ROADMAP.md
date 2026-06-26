# Roadmap

This roadmap assumes the application is **built by AI agents**. Work is therefore
sliced into **AI-sized tasks**: each task is a self-contained unit with a clear spec
and explicit acceptance criteria that one agent can complete in a single focused
session, and that another agent (or a human) can verify by running tests.

## How we work

Each task follows a **spec → test → implement → review** loop:

1. The owning agent reads the task and writes/extends a short spec if needed.
2. For protocol and asset work, write the test first (output must be verifiable).
3. Implement against the stable interface of the layer.
4. `code-reviewer` checks it against `AGENTS.md`, then it merges to `main`.

### Workstreams (parallel tracks)

Tasks are grouped so two agents rarely touch the same files. Default split:
**Claude** tends the `net` / `core` tracks, **Gemini** tends the `assets` / `render`
/ `ui` tracks — but whoever is free takes the next unblocked task.

| Track | Area | Lives in |
|---|---|---|
| `net` | Protocol, login, regions | `src/SLNG.Net` |
| `core` | World model / ECS | `src/SLNG.Core` |
| `assets` | Decode, mesh, materials, cache | `src/SLNG.Assets` |
| `render` | Godot scene, lighting, camera | `app/` |
| `ui` | HUD, chat, login screen | `app/` |
| `infra` | Build, CI, test grid | root / `tools/` |

### Columns

- **Owner** — set to `claude` or `gemini` when you start (claim it on your branch).
- **Agent** — the `.claude/agents/` role best suited to the task.
- **Tool** — suggested assistant for load-balancing parallel work (a hint, not a rule).
- **Dep** — task ids that must finish first.

---

## M0 — "It connects" (spike · ~1 week)

Goal: log in to a grid, join a region, and log incoming object/chat events.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M0-1 | Scaffold `SLNG.sln`, `src/*` projects, Godot `app/`, CI stub | infra | claude | architect | claude | — | `dotnet build` + `godot --path app` both succeed |
| M0-2 | Login flow via LibreMetaverse (grid/user/pass → connected) ✅ | net | claude | protocol-re | claude | M0-1 | Connects to OpenSim, returns agent/session id |
| M0-3 | Event logger: subscribe to ObjectUpdate + Chat, structured log ✅ | net | gemini | protocol-re | gemini | M0-2 | Object & chat events printed with key fields |
| M0-4 | Godot boot scene: login form + scrolling log panel ✅ | ui | gemini | ux-designer | gemini | M0-1 | Form submits creds; panel shows live events |
| M0-5 | Test harness + local OpenSim grid bring-up script ✅ | infra | gemini | test-engineer | gemini | M0-1 | `dotnet test` green; `tools/opensim-up` documented |

Parallel: M0-3/M0-4/M0-5 run concurrently after M0-1/M0-2.

## M1 — "World roughly visible" (~2–3 weeks)

Goal: terrain + placeholder prims rendered; free-fly camera.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M1-1 | World model / ECS: entities, components, diffable updates ✅ | core | gemini | architect | claude | M0-3 | Object updates produce a queryable world model + tests |
| M1-2 | Terrain: region heightmap → Godot mesh + collision ✅ | render | gemini | graphics-engineer | gemini | M0-1 | Region terrain visible and walkable |
| M1-3 | Prims as placeholder primitives (box/sphere/cylinder) ✅ | render | gemini | graphics-engineer | gemini | M1-1 | Objects appear at correct transforms |
| M1-4 | Free-fly camera + entity→Node3D scene sync ✅ | render | gemini | graphics-engineer | gemini | M1-1 | Camera flies; spawned/removed objects sync live |
| M1-5 | Interest management + region bounds / neighbor handoff ✅ | core | gemini | architect | claude | M1-1 | Only in-range objects instantiated; no leaks crossing regions |

Parallel: M1-2 (terrain) is independent of the core work and can start immediately.

## M2 — "It looks real" (~3–5 weeks)

Goal: real meshes + textures with modern PBR lighting. This is the graphics payoff.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M2-1 | Mesh fetch + LLMesh parse + LOD selection ✅ | assets | gemini | asset-pipeline | claude | M1-3 | Real object meshes render at correct LOD |
| M2-2 | JPEG2000 (.j2c) decode pool, fully off main thread ✅ | assets | gemini | asset-pipeline | gemini | M0-1 | Textures decode on workers; no main-thread stalls |
| M2-3 | glTF 2.0 PBR material resolve → Godot ORM material ✅ | render | gemini | graphics-engineer | claude | M2-2 | Metallic-roughness materials applied correctly |
| M2-4 | Texture streaming + RAM/disk/GPU cache + VRAM budget ✅ | assets | gemini | performance-engineer | gemini | M2-2 | Stable VRAM under a busy sim; mip prioritization works |
| M2-5 | Lighting & post-fx: sky, CSM shadows, GTAO, ACES tonemap ✅ | render | gemini | graphics-engineer | gemini | M1-2 | Scene has modern lighting; toggleable post-fx |

Parallel: assets track (M2-1/2/4) and render track (M2-5) progress side by side.

## M3 — "I'm in-world" (~1–2 weeks)

Goal: control your own avatar, move, and chat. Completes the **first shot**.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M3-1 | Self-avatar movement + AgentUpdate send | net | — | protocol-re | claude | M1-5 | Movement updates accepted by the sim |
| M3-2 | Local chat send/receive UI | ui | — | ux-designer | gemini | M0-4 | Two-way local chat works |
| M3-3 | Camera follow + avatar controller | render | — | graphics-engineer | claude | M3-1 | Third/first-person camera follows avatar |
| M3-4 | Own-avatar render (system avatar or placeholder mesh) ✅ | render | gemini | graphics-engineer | gemini | M1-4 | Your avatar is visible and moves |

**End of first shot.** A demoable client: connect → see a real, modern-lit world →
fly/walk around → chat. Other users' avatars are intentionally deferred to M4.

## M4+ — Beyond the first shot

The hard, long-tail work. Not part of the first shot; sequence later.

- **Other avatars (the endgame):** Bento skeleton, Bakes-on-Mesh, attachments,
  animations. Budget months, not weeks.
- **Viewer features:** inventory, world map, IM, friends, groups, teleport.
- **Performance hardening:** profiling on overloaded real sims; impostors; draw-call
  reduction; aggressive culling.
- **TPV compliance pass + registration** before any public SL build.
- **Cross-platform** (Linux/macOS) then **mobile**.

## Milestone summary

| Milestone | Outcome | Rough effort (solo, full-time) |
|---|---|---|
| M0 | Connects, logs events | ~1 week |
| M1 | World roughly visible | +2–3 weeks |
| M2 | Real meshes/textures, PBR | +3–5 weeks |
| M3 | Controllable avatar, chat | +1–2 weeks |
| **First shot total** | Demoable modern viewer | **~6–10 weeks** |
| M4+ | Other avatars, features, polish | months |
