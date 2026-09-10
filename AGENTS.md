# AGENTS.md — single source of truth

This file is the canonical context for **every** AI assistant and human working on
SLNG. Both `CLAUDE.md` and `GEMINI.md` import it. If a rule matters, it lives here,
not in a tool-specific file.

## What we are building

A modern viewer for **Second Life** and **OpenSimulator**. We keep full grid
compatibility by reusing **LibreMetaverse** for the protocol, and we replace the
legacy renderer with **Godot 4 (.NET / Vulkan)** for PBR lighting, shadows and
post-processing.

Read alongside this file:
- `docs/ARCHITECTURE.md` — the five-layer architecture and module map
- `docs/ROADMAP.md` — milestones broken into AI-sized tasks
- `docs/AI_WORKFLOW.md` — how Claude Code and Gemini work the repo in parallel
- `docs/adr/` — architecture decision records

## Tech stack (do not change without an ADR — versions/build config are in the .csproj files and project.godot)

- **Protocol:** LibreMetaverse (login via LLSD, legacy UDP message system, HTTP CAPS).
- **Test framework:** xUnit. Integration tests run against a local OpenSim grid.
- **Targets:** Windows first; Linux/macOS kept buildable. Mobile is a later goal.

## Build & run

```bash
dotnet build SLNG.sln              # build engine-agnostic libraries + tests
dotnet build app/SLNG.App.csproj   # build the Godot client -- NOT part of SLNG.sln
dotnet test                        # run unit tests
dotnet format SLNG.sln             # must be clean before a commit
godot --path app                   # launch the client (or open app/ in the Godot editor)
godot --headless --path app -- --selftest   # smoke test without a window
python tools/check_shader_globals.py        # global uniforms declared in shader AND project.godot
```

`app/SLNG.App.csproj` is deliberately outside `SLNG.sln` (the solution stays engine-agnostic),
which means **`dotnet build SLNG.sln` compiles none of `app/`** — a change to a renderer or a UI
window builds "clean" without ever being compiled. Build both, or the client will run yesterday's
assembly.

The full pre-commit sequence, including the traps that make a "clean" build a lie, is the
`slng-verify` skill (`.claude/skills/slng-verify/SKILL.md`) — readable as plain Markdown by
any tool, invocable as `/slng-verify` in Claude Code.

Verified toolchain: **.NET SDK 8** and **Godot 4.7-stable (.NET/mono)**.

> Dev-machine note: the .NET 8 SDK is installed per-user at `%USERPROFILE%\.dotnet`.
> A runtime-only `dotnet` in `C:\Program Files\dotnet` wins PATH precedence, so if a
> bare `dotnet` reports "no SDK", source `. tools/dev-env.ps1` first (or call
> `%USERPROFILE%\.dotnet\dotnet.exe` directly, or install system-wide via
> `winget install Microsoft.DotNet.SDK.8`). Godot is on PATH as `godot`.

## Coding conventions

- **C#:** `dotnet format` clean. `async`/`await` for all I/O; never block the
  Godot main thread.
- **Naming:** `PascalCase` types/methods, `_camelCase` private fields, one public
  type per file named after the file.
- **No magic UUIDs / endpoints** in code — constants in config.
- **Threading:** asset decode (J2K, mesh) happens on worker threads; only the
  final GPU upload / scene-graph mutation touches the Godot main thread. This is
  the single most important performance rule — see Non-negotiables.
- **Commits:** Conventional Commits (`feat:`, `fix:`, `docs:`, `refactor:`,
  `test:`, `chore:`). Reference the roadmap task id, e.g. `feat(net): login flow (M0-2)`.
- **Tests:** every non-trivial PR ships tests. Protocol/asset code is test-first
  where feasible because AI output must be verifiable.

## Non-negotiables

1. **TPV Policy compliance.** Any code path that touches the live Second Life grid
   must respect the Linden Lab Third-Party Viewer Policy: honor object permissions,
   never circumvent asset protection, never enable content theft. When in doubt,
   stop and flag it. OpenSim is the default test target precisely because it avoids
   this risk during early development.
2. **Never decode or upload assets on the main thread.** It freezes the renderer.
3. **Render budgets over fidelity.** Real sims are unbounded user content. Prefer
   graceful degradation (impostors, LOD, caps) to stutter.
4. **`src/` stays engine-agnostic.** No `using Godot;` outside `app/`.

## Layering & boundaries (enforced — from code review)

The project dependency direction is fixed. Do **not** invert it:

```
app          ->  SLNG.Core, SLNG.Net, SLNG.Assets
SLNG.Assets  ->  SLNG.Core   (+ SLNG.Net for fetching)
SLNG.Net     ->  SLNG.Core
SLNG.Core    ->  nothing in this repo          <- pure domain
```

