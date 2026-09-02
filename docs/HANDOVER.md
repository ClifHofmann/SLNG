# HANDOVER

**One file.** Overwrite the section below when you hand over new work; do not create
`handover-<date>-<topic>.md` alongside it.

---

# Six ways to bake nothing, and the test that could not see any of them

**State:** `v0.19.3-alpha`, **on `main`**, 2026-09-01. Solution and `app/` both build clean,
**490 tests green** (Core 173, Assets 79, Net 238), `dotnet format` clean, shader globals
consistent (27 registered), `--selftest` 26/26.

**37 commits pushed, `d568077..db71623`.** `git log --oneline d568077..db71623` reads them in
order; each carries its own reasoning and measurement, so the commit messages are the record
and this file is the map.

The session closed MVP 2, then spent most of its length on **FEAT-AVATAR-01**, which turned
out to be six independent defects stacked on top of each other. It is **not Done** — see §3,
which is the most important section here.

---

## Build and verify

`/slng-verify` covers it. The trap that matters: `dotnet build SLNG.sln` compiles **none** of
`app/`, so build both. `godot --headless` rewrites `app/project.godot` (drops
`directional_shadow/size=4096`) — revert before staging.

---

## 1. MVP 2 is closed

BUG-UI-05, FEAT-UI-18 and BUG-NET-03 all confirmed in-world. BUG-NET-03's root cause was
`Settings.Agent.MultipleSims` defaulting to false in LMV 3.1.3, not the render stack. Two
follow-ups came out of it and are also in: the 1 m terrain seam between regions (the mesh was
built only to `width-1`), and an `ObjectDisposedException` storm once neighbour sims started
streaming avatar updates. Also landed: FEAT-ENV-02's preset picker, and a teleport overlay.

## 2. FEAT-AVATAR-01 — the bake works, and here is why it did not

SLNG now composites, encodes, uploads and applies its own avatar bake, and the result is
visible without a relog. Confirmed in-world: *"das Baken funktioniert jetzt"*.

Six separate defects, none guessable from the symptom. Each is pinned by tests:

1. **CoreJ2K 2.3.3.91 has no working lossy encoder.** Every preset flattens a 512² gradient to
   ~292 bytes, and `WithBitrate`/`WithQuality` are **inert** — 244 bytes at any setting. This
   is why LibreMetaverse's `AssetTexture.Encode` produced 507-byte blanks for months. Only
   `ForLossless()` works. → `IBakeTextureEncoder` (Net) / `J2KBakeTextureEncoder` (Assets),
   wired in `Boot`. The tell was that channels with wildly different content produced
   *byte-identical* sizes.

2. **`AppearanceManager` keeps one `TextureData` per texture slot** and calls
   `DecodeWearableParams` once per wearable into the same array, so five worn Tattoo layers
   overwrote each other — four HeadTattoo textures lost, and the last one's `DEFAULT` emptied
   the slot entirely. The head baked the built-in Linden skin. `Baker` itself is fine:
   `AddTexture` appends to a flat list. The viewer keeps a texture per *(slot, wearable)* —
   `LLLocalTextureObject`. Each wearable now decodes into its own scratch array.

3. **`Baker`'s built-in layers are 512² while it composites at 1024²**, and `DrawLayer` walks
   one flat index over both — 512 source pixels laid across each 1024-pixel row, then a bounds
   check silently stopping a quarter of the way down. The head bake held the face twice,
   sheared, with a diagonal seam. → `BakeResourceLayers` rewrites them at bake size.
   **Trap:** `Baker.LoadResourceLayer` **caches**, so it cannot witness its own fix — it kept
   reporting 512×512 after the file on disk had become 4,194,322 bytes.

4. **Layer order lives in the COF link's description**, not in `GetWearables()` order:
   `build_order_string` → `@` + type*100 + index, sorted by `WearablesOrderComparator`
   (`llappearancemgr.cpp`). Index 0 is the bottom (`gatherAlphaMasks` takes `num_wearables - 1`
   as "the top wearable"). → `WearableLayerOrder`. And `WearWearableAsync` now **writes** that
   token, so anything worn in SLNG lands on top instead of at the bottom of its stack.

