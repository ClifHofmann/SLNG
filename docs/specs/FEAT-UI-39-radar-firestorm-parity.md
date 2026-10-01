# [FEAT-UI-39] Radar window — Firestorm parity (map image, teleport, notes, column table)

- **Feature ID:** `FEAT-UI-39`
- **Track:** `ui` / `net`
- **Status:** `🚧 In Progress`
- **Owner:** `claude`
- **Agent:** `ux-designer` (window) + `protocol-re` (profile/notes data) + `viewer-parity` (source checks)
- **Dep:** `MVP2-3` (the current `MinimapOverlay`), sibling of `BUG-UI-11` (neighbour avatars, in-region/on-parcel icon); the Voice column is fed later by `MVP5-1`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Bring `MinimapOverlay` close to Firestorm's *People → Nearby* floater: a map on top that shows the
**sim image** (not a black box), **double-click = teleport**, and below it a **column table** with
a per-user **note indicator**, whose columns the user can show/hide, sort and persist.

Asked for in-world 2026-10-01 with a Firestorm screenshot. Source claims below were read from the
vendored Linden viewer (`scratch/slviewer`) and `FirestormViewer/phoenix-firestorm` master
(76d0992), not from the screenshot.

## What Firestorm does (verified)
- **Map background** = per region a quad textured with `map-1-X-Y-objects.jpg` from the login
  response's `map-server-url` (`LLSurface::createSTexture` → `LLWorldMipmap::loadObjectsTile`,
  llsurface.cpp:230, llworldmipmap.cpp:182). Tint: own region white, neighbours 0.8 grey, dead
  region (1, 0.5, 0.5). **This is a server-rendered map tile, not the terrain composition** —
  `BUG-UI-11`'s earlier statement ("needs no map-tile server") was wrong and is corrected there.
  No region → only the background rect (black in Linden, 25 % black in Firestorm).
- **Object layer**: one shared RGBA texture (64–256 px), rebuilt every 0.5 s; prims drawn as filled
  squares. Only prims **you own** or with scale > 7.5 m are listed (llviewerobject.cpp:3999). These
  are the black blocks in the screenshot.
