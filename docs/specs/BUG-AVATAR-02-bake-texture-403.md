# [BUG-AVATAR-02] Every avatar bake channel returned HTTP 403 — wrong CDN entirely

- **Feature ID:** `BUG-AVATAR-02`
- **Track:** `net` / `assets` / `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.0-alpha`

## Overview & Goal

The root cause of `BUG-AVATAR-01`'s blank self-avatar ("das BOM fehlt"): every single bake
channel — head, upper, lower, eyes, hair, skirt, and all four BoM universal channels — failed to
fetch, HTTP 403, every time, across every session for hours. Found by reading `godot.log`
(`[SelfBake]` proved the sim relayed real, stable, identical bake ids across many relogs;
`[TextureFetch] ... HTTP fetch gave up: HTTP 403` showed every one of those ids failing the
same way), then confirmed conclusively by the user with HTTP Toolkit as a MITM proxy: SLNG's
request to `asset-cdn.glb.agni.lindenlab.com/?texture_id=...` got a raw AWS S3
`<Error><Code>AccessDenied</Code>` body — and a side-by-side capture of **Firestorm fetching the
identical texture id** showed it using a completely different host and URL shape entirely:
`http://bake-texture.glb.agni.lindenlab.com/texture/<agent-id>/eyes/<texture-id>` → `200 OK`,
real J2C bytes.

## Root cause

Avatar bake textures are not served through the generic asset CDN
(`GetTexture`/`ViewerAsset`, `{cap}?texture_id={id}`) that every other SL texture uses. They have
their **own dedicated host and URL shape**. This is real, documented reference-viewer
infrastructure, not a guess:

- `indra/newview/llappcorehttp.h` lists `bake-texture` as its own HTTP connection-pool
  destination, explicitly distinct from the general asset `cdn`.
- `indra/llappearance/llavatarappearancedefines.cpp` defines the eleven bake channels, each with
  a slot-name string (`"head"`, `"eyes"`, …) used nowhere else in the protocol except this URL.

LibreMetaverse 3.1.3 (the pinned package) does not implement this at all — its own
`AssetManager.HttpRequestTexture` (reached via `GridClientBakingTextureProvider`, LMV's own
bake-fetch abstraction) uses the same generic `{capUri}?texture_id={id}` shape for every fetch
regardless of `ImageType`, which is exactly the wrong host for a bake. SLNG's own custom texture
fetch (`FetchTextureViaHttpRangeAsync`, built for regular world-object textures) had the same gap.
Neither ever had a reason to know bakes were different, because nothing had been reported broken
on OpenSim (where this whole distinction doesn't exist) until now, on a real Linden grid.

## Acceptance Criteria

- [x] `GridSession.FetchBakeTextureDataAsync(Guid textureId, int bakeChannel, ...)` builds and
      fetches `http://bake-texture.glb.{grid}.lindenlab.com/texture/{agentId}/{slot}/{textureId}`.
- [x] The eleven bake-channel slot names are read from the reference viewer's own source table,
      not inferred from the one name ("eyes") actually observed.
- [x] Wired into both places a bake channel's texture is actually fetched for rendering: the
      system-avatar-mesh bake application (`AvatarRenderer.LoadAndApplyTextureAsync`) and the
      Bakes-on-Mesh face-texture resolution (`AvatarRenderer`'s face-material builder, for a mesh
      body/head that carries an `IMG_USE_BAKED_*` magic id).
- [x] Falls back to the generic path when off a Linden grid or when the bake-specific fetch fails,
      rather than only ever trying the one path.
- [x] Unit tests written and passing (16 new: 11 for the slot-name table, 5 for grid-name parsing).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Core/BakeChannelNames.cs` | new — the 11-entry channel→slot-name table |
| `src/SLNG.Net/GridSession.cs` | `_lindenGridShortName` field + `ParseLindenGridShortName`; `FetchBakeTextureDataAsync`; `FetchTextureViaHttpRangeAsync` gained an optional `fetchUrl` override so the bake fetch can reuse its validation/retry logic without a `{cap}?texture_id=` shape |
| `src/SLNG.Assets/AssetService.cs` | `GetBakeTextureAsync(Guid textureId, int bakeChannel, ...)` — a small, separate method (not threaded through the generic fetch/cache/dedup pipeline; see its own doc comment for why) |
| `app/scripts/GpuCache.cs` | `GetOrUploadTextureAsync`/`FetchAndUploadTextureAsync` gained an optional `bakeChannel` parameter |
| `app/scripts/AvatarRenderer.cs` | Both bake-texture call sites now pass their known channel index |
| `tests/SLNG.Core.Tests/BakeChannelNamesTests.cs` | new — 11 tests |
| `tests/SLNG.Net.Tests/LindenGridShortNameTests.cs` | new — 5 tests |

### Design notes

- **`FetchTextureViaHttpRangeAsync` grew one optional parameter (`fetchUrl`) instead of being
  duplicated.** All of its careful validation logic (J2C/JP2 signature check, short-read
  detection, the chunked-encoding EOC check) applies identically to a bake fetch; only the URL
  shape and the "always full fetch, no Range" behaviour differ, and both are already parameters
  the method understands.
- **`GetBakeTextureAsync` is its own method in `AssetService`, not a parameter threaded through
  `GetTextureAsync`'s pipeline.** That pipeline's `Lazy`-dedup and negative-cache machinery exist
  for a much higher call volume (every face of every object in view) than bake channels ever
  produce (at most 11 per avatar per rebake) — duplicating that complexity for this volume wasn't
  worth the risk of interacting badly with the well-tested generic path.
- **Grid short name parsed from the login URI, not hardcoded.** `ParseLindenGridShortName`
  extracts "agni"/"aditi" from `login.{grid}.lindenlab.com`, so the same code works on both
  without a grid-specific branch, and returns null (safe no-op) for anything else — including
  OpenSim, which has no such host at all.

## What the tests guarantee

`BakeChannelNamesTests` pins all eleven channel→slot-name mappings against the reference viewer's
own table (not just the one, "eyes", actually observed live) and confirms non-bake channel indices
correctly return null. `LindenGridShortNameTests` pins the URI parsing for both known Linden login
hosts and confirms OpenSim/garbage input safely returns null rather than guessing.

The actual HTTP fetch (`FetchBakeTextureDataAsync`, `GetBakeTextureAsync`) is **not** unit-tested —
same reasoning as every other live-grid-dependent piece from this session: it needs a connected
client and a real Linden grid, which `tests-rules` reserves local OpenSim for, and OpenSim has
none of this infrastructure to test against at all. The Aditi/Agni session that found this bug is
what will confirm the fix.

## Still open

- **Not yet re-verified in-world.** This is the most consequential fix from today's session and
  has not yet been tried against a real avatar.
- **The exact discovery mechanism for the `bake-texture` host is assumed, not confirmed from
  viewer source.** The reference viewer's own URL-construction call site could not be located via
  GitHub code search in the time available; the pattern used here (derived from the grid's login
  host) is inferred from the two captured hostnames sharing the same `.glb.{grid}.lindenlab.com`
  suffix, not read directly from a viewer source line that builds it. If a grid ever uses a
  differently-shaped bake host, this will need revisiting.
- **A related process failure, not a code bug**, is documented separately in `HANDOVER.md`: this
  session's earlier `BUG-AVATAR-01`, `BUG-NET-04`, and the TPV §1.a/§2.b guards were silently lost
  from the working tree between being tested and a later commit made from what turned out to be a
  stale partial copy of the file, and had to be re-applied from scratch after being found missing
  by a grep that turned up nothing. Worth remembering when handing this file between tools/sessions
  again.
