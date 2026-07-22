# Roadmap

This roadmap is structured into **MVP Milestones** focused on progressive end-user capabilities (playable increments), supported by **AI-sized tasks** with clear specs and acceptance criteria.

## How we work

Each task follows a **spec → test → implement → review** loop:

1. The owning agent reads the task and writes/extends a short spec if needed.
2. For protocol and asset work, write the test first (output must be verifiable).
3. Implement against the stable interface of the layer.
4. `code-reviewer` checks it against `AGENTS.md`, then it merges to `main`.

### Workstreams (parallel tracks)

| Track | Area | Lives in |
|---|---|---|
| `net` | Protocol, login, regions | `src/SLNG.Net` |
| `core` | World model / ECS | `src/SLNG.Core` |
| `assets` | Decode, mesh, materials, cache | `src/SLNG.Assets` |
| `render` | Godot scene, lighting, camera | `app/` |
| `ui` | HUD, chat, login screen | `app/` |
| `infra` | Build, CI, packaging, installer | root / `tools/` |

---

## MVP 1 — Foundation & Playable Client ✅

**Goal:** Connect to a grid, render terrain/prims/PBR/lighting, walk around, chat, and render Bento avatar.

### Phase 1A: Connection & World Engine (M0 – M2)
- [x] **M0-1** Scaffold solution & Godot app ✅
- [x] **M0-2** Login flow via LibreMetaverse ✅
- [x] **M0-3** Event logger ✅
- [x] **M0-4** Godot boot scene ✅
- [x] **M0-5** Test harness & OpenSim local grid ✅
- [x] **M1-1** World model / ECS ✅
- [x] **M1-2** Terrain rendering & collision ✅
- [x] **M1-3** Primitive shapes ✅
- [x] **M1-4** Free-fly camera & scene sync ✅
- [x] **M1-5** Interest management ✅
- [x] **M2-1** Mesh fetch & LOD selection ✅
- [x] **M2-2** JPEG2000 decode pool ✅
- [x] **M2-3** PBR material resolve ✅
- [x] **M2-4** Texture streaming & VRAM budget ✅
- [x] **M2-5** Lighting, CSM shadows & post-fx ✅
- [x] **M2-6** Terrain textures & water plane ✅

### Phase 1B: Controls & Bento Avatar (M3 – M4)
- [x] **M3-1** Avatar movement & network sync ✅
- [x] **M3-2** Local chat UI ✅
- [x] **M3-3** Third/first-person camera controller ✅
- [x] **M3-4** Own avatar render ✅
- [x] **M4-1** Bento skeleton & base mesh ✅
- [x] **M4-2** Animation decode & playback ✅
- [x] **M4-3** Appearance & Bakes-on-Mesh (BoM) ✅
- [x] **M4-4** Mesh attachments ✅
- [x] **M4-5** Vertex morphs engine pipeline ✅
- [x] **M4-6** Morphed body rendering ✅
- [x] **M4-8** SL-faithful joint composition ✅
- [x] **M4-9** HUD attachments overlay ✅

---

## MVP 1.5 — Build Distribution & Client Config 🚧

**Goal:** Automated CI/CD pipeline, standalone Windows Installer for test user distribution, and app configuration persistence.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M1.5-1 | Godot Headless Export Script | infra | | DevOps | claude | M0-1 | PowerShell script exports Windows binary (.exe + .pck + .NET assemblies) headlessly |
| M1.5-2 | InnoSetup / Installer Spec & Script | infra | | DevOps | claude | M1.5-1 | InnoSetup script bundles client build into single-click installer executable |
| M1.5-3 | GitHub Actions Continuous Delivery | infra | | DevOps | claude | M1.5-2 | GitHub Workflow builds installer on `main` push & attaches artifact/release for testers |
| FEAT-UI-03 | Boot Window Size & Profile Persistence ✅ | ui/infra | gemini | ux-designer | gemini | M0-4 | Client window resolution, grid profiles & user preferences persist across sessions in `user://logins.cfg`. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-UI-03-boot-window-persistence.md) |
| FEAT-UI-07 | UI Scale Setting ✅ | ui | claude | ux-designer | claude | M5-3 | Preferences slider (80%–160%) scales every `SLNGWindow` live, persisted in `preferences.cfg`. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-UI-07-ui-scaling.md) |

---

## MVP 2 — Navigation & Social Core ⏸️