- **Double-click** (`FSNetMapDoubleClickAction`, default 2 = teleport; Linden's default is "open world
  map"). Target Z = the camera's Z — no ground lookup, no check that a region exists. Single click
  does nothing; Firestorm pans only on **Shift-drag**.
- **Notes**: column `has_notes` shows a small "n" icon iff the note string is non-empty; tooltip =
  the note text. Requested once per avatar when it first enters the list. SL/AgentProfile-cap grids:
  HTTP profile JSON (`notes`, plus flags and born-on); otherwise UDP `avatarnotesrequest`.
- **Columns** (display order from `panel_fs_radar.xml`): Name `[total/in region/in chat range]` ·
  voice · in-region · typing · sitting · `$`/`$$` · notes · Age · Seen · Range. In the German UI
  "Alt." is the **account age in days** (not altitude), "Zeit" = time in the list, "Dist." = range.
  Refreshed every 1 s. The "S" in the screenshot is *sitting*, not the note.

## Decisions
| Topic | Decision |
|---|---|
| Notes source | **Server-side, like Firestorm — decided by the maintainer 2026-10-01.** SLNG stores notes only locally today (`UserProfileWindow.cs`, `preferences.cfg` `[avatar_notes]`), so a note written in Firestorm is invisible here and vice-versa. Local notes are imported once. |
| Look | Free to modernise (maintainer 2026-10-01), within SLNG's dark-glass window style — see **Design direction**. |
| Double-click Z | Ground height at the click (from `RegionTerrain` heights) + 1 m — better than Firestorm's camera-Z, and we already have the heightmap. |
| Map image | HTTP map tile first (matches the viewer, also fixes the world map on Agni, which has never been checked); `MapImageId` asset path as the fallback for OpenSim. |
| Own terrain render | Not now. Only as a fallback for a region whose tile is missing. |

## Design direction
Keep Firestorm's *information*, not its look. Mock-up agreed in chat 2026-10-01; the shape of it:
- **Icons, not letters.** Sitting, in-region, note and typing are small icons (the "S" and "n" glyphs
  are a 2009 compromise); `$` / `$$` stay as text. Header icons carry a tooltip with the full name.
- **Relationship colour as a dot** in front of the name (friend / muted / other / Linden), the same
  colour as the dot on the map, so row and dot read as one thing.
- **Range in colour bands**: inside say range amber, inside shout range normal, beyond muted. Bold only
  while the avatar is inside draw distance, as Firestorm does.
- **Flat rows**: no vertical grid lines, a hairline under the header, hover and selected as a soft
  white wash, 26–28 px rows. A grab handle on the divider between map and table.
- **Map** with the region image, the three rings, the view wedge and the object squares; the selected
  avatar gets a white ring.
- Done through the `Tree`'s theme (`StyleBoxFlat` for selected / hover / header, icon textures) so the
  Godot features below stay. Cells are single-line; a two-line row (display name over username) would
  mean drawing the rows ourselves and losing the column menu, sorting and tooltips — not worth it.
- Same chrome as every other window (`SLNGWindow`: black 85 %, 10 % white border).

## Use what Godot already has
Verified present in the Godot 4.7 .NET API (`GodotSharp.xml`). **Not** verified: dragging column
borders to resize — no such API is documented, so Phase 4 starts with a one-hour spike.

| Need | Godot feature |
|---|---|
| The table | `Tree` (columns, titles, per-cell icon / text / colour / tooltip, row select) — **updated in place**, never torn down per frame. This removes the bug class behind the old "roster click does nothing" fix (a rebuilt `Button` swallowed the click). Keep the selected agent in `TreeItem.SetMetadata`. |
| Column titles | `Tree.ColumnTitlesVisible`, `SetColumnTitle`, `SetColumnExpandRatio`, `SetColumnCustomMinimumWidth`, `SetColumnClipContent` |
| Sort + column chooser | `Tree.ColumnTitleClicked` fires for **left and right** button: left = sort (we sort the data, `TreeItem.MoveBefore` reorders in place), right = open the column menu |
| Show/hide columns | `PopupMenu.AddCheckItem` with `HideOnCheckableItemSelection = false` so the menu stays open while ticking several columns |
| Row actions | `Tree.ItemActivated` (double-click → focus camera, as today), `Tree.AllowRmbSelect` + `ItemMouseSelected` (right-click → the shared avatar context menu) |
| Gear / sort buttons from the screenshot | `MenuButton` + `PopupMenu` (radio items for sort order, check items for options, submenus) |
| Search box | `LineEdit.ClearButtonEnabled` |
| Map above, table below | `VSplitContainer` — user-draggable divider, height persisted like window geometry |
| Map drawing | existing `_Draw` API: `DrawTextureRect` with a modulate colour (tile tint), `DrawArc` (rings), `DrawColoredPolygon` (view wedge), `DrawSetTransform` (camera-up rotation — one call instead of rotating every point) |
| Map tile decode | `Image.LoadJpgFromBuffer` on a worker thread, `ImageTexture` upload on the main thread (AGENTS.md non-negotiable 2) |
| Persistence | `ConfigFile` in `preferences.cfg` — same pattern as window geometry: visible columns, order, widths, sort column, ring toggles, orientation, split height |
| Per-avatar marker colour (optional) | `ColorPickerButton` |

## Phases (each is its own commit + `AppVersion` patch bump)

### Phase 1 — Double-click teleport (small, no protocol work)
- `RadarCanvas._GuiInput`: left button + `DoubleClick` → invert `ToCanvas` → region-local X/Y; ignore
  clicks outside the region rectangle; Z = ground height + 1 m.
- Callback `OnTeleportRequested(handle, local)`, wired in `Boot.cs` to `GridSession.TeleportToAsync`
  (the in-flight guard and the teleport overlay already exist).
- Tooltip "Double-click to teleport". Single click does nothing. (Shift-drag panning is Phase 5; the
  radar has no drag today, so there is nothing to clash with.)
- One `RadarProjection` (in `SLNG.Core`, unit-tested) is used for drawing **and** for turning the click
  back into metres, so the click lands on what was drawn. `RadarTeleportTarget` picks the Z.

### Phase 2 — Sim image
- `GridSession` exposes the neutral `MapServerUrl` string (LibreMetaverse `LoginResponseData.MapServerUrl`).
- A tile fetcher (HTTP, disk cache, decode off-thread) shared by the radar **and** `WorldMapWindow`;
  fallback to `MapImageId` through `AssetService`/`GpuCache` (pin with `AddRef`, as `WorldMapWindow` does).
- `RadarCanvas` draws one tinted quad per known region (`World.Terrains` keys → region handle →
  global position). Own region 1.0, neighbours 0.8, no region = background only.
- Optional 2b: object layer from `PrimitiveComponent` + `MetadataComponent.OwnerId` / `YouAreOwner`
  (owned or > 7.5 m, squares, rebuilt ≤ every 0.5 s on a worker thread).

### Phase 3 — Profile / notes data layer (needs the notes decision)
- `GridSession`: neutral `AvatarListInfo` event (notes, payment flags, born-on / hide-age). SL: the
  AgentProfile cap (`RequestAgentProfileAsync`); OpenSim: UDP properties + `RequestAvatarNotes` /
  `AvatarNotesReply`. Write path: `UpdateProfileNotes`. No LibreMetaverse type crosses the boundary.
- Requested **once per avatar when it first enters the list**, through a throttled queue — never per
  frame. Cache by agent id; refreshed after a region change.
- `UserProfileWindow` reads/writes the server note; one-time import of the local `[avatar_notes]`.
- Check first whether SL still answers the UDP profile request `GridSession.Profiles.cs` sends today.

### Phase 4 — Table window
- Layout: `VSplitContainer` (map / table), filter `LineEdit`, gear and sort `MenuButton`s.
- Name header `Name [total/in region/in chat range]`.
- Columns and where each value comes from:

| Column | Source | Default |
|---|---|---|
| Name | roster (already there) | on |
| In region / on parcel | **BUG-UI-11** | on |
| Typing | inbound typing events — **to check** | on if data exists |
| Sitting | parent object **or** ground-sit animation | on |
| `$` / `$$` | Phase 3 flags; three-state, "used" wins over "on file" | on |
| Note | Phase 3 notes; icon shown iff the note is non-empty; **hovering the cell shows the note** (`TreeItem.SetTooltipText`; a long note wraps through a custom tooltip, `Control._MakeCustomTooltip`, capped at ~320 px) | on |
| Age | Phase 3 born-on; red under 7 days; "n.a." if hidden | on |
| Seen | first-seen timestamp per agent, `h:mm:ss`, resets on leaving the list | on |
| Range | 3D distance; coarse Z ≥ 1020 m → `>256` | on |
| Voice | Wanted visible (maintainer 2026-10-01). Shown as a "talking" icon, **on by default**, in the same place Firestorm has it (right after the name). SLNG has no voice yet (`MVP5-1`), so the cell stays **empty until that lands** — no fake data. The column reads a `VoiceLevel` from an interface `SLNG.Core` defines and the voice subsystem will implement, so turning it on later touches no table code. | on |
| Language spoken | Wanted later (maintainer 2026-10-01); **no data source today.** A viewer cannot see another resident's language: the `UpdateAgentLanguage` cap (llagentlanguage.cpp) only tells the *sim* ours, for `llGetAgentLanguage`. The one viewer-visible source is the profile's free-text "Languages" field (`AvatarProfileInterests.LanguagesText`), often empty. Registered in the column table but not offered until a source is wired. | not offered |

- **Column registry**: one list of column definitions (id, title key, icon, default visibility, whether
  its data source exists yet). The table, the column menu and the persistence are all generated from
  it, so Voice and Language switch on later by flipping a flag, not by editing the table.
- Column menu, sort, widths and order persisted. The one-second refresh updates cells in place.
- Selection works both ways: a row highlights its dot, a dot selects its row.

### Phase 5 — Map overlays and interaction
- Range rings 10 / 20 / 100 m (whisper / say / shout), alpha 0.3, one toggle each. Radii overridable
  from the `SimulatorFeatures` `OpenSimExtras` (`say-range` …) once `BUG-UI-11` parses them; defaults
  until then.
- View wedge: radius = draw distance, angle = horizontal FOV (today's arrow shows avatar heading, not camera).
- Dots: above/below arrows beyond ±7 m, friend / muted / self colours, red ring on the selection.
- Orientation north-up / camera-up, zoom presets, auto-center + re-center, Shift-drag pan.
- Tooltip: name + distance on an avatar, otherwise region / parcel. Right-click map menu (zoom, orientation,
  rings submenu, parcel lines, World Map); right-click a dot = the shared avatar context menu.

### Later (not in this task)
Parcel boundary lines, for-sale parcels, "Mark" colours, and People tabs (Nearby / Friends / Groups —
Godot `TabContainer`, wrapping the existing `FriendsPanel`).

## Acceptance Criteria
- [x] Double-clicking the map teleports to that spot; a click outside the region does nothing.
- [x] The map shows the region image, own region brighter than neighbours; black only where no region exists.
- [ ] A user with a note shows the "n" icon, tooltip = the note; a note written in Firestorm shows up.
- [ ] Columns can be shown / hidden from a header right-click and the choice survives a restart.
- [ ] Clicking a header sorts; the sort column is persisted.
- [ ] No row or button is rebuilt per frame; a click on a row is never lost.
- [ ] Tile and profile decode run off the main thread; no LibreMetaverse type crosses `GridSession`.
- [ ] Tests: row-model sort + column visibility, map-tile URL building, note/flag mapping.

## Technical Specs & Affected Files
- `app/scripts/UI/MinimapOverlay.cs` — window, split layout, `Tree`, map overlays
- `app/scripts/UI/WorldMapWindow.cs` — shares the tile fetcher
- `app/scripts/UI/UserProfileWindow.cs` — server-side notes
- `app/scripts/Boot.cs` — teleport callback, `AppVersion`
- `src/SLNG.Net/GridSession*.cs`, `src/SLNG.Core/` — `MapServerUrl`, `AvatarListInfo` DTO and event
- `app/i18n/en-US.json`, `de-DE.json` — column titles, menus, tooltips

## Sub-tasks / Progress
- [x] Maintainer decides: server-side notes (2026-10-01)
- [x] Maintainer clarifies "Sprache" (2026-10-01): Voice indicator now, spoken language later
- [ ] Later: voice indicator gets real data once `MVP5-1` exists
- [ ] Later: language column once a source exists
- [x] Phase 1 — double-click teleport (v0.24.120-alpha, confirmed in-world 2026-10-01)
- [x] Phase 2 — tile fetcher + map image (v0.24.121-alpha: `MapTileService`, `MapTileTextures`, own + neighbour regions drawn; confirmed in-world 2026-10-01). Still open: 2b object layer; switching `WorldMapWindow` over to the same tiles
- [x] Phase 3 — profile / notes data layer (v0.24.122-alpha, notes confirmed in-world 2026-10-01: `GridSession.RequestBriefProfile` / `SetAvatarNote`, profile window on server notes with one-time import and a local fallback; in-world test pending on Agni and OpenSim. The radar does not request anything yet — Phase 4 does.)
- [x] Phase 4 — Tree table, column chooser, sort, persistence (v0.24.123-alpha, look and window margins confirmed in-world 2026-10-01 after the standard-inset fix in v0.24.125/126; Spike result: Godot 4.7 has no API for dragging column borders, so columns are not user-resizable; their widths are fixed and the name column flexes. Column order is the registry order, not user-orderable. `Seen` keeps counting while the window is closed.)
- [ ] Phase 5 — rings, view wedge, dots, orientation, menus (v0.24.127-alpha; in-world test pending. Parcel lines and per-grid OpenSim chat ranges are not done: the rings use 10/20/100 m.)
