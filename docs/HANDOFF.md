# Handover from Claude to Gemini/Antigravity

## Task: MVP2-2 — Landmark Teleport & Creation

Full history, acceptance criteria, and file list: [docs/specs/MVP2-2-landmark-teleport.md](file:///E:/Git/SLNG/docs/specs/MVP2-2-landmark-teleport.md).
This note is the short version — read the spec's **Open Issue** section before touching code.

**What Claude accomplished:**
- Landmark teleport: `GridSession.TeleportToLandmarkAsync(Guid landmarkAssetId, CancellationToken)`
  wraps LibreMetaverse's `AgentManager.TeleportAsync(UUID, ct)`, returning a neutral
  `TeleportResult(bool Success, string Message)` — no LibreMetaverse type crosses `SLNG.Net`.
- Landmark creation: `GridSession.CreateLandmarkHereAsync` builds an `AssetLandmark` from the
  agent's current region + position and uploads it via LibreMetaverse's `NewFileAgentInventory`
  CAP. A **World → Create Landmark...** menu entry opens a Firestorm-style dialog
  (`app/scripts/UI/CreateLandmarkWindow.cs`): name, destination folder, inline "new folder", notes.
- Four rounds of live-test bugfixing (all confirmed fixed by the user, live, on what looks like the
  real Second Life grid — landmark names like "LBSA Plaza" showed up in a screenshot):
  1. A just-created landmark's `asset_id` briefly reads as empty from a fresh folder-contents fetch
     (server-side indexing lag) — fixed by trusting the asset id from the create response instead.
  2. Windows didn't raise above each other on click; clicks/wheel-scroll leaked through blank
     window areas to whatever was rendered behind (CameraHUD, 3D viewport).
  3. The round-2 fix for the above didn't actually work in practice — switched from relying on
     Godot's `MouseFilter`/event-consumption assumptions to direct `GuiGetHoveredControl() != null`
     checks in `AvatarController` (wheel-zoom) and `ObjectSelectionController` (world click, had NO
     guard at all before).
  4. Console diagnostics (added in round 3) showed the SAME indexing lag from #1 also hits
     `AssetType` (came back `0`, not `3`/Landmark) — fixed the same way. Also found a third
     unguarded click handler, `AvatarRenderer.TryClickHud` (worn HUD-attachment touch), which runs
     off `_Input` (fires regardless of GUI consumption) and had zero UI guard — same fix applied.

**Current status:** "Teleport" now correctly enables on a freshly created landmark, and every
window/input bug reported (focus, click-through, wheel-zoom) is confirmed fixed by live test.

**Still broken — this is the actual handoff:** clicking the now-enabled "Teleport" action does
**not** teleport the agent. The click registers (no crash, no stuck UI), but nothing happens
in-world.

**Where to start:** `app/scripts/UI/InventoryPanel.cs`'s `TeleportAsync` already has
`GD.Print`/`GD.PrintErr` diagnostics logging `item=/assetId=/parentFolder=`, a re-resolve line if
the asset id needed healing, and the final `result success=.../message='...'`. **Get that console
output from an actual test run first** (stdout or `%APPDATA%\Godot\app_userdata\SLNG\logs\`) —
it should immediately tell you whether the grid is rejecting the request with a reason, silently
not responding (the `Task<bool>` never resolves — note no `CancellationToken` is threaded from the
UI today, so a non-responding sim hangs with no visible failure), or reporting success while
nothing actually happens (a LibreMetaverse-side bug).

Four hypotheses, roughly in priority order, are written up with more detail in the spec's Open
Issue section — worth reading before diving in, especially #2 (the landmark asset's `Encode()`
output was trusted at face value against LibreMetaverse's own implementation, never independently
verified against a real SL-created landmark's bytes) and the note that `TeleportAsync(UUID, ct)`'s
full success/finish packet-handling path in LibreMetaverse's `AgentManager.PacketHandlers.cs`
wasn't actually read yet — only the `LandmarkID` field's wire semantics were (via
`data/message_template.msg`, confirmed it must be the landmark's *asset* id, not the inventory
item id — that part is right).

**Files:**
- `src/SLNG.Net/GridSession.cs` — `TeleportToLandmarkAsync` (~line 632), `CreateLandmarkHereAsync`
  (~line 808).
- `app/scripts/UI/InventoryPanel.cs` — `OnContextMenuIdPressed` (`id == 5` branch), `TeleportAsync`.
- `docs/specs/MVP2-2-landmark-teleport.md` — full acceptance criteria, all four live-test-fix
  writeups, and the Open Issue section with hypotheses.

**Next Roadmap Task:** fix the teleport execution bug above (still `MVP2-2`). Once it actually
teleports, `MVP2-2` still has minimap overlay, full grid map, region search, and
teleport-by-region-name/coordinates open — deliberately deferred, see the spec's Sub-tasks section.
