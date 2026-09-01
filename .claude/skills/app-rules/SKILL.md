---
name: app-rules
description: Rules for app/ (the Godot 4 .NET client) — the mandatory SLNGWindow base class for floating UI, the AppVersion bump rule, the separate build step app/ needs, and the Godot/SL rendering traps this project has hit. Loads automatically when working under app/.
paths: app/**
---

# Rules for `app/`

`app/` is the only place Godot exists. `AGENTS.md` stays authoritative for the
project-wide contract; this file is the app-specific detail.

## Build `app/` explicitly — the solution does not

`app/SLNG.App.csproj` is deliberately **outside `SLNG.sln`** (the solution stays
engine-agnostic). So:

```bash
dotnet build app/SLNG.App.csproj
```

`dotnet build SLNG.sln` compiles **none** of `app/`. A change to a renderer or a UI window
builds "clean" without ever being compiled, and the client then runs yesterday's assembly.
If behaviour does not change after an edit, verify the DLL actually contains it before
debugging anything else.

## Every floating window inherits `SLNGWindow`

All floating, draggable UI (Camera HUD, Inventory, Properties, …) **must** inherit from
`SLNG.App.UI.SLNGWindow` (`app/scripts/UI/SLNGWindow.cs`). Do not use native Godot `Window`
nodes or a bare `PanelContainer` for popups. This is what keeps the dark glassmorphism
style, the drag behaviour, the shared HUD scale and the title bars uniform.

## Bump `AppVersion` on every user-visible change

`app/scripts/Boot.cs` holds `public const string AppVersion`. It renders on the login
screen and in the window title.

- **Patch-bump for every fix** (`v0.18.0-alpha` → `v0.18.1-alpha`).
- **Minor-bump only at a genuinely testable milestone**, never mid-investigation.

## Threading

Decode on worker threads; marshal only the final GPU upload / node mutation to the main
thread. **`Callable.From(lambda).CallDeferred()` is not safe from a background thread** —
it crashes with `AccessViolationException`. Use `Node.CallDeferred(nameof(Method))`, or
buffer and drain on the main thread.

Render budgets over fidelity: don't rebuild a whole-region mesh per terrain patch — batch
and debounce. Reuse shared mesh and material resources instead of allocating per update.

## Godot / SL rendering traps (all confirmed the hard way)

- **`godot --headless` rewrites `app/project.godot`.** Every `--selftest` run silently drops
  `lights_and_shadows/directional_shadow/size=4096`. Check `git diff app/project.godot` and
  revert before staging.
- **SL is CCW-front, Godot is CW-front.** Get the winding wrong and every SL mesh rasterizes
  as a backface, flipping diffuse lighting scene-wide.
- **SL's texture V origin is bottom, Godot's is top.** Applies to shader-generated coords
  too, and is invisible to every position-based measurement.
- **SL scale does not inherit.** A joint's SL world matrix uses only its own scale, never
  compounded with ancestors. Godot's `Skeleton3D` compounds by default.
- **`AlbedoColor.A` needs `Transparency` set explicitly**; `DetectAlpha()` is unreliable, and
  a GPU-cache hit must still set `Transparency`.
- **`render_mode unshaded` drops `EMISSION`** — it outputs `ALBEDO` only. A fullbright
  albedo→emission routing renders black there (this blanked whole worn HUDs).
- **`SubViewport.World3D` is not the world in use.** Physics queries must go through
  `FindWorld3D()`.
- **`MouseMode.Captured` delivers zero motion over RDP/VM.** Poll `GetMousePosition()` with
  `Hidden` mode instead; never `WarpMouse` every frame.
- **Avatar alpha and world-object alpha are separate paths.** A hard `AlphaScissor(0.5)`
  blotches soft SL alpha-layer gradients — use `AlphaHash` for the avatar, keep
  `AlphaScissor` for per-prim world objects.

## Shaders

`python tools/check_shader_globals.py` — every global uniform must be declared in **both**
the shader and `project.godot`. Only `--selftest` actually compiles the shaders;
`godot --headless --editor --quit` reports success on a shader that cannot compile.

## Before committing

Run `/slng-verify`.
