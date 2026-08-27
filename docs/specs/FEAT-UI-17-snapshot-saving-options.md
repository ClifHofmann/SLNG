# [FEAT-UI-17] Snapshot Saving Options & Upload

- **Feature ID:** `FEAT-UI-17`
- **Track:** `ui` / `net`
- **Status:** `⏸️ Pending`
- **Owner:** —
- **Agent:** `ux-designer` (options UI) + `protocol-re` (inventory texture upload)
- **Dep:** `MVP3-4` (Snapshot window — Phase 1 landed)
- **GitHub:** [ClifHofmann/SLNG#35](https://github.com/ClifHofmann/SLNG/issues/35)

## Context
The base snapshot capture exists (`MVP3-4` Phase 1: `SnapshotWindow`, capture, save PNG to a
fixed `user://snapshots/` path). The user has no control over **where** or **how** the image is
saved. This adds the destination/format options a standard viewer offers.

## Requirements

### 1. Destination
- **Local disk:**
  - A native file dialog to pick directory + filename for a given save.
  - A **default output folder** setting in Preferences; the fixed `user://snapshots/` becomes
    the fallback default, not the only option.
- **Inventory upload:**
  - Upload the captured image as a **Texture** asset into the avatar's inventory
    (LibreMetaverse's asset-upload path — same one a real texture upload uses).
  - **Warn about the L$ fee.** SL/most grids charge the standard texture-upload fee (10 L$ on
    SL). The UI must surface the cost before the upload and handle "insufficient funds" without
    a silent failure. On a grid with free uploads, say so instead of showing a fee.

### 2. Format (local save)
- A dropdown: **PNG** (lossless, keeps alpha) / **JPG** (smaller).
- When JPG is selected, a **quality** slider (e.g. 1–100).
- `Image.SavePng` / `Image.SaveJpg(path, quality)` — Godot has both.

### 3. UI
- Extend `SnapshotWindow` (`MVP3-4`): the format dropdown, a destination choice
  (disk / inventory), and — for disk — the folder/filename control, all shown **before** the
  final Save/Upload action. Keep the current one-click "Save PNG" as the default path so the
  common case stays fast.

## Acceptance criteria
- [ ] A snapshot saves to a local, user-chosen folder; the default folder is configurable in
      Preferences and persists.
- [ ] A snapshot uploads directly into the avatar's inventory as a usable texture.
- [ ] Format selection (PNG/JPG, + JPG quality) works for local saves; the resulting file is a
      valid image of the chosen type.
- [ ] An inventory upload shows the L$ cost up front and reports insufficient-funds / grid
      rejection to the user rather than failing silently.

## Affected files (anticipated)
- `app/scripts/UI/SnapshotWindow.cs` — options row, format dropdown, destination switch, file
  dialog, upload trigger.
- `app/scripts/Boot.cs` / a Preferences page — default snapshot folder setting.
- `src/SLNG.Net/GridSession.cs` — a neutral `UploadTextureAsync(bytes, name)` (or similar)
  wrapping LibreMetaverse's asset upload + inventory-item creation, returning a neutral result
  (success / item id / cost / error) with no LMV type crossing the boundary.
