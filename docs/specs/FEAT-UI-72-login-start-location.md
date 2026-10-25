# [FEAT-UI-72] Choose where to log in: last location, home, a favorite or a typed region

- **Feature ID:** `FEAT-UI-72`
- **Track:** `ui` (with `core` and `net` parts; phase 2 writes to the SL inventory)
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
User request (German, 2026-10-17): "after choosing the user profile on the login screen, I want to choose
whether to log in at my last location, at home, or at a favorite."

Today the login screen has no such choice. `LoginCredentials.StartLocation` exists and defaults to `"last"`, but
`Boot.OnLoginPressed` never sets it, so every login starts at "last" (`app/scripts/Boot.cs:4322-4335`). This feature adds
a **Start at** dropdown, directly under the profile dropdown, with:

1. **My last location** (default, what happens today),
2. **My home**,
3. **Favorites** - the landmarks in the SL inventory **Favorites folder** (folder type 23), like Firestorm. Offered *before*
   login from a per-profile copy kept on disk, because the viewer cannot read inventory before it is logged in,
4. **Region...** - an optional typed region (`Region`, `Region/x/y/z`, or a pasted SLURL).

The choice is remembered per saved login profile. If the grid does not honour it, the user is told, and the viewer makes
**one** attempt to teleport to the requested place.

### Decisions taken by the user (2026-10-17)
1. **"Favorites" = the SL inventory Favorites folder, shared with other viewers.** SLNG's own Favorites bar (FEAT-UI-67) is
   reworked to show that same folder, so the bar, the login dropdown and Firestorm see one list. This is **phase 2**
   (commit D) so the login dropdown can ship first (phase 1 reads the folder read-only).
2. **Placement:** directly under the profile dropdown (not under the password field).
3. **Landing elsewhere:** a notice **plus one automatic teleport attempt** to the requested place (no loop). See the
   correction in "When the grid does not honour the start": the reference viewer's code for that is dead, so this goes
   beyond the viewer and is specified from first principles.

## Research findings (cited)

