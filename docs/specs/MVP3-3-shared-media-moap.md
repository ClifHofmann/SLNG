# [MVP3-3] Shared Media / MOAP (Media on a Prim)

- **Feature ID:** `MVP3-3`
- **Track:** `net` / `core` / `render`
- **Status:** `🚧 In Progress` (Phase 1 — protocol + data model — landed and tested; not yet
  confirmed in-world, since that requires a live session against a real grid. Phases 2–3
  are scoped below but not started. Phase 4 — an embedded web browser — is split into its
  own follow-up id, `FEAT-MEDIA-01`, gated on an ADR.)
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
| **1** | Data model + protocol fetch + version-gated, throttled change detection | No | ✅ Landed this spec |
| **2** | Object inspector display + click-a-media-face → confirm dialog (host + URL) → open in the system browser, permission/whitelist-checked | No | ⏸️ Pending — needs raycast-hit → SL-face-number resolution, which does not exist anywhere in the renderer yet (see Phase 2 notes) |
| **3** | Direct-image (and Theora) textures rendered live on the face | No (ADR 0002 already covers uniform-driven face content) | ⏸️ Pending |
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
- No automatic fetch/open ever, even of just a thumbnail: MOAP is a known IP-disclosure/
  griefing vector (a rezzed prim can point media at a server the griefer controls and log
  every visitor's IP). Everything here is user-click-gated by design; do not add an
  autoplay/auto-preview path.

## Phase 3 — direct-image / Theora face content (not started)

For a face whose `CurrentUrl` resolves to `image/jpeg`/`image/png` (vendor boards, gallery
prims, webcam stills) or a fully-downloadable `.ogv`, swap the face's albedo texture live —
`PrimShaderFamily`'s existing per-surface texture parameter, not a material rebuild. Owned
by `SLNG.Assets` (a bare `HttpClient`, **never** `Client.HttpCapsClient` — a third-party
media host must never receive the session's caps URL/agent id) in a cache namespace
separate from the J2K asset cache (media keys on URL+ETag, not a UUID).  Needs its own
teardown discipline for the (rare) Theora case, matching `ObjectParticles`' node-lifetime
pattern rather than inventing a new one.

## Phase 4 — embedded web browser (`FEAT-MEDIA-01`, not started)

Split into its own roadmap id because it is architecturally a different kind of decision:
a native GDExtension (e.g. a CEF wrapper) is a per-platform build and licensing commitment
that reshapes the installer/CI pipeline and forecloses the mobile goal if adopted casually.
First acceptance criterion for that id is "ADR accepted"; the ADR should weigh in-process
embedding against running the browser as its own process (the real viewer's own approach —
CEF runs in a separate `SLPlugin` process specifically to isolate crashes and keep the
extension surface small), and should revisit "is this still needed" if Godot ever ships a
first-class web view.

## Acceptance Criteria (Phase 1, this spec)

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
- [ ] Confirmed against a live region (needs an interactive session with real grid access —
      not available in the environment this phase was implemented in; the diagnostic
      `[Media] object ... version=... faces=N/M` log line is the thing to watch for on a
      known MOAP prim).