5. **The worn set must come from the COF.** The legacy `AgentWearablesReply` is only what the
   region was last told — a Firestorm login rewrote it from 9 wearables to 5, dropping the
   tattoo layer carrying the skin actually being worn. Duplicate COF links are de-duplicated,
   keeping the tokened one (this account had **20 links for 10 wearables**).

6. **CoreJ2K wraps its output in JP2 boxes.** Every SL texture is a raw codestream starting
   `FF 4F FF 51` — verified by parsing an asset out of SLNG's own cache (2048², 4 components,
   MCT=1). Our uploads began `00 00 00 0C 6A 50 20 20`. The grid **accepts, stores and serves**
   such an asset, and it renders flat grey in Firestorm while decoding perfectly here. →
   `WithFileFormat(false)`.

Alongside those: `AgentAppearanceParams` builds the visual-param array in wire order
(LibreMetaverse mis-orders 195 of 218 and truncates 35 — `VisualParamOrderTests`);
`AvatarAppearanceReceived` is raised locally after our own send, because the sim does not echo
one and that event is the only path carrying bake ids into the scene; and **body parts now
replace rather than layer, and cannot be taken off** (raised in review — neither rule existed,
so removing a shape was possible and wearing a second one stacked it).

Controls: `SLNG_BAKE_VERBOSE=1` for the full trace and PNG previews of every input and
composite, `SLNG_BAKE_DRY=1` to composite without touching the account, and
*Developer → Testmuster backen*, which bakes generated known-answer textures (head green,
upper blue, lower red, with a grid, a diagonal, a brightness ramp and four corner markers)
with **no inventory involved**.

## 3. FEAT-AVATAR-01 is NOT Done — start here

It was marked ✅ after one successful in-world bake and rolled back the same session, on the
user's objection: *"nur weil ich einmal das bake gesehen hab heißt nicht, dass das Feature
geht"*. That was correct. Open, in the roadmap entry too:

- **(a) The upload read-back has never been observed.** `verified WxH` has not appeared in a
  log once. This matters more than it looks: `MergeBakeSlots` fills empty slots from the
  simulator's relay, so a *silently failing upload still produces a correct-looking avatar*,
  for the wrong reason. Every successful observation so far also had a Firestorm bake on the
  account to fall back on. `v0.18.7` added the check; nobody has run it.
- **(b) Alpha wearables never reach the bake.** `[XBakes]: Number of alpha wearable textures:
  0` on **every** run. An alpha layer therefore cannot hide anything — which is the thing M4-7
  needed this task for in the first place. A real functional gap, not a test.
- **(c)** Repeatability — wear, bake, remove, bake, several times.
- **(d)** Persistence across a relog with no other viewer involved.
- **(e)** A **third party** must see the avatar correctly. Every check so far used two viewers
  on the same account, which could be reading the same cache.
- **(f)** A cold account, with no Firestorm bake as a safety net.
- **(g)** The body-part rules from §2, in-world: take-off refused, wearing replaces.

**(d) and (f) are not measurable right now** — see §5.

## 4. Inventory icons (FEAT-UI-16)

Items and folders in the inventory tree carry their type's icon; system folders (Trash,
Current Outfit, Favorites, Inbox…) are findable at a glance. One shared table,
`app/scripts/UI/InventoryIcons.cs` — the Outfits tab had its own coarser set of four, so the
same item could show a different glyph depending on the list. HUDs keep their own glyph via
`ForWornItem`, since they are Objects by asset type. The wire values it keys off joined
`AssetTypeIds` in `SLNG.Core`, alongside a new `FolderTypeIds`.

## 5. OSGrid lost inventory — reinterpret several findings

Reported late in the session: an OSGrid service bug wiped inventory, which is also why the
default avatars are gone. This weakens several conclusions above. The test skin not surviving
a relog, `legacy-not-stored` on the Bodypart create, Body Parts holding three items, 20 COF
links for 10 wearables — all consistent with grid damage rather than defects here.