### What SLNG does today
| Fact | Where |
|---|---|
| Login form nodes: `Spacer`, `ProfileDropdown`, `GridHBox` (`GridInput` + `GridDropdown`), `HBox` (`FirstInput`, `LastInput`), `PassInput`, `Spacer2`, `Actions` (`SaveLoginCheck` + `LoginButton`), all under `LoginScreen/Panel/Margin/VBox` | `app/scenes/Boot.tscn:125-262` |
| A control can be added to that VBox from code and moved next to a known sibling (precedent: the About link) | `app/scripts/Boot.cs:650-660` |
| Profiles live in `user://logins.cfg`, one section per profile named `"First Last @ <login uri>"`; keys `grid`, `first`, `last`, `pass_hash`, `mfa_hash`; `[Settings]`/`[Window]` are reserved | `Boot.cs:776-778` (`ProfileKey`), `3572-3615` (`LoadProfiles`), `4376-4424` (saved on login success) |
| Selecting a profile fills the fields and sets `last_profile` | `Boot.cs:3654-3695` (`OnProfileSelected`) |
| Editing first/last/grid drops the stored password hash (`ForgetStoredPassword`) | `Boot.cs:35-46`, hooked at `615-617` |
| `LoginCredentials.StartLocation`: plain string, default `"last"` | `src/SLNG.Net/LoginCredentials.cs:35-36` |
| The one place the request is built: `login.Start = string.IsNullOrEmpty(creds.StartLocation) ? "last" : creds.StartLocation;` | `src/SLNG.Net/GridSession.Session.cs:1159` (`BuildLoginParams`) |
| A test already pins `StartLocation = "home"` -> `login.Start == "home"` | `tests/SLNG.Net.Tests/LoginTermsGateTests.cs:77-83` |
| `LoadProfiles()` is skipped under `--selftest` (it is the developer's real data) | `Boot.cs:666-670` |
| **The current in-world Favorites bar is SLNG's own list**, not the SL folder: `LandmarkFavoritesList` of `(ItemId, AssetId, Name)`, max 64, JSON in `user://preferences.cfg` section `favorites_bar_{gridSlug}_{agentId}`. It has `Add`/`Remove`/`Move` in the model but the bar UI only calls `Add` and `Remove`; adds are appended | `src/SLNG.Core/LandmarkFavoritesList.cs`, `app/scripts/UI/LandmarkFavoritesStore.cs`, `app/scripts/UI/FavoritesBar.cs:165-224`, spec `FEAT-UI-67` |
| That list is keyed by **agent id**, unknown before the first login of a profile, so the login screen cannot read it | `Boot.cs:4570-4572` |
| Consumers of the current list: `FavoritesBar`, the add hooks (`InventoryPanel.OnAddToFavorites`, `LandmarksWindow.OnAddToFavoritesRequested`, `Boot.cs:1005-1015, 1242-1248, 1385-1391`) and the duplicate cleaner (`favIds`, `Boot.cs:4577-4580`, `LandmarkDuplicateDetector.cs:99-112`) | - |
| SLNG already knows the Favorites folder **type** (`InventoryEntry.cs:109`, `Favorites = 23`) but has no accessor, no fetch and no UI for it. `LandmarksFolderId` shows the pattern: `SystemFolderId(FolderType)` | `GridSession.Inventory.cs:431-444` |
| **`FindFolderForType` returns the inventory ROOT when no folder of that type exists** (decompiled 3.1.6: it scans only the root's children, then `return _Store.RootFolder.UUID`). A Favorites accessor built like `LandmarksFolderId` would, on a grid without that folder, make the whole root look like "Favorites" | decompile of `LibreMetaverse.InventoryManager.FindFolderForType(FolderType)`, 3.1.6; same note in the `LandmarksFolderId` doc, `GridSession.Inventory.cs:441-443` |
| Existing inventory write helpers: `CopyItemAsync` (`:1007`, result unreliable, see its doc), `MoveToTrashAsync`/`MoveInventoryAsync` (UDP `MoveInventoryItem`, `:880-956`), `CreateLandmarkHereAsync(name, desc, folderId)` (`:3685`, any folder), `CreateInventoryLinkAsync` (`:1078`, null on refusal) | `GridSession.Inventory.cs` |
| Folder fetch + "did the grid actually fill it" check: a folder is trustworthy only when its store node has `NeedsUpdate == false` after the fetch | `GridSession.Inventory.cs:730-753` (`FetchFolderFromGridAsync`) |
| Landmark -> region name + local position already exists inside the teleport fallback: landmark asset -> `AssetLandmark.RegionID` + `Position` -> `ResolveRegionHandleAsync` -> region | `GridSession.Session.cs:946-971`, `1018-1057`; `ResolveRegionByHandleAsync` `339-346`; `ResolveRegionByNameAsync` `332-337`; `TeleportToAsync(handle, localPos)` `421`; LMV `AssetLandmark.cs:40-42, 72-82` |
| `GridSession.CurrentRegionName`, and a first-arrival hook that waits for it to be non-empty | `GridSession.cs:900`, `Boot.cs:2971-2979` |
| On login success `LoginResult.Ok(...)` is built from LMV's `LoginResponseData`; `StartLocation` and `FirstLogin` are on that object but are not carried into `LoginResult` | `GridSession.Session.cs:828-830`; `LoginResult.cs` |
| SLNG has no parser for `Region/x/y/z` or SLURLs (grep `SlurlParser`/`Slurl` in `src`, `app`, `tests`: none) | - |

### What LibreMetaverse does with `LoginParams.Start` (verified on the pinned 3.1.6)
- The SL login field is `start`: `"last"`, `"home"`, or `"uri:Region Name&x&y&z"`. The request is an LLSD **XML** POST
  (`NetworkManager.BeginLogin`; v3.0.2 source `Login.cs:1004, 1073`), so the `&` is escaped by the serializer on the wire:
  SLNG must hand over the raw string.
- **Verified by decompiling 3.1.6** (`ilspycmd` with `DOTNET_ROLL_FORWARD=Major`, `NetworkManager.BeginLogin`, lines 817-836 of
  the decompile): `home` and `last` pass through; **anything else** is run through
  `new SlurlParser(Start.Trim()).GetStartLocationUri()` and re-emitted as `uri:{Sim}&{X}&{Y}&{Z}`. A string that already
  is `uri:Region&x&y&z` is parsed by the legacy branch and re-emitted unchanged (idempotent). `scratch/libremetaverse_src`
  is v3.0.2 and matches on this point (`Login.cs:977-994`, `SlurlParser.cs:98-144, 275-278`), but 3.1.6 was checked directly.
- That conversion is **lossy and unvalidated**: no percent-decoding (`Ahern%20Park` stays), non-numeric coordinates silently
  become `128/128/0`, no range check, garbage text becomes `uri:garbage&128&128&0`. So SLNG must build the canonical string
  itself and never rely on the fallback. Do **not** use `LoginParams.LoginLocation` (it overrides `Start`, same lossy path).
- The login **reply** is exposed: 3.1.6 `LoginResponseData` has public `StartLocation` (string, from `start_location`) and
  `FirstLogin` (bool) (decompile of `LibreMetaverse.LoginResponseData`).

### What the reference viewer does (secondlife/viewer 26.4.0 under `scratch/slviewer`)
- **The login string.** `construct_start_string()` builds `last` / `home` / `uri:<region>&<x>&<y>&<z>` into
  `request_params["start"]` (`indra/newview/lllogininstance.cpp:213, 569-598`). Its comment says *do not XML-escape here*
  (secondlife/viewer#2395): raw `&`, never `&amp;`. The older `LLSLURL::getLoginString()` that emits `&amp;`
  (`llslurl.cpp:420-443`) has **no caller left** except its unit test - do not copy it.
- **The panel.** `start_location_combo` is an editable combo (`allow_text_entry="true"`) with fixed items `My last location`
  (`value="last"`) and `My home` (`value="home"`), then favorites below a separator (`skins/default/xui/en/panel_login.xml:188-227`;
  `llpanellogin.cpp:342-443` `addFavoritesToStartLocation`: remove everything after index 1, then append the favorites of the
  user whose name is typed in the username box). Typing a region is a feature of the same control. Default selection:
  setting `LoginLocation` (default `'last'`, `app_settings/settings.xml:4450-4458`; `llpanellogin.cpp:291-304`) and the last
  attempted location `NextLoginLocation` (`llstartup.cpp:3415-3436`).
- **Typed text -> location.** `LLSLURL(string)`: empty/`last` and `home` first (`llslurl.cpp:62-69`); otherwise prepend the grid's
  SLURL base, percent-decode the region (`:292`), strip non-printables (`:296`), read x, y, optional z (`:304`), **reject** x/y outside
  0..256 or z outside 0..4096 (`:305-310`; `llcommon/indra_constants.h:34,38`); no coordinates -> middle of the region,
  z = 0 (`:318`).

### What the reference viewer does with the Favorites folder (for the bar rework and the login cache)
- **What the favorites are.** The landmark items (`AT_LANDMARK` only - links are not counted) in and below the folder of type
  `FT_FAVORITE`: `collectDescendentsIf(favorites_id, ..., EXCLUDE_TRASH, LLIsType(AT_LANDMARK))`
  (`indra/newview/llfavoritesbar.cpp:1135-1160, 2147-2157`; folder lookup `:779`). They are **real copies** of landmarks, not links.
- **Order is client-local, not on the grid.** Each item's sort field is `LLFavoritesOrderStorage::getSortIndex(item id)`
  (`llviewerinventory.cpp:2094-2097`), a map loaded from `landmarks_sorting.xml` in the per-SL-account directory
  (`llfavoritesbar.cpp:1827-1857`), or, when that file is missing, from the order of the entries in `stored_favorites_<grid>.xml`
  (`:1858-1894`); saved by `saveOrder`/`saveItemsOrder` as `1, 2, 3, ...` (`:2082-2114`). `updateServer(false)` is called per item
  but carries no order. So another viewer does **not** see this viewer's order. (The old convention of a `N@` prefix in the item
  name is dead code, `llviewerinventory.cpp:2229-2264`; do not write it.)
- **Comparator** (`LLFavoritesSort`, `llfavoritesbar.cpp:376-401`): items with an index sort ascending by it; an item **without** an
  index sorts **before** indexed ones; two items without an index: newer creation date first, ties by name (dictionary compare).
  `needToSaveItemsOrder` (`:1691-1706`) then freezes the shown order by assigning `1..n` on first display (`:1149-1157`).
- **Add** = drag from inventory/landmarks or paste: `copy_inventory_item(agent, item owner, item id, favorites_id, "" , cb)`
  (`:692-698`, paste `:1581-1606`), after inserting at the drop position and renumbering the sort indices (`:654-678`); a drag of an
  item that already lives in the Favorites folder is refused (`:567-572`); a drag within the bar only reorders (`:606-630`).
  The star in the location bar creates a landmark directly in that folder.
- **Remove** (context menu "delete") = `gInventory.removeItem` = **move to Trash** (`llfavoritesbar.cpp:1499-1502`;
  `llinventorymodel.cpp:4277-4293`), the same reparent the SLNG `MoveToTrashAsync` documents. "Move to landmarks" is a reparent
  (`:1513-1516`). Rename is a landmark rename (`:1503-1512`).
- **The login copy.** `stored_favorites_<grid>.xml` in the user settings directory (`llfavoritesbar.cpp:1776-1792`): map
  *"First Last" -> array of {name, slurl, asset_id, id}* (`saveFavoritesRecord`, `:2136-2264`, key `:2240-2244`), the SLURL resolved
  from each landmark asset (`getSLURL` -> `onLandmarkLoaded` -> `storeFavoriteSLURL`, `:1760-1773, 2021-2047`; an unresolved one is
  written empty and re-saved later, `:2222-2228`). Saved whenever the folder's contents or names change and once more on exit
  (`destroyClass` -> `saveFavoritesRecord(true)`, `:1795-1825`), gated by the per-account setting `ShowFavoritesOnLogin` (default
  **on**, `settings_per_account.xml:362-372`; `llfavoritesbar.cpp:2211`). The login panel reads it back at `llpanellogin.cpp:357-435`
  (entries with an empty label or slurl are skipped, `:420`). Firestorm uses the same Favorites folder and mechanism; its own source
  is not vendored here, so that is reference behaviour, not re-verified.

### When the grid does not honour the start (corrected)
- **The viewer is told by the login reply.** The reply's `start_location` is stored (`llstartup.cpp:3902-3906`, default `"safe"`
  at `:322`). After login, if this is **not the first login** (`:2348`), the viewer compares the *requested* kind with it:
  requested location + reply `"url"`, requested last + `"last"`, requested home + `"home"` = honoured; anything else shows a modal
  alert `AvatarMovedDesired` / `AvatarMovedLast` / `AvatarMovedHome` ("Your desired location is not currently available. You have
  been moved into a nearby region.", `llstartup.cpp:2340-2392`; text `skins/default/xui/en/notifications.xml:3775-3799`).
  That is the protocol-level signal for "your region was down or refused you".
- **`dispatchURL` is dead code.** `LLStartUp::dispatchURL()` (`llstartup.cpp:3385-3413`) would compare the start SLURL's region name
  and x/y (2 m slop, z ignored) with where the agent is and dispatch the SLURL, but its only call site is commented out
  (`llstartup.cpp:2571`: `//LLStartUp::dispatchURL();`) and `grep` finds no other caller. Even if called, `dispatchRegion`
  (`llurldispatcher.cpp:177-205, 243-272`) resolves the region by name and then either teleports or only opens the Places panel,
  depending on the setting `SLURLTeleportDirectly`. **So the real viewer 26.4 does not teleport after a refused start; it only
  shows the alert.** The user's decision (notice + one teleport attempt) is therefore a SLNG-specific behaviour, specified below.
- The login server decides the fallback. Second Life and OpenSim differ (OpenSim: unknown region -> its configured default
  regions, none configured -> the login is refused; known region with a dead simulator -> its fallback regions; from memory of
  OpenSim's `LLLoginService`, which is not vendored: `scratch/opensim_fetch` holds only client-view files - confirm on the local
  grid, manual step 5).

## Design

### Principles
- The UI thread never waits on the network: the login screen reads only local files; resolving favorites happens in the
  background **during a session** and writes the result for the **next** login.
- `src/` stays free of Godot. The string builder, the typed-text parser, the choice model, the favorites JSON and the
  favorites ordering live in `SLNG.Core` (pure, xUnit-tested). `ConfigFile` and `Control` code lives in `app/`.
- One list of favorites (the SL Favorites folder), three consumers: the bar, the login cache, the duplicate cleaner.
  The login cache is a *derived copy* of the folder (exactly as the viewer's `stored_favorites_<grid>.xml` is).

### Layout (`LoginStartPicker`, a reusable Control, built in code)
```
 [ Select profile          v ]
 Start at  [ My last location    v ]        <- new row, directly below the profile dropdown
           [ Region/x/y/z or SLURL ]        <- only when "Region..." is selected
           Not a valid location             <- only while that text is invalid
 [ Grid URL ........... ][ Grid v ]
 [ First name ][ Last name ]
 [ Password ................... ]
 [x] Save Login                  [ Login ]
```
Popup (Godot `OptionButton`, separators via `AddSeparator(text)`):
```
 My last location
 My home
 -- Favorites --                      <- only if the profile has >= 1 cached favorite
 * Puris Town                         <- tooltip: "Puris Town - Ahern (128, 64, 30)"
 * Sunset Beach
 -- --
 Region...
```
It is inserted with `_profileDropdown.GetParent().AddChild(picker); MoveChild(picker, _profileDropdown.GetIndex() + 1)`
(precedent `Boot.cs:650-660`) and copies the `normal/hover/focus` styleboxes and font colour from `ProfileDropdown`
(`ApplyStylesFrom`) so it matches the glass theme. It reloads whenever the profile or the first/last/grid fields change.

States that must be legible:
- Typed region invalid: red hint line under the field, `Login` disabled (never a silent fall-back to "last").
- Profile exists but its favorites cache was never written (first login after the upgrade): one disabled item
  "Favorites appear here after your next login" under Home.
- After login, the requested place was not honoured: toast + notification (below).

### Remembered choice (per profile)
Two new keys in the profile's own `logins.cfg` section (next to `pass_hash` / `mfa_hash`; same lifetime - they disappear with the
profile, and "Save Login" unticked erases them like it erases the password):

| Key | Value |
|---|---|
| `start` | `last` \| `home` \| `fav:<landmark asset guid, N format>` \| `region:<typed text>` |
| `start_favorites` | one-line JSON, see below; `{"v":1,"items":[]}` when the folder was confirmed empty (key present = "has been populated") |

Compatibility: a `logins.cfg` without these keys reads as `last` / no favorites. An older build ignores the keys; Godot's
`ConfigFile` preserves unknown keys when it rewrites the section. Why not `preferences.cfg`, where the old bar list lives: the
login screen only knows (first, last, grid), not the agent id that keys `favorites_bar_{grid}_{agentId}`.

The choice is saved **on successful login** (when the profile's grid/first/last are saved, `Boot.cs:4376-4402`), only if "Save Login"
is ticked. A failed login keeps the selection because the screen stays up. A remembered favorite that is no longer in the cache
makes the picker show "My last location"; `LoginStart.Resolve` reports `StartFallback.FavoriteMissing`; nothing is rewritten until
the next successful login.

### Favorites cache (what the login screen reads)
`start_favorites` JSON (ordered like the bar, max 64):
```json
{"v":1,"items":[{"asset":"<guid>","item":"<guid>","name":"Puris Town","region":"Ahern","x":128,"y":64,"z":30}]}
```
- **Source: the Favorites folder** (`GridSession.GetFavoritesAsync`, below), not the old local list. **Key = landmark asset id**: a
  landmark asset is immutable (region id + local position), so it is resolved once; the name and the order come from the folder
  every time.
- Order: the bar's order. Until phase 2 exists that is the viewer's own default order for items without a sort index (newest
  creation date first, then name - `llfavoritesbar.cpp:385-399`), computed by `FavoritesOrder.Sort`; from phase 2 on it is the
  user's saved order.
- Entries with `AssetId == Guid.Empty` are skipped until they have one. Anything unresolvable (landmark to a Hypergrid/foreign
  region, asset fetch failed, region handle never answered) is left **out**; one terse log line per unresolved landmark:
  `[LoginStart] could not resolve '<name>': <reason>`.
- **Never believe an unconfirmed empty list.** `GetFavoritesAsync` returns `Confirmed = false` when the folder is missing or the
  fetch did not clear `NeedsUpdate` (a refused fetch reads as an empty folder - memory `lmv-folder-fetch-refusal-reads-as-empty`;
  a cache restore returns a never-fetched folder as current-and-empty - `lmv-inventory-cache-trusts-unfetched-folders`). An
  unconfirmed answer **never** touches `start_favorites`; otherwise one bad fetch would wipe the login list.

Resolution (new, read-only, never teleports): `GridSession.ResolveLandmarkTargetAsync(Guid landmarkAssetId, CancellationToken)`
-> `StartTarget?`. The steps are the ones the teleport fallback already performs (`GridSession.Session.cs:946-971`):
`RequestAssetAsync(..., AssetType.Landmark)` -> `Decode()` -> `ResolveRegionHandleAsync(RegionID)` ->
`ResolveRegionByHandleAsync(handle)` for the canonical name -> `StartTarget.TryCreate(...)`. 15 s timeout, null on any failure,
does not touch `_teleportInProgress`.

When it runs (`LoginStartFavorites`, an app-side class, no Godot calls):
1. After the loading screen clears (the `_waitingForWorldLoad` completion block, `Boot.cs:3040-3051`, next to
   `RestoreOpenWindows`) so it never competes with the initial region load: `GetFavoritesAsync`, then resolve the entries with no
   cache entry, **one at a time**, ~250 ms apart.
2. Again (debounced 1.5 s) whenever the favorites change: phase 1 - `InventoryManager.FolderUpdated` for the Favorites folder
   (the implementer verifies it fires for items copied in by another viewer; if not, the cache refreshes at the next login);
   phase 2 - `FavoritesService.Changed`.
3. Each result is published as an immutable snapshot (volatile field / `Interlocked.Exchange`); `Boot._Process` drains it and does
   the `ConfigFile` write on the main thread (**`ConfigFile` is not thread-safe; never touch it from the resolver; never use
   `Callable.From(...).CallDeferred()` from this thread** - `app-rules`).
4. A write happens only if the profile section still exists and "Save Login" was ticked for this session (`_sessionStartCacheKey`),
   then `_loginsConfig.Save("user://logins.cfg")`.
5. Cancelled and disposed when the session ends or a new login starts (`OnLoginPressed` disposal block `Boot.cs:4009-4023`,
   `QuitGracefully` near `SaveInventoryCache` `5905`). One last flush of what is already known just before the logout call (no
   network involved), so "at logout" is covered.

### From a favorite to the login string
`StartTarget(Region, X, Y, Z).ToLoginString()` = `uri:{Region}&{X}&{Y}&{Z}` - **raw `&`, integers, region name unescaped**
(LMV escapes it on the wire). Rules (`StartTarget.TryCreate`):
- Region: trim, drop control characters, non-empty, **no `&`** (the `uri:` form cannot express it; refuse, never mangle), <= 255 chars.
- X, Y, Z: finite, rounded half away from zero (viewer `ll_round`), X/Y in `0..maxXY`, Z in `0..4096`. `maxXY` = 256 for typed text
  on a Linden grid (`GridSession.IsLindenLabUri`), 4096 elsewhere (OpenSim var-regions) and 4096 for landmark-derived values.

### Typed region (`LoginStart.TryParseTyped`)
Accepts exactly these forms (region percent-decoded; whitespace trimmed; a leading `/` ignored; one trailing `/` ignored):
`Region`, `Region/x/y`, `Region/x/y/z`, `secondlife://Region/x/y/z`, `http(s)://<host>/(secondlife|region)/Region/x/y/z`,
and `uri:Region&x&y&z`. Coordinates may be decimals (rounded). Missing coordinates -> `128, 128, 0` (the viewer's "middle of the
region", `llslurl.cpp:318`). More than four path segments, one lone coordinate, non-numeric coordinates, out-of-range values, an
empty region or `&` in the name -> invalid. The grid part of a pasted SLURL is ignored: the region is looked up on the grid selected
in the profile. Hypergrid addresses (`host:port:Region`) are out of scope.

### OpenSim compatibility
- `last` and `home` are standard; they have no map/landmark dependency.
- `uri:Region&x&y&z` is the form OpenSim's login service parses (a regex over `uri:<region>&<x>&<y>&<z>` with decimal digits only -
  hence **integers**). Reference behaviour from memory (OpenSim source not vendored); confirm on the local grid.
- Favorites resolve through the calls the teleport fallback uses (`RegionHandleRequest`, map block request); a landmark to a
  Hypergrid destination will not resolve and is simply not offered.
- **The Favorites system folder may be missing on OpenSim** (depends on server version and account age; from memory). Phase 2 handles
  that without inventing a folder, see "Mode".
- Region names on OpenSim may contain characters SL forbids; only `&` is refused by the builder.

### When the grid does not honour the start: notice and ONE teleport attempt
**Detect** (once per login, on the first arrival - the `_pendingArrivalToast` drain, `Boot.cs:2971-2979`; needs the new
`LoginResult.StartLocation` / `FirstLogin`, copied from `response.StartLocation` / `response.FirstLogin` at
`GridSession.Session.cs:828-830`):
1. Not the first login (`FirstLogin == false`; the viewer suppresses its alert on a first login, `llstartup.cpp:2348`).
2. The grid's `start_location` does not match what was asked, using the viewer's table (`llstartup.cpp:2354-2356`): asked
   Favorite/Region -> expect `"url"`; asked Last -> `"last"`; asked Home -> `"home"`. If the reply carries no `start_location`
   (OpenSim may send none), fall back to comparing the requested region name with `GridSession.CurrentRegionName`
   (case-insensitive) - only for Favorite/Region.
3. For Favorite/Region only: the viewer's own position test from `dispatchURL` (`llstartup.cpp:3397-3405`): different region name,
   or |dx| > 2 m or |dy| > 2 m (x/y only). Same region within 2 m is not reported and not retried (the grid may adjust height,
   landing point, ground).

**Notice:** toast + system notification (viewer parity, three variants): `ui.login.start_fallback_place` /
`start_fallback_home` / `start_fallback_last`, naming the region the grid chose. For a place the notice ends with
`ui.login.start_fallback_retry` when a retry will follow.

**One teleport attempt** - only for Favorite/Region (a failed home or last cannot be fixed by teleporting to them), only when the
detection above holds:
1. Wait until the loading screen has cleared (`_waitingForWorldLoad` false) plus ~3 s.
2. Set `_startRetryDone = true` **before** starting (one attempt, never a loop, even if it throws or fails).
3. Abort if any teleport is in progress or was seen since login (`_teleportActive`, a `TeleportProgress` event), if the session ended,
   or if the user has already left the arrival region.
4. `GridSession.ResolveRegionByNameAsync(requested.Region)`; `null` (region unknown/down) -> stop quietly, the notice already
   said so. Otherwise `GridSession.TeleportToAsync(handle, new Vector3(x, y, z))` (`GridSession.Session.cs:421`; it has its own
   re-entrancy guard).
5. The outcome is the normal teleport UI (FEAT-UI-18 overlay and failure text); nothing extra, no second attempt.

### Phase 2: the Favorites bar becomes a view of the SL Favorites folder

**Why a phase of its own.** It changes what an existing, user-visible, verified feature (FEAT-UI-67) is, and it is the only part
that **writes to the user's inventory**. Phase 1 (the login dropdown) only reads the folder and ships first.

**One service, three consumers.** `app/scripts/FavoritesService.cs` (no Godot nodes) owns the list and notifies; `FavoritesBar`, the
login-cache writer (`LoginStartFavorites`) and `LandmarkDedupWindow` (`favIds`, `Boot.cs:4577-4580`) all read it. Its awaits do
**not** use `ConfigureAwait(false)` so continuations run on the Godot main thread (the resolver is the opposite and publishes
snapshots).

**Net API (`GridSession.Favorites.cs`, new partial):**
- `Guid? FavoritesFolderId` - **strict**: the folder under the root whose `PreferredType == FolderType.Favorites`, else `null`. Never
  `FindFolderForType` (root fallback). The decision itself is a pure function in Core,
  `FavoritesFolderResolver.Resolve(Guid rootId, IEnumerable<(Guid Id, int PreferredType)> rootChildren)`, so it is unit-testable
  without a LibreMetaverse store. Never create the folder (the viewer never does; `CreateFolder(parent, name, FolderType.X)` would
  de-dupe on type and `GridSession.Inventory.cs:3673` documents the trap).
- `Task<FavoritesSnapshot> GetFavoritesAsync(CancellationToken)` - fetches the folder and its subfolders like the viewer's
  `collectDescendentsIf` (use `FetchInventoryChildrenAsync`, capped like `MaxLandmarkFolderFetches`), returns landmark **items**
  (`!IsLink`, `InventoryType == Landmark`, non-zero `ResolvedAssetID`, not in Trash) as `LandmarkInventoryItem`
  (`Id, AssetId, Name, CreationDate`) plus `Confirmed` (every fetched folder has `NeedsUpdate == false`).
- `Task<bool> AddFavoriteAsync(Guid landmarkItemId, CancellationToken)` - `CopyItemAsync(itemId, favoritesFolderId, "")` (a
  **copy**, as the viewer does, `llfavoritesbar.cpp:692-698`). Refused client-side, before any write: item already in the Favorites
  folder (viewer `:567-572`), no Copy permission (the copy would fail on the server, and a *move* would take the user's only copy out
  of Landmarks - the viewer never does that), asset already a favorite (stricter than the viewer, matches the old SLNG list),
  Favorites folder missing. The returned bool is **not** the truth (see `CopyItemAsync` doc, `:986-1005`): confirm by refetching.
- `Task RemoveFavoriteAsync(Guid itemId)` - `MoveToTrashAsync` (UDP `MoveInventoryItem` with restamp - what the viewer's
  `removeItem` does).
- Adding the **current location** = `CreateLandmarkHereAsync(name, "", favoritesFolderId)` (the existing method takes any folder).

**Core (`SLNG.Core.Landmarks`):** `FavoritesOrder.Sort(IEnumerable<LandmarkInventoryItem>, IReadOnlyList<Guid> userOrder)` - a port of
`LLFavoritesSort` (`llfavoritesbar.cpp:376-401`): indexed ascending by position in `userOrder`; **unindexed first**, newer
`CreationDate` first, ties by name (case-insensitive); plus `WithAdded`, `Pruned`. `needToSaveItemsOrder` is ported too: when the shown
list has an item that is not in `userOrder`, the full shown order is persisted (`LandmarkFavoritesStore.SaveOrder`).

**Order storage:** new key `order` (JSON array of item ids) in the existing `favorites_bar_{grid}_{agentId}` section of
`preferences.cfg`; the legacy key `data` stays untouched. Local to SLNG, like the viewer's `landmarks_sorting.xml` - other viewers do
not see it (and SLNG does not see theirs). A reorder-by-drag is **not** added (the current bar has none either); new favorites
are appended to `order`.

**Mode (what if there is no Favorites folder, or it cannot be read):**
- `Folder` - `FavoritesFolderId != null` and the fetch is `Confirmed`. The normal case.
- `Local` - the grid has **no** Favorites folder: the bar keeps working on the legacy local list exactly as today, and shows a one-line
  hint (`ui.favorites_bar.no_folder`). The login cache is then fed from that local list too (it has item/asset ids). This is the
  OpenSim safety net; it avoids a regression for accounts without the folder and needs no inventory write.
- `Unavailable` - the folder exists but the fetch was refused/unconfirmed: the bar shows a "could not load" state with one retry on
  the next refresh; it never writes the login cache and never shows the legacy list as if it were the folder.

**Migration of the existing SLNG list (decision: offer once, then copy; keep the old data).**
Options were: (a) silently drop it, (b) copy it into the SL folder automatically, (c) ask once. **Recommendation and spec: (c).**
- (a) loses the user's curated bar with no way back. (b) writes N items into a *shared* inventory folder without being asked - if the
  user also uses Firestorm those N places suddenly appear there. (c) is honest, reversible and costs one dialog.
- Trigger, once per account, after login and after the Favorites folder is `Confirmed`: the legacy `data` list is non-empty, the
  account section has no `migration` key, mode is `Folder`.
- A `ConfirmWindow` (`app/scripts/UI/ConfirmWindow.cs`; confirm label **Copy**, cancel label **Not now**; dismissing = cancel):
  "Your Favorites bar now shows the Favorites folder of your inventory, shared with other viewers such as Firestorm. Copy your {0}
  old favorites into it?"
- **Copy:** for each legacy entry in order - find the landmark item in the store by `ItemId`, else by `AssetId`; skip it if it is gone,
  not copyable, or its asset id is already in the Favorites folder; otherwise `AddFavoriteAsync`, sequentially ~200 ms apart. After
  ~5 s refetch the folder and toast `ui.favorites_bar.migrate_done` ("{0} of {1} favorites copied") with the **real** numbers from the
  refetch. Seed `order` from the legacy order (matched by asset id). Set `migration=done`.
- **Not now:** set `migration=declined`; never ask again. The bar's context menu keeps an "Import old favorites..." entry
  (visible while `data` exists and `migration != done`) that runs the same flow.
- The legacy `data` is **never deleted**: an older build still works, and the user can import later.

**TPV / inventory-write implications.**
- The only writes are the ones the reference viewer makes for favorites: copy an item into the Favorites folder
  (`CopyInventoryItem`, or AIS COPY on SL), move an item to Trash (`MoveInventoryItem`), create a landmark item
  (`CreateInventoryItem`). No asset is uploaded or altered, no permission is changed. Landmark assets are only *read* (own
  inventory) to resolve region and position for the login cache.
- AGENTS.md non-negotiable 1: no-copy landmarks are never copied or moved into Favorites (refused with
  `ui.favorites_bar.not_copyable`); nothing here weakens asset protection.
- Known LibreMetaverse traps for this work (each already cost a bug here):
  - `InventoryManager.MoveItem`/`MoveFolder` prefer AIS and PATCH `parent_id`, which **Second Life answers 400**: use
    `GridSession.MoveToTrashAsync`/`MoveInventoryAsync` (UDP), never the LMV convenience call (memory `lmv-move-prefers-ais-sl-400s`).
  - `CreateLinkAsync` signals an AIS refusal by returning **null**, not by throwing (memory `createlinkasync-returns-null-on-refusal`).
    Favorites are copies, not links, so it should not be needed; if a link is ever created, read the return value, and create before
    you delete anything.
  - `CopyItemAsync`'s `true/false` is not proof (`GridSession.Inventory.cs:986-1005`): confirm by refetching the folder and report the
    real count.
  - `FindFolderForType` returns the **root** when the type is absent (verified in 3.1.6): use the strict accessor.
  - A refused folder fetch reads as empty and a restored cache returns an unfetched folder as empty: trust a folder only with
    `NeedsUpdate == false` (memories `lmv-folder-fetch-refusal-reads-as-empty`, `lmv-inventory-cache-trusts-unfetched-folders`).
  - Copies keep the item's **owner** id in the request (`CopyItemAsync` already does, viewer `copy_inventory_item` too).
- `GetLandmarksWithFoldersAsync` walks the whole tree, so the Favorites folder's copies would show up as "duplicates" of their
  originals in the cleaner. The cleaner must **exclude the Favorites subtree from its candidates** and treat the favorites' asset ids
  as "keep" hints (it already prefers favorites, `LandmarkDuplicateDetector.cs:99-112`). The Landmarks window may list the
  Favorites folder as its own group.
- Rename of a favorite and drag-reorder are not part of this ticket.

### Non-goals
- Hypergrid destinations, `hop://` links, grid-qualified SLURLs.
- A "show favorites on login" preference (the viewer has one; here the data is local like the saved password and "Save Login" is
  the switch).
- Command-line `--start <place>`; a global default start location; renaming or drag-reordering favorites; teleporting for a
  failed Home/Last; more than one teleport attempt.
- A keyboard shortcut (no Second Life shortcut exists for this, so `SlShortcutGaps` is untouched).

## Acceptance Criteria

### Phase 1 (commits A, B, C): the login dropdown
- [ ] The login form shows **Start at** directly under the profile dropdown: My last location (default), My home, Favorites (if any),
      Region....
- [ ] Profile/first/last/grid changes reload the dropdown for that profile's saved choice and favorites; an unsaved or unknown
      account shows only Last / Home / Region....
- [ ] The choice is sent as `start`: `last`, `home`, or `uri:Region&x&y&z` (unit test + wire test).
- [ ] The choice is remembered per saved profile; unticking "Save Login" erases it together with the favorites cache.
- [ ] After a session the profile has a favorites cache built from the SL **Favorites folder** (name, region, x, y, z); unresolved
      entries are omitted; an unconfirmed folder read never changes the cache.
- [ ] The cache is written from the main thread only; the login screen does no network and no asset access.
- [ ] A remembered favorite that left the cache falls back to My last location (logged only).
- [ ] Typed region: valid forms accepted, invalid forms keep `Login` disabled with a visible hint; `Enter` in the field logs in.
- [ ] If the grid does not honour a Favorite/Region start: toast + notification naming both places, then exactly one teleport attempt;
      for Last/Home only the notice. Never on a first login, never twice per login.
- [ ] `--selftest` passes, `CheckUserDataUntouched` too (new checks use in-memory `ConfigFile`s only); both locale files carry every key.
- [ ] `AppVersion` patch-bumped per user-visible commit; German `BENUTZERHANDBUCH.md` updated; ROADMAP row updated.

### Phase 2 (commit D): the bar is the SL Favorites folder
- [ ] With a Favorites folder, the bar shows exactly its landmark items in `FavoritesOrder` order; an item added in another viewer
      appears without a relog being needed beyond the normal refresh; items in Trash do not.
- [ ] Add (inventory menu, Landmarks window, drag, "Add current location") copies / creates in the Favorites folder; Remove moves to
      Trash; no-copy landmarks are refused with a message; duplicates by asset are refused.
- [ ] No Favorites folder -> `Local` mode (today's behaviour) with a hint; refused fetch -> `Unavailable`, login cache untouched.
- [ ] One-time migration dialog (Copy / Not now), real counts reported, legacy data kept, "Import old favorites..." available.
- [ ] The duplicate cleaner no longer proposes favorites' copies as duplicates.
- [ ] The login dropdown and the bar show the same places in the same order.

## Technical Specs & Affected Files

### New - `src/SLNG.Core/Login/` (namespace `SLNG.Core.Login`, no Godot, no LibreMetaverse)
- `StartTarget.cs` - `sealed record StartTarget(string Region, int X, int Y, int Z)`;
  `static bool TryCreate(string? region, double x, double y, double z, int maxXY, out StartTarget target)`; `string ToLoginString()`.
- `StartChoice.cs` - `enum StartKind { Last, Home, Favorite, Region }`; `readonly record struct StartChoice(StartKind Kind, string Key)`
  with factories `Last`, `Home`, `Favorite(Guid)`, `Region(string)`; `Serialize()`; `static Parse(string?)` (anything unreadable -> `Last`).
- `LoginFavorite.cs` - `sealed record LoginFavorite(Guid AssetId, Guid ItemId, string Name, StartTarget Target)`.
- `LoginFavoriteBook.cs` - `ToJson`, tolerant `FromJson` (bad JSON/version -> empty; bad entry skipped; cap 64; duplicate asset
  ids collapse, first wins); `Reconcile(IReadOnlyList<LandmarkInventoryItem> favorites, IReadOnlyList<LoginFavorite> known)` ->
  `ReconcileResult(Favorites, Unresolved)`: favorites order and names win, known targets are reused, entries without a known target or
  with an empty `AssetId` go to `Unresolved` and are not in `Favorites`.
- `LoginStart.cs` - `Resolve(StartChoice, IReadOnlyList<LoginFavorite>, int maxXY)` -> `StartResolution(LoginString, Effective, Fallback,
  Target)`; `enum StartFallback { None, FavoriteMissing, RegionInvalid }`; `TryParseTyped(string?, int maxXY, out StartTarget)`;
  `static bool HonoursStart(StartKind asked, string? replyStartLocation)` (the viewer's table) and `static bool NeedsMove(...)` (the 2 m rule).

### New - `src/SLNG.Core/Landmarks/` (phase 2)
- `FavoritesOrder.cs` - `Sort`, `WithAdded`, `Pruned`. `FavoritesFolderResolver.cs` - the strict folder decision.

### New / edited - `src/SLNG.Net/`
- `GridSession.LandmarkTarget.cs` (partial, phase 1) - `ResolveLandmarkTargetAsync(Guid, CancellationToken)` -> `StartTarget?`.
- `GridSession.Favorites.cs` (partial) - phase 1: `FavoritesFolderId`, `GetFavoritesAsync` (read-only); phase 2: `AddFavoriteAsync`,
  `RemoveFavoriteAsync`. `FavoritesSnapshot(bool Confirmed, IReadOnlyList<LandmarkInventoryItem> Items)` in Core.
- `LoginResult.cs` - `StartLocation` (string?) and `FirstLogin` (bool) on `Ok(...)` (optional params, `PrintMembers` updated);
  `GridSession.Session.cs:829` passes `response.StartLocation` / `response.FirstLogin`.
- `LoginCredentials.cs` - only the doc comment of `StartLocation` (canonical `last` / `home` / `uri:...`; free text not accepted).
- `GridSession.Session.cs:1159` - no change. `GridSession.Inventory.cs` - `GetLandmarksWithFoldersAsync` gets an exclude-folder
  parameter (phase 2).

### New - `app/`
- `app/scripts/LoginStartStore.cs` - `internal static`, same shape as `MfaHashStore` (a `ConfigFile` it is given, never a path):
  `ReadChoice`, `WriteChoice`, `ReadFavorites`, `WriteFavorites` (returns whether the file changed), `Erase` (no-op when the section or
  key is missing - erasing a missing key is an error in Godot).
- `app/scripts/UI/LoginStartPicker.cs` - `partial class LoginStartPicker : VBoxContainer`: `SetFavorites(IReadOnlyList<LoginFavorite>,
  bool neverPopulated)`, `SetChoice`, `Choice`, `IsValid`, `Resolve(int maxXY)`, events `ChoiceChanged`, `Submitted`, `ApplyStylesFrom(OptionButton)`.
- `app/scripts/LoginStartFavorites.cs` - the background resolver (`Poke()`, `TryTakeUpdate(out ...)`).
- `app/scripts/SelfTest.LoginStart.cs` - `CheckLoginStartStore()`, `CheckLoginStartPicker(SceneTree)` (+ phase 2 `CheckFavoritesStoreOrder()`).
- Phase 2: `app/scripts/FavoritesService.cs`; `LandmarkFavoritesStore` gets `LoadOrder/SaveOrder` (+ `ConfigFile` variants);
  `FavoritesBar.cs` reads the service instead of `LandmarkFavoritesList` (keeps the list for `Local` mode); `InventoryPanel`/`LandmarksWindow`/`Boot`
  add hooks call `FavoritesService.AddAsync`; `LandmarkDedupWindow` gets its keep-ids from the service.

### Edited - `app/scripts/Boot.cs`
- Fields `_startPicker`, `_loginStartFavorites`, `_sessionStartChoice`, `_sessionStartTarget`, `_sessionStartCacheKey`, `_startRetryDone`.
- `_Ready`: create/insert the picker after `_profileDropdown` (before `LoadProfiles()` at `:670`).
- `RefreshStartPicker()` from `OnProfileSelected` (`:3654`), from the first/last/grid `TextChanged` handlers (`:615-617`), and from the
  `else` branch of `if (quitProcess)` in `QuitGracefully` (login screen shown again).
- `OnLoginPressed`: read the picker **before** `_loginButton.Disabled = true` (`:3965`) and before any `await`; set `StartLocation` in the
  `LoginCredentials` initializer (`:4322-4335`).
- Login-success block (`:4376-4402`): persist the choice (ticked) / erase `start` + `start_favorites` (unticked); set `_sessionStartCacheKey`.
- Arrival hook (`:2971-2979`): fallback detection, notice, one teleport. Resolver start/drain/stop as described.
- `ProfileKey(first, last, grid)` overload, with `ProfileKey(creds)` calling it, so the key has one spelling.
- Phase 2: `FavoritesBar.Initialize` call (`:4572`) becomes `FavoritesService.InitializeAsync`; migration trigger.
- `AppVersion` (`:435`).

### Docs, i18n, tests
- `app/i18n/en-US.json`, `app/i18n/de-DE.json`; `docs/BENUTZERHANDBUCH.md`; `docs/ROADMAP.md` (FEAT-UI-72 and a note on FEAT-UI-67); this file.
- Tests: `tests/SLNG.Core.Tests/LoginStartTests.cs`, `StartChoiceTests.cs`, `LoginFavoriteBookTests.cs`, (phase 2) `FavoritesOrderTests.cs`,
  `FavoritesFolderResolverTests.cs`; `tests/SLNG.Net.Tests/LoginStartLocationTests.cs`, `LoginResultStartLocationTests.cs`.

### i18n keys
Under `ui.login` (nested JSON like the existing block). German app strings use "du" (see `ui.topmenu.balance_tooltip`); keep the file UTF-8.

| Key | en-US | de-DE |
|---|---|---|
| `start_location` | Start at | Starten bei |
| `start_last` | My last location | Mein letzter Standort |
| `start_home` | My home | Mein Zuhause |
| `start_favorites_heading` | Favorites | Favoriten |
| `start_favorites_pending` | Favorites appear here after your next login | Favoriten erscheinen hier nach dem nächsten Login |
| `start_favorite_tooltip` | {0} - {1} ({2}, {3}, {4}) | {0} - {1} ({2}, {3}, {4}) |
| `start_region` | Region... | Region eingeben ... |
| `start_region_placeholder` | Region, Region/x/y/z or SLURL | Region, Region/x/y/z oder SLURL |
| `start_region_invalid` | Not a valid location | Kein gültiger Ort |
| `start_fallback_place` | Could not start at {0}. The grid placed you in {1} instead. | Start bei {0} war nicht möglich. Das Grid hat dich stattdessen in {1} angemeldet. |
| `start_fallback_home` | Your home is not available. The grid placed you in {0} instead. | Dein Zuhause ist nicht verfügbar. Das Grid hat dich stattdessen in {0} angemeldet. |
| `start_fallback_last` | Your last location is not available. The grid placed you in {0} instead. | Dein letzter Standort ist nicht verfügbar. Das Grid hat dich stattdessen in {0} angemeldet. |
| `start_fallback_retry` | Trying once to teleport you to {0}. | Es wird einmal versucht, dich nach {0} zu teleportieren. |

Phase 2, under `ui.favorites_bar` (existing keys such as `added_toast`, `removed_toast`, `remove` are reused, `removed_toast`
now means "moved to Trash"): `no_folder` ("This grid has no Favorites folder - showing your local list" / "Dieses Grid hat keinen
Favoriten-Ordner - es wird deine lokale Liste angezeigt"), `unavailable` ("Favorites could not be loaded" / "Favoriten konnten nicht
geladen werden"), `not_copyable` ("\"{0}\" cannot be copied, so it cannot be a favorite." / "\"{0}\" lässt sich nicht kopieren und kann
deshalb kein Favorit sein."), `already` ("\"{0}\" is already a favorite." / "\"{0}\" ist schon ein Favorit."), `migrate_title`
("Copy your favorites" / "Favoriten übernehmen"), `migrate_text` (text above, `{0}` = count / "Deine Favoritenleiste zeigt jetzt den Favoriten-Ordner
deines Inventars, den auch andere Viewer wie Firestorm benutzen. Deine {0} bisherigen Favoriten dorthin kopieren?"), `migrate_copy`
("Copy" / "Kopieren"), `migrate_later` ("Not now" / "Jetzt nicht"), `migrate_done` ("{0} of {1} favorites copied." / "{0} von {1}
Favoriten kopiert."), `import_old` ("Import old favorites..." / "Alte Favoriten übernehmen ...").

### `BENUTZERHANDBUCH.md` lines (German)
Section "2. Login", after the bullet "Der Viewer merkt sich Ihre Login-Profile ...":
> - **Startort wählen:** Direkt unter der Profilauswahl legen Sie fest, wo Sie nach dem Login ankommen: **Mein letzter Standort**, **Mein Zuhause**, einer Ihrer **Favoriten** oder eine **Region**, die Sie selbst eintippen (`Regionsname`, `Regionsname/x/y/z` oder ein SLURL). Die Auswahl wird pro gespeichertem Login-Profil gemerkt. Favoriten sind die Orte in Ihrem Inventar-Ordner „Favoriten“ (derselbe Ordner wie bei Firestorm). Weil der Viewer vor dem Login nicht in Ihr Inventar schauen kann, merkt er sich beim Spielen Region und Position jedes Favoriten; sie stehen in der Liste, sobald Sie mit diesem Profil einmal angemeldet waren und „Save Login“ angehakt war. Kann das Grid Sie nicht am gewünschten Ort anmelden (zum Beispiel, weil die Region offline ist), entscheidet das Grid, wo Sie landen; der Viewer sagt Ihnen, wo das war, und versucht bei einem Favoriten oder einer getippten Region **einmal**, Sie dorthin zu teleportieren. (ab Version vX.Y.Z-alpha)

The section for the Favorites bar (search the manual for „Favoritenleiste“; add if absent) — phase 2:
> - **Favoritenleiste:** Die Leiste unter der Menüleiste zeigt die Orte aus Ihrem Inventar-Ordner „Favoriten“, also dieselben wie in Firestorm. Ein Ort wird als **Kopie** in diesen Ordner gelegt (das Original bleibt in „Landmarken“); „Aus Favoriten entfernen“ legt die Kopie in den Papierkorb. Orte, die sich nicht kopieren lassen, können keine Favoriten werden. Die Reihenfolge merkt sich nur dieser Viewer. Beim ersten Start nach dem Update fragt der Viewer einmal, ob Ihre bisherigen Favoriten in den Ordner kopiert werden sollen; Ihre alte Liste bleibt dabei erhalten. Hat ein Grid keinen Favoriten-Ordner, bleibt die bisherige lokale Liste in Gebrauch. (ab Version vX.Y.Z-alpha)

`vX.Y.Z-alpha` = the `AppVersion` that commit ships, read from `Boot.cs` at that moment. Leave the manual's header version line alone unless the
whole manual is being synced (it says v0.27.71-alpha).

## Implementation plan (for the implementer)

Branch (in place, no worktree): `feature/FEAT-UI-72-login-start-location`. Set `Owner` = `claude`, status `🚧` in `docs/ROADMAP.md` in the
first commit. **Read first:** `AGENTS.md`, `.claude/skills/app-rules/SKILL.md`, `.claude/skills/src-rules/SKILL.md`,
`.claude/skills/tests-rules/SKILL.md`. If bare `dotnet` says no SDK: `. tools/dev-env.ps1`. Build both: `dotnet build SLNG.sln` and
`dotnet build app/SLNG.App.csproj`. Phase 1 ships first; phase 2 (commit D) may follow in its own branch/PR.

**Commit A - Core model, test-first (nothing user-visible: no `AppVersion` bump, no version suffix in the message)**
1. Failing tests first in `tests/SLNG.Core.Tests/` (tables below), then `src/SLNG.Core/Login/*` until green. Include `FavoritesOrder` and
   `FavoritesFolderResolver` (phase 2 code, but pure and cheap now; they also give phase 1 its default order).
2. `dotnet test tests/SLNG.Core.Tests`, `dotnet format SLNG.sln`.
3. Commit: `feat(core): [FEAT-UI-72] start-location model, typed-region parser, favorites book and ordering`.

**Commit B - login dropdown with Last / Home / Favorites (user-visible: bump `AppVersion`)**
4. `LoginResult.StartLocation/FirstLogin` + `GridSession.Session.cs:829`. `GridSession.Favorites.cs` read-only part
   (`FavoritesFolderId` strict via `FavoritesFolderResolver`, `GetFavoritesAsync` with `Confirmed`). `GridSession.LandmarkTarget.cs`
   (copy the asset/handle steps from `GridSession.Session.cs:946-971`; reuse `ResolveRegionHandleAsync` and `ResolveRegionByHandleAsync`).
5. `LoginStartStore` (+ `CheckLoginStartStore`), `LoginStartPicker` (+ `CheckLoginStartPicker`; the `Region...` entry is present but disabled
   until commit C), `LoginStartFavorites`.
6. Net tests `tests/SLNG.Net.Tests/LoginStartLocationTests.cs`: (a) `BuildLoginParams` keeps `uri:Ahern Park&10&20&30` byte-identical and still
   maps null/empty to `last` (extends `LoginTermsGateTests.cs:77-83`); (b) a `LoginResult.Ok` carrying `StartLocation`/`FirstLogin`;
   (c) **optional wire test**: a loopback `HttpListener` on `127.0.0.1` as `GridLoginUri` captures the LLSD-XML POST of
   `GridSession.LoginAsync`; parse it and assert the decoded `start` value is exactly `uri:Ahern Park&10&20&30`. The pass-through itself is
   already proven by the 3.1.6 decompile; this test only proves the escaping. Time-box 30 min (the repo has a precedent fake grid:
   `tests/SLNG.Net.Tests/FakeInventoryCaps.cs`); if it needs more than a single reply, drop it and say so in the commit message. **Never** point a
   test at the live Second Life grid.
7. Wire into `Boot.cs` as listed. Details that bite:
   - Read the picker **synchronously at the top of `OnLoginPressed`**, before `_loginButton.Disabled = true` and any `await`; an invalid typed
     region -> `LogMessage(...)` and return without disabling the button.
   - `_sessionStartCacheKey`: set in the login-success block to `profileName` **only when `_saveLoginCheck.ButtonPressed`**, else `""`. Use it (not
     `_sessionFirstName`/`_sessionLastName`, which are trimmed while `ProfileKey(creds)` uses the raw field text) for every later cache write. In the
     unticked branch also erase `start` and `start_favorites`.
   - `RefreshStartPicker()` must tolerate an empty `_loginsConfig` (selftest skips `LoadProfiles`) and must never write.
   - The picker's `Submitted` calls `OnLoginPressed()` only if `!_loginButton.Disabled`.
   - Log the single `[LoginStart] could not resolve ...` line per unresolved landmark and nothing chattier.
8. i18n keys, `AppVersion` patch bump (read the value in `Boot.cs:435` and increment its last component - never copy a number from this spec),
   `BENUTZERHANDBUCH.md` login line.
9. `/slng-verify` + manual rounds 1-4 below. Commit: `feat(ui): [FEAT-UI-72] choose last location, home or a favorite at login (v0.x.y-alpha)`.

**Commit C - typed region + landing notice + one teleport attempt (bump `AppVersion` again)**
10. Enable `Region...`, `TryParseTyped` wiring, `Login` disabled while invalid, `ui.login.start_region*` keys.
11. Detection + notice + the single teleport attempt exactly as specified (`_startRetryDone` set before starting). Core helpers `HonoursStart`/`NeedsMove` are
    unit-tested in A. Manual rounds 5-7.
12. Commit: `feat(ui): [FEAT-UI-72] typed start region, landing notice and one teleport attempt (v0.x.y-alpha)`.

**Commit D - Phase 2: the bar is the SL Favorites folder (own branch/PR allowed; bump `AppVersion`)**
13. `GridSession.Favorites.cs` write part (`AddFavoriteAsync`, `RemoveFavoriteAsync`, refusal rules), `GetLandmarksWithFoldersAsync` exclude parameter.
14. `FavoritesService` (modes `Folder` / `Local` / `Unavailable`, order via `LandmarkFavoritesStore.LoadOrder/SaveOrder`), `FavoritesBar` on the service,
    the three add hooks, `LandmarkDedupWindow` keep-ids, login cache fed from the service, `Changed` replaces the folder-event trigger from commit B.
15. Migration dialog (`ConfirmWindow`), "Import old favorites..." menu entry, i18n, handbook paragraph, `AppVersion`.
16. Manual rounds 8-12. Commit: `feat(ui): [FEAT-UI-72] Favorites bar shows the SL Favorites folder (v0.x.y-alpha)`.

### Test tables (xUnit, `SLNG.Core.Tests`)
`LoginStart.TryParseTyped(text, maxXY: 256)` (expected = `ToLoginString()` or `invalid`):

| Input | Expected |
|---|---|
| `Ahern` | `uri:Ahern&128&128&0` |
| `Ahern/10/20` | `uri:Ahern&10&20&0` |
| `Ahern/10/20/30` | `uri:Ahern&10&20&30` |
| `Ahern Park/10/20/30` | `uri:Ahern Park&10&20&30` |
| `Ahern%20Park/10/20/30` | `uri:Ahern Park&10&20&30` |
| `/Ahern/1/2/3` and `  Ahern/1/2/3  ` | `uri:Ahern&1&2&3` |
| `secondlife://Ahern%20Park/10/20/30/` | `uri:Ahern Park&10&20&30` |
| `https://maps.secondlife.com/secondlife/Ahern%20Park/10/20/30` | `uri:Ahern Park&10&20&30` |
| `uri:Ahern&1&2&3` | `uri:Ahern&1&2&3` |
| `Ahern/10.6/20.4/30.5` | `uri:Ahern&11&20&31` |
| `Ahern/256/256/4096` | valid; `Ahern/257/0/0` invalid at 256, valid at `maxXY: 4096` |
| `Ahern/10` (one coordinate), `Ahern/-1/0/0`, `Ahern/abc/1/2`, `Ahern/1/2/3/4`, `A&B/1/2/3`, `""`, `"   "`, `Ahern/NaN/1/2` | invalid |
| 256-character region name | invalid; 255 valid |
| `Ahern\u0007Park/1/2/3` (control char) | `uri:AhernPark&1&2&3` |

`LoginStart.Resolve`: Last -> `last`; Home -> `home`; Favorite present -> `uri:...`; Favorite absent -> `last` + `FavoriteMissing`; Region
valid/invalid -> `uri:...` / `last` + `RegionInvalid`. `HonoursStart(asked, reply)`: Favorite/Region + `"url"` true, + `"home"`/`"last"`/`"safe"` false; Last +
`"last"` true; Home + `"home"` true; empty reply -> unknown (caller falls back to the region-name comparison). `NeedsMove`: same region within 2 m -> false;
different region -> true; same region, dx = 2.1 -> true; only z different -> false.
`StartChoice`: `Serialize`/`Parse` round-trip for all kinds; `Parse(null)`, `""`, `"garbage"`, `"fav:not-a-guid"`, `"region:"` -> `Last`; a region containing `:` survives.
`LoginFavoriteBook`: JSON round-trip; non-JSON, `{"v":2}`, entry without region, 70 entries -> 64, duplicate asset ids collapse; `Reconcile`: folder order wins,
a renamed item keeps its old target under the new name, a removed item disappears, a new item lands in `Unresolved`, an item with `AssetId == Guid.Empty` lands in
`Unresolved`, nothing with an unknown target is in `Favorites`.
`FavoritesOrder.Sort`: all indexed -> by index; an unindexed item sorts **before** indexed ones; two unindexed -> newer `CreationDate` first, equal dates by name
case-insensitively; `userOrder` entries that no longer exist are ignored; `Pruned` drops them; `WithAdded` appends once.
`FavoritesFolderResolver.Resolve`: root children contain preferred type 23 -> its id; none -> `null` (**not** the root id); a type-23 folder deeper than the
root's children -> `null`; two type-23 folders -> the first.

### Selftest (`--selftest`, headless; must not write `user://`)
- `CheckLoginStartStore`: on an in-memory `ConfigFile`: write/read a choice and a favorites list; `Erase` removes both keys and is a no-op on a missing section;
  hostile values (wrong Variant type, 1 MB string, non-JSON) read as `Last`/empty.
- `CheckLoginStartPicker(tree)`: build a `LoginStartPicker`, add to `tree.Root`, `SetFavorites` with two entries: options are Last, Home, heading, fav 1, fav 2,
  separator, Region...; selecting fav 2 gives `Resolve(256).LoginString == "uri:<region>&x&y&z"`; Region... with `"Ahern/1/2/3"` is valid, with `"nope/x"` invalid
  (`IsValid == false`, hint visible); `SetChoice` of a favorite not in the list selects Last; `neverPopulated` shows the disabled pending item. Free the node
  afterwards. Pattern: `CheckMfaPromptWindow` (`SelfTest.Mfa.cs`).
- Phase 2 `CheckFavoritesStoreOrder`: `LandmarkFavoritesStore.SaveOrder/LoadOrder` round-trip on an in-memory `ConfigFile`, and the legacy `data` key is untouched.
- Existing `CheckLoginScreenCoversTheWorld`, the locale coverage check and `CheckUserDataUntouched` must stay green. The picker adds a row to the login panel: look at it
  at the smallest supported window size.

### Manual verification (local OpenSim; the AI never uses live Second Life)
1. Fresh profile, "Save Login": Start at shows Last / Home / Region... only. In a viewer (or SLNG phase 2) put two landmarks into the Favorites folder, log in with SLNG,
   wait until the loading screen is gone, log out. The profile section has `start` and a `start_favorites` with both entries (names, region, x, y, z). Log in again:
   both are in the list, in the order of `FavoritesOrder`.
2. Pick a favorite in another region, log in: lands there; the choice is preselected next time.
3. Pick Home, then Last: lands accordingly.
4. Untick "Save Login", log in: `start` and `start_favorites` are gone from the section. With the Favorites folder fetch refused (stop the inventory cap or use the fake
   caps): `start_favorites` is unchanged after the session.
5. Type a region that does not exist: record what the grid does (default region or refusal). With a default region: notice, then one teleport attempt (which finds
   no such region and stops quietly); no second attempt after a relog-free wait of a minute.
6. Take a known region's simulator down (if the grid allows): same, plus the teleport failure message appears once.
7. Typed region equal to the home region within 2 m: no notice, no teleport.
8. (Phase 2) Add a favorite from the inventory menu, from the Landmarks window and via "Add current location": a copy appears in the Favorites folder (check in
   another viewer or the inventory panel). Remove: the copy is in Trash. A no-copy landmark is refused.
9. (Phase 2) Account without a Favorites folder: `Local` mode and hint, no write.
10. (Phase 2) Migration: legacy list with 3 items, Favorites folder empty -> dialog -> Copy -> "3 of 3 copied" after the refetch; relog: no dialog; `data` still in
    `preferences.cfg`. Decline path: no dialog next login; "Import old favorites..." works.
11. (Phase 2) The duplicate cleaner does not list a favorite and its original as duplicates.
12. Selftest log shows no new warnings; `git diff app/project.godot` is reverted if the headless run touched it (`app-rules`). On Aditi/Agni the user checks rounds 2, 3 and 5 by hand.

## Corrections and confirmations for the user
1. **Correction to an earlier statement:** the reference viewer does **not** teleport after a refused start. `dispatchURL()` exists but its only call is commented out
   (`llstartup.cpp:2571`); the viewer 26.4 shows the `AvatarMoved*` alert driven by the login reply's `start_location`. Decision 3 (notice + one teleport attempt) is
   specified as a SLNG behaviour. Say if you would rather have the viewer's behaviour (notice only).
2. **OpenSim without a Favorites folder:** phase 2 keeps today's local list as a fallback instead of creating a folder or showing an empty bar. Confirm.
3. **Migration:** offered once with a dialog (copy; old list kept; import later from the bar menu), not automatic and not dropped. Confirm.

## Sub-tasks / Progress
- [ ] Claim in ROADMAP (`Owner` `claude`, `🚧`), create the branch
- [ ] Commit A: `SLNG.Core.Login` model, `FavoritesOrder`, `FavoritesFolderResolver` + xUnit tables
- [ ] Commit B: `LoginResult` fields, `GridSession.Favorites` (read) / `LandmarkTarget`, `LoginStartStore`, `LoginStartPicker`, `LoginStartFavorites`, Boot wiring, i18n,
      `AppVersion`, handbook, Net tests, selftest checks
- [ ] Commit C: typed region, landing notice, one teleport attempt
- [ ] Commit D (phase 2): bar on the SL Favorites folder, write API, `FavoritesService`, migration, dedupe exclusion
- [ ] `/slng-verify` green, manual rounds on local OpenSim, user check on Aditi/Agni
- [ ] ROADMAP -> `🧪 Review` / `✅ Done` after the user's in-world confirmation

## Status
Spec written 2026-10-17, revised the same day with the user's three decisions. Nothing implemented. Research only: no code was changed.