- **`SLNG.Core` must not reference `SLNG.Net` or `SLNG.Assets`.** It is the engine- *and*
  protocol-agnostic world model; a `Core -> Net` reference drags LibreMetaverse into the
  domain. A bridge that needs both world state and the network (e.g. feeding
  `GridSession` events into `World`) belongs in `app`, or `Core` depends only on an
  **interface it defines** that `Net` implements (dependency inversion).
- **No LibreMetaverse type crosses a public boundary of `SLNG.Net` / `SLNG.Assets`.**
  Convert at the boundary to `System.Numerics` / engine-neutral DTOs. The renderer must
  never see `FacetedMesh`, `AssetMesh`, `Primitive`, `UUID`, etc. — `SLNG.Assets` emits a
  neutral mesh/material description.
- **Asset codecs (CoreJ2K / JPEG2000, mesh decode) live in `SLNG.Assets`, not `SLNG.Net`.**

## Threading model (enforced — from code review)

- LibreMetaverse raises events on **background network threads**. **Never mutate the
  `World` directly from those callbacks** — the `Dictionary`-based world state is not
  thread-safe and the Godot main thread reads it concurrently. Buffer incoming events and
  apply them to the world on **one** thread (drain once per frame); read world state on
  the main thread only.
- Decode (J2K, mesh) on worker threads; marshal only the final GPU upload / node mutation
  to the Godot main thread via `CallDeferred`. (The asset path already does this — keep
  it that way.)
- Render budgets: don't rebuild a whole-region mesh per terrain patch — batch/debounce.
  Reuse shared mesh & material resources instead of allocating per update.

## Definition of done

A task is done when: it builds (`dotnet build`), tests pass (`dotnet test`), the
acceptance criteria in its roadmap entry are met, `dotnet format` is clean, and the
change is committed on its own branch with a Conventional-Commit message.

Mechanical checklist: `.claude/skills/slng-verify/SKILL.md` (`/slng-verify`).

## Parallel-agent rules (short form — full version in docs/AI_WORKFLOW.md)

- Each work item runs on its **own branch + git worktree**. Never two agents in one
  working tree.
- **One owner per task.** Claim a task by setting its `Owner` in `docs/ROADMAP.md`
  to `claude` or `gemini` in the same branch you start work on.
- Workstreams are designed to be **independent** (net vs. render vs. assets vs. ui)
  so two agents rarely touch the same files. If you must edit a shared file
  (`AGENTS.md`, `SLNG.sln`), do it in a tiny dedicated commit and rebase often.
- Integrate through `main` via small PRs, not long-lived branches.

## Area-specific rules

The rules below are scoped to one directory and live next to it, so they load only when
that area is being worked on. All three are plain Markdown — Gemini CLI can load them with
`@<path>`; Claude Code picks them up automatically via each skill's `paths:` field.

| Area | File | Covers |
|---|---|---|
| `src/**` | `.claude/skills/src-rules/SKILL.md` | layering detail, the LibreMetaverse boundary, network-thread safety, LMV decoding traps |
| `app/**` | `.claude/skills/app-rules/SKILL.md` | **`SLNGWindow` for all floating UI**, **the `AppVersion` bump rule**, the separate `app/` build, Godot/SL rendering traps |
| `tests/**` | `.claude/skills/tests-rules/SKILL.md` | what a test must assert, fixtures, the local-OpenSim-only policy |

Two of these are hard requirements and are called out here so they are not missed:

- **Every floating, draggable UI window MUST inherit `SLNG.App.UI.SLNGWindow`.** No native
  Godot `Window` nodes, no bare `PanelContainer` popups.
- **Every feature, fix or significant UI change MUST bump `AppVersion`** in
  `app/scripts/Boot.cs` — it is shown on the login screen and in the title bar.

## Feature Tracking & ID Convention

All features, tasks, and specs MUST use a unified Feature ID scheme across code, git, and documentation:
- **ID Format:** `M<Milestone>-<Number>` (e.g. `M4-7`) or `FEAT-<AREA>-<Number>` for post-milestone tasks.
- **Spec files:** Created under `docs/specs/<ID>-<short-description>.md` using `docs/specs/TEMPLATE.md`.
- **Branch names:** `feature/<ID>-<short-description>`.
- **Commit messages:** `feat(<area>): [<ID>] <description>` (e.g. `feat(render): [M4-7] implement base mesh hiding`).
- **Status tracking in `docs/ROADMAP.md`:** Standardized status flags (`⏸️ Pending`, `🚧 In Progress`, `🧪 Review`, `✅ Done`).
- **Progress dashboard:** those same flags render the public status page at
  <https://clifhofmann.github.io/SLNG/>, auto-built from `docs/ROADMAP.md` + git by
  `tools/roadmap-dashboard.py` in `.github/workflows/dashboard.yml` on every push to
  `main` that touches the roadmap, `docs/specs/**` or the script. `docs/dashboard.html`
  is a generated artifact, never hand-edited, git-ignored. Update procedure (edit flags,
  commit, push — identical for Claude Code and Gemini): **`docs/PROGRESS_UPDATE.md`**.