What SLNG can repair: duplicate COF links (done, in *"Outfit aufräumen"*). What it cannot:
lost inventory, and the **default avatars live in the grid-owned Library**, not the user's
inventory — that is OpenSim-side. Proposed but unbuilt: generate replacement default body
parts locally, since the test skin proved SLNG can create wearables end to end.

## 6. Next: Second Life, not OpenSim

New priority, stated at the end of the session. **On SL the bake is server-side** — the whole
client-side bake of §2 is the OpenSim path and is not needed there. What *does* carry over,
and matters more on SL: the COF-based worn set, the layer-ordering tokens, the body-part
rules, the duplicate cleanup, and the whole render side (BoM channel binding, bake fetch,
local appearance refresh). Open item (a) becomes meaningless on SL; **(b) does not**.

To reach the beta grid (Aditi) without breaching the TPV Policy — read from the policy, not
from memory (<https://secondlife.com/corporate/third-party-viewers>):

- **Directory listing is not required to connect** (§6); it only matters for distribution, as
  do the disclosure duties in §1.c.
- **Already satisfied:** `Boot.cs` reports the real `AppVersion` (§1.b half), shown on the login
  screen.

**All findings so far are closed in code — `FEAT-SL-01`, `v0.20.0-alpha`, branch
`feature/FEAT-SL-01-second-life-readiness`.** Full detail in
[the spec](file:///E:/Git/SLNG/docs/specs/FEAT-SL-01-second-life-readiness.md); the short version:

- **§1.f was a live violation, not a missing feature.** LibreMetaverse's `LoginParams`
  constructor defaults `AgreeToTos` **and** `ReadCritical` to **true** — verified by reflection
  against the pinned 3.1.3 assembly — and SLNG never touched either, so every login it has ever
  sent asserted acceptance of that grid's Terms of Service, sight unseen. Both now default to
  false on `LoginCredentials` and are set only by a retry following a real acceptance in
  `TermsOfServiceWindow`, exactly as `lllogininstance.cpp` does it ("Always false here. Set true
  in handleTOSResponse").
- **A second defect fell out of it:** `LoginWithResponseAsync` returns the parsed response *only*
  on success, so every failure reached SLNG as a bare null reported as `"no-response"` — a wrong
  password and a ToS refusal were indistinguishable, and the gate could never have fired. The
  reason is now read back off `NetworkManager.LoginErrorKey`.
- **§1.g:** an About window (`SLNGWindow`), reachable from the login screen and App → About,
  carrying the same name/version/channel the grid is told.
- **Aditi** is a grid entry, listed ahead of Agni. It has **separate passwords** — the account is
  a periodic copy, and the password is whatever it was at copy time.

**A second pass over §2/§5, requested explicitly as a follow-up audit, found two more findings
that were live, not merely absent:**

- **§2.b, twice.** The inventory "Export (Full Perm)" context-menu entry was gated on
  copy/modify/transfer alone — exactly the shortcut §2.b calls out by name as not exempting
  "full permissions" content, since it does not verify the SL creator name matches the viewer
  user's own. Removed (it had no handler behind it — a dead menu item). Separately,
  `SLNG_BAKE_VERBOSE=1` wrote every DECODED wearable texture feeding a bake — other people's
  skins, tattoos, clothing — to `%TEMP%\slng_bake\*.png`, with no creator check at all. Both were
  harmless on OpenSim and invisible there; both are now refused outright on a Linden grid.
- **§1.a, pre-emptively.** The manual client-side bake ("Avatar neu backen", "Testmuster backen")
  and the test-skin generator never checked `RegionHasServerSideBaking()` — the same check the
  wearable-edit path already makes correctly. Unguarded, both would run SLNG's OpenSim (XBakes)
  composite-and-upload path against a region that already composites bakes server-side: an
  unrequested protocol departure, and for the test-skin generator specifically, real L$ upload
  fees spent on a diagnostic tool built for OpenSim. Both now refuse with a chat message on SSB.
- **§5.b.** The channel sent at login — the "viewer identifier" a sim log actually shows — was
  `"SLNG"`, which starts with "SL" and fails the trademark-fragment rule on the letter even
  though nothing about it was ever meant to imply a Linden Lab connection. Renamed to `"Puris"`,
  the viewer's real public name. `BuildInfo.Name`, the local chat-log directory name, and the LMV
  logger category are left as `"SLNG"` — none of them is grid-facing or user-facing.

**Update, same session: a login to Aditi happened and reached the world** (chat, travel/teleport
activity observed) — the first real confirmation this branch connects to a Linden grid at all.
Not separately confirmed: whether the login actually went through the ToS gate (an account
already accepted via another viewer would never see it), and whether the two
`RegionHasServerSideBaking()` refusals or the §2.b bake-dump refusal have fired for real — none
of that was exercised this session. What WAS exercised, and found broken, is covered in
`FEAT-SL-02` (Age Settings showed "grid doesn't support it" on Aditi, a stale-capability-read
bug, fixed same session) and `BUG-RENDER-01` (a fatal crash, also found live, also fixed — see
its own note below).

**Also added, requested directly: "Age settings"** — `FEAT-SL-02`, same branch, same version.
Second Life gates regions/content by a content-rating preference (General/Moderate/Adult) SLNG
never had any control for; without it an account defaults to General and cannot see
Moderate/Adult regions regardless of what it's actually verified for. Not a TPV gap, a real
missing feature. Confirmed against the reference viewer's own source: login's
`agent_access_max` is the account's verified ceiling, `agent_region_access` its current
preference, and changing it POSTs to the `UpdateAgentInformation` capability, which echoes back
what the server actually granted (it clamps an unverified account's request down, same as
`LLAgentAccess::setMaturity`). New "Age Settings" preferences tab, `GridSession.
AccountMaturityMax`/`PreferredMaturity`/`SetPreferredMaturityAsync`. **OpenSim has no
equivalent** — no `UpdateAgentInformation` capability anywhere in its source — so the tab says so
there instead of showing a control that would do nothing.

**Tested on Aditi the same session it shipped, and it was wrong:** the tab said "grid doesn't
support it" — on a grid that does. `SupportsMaturityPreference` reads the region's capability
list live, but the page was only ever refreshed once, right after login, before the capability
seed necessarily resolves (the same `SimConnected`-vs-`EventQueueRunning` race `GridSession`
already documents for the environment capabilities — never applied here). Fixed by refreshing
again every time Preferences opens, matching the codebase's existing pattern for the same class
of problem. [Spec](file:///E:/Git/SLNG/docs/specs/FEAT-SL-02-age-settings.md) has the "Found live
on Aditi" section.

**`BUG-RENDER-01`, same session, more serious: a fatal crash.** Reported mid-session on Aditi —
`System.AccessViolationException` inside `godotsharp_callable_call_deferred`, stack through
`GpuCache.FetchAndUploadTextureAsync`, taking the whole process down. This project's own
documented trap ([[godot-callable-from-not-threadsafe]]): `Callable.From(lambda).CallDeferred()`
is not safe off the main thread, and this method is reached from asset-decode worker threads,
never the main thread. **Auditing every `Callable.From(` in `app/scripts/*.cs` found the same
anti-pattern at 13 MORE sites** across `AvatarRenderer.cs` and `ObjectRenderer.cs` — bake apply,
rigged-mesh skin, HUD geometry/materials, animation apply, legacy normal/specular maps, every
glTF PBR channel. One had crashed; the other 13 were the same fuse, unlit. The fix already
existed in the codebase — `MainThreadWorkQueue`, a thread-safe budgeted queue, was built for
exactly this and sits a few dozen lines above the crashing method in the same file, already used
correctly by its neighbour (`TryUpgradeCachedTexture`) — it was just never applied to these 14
call sites. All 14 converted; no behaviour change beyond thread safety, every lambda body
untouched. **Not yet re-verified against the traffic that produced the original crash.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-01-callable-from-crash.md).

**`BUG-NET-04`, same session, third live finding: teleport left the old sim rendered.** *"nach
dem Teleport sehe ich noch die sim auf der ich gerade war also auch die objekte."* BUG-NET-03's
cleanup (grid sends `DisableSimulator` → `World.RemoveRegion`) only really works for a walking
border-crossing — the old region stays a genuine neighbor circuit and the grid decides when to
drop it. A teleport is different: `NetworkManager.Connect(..., setDefault: true, ...)` (what
fires on `TeleportFinish`) never disconnects the old `CurrentSim`, it only swaps the pointer, so
cleanup depended entirely on the ORIGINATING sim eventually sending `DisableSimulator` — not
guaranteed promptly for a teleport to a distant region. Fixed with a new `GridSession.
OnSimChanged` on LibreMetaverse's `Network.SimChanged` (fires exactly when `CurrentSim` changes,
carries the previous simulator): removes the old region EAGERLY through the same
`RegionDisconnectedReceived` path, but only when it's more than one region-grid step away — an
adjacent region is left entirely to the existing, working `DisableSimulator` path so this can't
race BUG-NET-03's own cleanup. `v0.20.0-alpha`, 12 new tests. **Not yet re-verified in-world.**
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-NET-04-teleport-leaves-old-region-rendered.md).

**`BUG-AVATAR-01`, same session, fourth live finding: "das BOM fehlt" — self avatar head/hands
rendered blank white**, screenshot confirmed. Traced to "Avatar neu backen" (Ctrl+Alt+R) being a
COMPLETE NO-OP on every grid, SL included: a 2026-08-31 hard stop refuses
`AppearanceManager.RequestSetAppearance()` unconditionally, because on the legacy (client-side
baking) path it sends whatever `MakeAppearancePacket` produces — a scrambled/all-zero bake from a
cold state, which is what broke this avatar repeatedly back then. That does not hold on a
server-side-baking region: verified by reading the pinned package's own branch
(`RequestSetAppearanceAsync` on SSB calls `UpdateAvatarAppearanceAsync`, a `{ cof_version }`
capability POST that never reaches `MakeAppearancePacket`). Fix: `RebakeAvatar()` now branches on
`RegionHasServerSideBaking()` and calls `RequestSetAppearance(forceRebake: true)` directly on SSB;
the OpenSim hard stop is untouched. **Tried in-world, same session: avatar is STILL blank after
this fix.** So the no-op was real and worth fixing, but not the (or not the only) cause. **Why it
went blank in the first place is still genuinely open** — next step, not yet tried:
`SLNG_LMV_DEBUG=1`, watching for LibreMetaverse's "Dropping stale AvatarAppearance for self" line
(a COF-version staleness guard that silently discards an inbound self `AvatarAppearance` packet
if its version isn't newer than LMV's own internal tracker — `Logger.DebugLog` only, easy to
miss). If that line never appears either, the packet may not be arriving at all, which is a
different investigation.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-AVATAR-01-rebake-noop-on-ssb.md).

