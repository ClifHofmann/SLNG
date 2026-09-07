# [BUG-AVATAR-02] Every avatar bake channel returned HTTP 403 — wrong CDN entirely

- **Feature ID:** `BUG-AVATAR-02`
- **Track:** `net` / `assets` / `render`
- **Status:** `✅ Done` — confirmed in-world 2026-09-07 (Agni): self avatar's system head and hands render the real skin, no HTTP 403 on the bake channels.
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

- [x] `GridSession.FetchBakeTextureDataAsync(Guid textureId, int bakeChannel, Guid agentId, ...)`
      builds and fetches `http://bake-texture.glb.{grid}.lindenlab.com/texture/{agentId}/{slot}/{textureId}`,
      where `agentId` is the avatar **wearing** the bake (v0.20.12-alpha — was hardcoded to self).
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
| `src/SLNG.Net/GridSession.cs` | `_lindenGridShortName` field + `ParseLindenGridShortName`; `FetchBakeTextureDataAsync` (+ `Guid agentId` param, v0.20.12); `BuildBakeTextureUrl` pure helper (v0.20.12); `FetchTextureViaHttpRangeAsync` gained an optional `fetchUrl` override so the bake fetch can reuse its validation/retry logic without a `{cap}?texture_id=` shape |
| `src/SLNG.Assets/AssetService.cs` | `GetBakeTextureAsync(Guid textureId, int bakeChannel, float priority, Guid bakeAgentId)` — a small, separate method (not threaded through the generic fetch/cache/dedup pipeline; see its own doc comment for why) |
| `app/scripts/GpuCache.cs` | `GetOrUploadTextureAsync`/`FetchAndUploadTextureAsync` gained optional `bakeChannel` + `bakeAgentId` parameters |
| `app/scripts/AvatarRenderer.cs` | `AvatarVisual.AgentId` (set from `AvatarComponent.AgentId`); both bake-texture call sites now pass their channel index **and** the wearing avatar's id |
| `tests/SLNG.Core.Tests/BakeChannelNamesTests.cs` | new — 11 tests |
| `tests/SLNG.Net.Tests/LindenGridShortNameTests.cs` | new — 5 tests |
| `tests/SLNG.Net.Tests/BakeTextureUrlTests.cs` | new (v0.20.12) — 3 tests: wearer's id (not viewer's) in the path, grid name in the host, two wearers → distinct URLs |

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

## Follow-up (v0.20.12-alpha) — only the LOCAL avatar's bakes ever resolved

Tried in-world on Agni 2026-09-02 (`v0.20.11-alpha`). The self avatar's own BoM head/body
textured correctly — the dedicated-CDN path works — but the log was flooded with
`[FaceTex] ... fetch/decode returned null` (214× `8a2f74fd`, 135× `27d8904e`, 78× `333778c6`,
75× `2cae1bdb`, …), and **not one of those ids was a self bake channel** (`[SelfBake]` listed
`8=784033ee 9=9965f08e 10=e1baf1d1 …`, none of which failed). Every failing id belonged to
*another* avatar's mesh body/head.

**Root cause:** `FetchBakeTextureDataAsync` hardcoded `_client.Self.AgentID` in the URL path.
The reference viewer builds it from the DISPLAYED avatar's id, not the viewer's —
`LLVOAvatar::getImageURL` (`indra/newview/llvoavatar.cpp`, `getImageURL`):

```cpp
url = appearance_service_url + "texture/" + getID().asString() + "/"
      + texture_entry->mDefaultImageName + "/" + uuid.asString();
```

`getID()` is the `LLVOAvatar` instance's own id. Requesting someone else's bake at
`…/texture/<our-id>/<slot>/<their-texture>` gets a 403 from the CDN, then a second 403 from the
generic fallback, so **every other person wearing a Bakes-on-Mesh body rendered untextured
(white)**, and the failed fetches retried in a loop (no negative cache on the bake path).

**Fix:** thread the owning avatar's id from the render layer to the URL builder —
`AvatarVisual.AgentId` (set from `AvatarComponent.AgentId`) →
`AvatarRenderer.LoadAndApplyTextureAsync` / `BuildFaceMaterialAsync` →
`GpuCache.GetOrUploadTextureAsync` (`bakeAgentId`) → `AssetService.GetBakeTextureAsync` →
`GridSession.FetchBakeTextureDataAsync(…, Guid agentId = default, …)`, which falls back to
`_client.Self.AgentID` when the caller passes `Guid.Empty`. URL construction pulled out into
`GridSession.BuildBakeTextureUrl` (pure, `internal static`) with `BakeTextureUrlTests` pinning
that the *wearing* avatar's id lands in the path. `AppVersion` → `v0.20.12-alpha`.
**Not yet re-verified in-world.**

## Still open

- **Not yet re-verified in-world.** This is the most consequential fix from today's session and
  has not yet been tried against a real avatar.
- **The `appearance_service_url` base is still constructed, not read from the login response.**
  The real viewer takes it from `LLAppearanceMgr::getAppearanceServiceURL()` (the login
  response's `agent_appearance_service`); SLNG builds `http://bake-texture.glb.{grid}.lindenlab.com/`
  from the parsed grid short name. That host is demonstrably correct for self on Agni, but a
  grid that returns a differently-shaped `agent_appearance_service` would still need the real
  field. `LLVOAvatar::getImageURL`'s `texture/<id>/<slot>/<uuid>` path shape is now confirmed
  from viewer source (was previously only inferred from captured hostnames).
- **A related process failure, not a code bug**, is documented separately in `HANDOVER.md`: this
  session's earlier `BUG-AVATAR-01`, `BUG-NET-04`, and the TPV §1.a/§2.b guards were silently lost
  from the working tree between being tested and a later commit made from what turned out to be a
  stale partial copy of the file, and had to be re-applied from scratch after being found missing
  by a grep that turned up nothing. Worth remembering when handing this file between tools/sessions
  again.
