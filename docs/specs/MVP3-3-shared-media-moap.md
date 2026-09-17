# [MVP3-3] Shared Media / MOAP (Media on a Prim)

- **Feature ID:** `MVP3-3`
- **Track:** `net` / `core` / `render`
- **Status:** `🚧 In Progress` (Phases 1 and 3 landed and confirmed/tested; Phase 2 (click to
  open in the system browser) is scoped below but not started. Phase 4 — an embedded web
  browser — is split into its own follow-up id, `FEAT-MEDIA-01`, gated on an ADR.)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

MOAP ("Media On A Prim") lets a creator put a live web page, video, or slideshow on a
specific prim face — vendor boards, video screens, dashboards, in-world web kiosks. The
roadmap's "done when" — "web browser / video streaming on prim faces" — is really two very
different halves, and this spec stages them on purpose rather than blocking on the harder
one:

1. **Protocol/data half:** fetch per-face `MediaEntry` data (URL, controls, permissions,
   whitelist) via the sim's `ObjectMedia` HTTP capability and model it engine-neutrally.
   Fully tractable with what's already pinned — no new dependency.
2. **Rendering half:** actually showing a live web page or video stream ON the face.
   Godot 4 core has **no built-in web view** and its only native video codec
   (`VideoStreamTheora`) needs a fully-downloaded file, not a stream, and covers roughly
   none of what SL residents actually put on a MOAP face (YouTube/Vimeo embeds, MP4/HLS,
   arbitrary HTML). A real embedded browser needs a new native dependency (a
   CEF-based GDExtension) — a per-platform build/licensing commitment squarely inside
   AGENTS.md's "do not change tech stack without an ADR" gate, and one that kills the
   mobile goal outright if taken lightly. That does not block everything else: direct
   **image** URLs and Theora video are both renderable with zero new dependencies and are
   common in-world (vendor boards, gallery prims, webcam stills).

## Phasing

| Phase | What ships | New dependency? | Status |
|---|---|---|---|
| **1** | Data model + protocol fetch + version-gated, throttled change detection | No | ✅ Landed and confirmed in-world |
| **2** | Object inspector display + click-a-media-face → confirm dialog (host + URL) → open in the system browser, permission/whitelist-checked | No | ⏸️ Pending — needs raycast-hit → SL-face-number resolution, which does not exist anywhere in the renderer yet (see Phase 2 notes) |
| **3** | Direct-image (and, later, Theora) textures rendered live on AUTO_PLAY faces | No (ADR 0002 already covers uniform-driven face content; SkiaSharp/Magick.NET already referenced) | ✅ Landed — not yet confirmed in-world |
| **4** (own id: `FEAT-MEDIA-01`) | Full embedded web browser | **Yes — needs an ADR** | ⏸️ Not started |

Parcel-wide media (`ParcelMediaCommandMessage`/`ParcelMediaUpdateReply`) is a different,
legacy, non-per-face feature that happens to share the word "media" — explicitly out of
scope here; give it its own id if it's ever tackled.

## Phase 1 — protocol + data model (this spec, landed)

### Verified wire protocol

Verified against the real `secondlife/viewer` source (blob-hash-compared against
`gh api repos/secondlife/viewer/contents/...`) and OpenSim's `master`, not guessed:

- **`ObjectMedia` capability** (`llmediadataclient.cpp:666-669`) — POST per **object UUID**
  (not batched, not a local id — verified at `:872-879`). `verb: "GET"` returns
  `object_media_data` (an array, **positional by face index, undefined/null for faces with
  no media** — `llmediadataclient.cpp:942-964`) and `object_media_version` (the `x-mv:`
  string below).
- **`MediaEntry`** — all 15 LLSD keys pinned from `llmediaentry.cpp:35-75,143-169`:
  `home_url`, `current_url`, `auto_play`, `auto_loop`, `auto_scale`, `auto_zoom`,
  `first_click_interact`, `controls` (`STANDARD=0`/`MINI=1`), `width_pixels`,
  `height_pixels`, `perms_control`/`perms_interact` (bits `NONE=0`/`OWNER=1`/`GROUP=2`/
  `ANYONE=4`, default **`PERM_ALL`=7** — see the LMV-default trap below),
  `whitelist_enable`, `whitelist` (≤64 entries), `alt_image_enable`. There is **no**
  `enable_alpha_blend` key — that's a renderer decision, not a wire field.