**Goal:** Complete multi-user social interaction, navigation, and script dialogs.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M5-3 | Tabbed Chat, Friends & Groups Window 🚧 | ui/net | claude | ux-designer | claude | M3-2 | Multi-tabbed window (Chat, Friends, Groups, Dynamic IMs) inheriting from `SLNGWindow`. [Spec](file:///E:/Git/SLNG/docs/specs/M5-3-tabbed-chat-window.md) |
| M5-4 | LSL / SLS Script Dialog System (`llDialog`) ⏸️ | ui/net | gemini | ux-designer | gemini | M0-3 | Modal UI popups inheriting from `SLNGWindow` with 3x4 button grid & channel reply. [Spec](file:///E:/Git/SLNG/docs/specs/M5-4-script-dialogs.md) |
| FEAT-UI-01 | Contextual Cursor Interaction States ✅ | ui/render | gemini | ux-designer | gemini | M1-3 | Viewport mouse cursor dynamically changes based on target object state (Touch, Sit, Inspect, Media). [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-UI-01-cursor-interaction-states.md) |
| MVP2-1 | Object Interaction & Sit / Touch | render/net | | protocol-re | gemini | M1-3 | Touch objects, sit on prims/anim-seats, stand up |
| MVP2-2 | World Map, Minimap & Teleport ✅ | ui/net | gemini | protocol-re | gemini | M0-2 | Minimap overlay, full grid map, search regions & teleport to landmark/coords. Create Landmark + Teleport UI & execution fallback fixed (direct packet + asset-decode fallback). [Spec](file:///E:/Git/SLNG/docs/specs/MVP2-2-landmark-teleport.md) |

---

## MVP 3 — Avatar, Posing & Media Studio ⏸️

**Goal:** Full avatar customization, animation/pose controls, shared media, and snapshot tools.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M4-7 | Base-mesh Hiding Under Worn Mesh | render | | graphics-engineer | | M4-3 | System head/body parts hidden per worn alpha layers & BoM rules |
| M5-1 | Inventory Browser v1 — Read-only ✅ | net/ui | claude | ux-designer | | M0-2 | Lazy tree view of folders & items |
| MVP3-1 | Inventory v2 — Wear, Detach & Drag-Drop | net/ui | | ux-designer | claude | M5-1 | Wear/detach clothing & attachments, move/delete items |
| MVP3-2 | Pose Stand & Animation Override (AO) | render/ui | | graphics-engineer | gemini | M4-2 | Play/stop custom poses, pose stands, basic AO system |
| MVP3-3 | Shared Media / MOAP (Media on a Prim) | render/net | | graphics-engineer | claude | M2-3 | Web browser / video streaming on prim faces |
| MVP3-4 | Snapshot & Photography Studio | ui/render | | graphics-engineer | gemini | M2-5 | High-res screenshot capture, DoF, FOV control, EEP/environment presets |

---

## MVP 4 — Building & Creator Suite ⏸️

**Goal:** Complete in-world building, editing, prim linking, and terrain manipulation tools.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| M5-2 | In-World Object Selection & Editing 🚧 | render/ui/net | claude | graphics-engineer | claude | M1-3 | Raycast selection, context menu, inspector tabbed window (General/Object/Features/Texture/Content) — implemented on `feature/M5-2-object-editing`, not yet merged; 3D transform gizmos still outstanding (tracked as FEAT-UI-04). [Spec](file:///E:/Git/SLNG/docs/specs/M5-2-object-editing.md) |
| FEAT-UI-04 | In-World 3D Transform Gizmos | render/ui | | graphics-engineer | claude | M5-2 | Interactive 3D translation/rotation/scale handles in viewport. Not started — no `SelectionGizmo3D` yet; transform edits currently spinbox-only in the inspector. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-UI-04-transform-gizmos.md) |
| FEAT-UI-05 | Multi-Select & Prim Linking/Unlinking | render/ui/net | | graphics-engineer | claude | M5-2 | Box-select/Shift-select multiple prims, link/unlink root & child objects. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-UI-05-multi-select-linking.md) |
| FEAT-UI-06 | Edit Linked Parts & Child Prims 🚧 | render/ui/net | | graphics-engineer | claude | M5-2 | "Edit linked" checkbox mode to transform individual child prims inside a linkset. Groundwork landed on `feature/M5-2-object-editing` (`SelectionSettings.EditLinkedParts`). [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-UI-06-edit-linked-parts.md) |
| MVP4-1 | Prim Rezzing & Mesh Import | render/net | | graphics-engineer | claude | M5-2 | Create new prim shapes in-world, upload/import custom meshes |
| MVP4-2 | Land & Terrain Sculpting | render/core | | graphics-engineer | gemini | M1-2 | In-world raise/lower/flatten terrain tools |

---

## MVP 5 — Economy, Voice & Marketplace ⏸️

**Goal:** Advanced ecosystem features: L$ economy, voice chat, and marketplace integration.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| MVP5-1 | Spatial Voice Chat (Vivox / WebRTC) | net/render | | protocol-re | claude | MVP2-1 | 3D positional voice, group voice, mic input controls |
| MVP5-2 | L$ Transactions & In-World Economy | net/ui | | protocol-re | claude | M0-2 | View balance, pay avatars/objects, buy land/items |
| MVP5-3 | In-Viewer Marketplace Browser | ui/net | | ux-designer | gemini | MVP5-2 | Integrated web marketplace, direct delivery & unpacking |

---

## MVP 6 — Multi-Platform, Polish & Compliance ⏸️

**Goal:** Global readiness, performance optimizations, TPV compliance, and multi-platform deployment.

| ID | Task | Track | Owner | Agent | Tool | Dep | Done when |
|---|---|---|---|---|---|---|---|
| FEAT-UI-02 | Client Localization & Multi-Language (i18n) | ui | | ux-designer | gemini | M0-4 | Full UI text translation pipeline & multi-language support. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-UI-02-localization-i18n.md) |
| MVP6-1 | Performance Hardening & Occlusion Culling | render/assets | | performance-engineer | gemini | M2-4 | Impostors, aggressive culling, draw-call reduction on busy sims |
| MVP6-2 | TPV Policy Compliance Pass & Registration | infra/net | | architect | claude | M0-2 | Full Linden Lab Third-Party Viewer Policy audit & official registration |
| MVP6-3 | Cross-Platform Build Pipeline (Linux & macOS) | infra | | DevOps | claude | M1.5-3 | Native Linux & macOS releases packaged in CI/CD pipeline |
| MVP6-4 | Mobile Target Optimization (Android / iOS) | render/ui | | graphics-engineer | gemini | MVP6-3 | Touch controls UI, mobile shading profile & lower memory footprint |