**`BUG-RENDER-02`, same session, fifth live finding — WRONG DIAGNOSIS, corrected same session by
`BUG-RENDER-03` below.** First report: "the water plane overwrites textures", clarified as
*"das mit dem wasser tritt auf der ganzen sim bei den bäumen auf"* — every TREE, sim-wide. Read
that as SL system trees (Tree/NewTree/Grass pcodes) — `PrimMeshService` gives those a crossed-planes
placeholder mesh, and `ApplyFaceMaterialsAsync` fetched `prim.TextureId` for it as a real texture,
meaningless for that pcode. Fixed that (new `SLNG.Core.PrimPCode`, opaque-placeholder
short-circuit) — a real, separate bug, kept — **but the user corrected it directly: "das hat nix
mit den SL Bäumen zu tun das sind mesh bäume und die Blätter sind Texturen mit Transparenz."**
Mesh trees, alpha-blended leaf textures, nothing to do with system-tree pcodes at all. Lesson: the
"sim-wide, at trees" clue was right, the mechanism guessed from it was wrong — should have compared
shader `render_mode`s before reaching for the tree-geometry code.
[Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-02-tree-water-like-artifact.md) (now carries a
correction note pointing here).

**`BUG-RENDER-03`, same session, the ACTUAL fix for the water/tree report.** Grepped every
shader's `render_mode`: `prim_opaque`, `prim_blend`, `prim_scissor` and every `_avatar`/`_hud`
variant all use `depth_draw_opaque` — `water.gdshader` was the ONE outlier, `depth_draw_always`,
no comment, nothing depending on it. `depth_draw_always` forces water to write depth
unconditionally; Godot's transparent-pass order between two SEPARATE transparent objects (water,
a tree's alpha-blended leaf mesh) is only an approximate object-level sort, not a guaranteed
per-pixel one — wherever water rendered first in that approximate order, its forced depth write
could fail the depth test for leaf fragments that were meant to BLEND with it, not be occluded,
discarding them outright: a hole cut at the water's height, on every tree crossing it, sim-wide.
Fix: `water.gdshader` → `depth_draw_opaque`, matching every sibling shader's own convention.
`--selftest` confirms it still compiles (11 uniforms, unchanged). **Not yet re-verified
in-world.** [Spec](file:///E:/Git/SLNG/docs/specs/BUG-RENDER-03-water-depth-draw-always.md).

## 7. Also still open

- **The avatar stands too low**, feet sunk into the ground. Predates this session. Concrete
  lead: `AvatarRenderer.cs:2323` measures the **rest** pose (`GetBoneRest`) while the comment
  above it says the *live* pose (`GetBoneGlobalPose`). Worn mesh joint overrides are invisible
  to the rest pose, which would sink the avatar by exactly that difference.
- **Attachments occasionally drop out of BoM** — 25 registrations of "NO BoM channels" against
  197 good ones, with real (non-magic) texture ids. `v0.18.5` logs the ids when it happens.

## 8. Method notes, from what actually cost the most

- **A round trip through the same library proves nothing.**
  `SLNGs_bake_encoder_round_trips_the_image_exactly` passed with error 0 while every upload
  was unreadable by any other viewer. Decoding with the encoder's own library proves the data
  survives, not that anyone else can read it. The tests now parse the SIZ/COD markers and
  compare against a real grid asset.
- **An upload or create returning an id is not proof the asset exists.** Three times this
  session: `NewFileAgentInventory` answered four creates with real `new_inventory_item` +
  `new_asset` ids and stored none of them; the bake upload capability has never been verified
  at all. Read back what you wrote.
- **Byte counts do not tell you whether a picture is right.** Much of this task was spent on
  numbers. Dumping the composite and the inputs as PNGs and *looking* found the doubled face,
  the wrong skin and the missing layers within one run each.
- **The reference viewer is the only arbiter.** The container bug was invisible from inside
  SLNG by construction. One screenshot from Firestorm found it.
- **`scratch/libremetaverse_src` is not the pinned package.** `Targa.Decode`,
  `ManagedImage(bitmap)` and `ExportTGA` exist in the vendored source and not in the assembly;
  `CreateItemAsync` exists in the assembly and not in the source. Reflect over the DLL when the
  API matters.
- **`scratch/slviewer` has no `newview` sources.** Use
  `gh api repos/secondlife/viewer/contents/...` — that is how `build_order_string` and
  `LLTexLayerTemplate` were settled.
- **Inventory item names go through the legacy packet.** A name containing `:` or `—` came
  back as a hex dump of itself, truncated at the colon. Plain ASCII.


**BUG-NET-05 / BUG-NET-06, same session:** Fixed HTTP texture fetch spam and environment polling 503 spam. The custom HTTP texture fetch in GridSession.cs lacked backoff and HTTP retries (403, 404, 503) and LibreMetaverse's fallback internally spammed Failed to fetch texture ... over HTTP: Forbidden because its UseHttpTextures was still true. Added an async 5x2sec retry loop in FetchTextureViaHttpRangeAsync, bypassed LMV's internal HTTP texture fallback, and added a 60-second backoff in RepollEnvironmentLoopAsync for ANY FetchRegionEnvironmentAsync error to stop the EnvironmentSettings GET non-success: ServiceUnavailable infinite loop.