- **The doorbell, not the data.** A prim signals "this face changed media" two ways, and
  neither ever carries the actual `MediaEntry`: (a) a per-face bit in the TextureEntry byte
  (`TEM_MEDIA_MASK = 0x01`, `lltextureentry.h:64`, LibreMetaverse's
  `TextureEntryFace.MediaFlags`), and (b) the `ObjectUpdate` `MediaURL` field holding a
  version string `x-mv:<10-digit sequence>/<agent-uuid>` (`lltextureentry.cpp:53,745-797`),
  diffed and staleness-gated by the real viewer in
  `LLVOVolume::processUpdateMessage`/`updateObjectMediaData`
  (`llvovolume.cpp:436-565,2640-2660`). The actual content only ever arrives from an
  explicit `ObjectMedia` GET.
- **`ObjectMediaNavigate`** (`llmediadataclient.cpp:1049-1057`) — `{object_id, current_url,
  texture_index}`. Permission gate, `LLVOVolume::hasMediaPermission`
  (`llvovolume.cpp:2782-2822`), is an **if/else-if chain**, not a bitwise OR — see
  `MediaPermissionEvaluator`'s doc comment for the exact quirk this preserves. Deferred to
  Phase 2 (nothing calls it yet).
- **OpenSim implements MOAP fully and on by default**
  (`OpenSim/Region/CoreModules/World/Media/Moap/MoapModule.cs`, `Cap_ObjectMedia`/
  `Cap_ObjectMediaNavigate = "localhost"` in `OpenSimDefaults.ini`), and — better still —
  it deserializes with **LibreMetaverse's own** `ObjectMediaMessage`/`ObjectMediaRequest`/
  `ObjectMediaUpdate` classes, so wire compatibility with our default test target is close
  to guaranteed by construction. Two OpenSim-specific traps worth knowing before ever
  authoring media from SLNG: the `UPDATE` array length must be **exactly**
  `GetNumberOfSides()` (a shorter array indexes out of range server-side, `:419-437`), and
  clearing a face's media is **not** done through this cap at all — it's a `MediaFlags`
  bit flip via an ordinary `ObjectImage` update (`:425-426`).

### What LibreMetaverse 3.1.3 gives for free (verified against the actual pinned assembly)

Reflected directly off `~/.nuget/packages/libremetaverse/3.1.3/lib/net8.0/LibreMetaverse.dll`
— not the newer `scratch/libremetaverse_src` checkout (tag `v3.0.2-1-gcd132b87`, used only
to cross-check method-body *shape*, per the same caution `scratch/slviewer` already needed):

- `ObjectManager.RequestObjectMediaAsync(UUID, Simulator, CancellationToken) ->
  Task<(bool success, string version, MediaEntry[] faceMedia)>`
- `ObjectManager.NavigateObjectMediaAsync(UUID, int face, string newURL, Simulator,
  CancellationToken) -> Task`
- `ObjectManager.UpdateObjectMediaAsync(UUID, MediaEntry[], Simulator, CancellationToken)
  -> Task`
- `MediaEntry` — every field above, `MediaControls`/`MediaPermission` enums with the exact
  same numeric values as the wire bits (confirmed by reflecting the actual enum values, not
  just the names).
- `Primitive.MediaURL`/`MediaVersion`/`FaceMedia`, `TextureEntryFace.MediaFlags`.

**The real gap: no event.** `ObjectManager` raises nothing when a prim's media changes —
`RequestObjectMediaAsync` must be called explicitly, and nothing in LMV ever calls it. This
is why Phase 1 is a genuine (if small) protocol task, not just a DTO-conversion one: SLNG
has to replicate the doorbell-watching job `LLVOVolume::processUpdateMessage` does in the
real viewer.

