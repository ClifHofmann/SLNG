# Architecture

SLNG is split into five layers. The guiding principle: **the core knows nothing
about rendering.** The networking and world-state layers maintain a pure data model;
the engine is an observer that mirrors that model into the Godot scene graph. This
decoupling is what lets multiple agents work in parallel and what lets us swap or
upgrade the renderer later.

```
┌──────────────────────────────────────────────────────────┐
│  UI layer            inworld HUD · inventory · map · prefs │  app/ (Godot)
├──────────────────────────────────────────────────────────┤
│  Graphics engine     PBR forward+ · shadows/GI · post-fx   │  app/ (Godot)
├──────────────────────────────────────────────────────────┤
│  Asset pipeline      J2K decode · mesh/LOD · glTF · bake   │  src/SLNG.Assets
├──────────────────────────────────────────────────────────┤
│  World state (ECS)   scene graph · object cache · physics  │  src/SLNG.Core
├──────────────────────────────────────────────────────────┤
│  Network core        login · UDP legacy · HTTP CAPS        │  src/SLNG.Net
└──────────────────────────────────────────────────────────┘
            ▲ data streams up from the grid · actions flow down
         Second Life grid  /  OpenSim grid   (external servers)
```

## Layers

### 1. Network core — `src/SLNG.Net`
Wraps LibreMetaverse. Responsibilities: login (LLSD/HTTP), region connect and
hand-off, the legacy UDP message system (object updates, movement, chat), and HTTP
CAPS (mesh/texture fetch, inventory, `EventQueueGet` server→client push). Handles
multiple simultaneous regions (neighboring sims). Emits plain C# events/DTOs — no
Godot types cross this boundary.

### 2. World state (ECS) — `src/SLNG.Core`
The single source of truth. Every prim, object and avatar is an entity with
components (transform, geometry ref, material ref, script flags, interest level).
Owns object lifecycle and interest management (what is relevant / visible).
Translates raw network DTOs into a stable world model the renderer can diff against.

### 3. Asset pipeline — `src/SLNG.Assets`
Asynchronous streaming system. JPEG2000 decode pool, mesh download + LOD selection,
glTF 2.0 PBR material resolution, avatar bake (Bakes-on-Mesh). Multi-tier cache:
RAM → disk → GPU. **All decoding happens off the main thread**; only the final
hand-off of ready data is marshaled to the engine.

### 4. Graphics engine — `app/` (Godot)
Consumes the world model and ready assets. Uses Godot's Vulkan renderer: PBR
(metallic-roughness), clustered lighting, cascaded shadow maps, SSGI / light probes,
HDR post-processing (TAA, GTAO, bloom, ACES tonemapping). GPU-driven culling and LOD.
Render budgets with graceful degradation (avatar impostors) instead of stutter.

### 5. UI layer — `app/` (Godot)
Inworld HUD, inventory, chat, world map, settings. Decoupled from the render loop.

## Data flow

- **Up (grid → screen):** UDP/CAPS → `SLNG.Net` DTOs → `SLNG.Core` world model →
  `SLNG.Assets` resolves geometry/textures → `app/` mirrors entities into the scene.
- **Down (user → grid):** UI/input → `SLNG.Core` intent → `SLNG.Net` agent updates
  (movement, chat, interactions) → grid.

## Why this split

- **Parallel development.** `SLNG.Net`, `SLNG.Assets` and `app/` can be built by
  different agents at the same time against stable interfaces.
- **Testability.** Core and net are pure .NET → unit/integration testable headless
  against OpenSim, no engine needed.
- **Renderer independence.** Godot is an implementation detail behind the world
  model; a future renderer swap does not touch the protocol.

## Key decisions

- Reuse LibreMetaverse instead of rewriting the protocol — see
  [adr/0001-engine-and-stack.md](adr/0001-engine-and-stack.md).
- All world surfaces (prims, avatars, terrain, water) share one custom Godot spatial
  shader family rather than `StandardMaterial3D` — see
  [adr/0002-custom-spatial-shader-family.md](adr/0002-custom-spatial-shader-family.md).
- Engine-agnostic `src/`; only `app/` references Godot. Enforced in review.
- OpenSim is the primary early test target; SL grid is added once TPV-sensitive
  paths are reviewed.
