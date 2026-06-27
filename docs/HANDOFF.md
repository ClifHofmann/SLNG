# SLNG — Handoff / continuation guide

Snapshot for picking the project up in another tool (e.g. Antigravity). Read this with
`AGENTS.md` (authoritative rules), `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`,
`docs/AI_WORKFLOW.md`.

## What SLNG is

A next-gen **Second Life / OpenSim** viewer: **LibreMetaverse** for the protocol,
**Godot 4 (.NET / Vulkan)** for rendering. Engine-agnostic logic in `src/`, Godot only
in `app/`.

## Current state (works today)

Logs in to an OpenSim grid and renders a textured, lit world you can walk and chat in:

- **M0** login, event logging, Godot boot UI, local OpenSim docker grid (`tools/opensim-up`).
- **M1** ECS world model, terrain mesh + collision, prims, scene sync, free camera.
- **M2** JPEG2000 textures, glTF **PBR** materials, lighting, sky, water, terrain texturing.
- **M3** avatar movement (local prediction, SL-style controls), local chat.
- **M4-1** Bento skeleton parsed; avatar rendered as a **static box-man placeholder**.

Health: `dotnet build` 0/0, **`dotnet test` 23/23** (Core 17, Net 6).

## Build / run / verify (Windows dev box)

The .NET 8 SDK is per-user at `%USERPROFILE%\.dotnet` (a runtime-only `dotnet` in
Program Files shadows it). **Source the env first**, then build:

```powershell
. .\tools\dev-env.ps1            # makes `dotnet` resolve to the per-user SDK 8.0.x
dotnet build SLNG.sln            # engine-agnostic libs + tests
dotnet test SLNG.sln             # 23 tests
dotnet build app/SLNG.App.csproj # the Godot C# assembly
godot --path app                 # run the client (Godot 4.7 .NET, on PATH)
godot --headless --path app --quit-after 30   # smoke test: loads scene, no render
```

**Verification limit:** headless **cannot render** the 3D scene, and the world needs a
login. So build + test + headless-load is all that can be auto-verified; **visual
changes (avatar, materials, lighting) must be checked by a human running the client and
logging into the grid.** Plan visual work as: implement → build/test → ask the operator
for a screenshot.

## Architecture invariants (enforced — see AGENTS.md)

- **Layering:** `SLNG.Core` references **nothing** (pure domain). `Net`→`Core`,
  `Assets`→`Core`(+`Net`), `app`→all. Never invert.
- **No LibreMetaverse type crosses the `Net`/`Assets` public boundary.** Emit neutral
  DTOs: `GridEvents` (System.Numerics), `MeshData`, `TextureData`, `PbrMaterialData`.
- **Threading:** LibreMetaverse raises events on background threads. `WorldSimulation`
  only **enqueues** them; `Pump()` (called once per frame on the Godot main thread from
  `Boot._Process`) applies them. **Never mutate `World` off the main thread.**
- **Asset decode off the main thread**, GPU upload via `CallDeferred`.
- LibreMetaverse 3.0.0 root namespace is **`LibreMetaverse`** (not `OpenMetaverse`).

## Open work

- **PR #12** (`fix/avatar-skeleton-load`) — fixes the avatar (capsule→box-man): load
  skeleton via Godot `FileAccess`, place parts at bone rest positions. **Merge this**;
  the box-man state above assumes it's merged.

## Known issues / tech debt

- **#4 layering:** J2C decode now uses **Magick.NET** in `SLNG.Assets`, but the old
  **CoreJ2K** packages + a SKBitmap image-creator registration are still in `SLNG.Net`
  (`GridSession`) and are now **vestigial dead code**. Remove them — `SLNG.Net` should
  not reference an image codec at all.
- **Avatar placeholder:** the box-man (`app/scripts/ProceduralAvatarMesh.cs`) ignores
  bone rest *rotations* (positions only), so limbs are slightly off. It's a throwaway —
  replaced by M4-3. `SkeletonBuilder.cs` (Godot `Skeleton3D`) is currently unused but
  kept for animation (M4-2).
- **Networked avatars:** the local agent's position is placed feet-at-origin; SL reports
  avatar positions center-based, so *other* avatars may float ~1m. Reconcile when wiring
  remote avatars.
- **Warning:** `app/scripts/TerrainRenderer.cs:80` — CS8604 nullable on incremental
  builds (CallDeferred args). Cosmetic.

## Next steps (priority)

1. **M4-3 Appearance / Bakes-on-Mesh** — real avatar look (skin textures + shape params).
   The biggest visible win; replaces the box-man.
2. **M4-2 Animation** — decode/play server `.anim`/BVH on the skeleton (use
   `SkeletonBuilder`).
3. **M4-4 Attachments** — equipped meshes on skeleton bones.
4. **#4 cleanup** — CoreJ2K → `SLNG.Assets`.

## Key files

| Area | Files |
|---|---|
| Net (protocol) | `src/SLNG.Net/GridSession.cs`, `ProtocolLogger.cs`, `LoginCredentials/Result.cs` |
| Core (domain) | `src/SLNG.Core/ECS/World.cs`, `WorldSimulation.cs`, `IWorldEventSource.cs`, `GridEvents.cs`, `AvatarSkeleton.cs`, `Components/*` |
| Assets | `src/SLNG.Assets/AssetService.cs`, `MeshData.cs`, `TextureData.cs`, `PbrMaterialData.cs` |
| Render/UI | `app/scripts/Boot.cs`, `TerrainRenderer.cs`, `ObjectRenderer.cs`, `AvatarRenderer.cs`, `AvatarController.cs`, `ProceduralAvatarMesh.cs`, `SkeletonBuilder.cs`, `FreeCamera.cs` |

## Workflow (parallel agents)

Branch per task, PR into `main`, own git worktree per agent (`docs/AI_WORKFLOW.md`).
Claim a task by setting its `Owner` in `docs/ROADMAP.md`.

## Useful technique

To discover the LibreMetaverse 3.0.0 API (its namespaces/types changed and docs are
thin), a throwaway console that `PackageReference`s `LibreMetaverse` and reflects over
the loaded assembly was repeatedly faster than guessing — see how `AssetMesh`,
`AssetTexture`, `J2kImage`, `TextureEntry` were found during M2.