**One real trap, documented for whenever Phase 4+ ever authors media:** a
default-constructed LibreMetaverse `MediaEntry` serializes both permission fields as
`MediaPermission.None` (0), while the real viewer's `LLMediaEntry()` constructor defaults
both to `PERM_ALL` (7) (`llmediaentry.cpp:96-97`, confirmed by executing both
constructors). Sending an update built from a bare `new MediaEntry()` would silently lock
every agent — including the owner — out of the face. `SLNG.Core.MediaFace`'s own defaults
use `MediaPermission.All`, matching the viewer, precisely to not propagate this trap.

### What Phase 1 ships

| Piece | Where | Why there |
|---|---|---|
| `MediaFace`, `MediaControlStyle`, `MediaPermission`, `MediaPermissionEvaluator` | `src/SLNG.Core/MediaFace.cs` | Pure DTO + pure logic, engine- and protocol-agnostic, same shape as `TextureAnimation`/`TextureAnimator` |
| `MediaWhitelist.IsAllowed` | `src/SLNG.Core/MediaWhitelist.cs` | Ports `checkUrlAgainstWhitelist`/`pattern_match` (`llmediaentry.cpp:443-511`) byte-for-byte in behaviour; pure string logic, no LMV type |
| `MediaVersionString` | `src/SLNG.Core/MediaFace.cs` | Parses/recognizes the `x-mv:` doorbell string; pure, no LMV type |
| `FaceTexture.HasMedia` | `src/SLNG.Core/FaceTexture.cs` | The free per-face doorbell bit, carried the same way `Fullbright`/`Shiny` already are. Also closes a real safety gap for later phases: because `FaceTexture` is a record struct and `FaceSurfaceMerge.Plan` merges Godot surfaces on full equality, a media face now can **never** silently merge with an otherwise-identical non-media face — see `FaceSurfaceMerge`'s doc comment. |
| `PrimitiveComponent.MediaFaces`/`MediaVersion` | `src/SLNG.Core/Components/PrimitiveComponent.cs` | Per-entity storage for the fetched content, mirroring `HasPhysicsProperties`'s "confirmed server state" pattern |
| `ObjectMediaEvent`, `IWorldEventSource.ObjectMediaReceived` | `src/SLNG.Core/GridEvents.cs`, `IWorldEventSource.cs` | Same buffered-event seam every other async net→world hop uses (`PhysicsPropertiesEvent` is the closest analogue) |
| `WorldSimulation.ApplyObjectMedia` | `src/SLNG.Core/WorldSimulation.cs` | Drains the event on the main thread only, like every other `ApplyXxx` |
| `GridSession.RequestObjectMediaAsync`, `MaybeQueueMediaFetch`, `FetchAndPublishObjectMediaAsync`, `ToMediaFace` | `src/SLNG.Net/GridSession.cs` | The actual CAP fetch + boundary conversion + the doorbell-watching/throttling this phase exists to add |

**Caps-flood guard (BUG-NET-11's lesson, applied up front):** `GridSession` only queues a
fetch when (a) at least one face's `MediaFlags` bit is set on a **full** update (never a
terse one — `ImprovedTerseObjectUpdate` carries no `TextureEntry`), and (b) the prim's
`x-mv:` version string actually changed since the last fetch queued for that `LocalID`
(`_lastMediaVersionByLocalId`). Concurrent fetches are bounded by a 4-slot semaphore
(`_mediaFetchSemaphore`), the same pattern as the existing 8-slot texture-fetch semaphore.

