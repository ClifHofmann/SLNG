# [FEAT-UI-13] User Profile Window (Firestorm-style)

- **Feature ID:** `FEAT-UI-13`
- **Track:** `ui` / `net`
- **Status:** `✅ Done` — implemented on `feature/FEAT-UI-13-user-profile-window`, builds + 340
  tests + `--selftest` green, **confirmed in-world 2026-08-27** (own profile edited and saved,
  a Pick opened with image/text/location and teleported to, another resident's profile opened
  from the in-world right-click menu).
- **Owner:** `claude`
- **Dep:** `M5-3` (ChatWindow IM tabs + FriendsPanel — the "IM" action reuses
  `ChatWindow.OpenOrFocusImTab`, and the Friends tab gets the "Profile" button wired here).
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Context

A per-avatar profile window is the social hub every SL-family viewer has: public info about a
resident plus one-click social actions. Before this task there was no way to see who another
avatar is, and the Friends tab's "Profile" button was a disabled placeholder.

## What shipped

### Net layer (`src/SLNG.Net/GridSession.cs`, `src/SLNG.Core/AvatarProfile.cs`)

- **Profile fetch:** `RequestAvatarProfile(Guid)` sends one `AvatarPropertiesRequest` (the sim
  answers with Properties + Interests + Groups) plus the separate `RequestAvatarPicks` /
  `RequestAvatarClassified` requests — the legacy UDP path, which OpenSim supports (the modern
  `AgentProfile` CAP is SL-only and not used here).
