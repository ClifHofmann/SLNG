# Handoff → Claude (2026-06-29)

The repository is now on the `main` branch. I have merged my recent work (`feat/login-profiles`) and the user's manual updates into `main`.

## Done this session (by Gemini)
- **Login Profiles (UI)**: Added a dropdown and a "Save" checkbox to the boot screen (`Boot.tscn`, `Boot.cs`). It securely saves and restores login configurations using Godot's `ConfigFile` (`user://logins.cfg`).
- **Fixed VRAM Exhaustion / Invisible Objects**: 
  - **The Bug:** The Godot `GpuCache` was silently exhausting all VRAM on busy grids (like OSgrid). All objects in the region (10,000+) were eagerly triggering network requests and decoding meshes/textures *immediately* on spawn, regardless of distance. The VRAM release radius (`releaseSq`) was 192m (covering the entire region), so nothing was ever evicted. Once VRAM was full, Godot's Vulkan backend failed to allocate any new buffers, causing all new objects to be permanently invisible ("Nun sehe ich nix mehr").
  - **The Fix:** In `ObjectRenderer.cs`, newly spawned `VisualState`s are now correctly initialized with `ResourcesReleased = true` and `Visible = false`. Heavy GPU assets (meshes, textures) are now *deferred* and only loaded into VRAM when the object actually moves into `showSq` (96m). 
  - **Aggressive Culling:** Tightened `releaseSq` from `2.0 * draw` to `1.25 * draw` (120m). If an object moves out of this range, its mesh and textures are now aggressively released, dropping the ref count and allowing `GpuCache` to cleanly evict them.

## Current State & Health
- `main` branch.
- **Build / Tests**: `dotnet build` succeeds 0/0. `dotnet test` 25/25.
- The avatar (Claude's M4-5 work) and animation fixes are present in `main`.
- `Magick.NET failed to decode texture` logs still sporadically appear on OSgrid due to LibreMetaverse's UDP timeout passing truncated J2C streams; the code safely drops them and retries, but it clutters the Godot output.

## Open / Next
- Continue with **M4-3** (Bakes-on-Mesh) or whatever task is unblocked in the roadmap.
- Keep an eye on avatar positioning (feet vs center) which might still be floating.
