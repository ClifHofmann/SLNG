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
- **Already satisfied:** channel `SLNG` is honest and distinct, and `Boot.cs:1727` reports the
  real `AppVersion` (§1.b), shown on the login screen.
- **Missing, §1.f — the only real blocker:** the viewer must present the ToS and require
  acceptance. SLNG does not handle the `tos` login response at all. LibreMetaverse has an
  `AgreeToTos` flag, and **setting it blindly is precisely the violation** — that accepts on
  the user's behalf, sight unseen. Correct flow: login returns `reason: "tos"` with text →
  display → user accepts → retry with the flag. `critical_message` uses the same mechanism.
- **Missing, §1.g:** an "About This Viewer" window carrying name and version. Must inherit
  `SLNGWindow` (app-rules).
- **Missing:** the Aditi login URI, `https://login.aditi.lindenlab.com/cgi-bin/login.cgi`, as
  its own grid entry. Aditi has **separate passwords** — the account is a periodic copy, and
  the password is whatever it was at copy time.

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