- **Own-profile write:** `UpdateOwnProfile(about, firstLife, url, profileImgId, firstLifeImgId,
  allowPublish, maturePublish)` → `Self.UpdateProfile` (`AvatarPropertiesUpdate`, or the CAP where
  present); `UpdateOwnInterests(languages, skills, wantTo)` → `Self.UpdateInterests`
  (`AvatarInterestsUpdate`). The whole struct goes every time, so the caller re-sends the current
  image ids unchanged — picture editing is a separate upload/pick feature. The skill / want-to
  bitmasks (the viewer's checkbox lists) are sent as 0; text only. Silent no-op on a grid with no
  profile service.
- **Pick detail:** `RequestAvatarPickInfo(agentId, pickId)` → `PickInfoReply` →
  `AvatarPickDetailReceived` (`AvatarPickDetail`: name, description, snapshot id, sim name, global
  x/y/z as doubles). `TeleportToGlobalPosition(regionName, gx, gy, gz)` resolves the region by
  name and teleports (region-local = global mod 256 — a varregion pick could land off-centre).
- **Neutral DTOs + events** — no LibreMetaverse type crosses `GridSession` (AGENTS.md):
  `AvatarProfileProperties` / `AvatarProfileInterests` / `AvatarProfileGroup` / `AvatarPickInfo`
  / `AvatarPickDetail` / `AvatarClassifiedInfo`, delivered on `AvatarPropertiesReceived` /
  `AvatarInterestsReceived` / `AvatarGroupsReceived` / `AvatarPicksReceived` /
  `AvatarPickDetailReceived` / `AvatarClassifiedsReceived`. All raised on a LibreMetaverse
  network thread.
- **Social actions:** `OfferFriendship`, `OfferTeleport` (`SendTeleportLure`), `PayAvatar`
  (`GiveAvatarMoney`, no-op for a non-positive amount), `SetAvatarMuted` / `IsAvatarMuted` /
  `RequestMuteList` (`MuteType.Resident`). Every send path no-ops while disconnected.
- **`ChatMessageEvent` gained `SourceId` + `FromAgent`** (trailing optional params) so the chat
  log can turn a real resident's name into a profile link without mis-linking object chat.

### UI (`app/scripts/UI/UserProfileWindow.cs`)

`UserProfileWindow : SLNGWindow`, one instance per avatar keyed by agent id in `Boot`
(`_userProfileWindows`, same multi-instance pattern as `ObjectEditWindow`). Whether it is the
local agent's own profile is only known in `Initialize`, so the tabs/action-bar are built there,
not in `_Ready`.

**Viewing another resident:**

- **2nd Life:** profile picture, About text (shown literally — `BbcodeEnabled = false`), account
  / charter string, partner (name resolved via `NameResolved`), optional web URL, Interests
  (once they arrive), and the listed Groups.
- **1st Life:** 1st-life picture + text.
- **Picks:** a list of pick names; selecting one loads its detail — snapshot image, title,
  description, `SimName (x, y, z)` location, and a **Teleport** button (a single pick auto-selects).
- **Classifieds:** name list only (per-item detail is not in this pass).
- **Notes:** local `TextEdit`, debounced-autosave to `user://preferences.cfg` `[avatar_notes]`
  keyed by agent UUID; never sent to the grid. Also flushed on close / `_ExitTree`.
- Action bar: **Add Friend** (disabled if already a friend), **IM** (opens a ChatWindow IM tab),
  **Pay…** (reveals an inline L$ spinbox), **Offer TP**, **Mute/Unmute**.

**Your own profile (`_isSelf`):** the 2nd Life page shows editable fields — About (`TextEdit`),
Web URL, "Show in search" / "Mature profile" checkboxes, and Interests (Languages / Skills /
Want to `LineEdit`s); the 1st Life page has an editable text field. Fields are seeded from the
first server reply and not re-clobbered by a later duplicate. A **💾 Save Profile** button writes
both packets. The Notes tab and the social-action buttons are omitted. The profile picture is
still read-only (upload/pick is deferred).

Profile pictures load through `GpuCache.GetOrUploadTextureAsync` on a worker thread and are
marshalled back with `CallDeferred(MethodName.ApplyTexture, …)` (safe from a worker thread,
unlike `Callable.From(lambda).CallDeferred`).

### Entry points

1. **In-world right-click an avatar** → context menu "👤 Profile" / "💬 IM"
   (`ObjectSelectionController` avatar branch + `InWorldContextMenu.ShowAvatarMenu`). Avatars
   already carry a `StaticBody3D` with `LocalId == "Avatar"` and an `EntityId` meta
   (`AvatarRenderer.CreateVisual`); previously a right-click on an avatar wrongly showed the
   ground "Create" menu — now intercepted.
2. **Friends tab → "Profile" button** (`FriendsPanel.OnOpenProfileRequested` → `ChatWindow` →
   Boot).
3. **Click a resident's name in the chat log** — names are emitted as
   `[url=avatar:<guid>]…[/url]`; `ChatWindow.OnLogMetaClicked` opens the profile. Local-chat
   lines only get a link when the sim tagged the source as an agent; IM lines link the other
   party.

### Boot wiring

`Boot` subscribes the five profile-reply events + `NameResolved` / `DisplayNameResolved` and,
because the DTO payloads are not Variant-safe for `CallDeferred`, buffers them on a
`ConcurrentQueue<Action>` drained in `_Process`. A `volatile int _openProfileWindows` mirrors
the window-map count so the network-thread handlers can early-out without racing the plain
`Dictionary`. Open profile windows are freed on re-login alongside `_objectEditWindows`.

## Deliberately deferred (not regressions — scoping, matches the M5-3 precedent)

- **Pick "Show on Map" / "Set location", pick create/edit, Classified detail** — "Show on Map"
  needs the world map (MVP2-3, not built); editing picks needs `PickInfoUpdate`; Classified detail
  needs `RequestClassifiedInfo`.
- **Profile / 1st-life picture editing** — needs a texture upload or an inventory picker; the
  current image ids are re-sent unchanged on save.
- **Skill / want-to checkbox lists** — the interests bitmasks; free text only for now.
- **Clickable web URL / partner name** — shown as plain text.
- **Copy-UUID affordance** — the id is shown but not selectable.
- **Live language switch** — like every other window, translations bind at open time; changing
  language needs the window reopened (matches the Preferences hint).

## Affected files

- `src/SLNG.Core/AvatarProfile.cs` *(new)* — DTOs + event records (incl. `AvatarPickDetail`).
- `app/i18n/en-US.json` + `app/i18n/de-DE.json` — `ui.profile.*` (~58 keys, both locales;
  `--selftest` enforces parity).
- `src/SLNG.Core/GridEvents.cs` — `ChatMessageEvent.SourceId` / `.FromAgent`.
- `src/SLNG.Net/GridSession.cs` — profile-reply handlers, `RequestAvatarProfile` /
  `RequestAvatarPickInfo`, `UpdateOwnProfile` / `UpdateOwnInterests`, `TeleportToGlobalPosition`,
  social-action wrappers, chat source id.
- `app/scripts/UI/UserProfileWindow.cs` *(new)*.
- `app/scripts/UI/InWorldContextMenu.cs` — avatar menu.
- `app/scripts/ObjectSelectionController.cs` — avatar right-click branch.
- `app/scripts/UI/FriendsPanel.cs` — "Profile" button wired.
- `app/scripts/UI/ChatWindow.cs` — clickable names, `OnOpenProfileRequested` passthrough.
- `app/scripts/Boot.cs` — `OpenUserProfileWindow`, event plumbing, `RequestMuteList` post-login,
  `AppVersion` → `v0.9.55-alpha`.
- `tests/SLNG.Net.Tests/AvatarProfileTests.cs` *(new)* — 7 tests: reply→DTO mapping (reflection
  on the private handlers, hand-built LibreMetaverse EventArgs, incl. `PickInfoReply`) + every
  send path no-op while disconnected.

## Acceptance criteria

- [x] Right-clicking an avatar in-world (or clicking a name in chat / the Friends "Profile"
      button) opens the User Profile window. *(code complete — in-world confirmation pending)*
- [x] Profile data (pictures, text, groups) is fetched and displayed across the tabs.
- [x] Selecting a Pick shows its image, title, description and location, with a working Teleport.
- [x] All strings are localised (`ui.profile.*`, en-US + de-DE).
- [x] Private "Notes" are saved locally (`preferences.cfg [avatar_notes]`) and persist between
      sessions.
- [x] Action buttons trigger the correct network requests (IM, friend request, pay, teleport
      offer, mute).
- [x] Your own profile is editable — About / 1st Life / web URL / publish flags / interests
      text — and "Save Profile" writes `AvatarPropertiesUpdate` + `AvatarInterestsUpdate`.
- [x] Window inherits `SLNGWindow` (FEAT-UI-11 clamp / BUG-UI-01 minimize behaviour come for
      free).
- [x] No LibreMetaverse type on `GridSession`'s public API; network events marshalled before
      touching the Control tree.
- [x] Confirmed in-world against a real avatar (OSGrid, 2026-08-27).
