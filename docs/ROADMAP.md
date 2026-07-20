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
| M2-6 | Terrain textures, water plane, and dynamic sky ✅ | render | gemini | graphics-engineer | gemini | M2-2 | Terrain uses SL textures; water renders at WaterHeight; nice sky |

Parallel: assets track (M2-1/2/4) and render track (M2-5/M2-6) progress side by side.

## M3 — "I'm in-world" (~1–2 weeks)

Goal: control your own avatar, move, and chat. Completes the **first shot**.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M3-1 | Self-avatar movement + AgentUpdate send ✅ | net | gemini | protocol-re | gemini | M1-5 | Movement updates accepted by the sim |
| M3-2 | Local chat send/receive UI ✅ | ui | gemini | ux-designer | gemini | M0-4 | Two-way local chat works |
| M3-3 | Camera follow + avatar controller ✅ | render | gemini | graphics-engineer | gemini | M3-1 | Third/first-person camera follows avatar |
| M3-4 | Own-avatar render (system avatar or placeholder mesh) ✅ | render | gemini | graphics-engineer | gemini | M1-4 | Your avatar is visible and moves |

**End of first shot.** A demoable client: connect → see a real, modern-lit world →
fly/walk around → chat. Other users' avatars are intentionally deferred to M4.

## M4 — "The Avatar" (~4–8 weeks)

Goal: Replace the placeholder capsule with a real Second Life avatar (Bento skeleton) and sync animations.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M4-1 | Bento Skeleton & Base Mesh ✅ | render | gemini | graphics-engineer | | M3-4 | A static system avatar mesh with the SL skeleton is rendered instead of a capsule |
| M4-2 | Animation decode & playback ✅ | assets | gemini | asset-pipeline | | M4-1 | Server-sent animations (`.anim` / `.bvh`) play correctly on the skeleton |
| M4-3 | Appearance & Bakes-on-Mesh (BoM) ✅ | net/render | gemini | graphics-engineer | | M4-1 | Avatar shape params and baked skin textures are downloaded and applied |
| M4-4 | Mesh Attachments ✅ | assets/render | claude | graphics-engineer | | M4-1 | Equipped objects (hair, clothes) are attached to the correct skeleton bones |
| M4-5 | Vertex morphs (LLPolyMorphTarget) — engine-neutral pipeline ✅ | assets | claude | asset-pipeline | | M4-3 | `SLNG.Assets` exposes per-param effective weights + morphed body-part vertices (`pos += w·delta`, normals softened 0.65 + renormalized), verified against `llpolymorph.cpp` with unit tests |
| M4-6 | Morphed-body rendering — rebuild on shape change ✅ | render | claude | graphics-engineer | | M4-5 | System body renders with the avatar's real proportions (male/muscle/breast sliders) matching Firestorm; morph rebuild off the main thread |
| M4-7 | Base-mesh hiding under worn mesh (alpha/BoM correctness) | render | | graphics-engineer | | M4-3 | System head/body parts hidden exactly per worn alpha layers & BoM rules instead of the temporary hard-hide experiment |
| M4-8 | SL-faithful joint composition — scale does NOT inherit ✅ | render/core | claude | graphics-engineer | | M4-3 | Godot bone global poses match `LLXformMatrix::update` exactly (basis = own scale only; parent scale offsets children one level; rotation inherits): parity checker reports <1 cm/<1 % deviation on every bone for real avatars, coat-sleeve meshes land on their joints, proportions match Firestorm |
| M4-9 | HUD attachments — screen-space ortho overlay ✅ | render/ui | claude | graphics-engineer | | M4-4 | Local avatar's HUD objects (points 31–38) render textured in their correct screen quadrants (SL ortho volume: 1 unit tall, anchors at ±0.5, aspect-scaled horizontals), camera-locked, independent of world lighting; other avatars' HUDs never shown |

## M5 — Viewer features

Pulled forward opportunistically from the M5+ list below when a natural pairing with
other in-flight work made it cheap to start.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M5-1 | Inventory browser v1 — read-only, lazy folder fetch ✅ | net/ui | claude | ux-designer | | M0-2 | Ctrl+I opens a tree showing "My Inventory" + "Library"; each folder's children are fetched on first expand (no recursive/whole-tree fetch); folders and items both render with correct names; inventory links are marked as links. Wearing/moving/deleting items is out of scope for v1. |

## M5+ — Beyond the first shot

The hard, long-tail work. Not part of the first shot; sequence later.

- **Viewer features:** world map, IM, friends, groups, teleport; inventory v2 (wear/attach/move/delete, drag-drop).
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