**Known limitation, accepted for Phase 1:** clearing media (the version string reverting to
a non-`x-mv:` value, or the doorbell bit clearing on every face) is not detected — SLNG only
ever *adds* `MediaFaces` data today, never clears it. Low-risk (a stale "this face has
media" reading is far less harmful than missing one that just appeared) and cheap to add
alongside Phase 2's UI once there's something visible to keep in sync.

### In-world bug found and fixed the same day (`v0.22.198-alpha`)

First live test on Agni surfaced `fail: SLNG[0] [PurisViewer Resident] ObjectMedia
capability not available` (LibreMetaverse's own `ObjectManager.RequestObjectMediaAsync`
logging, `ObjectManager.cs:2183`) on the region's **very first** ObjectUpdate — and the
media was then never fetched again for the rest of the session.

**Cause:** `RaiseObjectUpdate`'s `MaybeQueueMediaFetch` committed the prim's `x-mv:`
version to `_lastMediaVersionByLocalId` **before** awaiting the fetch, so that one transient
failure permanently marked the version "already handled". The failure itself is a real,
known class of race — `CapabilityURI("ObjectMedia")` can read null for a few seconds right
after region entry even on a region that has the capability, because the caps seed is not
necessarily resolved yet (the same race `RegionHasServerSideBaking`'s own doc comment
describes, and the reason the environment code hangs off `EventQueueRunning` rather than
`SimConnected`). The very first ObjectUpdate for a region — exactly the one most likely to
carry an already-in-view MOAP prim — is the update most likely to race it.

**Fix, two parts:**
1. The version is committed to `_lastMediaVersionByLocalId` only on actual fetch success.
   `FetchAndPublishObjectMediaAsync` also now waits out a short capability-seeding window
   itself (5 attempts, 1s apart) before giving up on one fetch.
2. `RetryPendingMediaFetches` sweeps every primitive LibreMetaverse already knows about for
   the sim once `RegionCapabilitiesReady` actually fires (wired into the existing
   `OnEventQueueRunning` handler) — so a fetch that raced the very first update self-heals
   the moment caps are confirmed ready, rather than waiting for some later, incidental
   ObjectUpdate for that same prim that might not come for a long time.

**Confirmed in-world 2026-09-17 (`v0.22.200-alpha`):** clean `[Media] object ... faces=2/6` on
the very next login, no capability-race failure.

## Phase 3 — direct-image face content (landed, `v0.22.201-alpha`)

Renders a MOAP face's image directly onto the prim, for the common in-world case a full
embedded browser is not needed for (vendor boards, gallery prims, webcam stills) — exactly the
shape confirmed live: Firestorm itself only auto-renders the probe's AUTO_PLAY image face
without any click, never the non-autoplay webpage face, so that is the realistic Phase 3
target, not an arbitrary simplification.

**`SLNG.Assets.MediaImageService`** (new): fetches `MediaFace.CurrentUrl` with a bare
`HttpClient` — never `Client.HttpCapsClient`, a third-party media host must never see the
session's caps URL, agent id or cookies — validates the response is actually `image/*`, caps
the download at 8 MB, and decodes it with the SAME `SKBitmap` → exact-RGBA path
`AssetService`'s own CoreJ2K fallback already uses, returning the same neutral `TextureData`
every other texture path returns. Cached by URL for the process lifetime (a face's material
rebuilds on ordinary scene churn far more often than its media actually changes). SkiaSharp and
Magick.NET were already referenced in `SLNG.Assets` — no new dependency. One real trap this
surfaced: `SKBitmap.Decode` does not return null for input it can't parse, it throws
`ArgumentNullException` from inside its own codec lookup — caught by this session's own test
for garbage bytes before it could reach a live host's malformed response.

**`ObjectRenderer.ApplyMediaImageAsync`** (new, `app/`): fire-and-forget, applied strictly
AFTER a face's ordinary material has already landed, so a slow/dead/non-image URL never blocks
or breaks the object's normal appearance — only `AutoPlay` faces fetch automatically, matching
what a reference viewer shows without interaction. Pixel decode stays off the main thread; only
the final `ImageTexture.CreateFromImage` GPU upload is marshalled through `MainThreadWorkQueue`,
the same split every other texture path (`GpuCache.cs`) already uses.

**A real instancing leak, caught before landing, not after:** a MOAP face's material gets its
albedo swapped live, well after `ObjectInstanceGroups` may have already put it in a shared
`MultiMesh` group with other identical-looking instances — sharing that material would leak one
prim's fetched media onto every other member. `FaceSurfaceMerge`'s `HasMedia`-based equality
already stops a media face from merging into ONE surface with a differently-configured
neighbour on the SAME object, which means a MOAP object usually has `GetSurfaceCount() > 1`
and already fails instancing's existing single-surface check — but a single-face object
entirely covered by one MOAP entry has nothing to differ from and would still pass it, so
`EvaluateInstancing` now excludes any object with `PrimitiveComponent.MediaFaces != null`
explicitly, regardless of surface count.

### Tests

- `MediaWhitelistTests` — the whitelist matcher against real `checkUrlAgainstWhitelist`
  cases, including the schemeless-filter quirk.
- `MediaPermissionEvaluatorTests` — the permission chain, including the Owner+Group
  short-circuit quirk.
- `MediaVersionStringTests` — `x-mv:` recognition/parsing, including rejecting a legacy
  (non-MOAP) `MediaURL`.
- `ObjectMediaConversionTests` (`SLNG.Net.Tests`) — `MediaEntry -> MediaFace` at the actual
  boundary method, including the null-means-no-media-on-that-face convention.
- `ObjectMediaApplyTests` — the full buffered-event path (`GridSession.RaiseObjectMedia` →
  `WorldSimulation.Pump` → `PrimitiveComponent`), plus the silently-dropped-for-an-unknown-
  entity case `ApplyPhysicsProperties` already established the pattern for.
- `FaceSurfaceMergeTests` — extended `DifferingFaces()`/the constructor-field-count guard
  with the new `HasMedia` field, proving a media face can't merge with a non-media one.
- `MediaImageServiceTests` (`SLNG.Assets.Tests`) — the SKBitmap decode path against a real
  1x1 PNG and against garbage bytes (catching the `SKBitmap.Decode` throws-don't-return-null
  trap before it could reach a live host's malformed response), plus the non-http(s)/
  unparseable-URL guards that must never make a network call.

## Phase 2 — click-to-open fallback (not started)

The interaction fallback: clicking a MOAP face shows a confirm dialog naming the host and
full URL, then opens it in the system's default browser — real user value with zero new
engine dependency, while Phase 4's embedded browser is pending its ADR.

**The blocker this phase actually has to solve:** `ObjectSelectionController`'s left-click
handler resolves a raycast hit to an **object** (`_session.ClickObjectAsync(rawLocalId,
position: hitPosSl)`, `ObjectSelectionController.cs:228`) but never to a **face** — every
call site passes `faceIndex: 0` even though `ClickObjectAsync` already accepts a real one.
Prim collision is one `ConcavePolygonShape3D` per whole object
(`ObjectRenderer.cs:4480,4521`), not per face, so Godot's raycast result gives only a
world-space hit point, no face/triangle id. Resolving "which SL face was clicked" needs a
small CPU-side picking step against the object's own `MeshData` (which submeshes already
carry `FaceIndex`, see `FaceSurfaceMerge`) — this does not exist anywhere in the renderer
today and is this phase's real scope, not the dialog/`OS.ShellOpen` part.

**Guardrails for whoever picks this up (from the architecture review, not yet implemented):**
- Validate with `Uri.TryCreate(url, UriKind.Absolute, ...)` and allow only `http`/`https`
  before ever calling `OS.ShellOpen` — `CurrentUrl` comes from an in-world object and an
  unvalidated scheme (`file://`, `ms-msdt:`, ...) is arbitrary URI-handler invocation.
- Check `MediaPermissionEvaluator`/`MediaWhitelist` client-side before offering the dialog
  at all, not just server-side — TPV Non-negotiable #1 (honor creator permissions) applies
  regardless of what the sim also enforces.
- No automatic OS-browser open, ever, not even of just a preview: opening an external
  program is a materially bigger action than an in-scene texture, and MOAP is a known
  IP-disclosure/griefing vector (a rezzed prim can point media at a server the griefer
  controls and log every visitor's IP) — for THIS phase's click-to-`OS.ShellOpen` action,
  everything must stay user-click-gated. This does not contradict Phase 3's auto-rendered
  image: an in-scene texture swap is the lower-risk half of the same risk (still an IP
  disclosure to whatever host the creator pointed the face at, but not also handing that host
  an invitation to run in a full external browser process), it only ever fires for a face the
  CREATOR explicitly flagged `AUTO_PLAY` (their declared intent, not SLNG inventing
  auto-fetch), and it's exactly the behaviour a reference viewer already shows with zero
  clicks — confirmed live: Firestorm auto-renders the probe's `AUTO_PLAY` image face and
  shows nothing at all for the non-`AUTO_PLAY` webpage face until clicked.

## Phase 3 — direct-image face content (landed, `v0.22.201-alpha`)

For a face whose `CurrentUrl` resolves to `image/*` (vendor boards, gallery prims, webcam
stills) and whose `MediaFace.AutoPlay` is true, swap the face's albedo texture live —
`PrimShaderFamily`'s existing per-surface texture parameter, not a material rebuild. Owned by
`SLNG.Assets.MediaImageService` (a bare `HttpClient`, **never** `Client.HttpCapsClient` — a
third-party media host must never receive the session's caps URL/agent id), decoding via the
same `SKBitmap` path `AssetService`'s own CoreJ2K fallback already uses, cached by URL for the
process lifetime — SkiaSharp and Magick.NET were already referenced in `SLNG.Assets`, no new
dependency. `ObjectRenderer.ApplyMediaImageAsync` applies it strictly AFTER the face's ordinary
material has already landed, fire-and-forget, so a slow/dead/non-image URL never blocks or
breaks the object's normal appearance. See the in-world bug/fix log above this section for the
one real trap it surfaced (`SKBitmap.Decode` throws rather than returning null on bad input)
and the instancing-leak gap it closed before ever landing (`EvaluateInstancing` now excludes
any MOAP-carrying object regardless of surface count).

Video (Theora, for a fully-downloadable `.ogv`) is a smaller follow-up on the same seam once
there's a real test case for it — needs its own teardown discipline, matching
`ObjectParticles`' node-lifetime pattern rather than inventing a new one. Not started; no
in-world MOAP video test target has been found yet.

## Phase 4 — embedded web browser (`FEAT-MEDIA-01`, not started)

Split into its own roadmap id because it is architecturally a different kind of decision:
a native GDExtension (e.g. a CEF wrapper) is a per-platform build and licensing commitment
that reshapes the installer/CI pipeline and forecloses the mobile goal if adopted casually.
First acceptance criterion for that id is "ADR accepted"; the ADR should weigh in-process
embedding against running the browser as its own process (the real viewer's own approach —
CEF runs in a separate `SLPlugin` process specifically to isolate crashes and keep the
extension surface small), and should revisit "is this still needed" if Godot ever ships a
first-class web view.

## Acceptance Criteria

### Phase 1

- [x] `ObjectMedia` fetch, per-face doorbell detection, and version-gated/throttled
      change-triggering implemented in `SLNG.Net`, with no LibreMetaverse type crossing the
      boundary.
- [x] `MediaFace`/whitelist/permission-evaluator/version-string logic in `SLNG.Core`,
      engine- and protocol-agnostic.
- [x] `WorldSimulation` applies a completed fetch onto `PrimitiveComponent` on the main
      thread only.
- [x] Unit tests for the whitelist matcher, permission chain (including its quirk),
      version-string parsing, the LMV↔neutral conversion, and the end-to-end buffered-event
      path.
- [x] `dotnet build` (solution + `app/`), `dotnet test`, `dotnet format` clean.
- [x] Confirmed against a live region — `v0.22.198-alpha`, Agni: clean `[Media] object ...
      faces=2/6` on login (after the cap-race fix; the first live attempt caught and fixed
      that bug).

### Phase 3

- [x] `SLNG.Assets.MediaImageService`: fetch (bare `HttpClient`, image/* content-type
      validated, 8 MB cap, URL-cached) and decode (SKBitmap → neutral `TextureData`) with no
      LibreMetaverse or Godot type crossing the boundary.
- [x] `ObjectRenderer.ApplyMediaImageAsync`: fire-and-forget, applied after the face's
      ordinary material, `AutoPlay`-gated, pixel decode off the main thread / GPU upload on it.
- [x] MOAP-carrying objects excluded from `MultiMesh` instancing regardless of surface count.
- [x] Unit tests for the decode path (real PNG, garbage bytes, empty bytes) and the
      non-network URL guards.
- [x] `dotnet build` (solution + `app/`), `dotnet test`, `dotnet format`, shader-globals,
      `--selftest` all clean.
- [ ] Confirmed against a live region (needs a rebuild + a live session against the probe's
      `AUTO_PLAY` image face).
