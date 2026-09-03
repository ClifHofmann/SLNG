# [BUG-RENDER-10] A 403-denied texture is re-fetched every ~45 s → face flickers, LMV logger spam

- **Feature ID:** `BUG-RENDER-10`
- **Track:** `net` / `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Reported:** live, Agni, 2026-09-03. *"Ich hab diesen Textur-blinkt-Bug bei dem Haar von einem
  Avatar auf der SIM."*

## What's happening

A remote avatar's hair face carries texture `dda710d4-…` (a small on-screen object,
`[FaceTex] object texture … pixelArea=56`). The sim's `GetTexture` / `ViewerAsset` cap answers
**HTTP 403** for it. SLNG's own HTTP path (`FetchTextureViaHttpRangeAsync`) correctly treats 403
as non-retryable and returns null — but `FetchTextureDataAsync` then **falls back to
LibreMetaverse's UDP `TexturePipeline`**, which re-tries its own HTTP GetTexture, hits the same
403, and logs `[PurisViewer Resident] Failed to fetch texture … over HTTP: Forbidden` several
times per attempt.

`AssetService.GetTextureAsync` has a 45 s negative cache (`_recentTextureFailures`), so it stops
for 45 s — then expires. The hair face is rebuilt constantly (terse updates on the animating
avatar), so the next rebuild after each expiry pays the **full fetch + LMV-pipeline** cost again.
Result over a session: dozens of `Forbidden` bursts, and the face visibly **flickers** (each
rebuild re-sets the material to untextured; an occasional degraded UDP body is briefly accepted).

`33192a49-…` in the same session 403s too but settles cleanly — it happened to reach
`[TextureGiveUp] all 3 attempts` and stay null. `dda710d4` kept getting re-requested because
nothing marked it *permanently* dead.

## Fix (`v0.20.40-alpha`)

A 403/401 from the generic cap is a permission decision, not a transient error — no transport
gets those bytes.

- `GridSession.TextureFetchResult` gains `bool Gone`. `FetchTextureViaHttpRangeAsync` records a
  403/401 from the **generic** cap path (`fetchUrl == null`) into a session-static
  `_permanentlyDeniedTextures` set; the bake path (`fetchUrl != null`) is excluded — BUG-AVATAR-02
  bakes 403 by design and legitimately fall back to the generic cap.
- `FetchTextureDataAsync` returns `{ Data: null, Gone: true }` for a denied id **without invoking
  the LMV UDP pipeline** — both at method entry (subsequent calls) and after the cap attempts.
- `AssetService` keeps a session-permanent `_goneTextures` set: `GetTextureAsync` returns null
  immediately for a member, and `FetchAndDecodeTextureAsync` adds an id the moment
  `TextureFetchResult.Gone` comes back, logging `[TextureGiveUp] … sim denied it (403/401) -- not
  retrying this session`.

Net: one 403, then the texture is dead for the session — no LMV pipeline churn, no 45 s re-fetch
cycle, and the face settles to a stable untextured state instead of blinking. A restart clears
the sets (in case a 403 was somehow transient).

## Follow-up (`v0.20.42-alpha`) — an untextured hair face rendered inside-out

User, after `v0.20.40`: *"bei den Haaren kann man jetzt 'falsch' durchschauen, als ob innen und
außen vertauscht ist."* Making the denial permanent exposed a pre-existing gap: a hair face
whose texture never loads has no per-pixel alpha to cut it into strands, and if it was heading
for `Kind.Blend` only because of a soft per-face **tint**, it renders as a translucent
DOUBLE-SIDED card (the Avatar surface shader is `cull_disabled`) with no depth write — overlapping
cards then sort against each other unpredictably (the "inside/outside swapped" look). Before
`v0.20.40` the texture flickered in often enough to mask it; now it's permanent.

Fix in `BuildFaceMaterialAsync`'s `built == null` branch: if `kind == Blend` purely from a tint
with `tint.A > 0.02` (and not a HUD), fall back to `Kind.Opaque` — a solid flat-colour shape is
far less broken than a see-through ghost. A genuinely-hidden face (`tint.A ≈ 0`, a common way to
hide a mesh face) is left alone.

## Still open / verify

- **Not re-verified in-world.** Confirm the hair face stops flickering and the
  `Failed to fetch texture … Forbidden` spam is gone (one `[TextureGiveUp] … sim denied it` and
  then silence for that id).
- A permanently-untextured hair face still renders as flat albedo (hair tint). If that reads
  badly, a neutral hair fallback texture is a separate follow-up — but stable-and-plain beats
  flickering.
- Why the sim 403s `dda710d4` at all (no-transfer texture on the hair? an asset that outlived its
  permission?) is not investigated here — SLNG can't fetch it either way.

## `v0.20.54` — "no transport will get the bytes" was wrong

`v0.20.40` skipped the UDP fallback on a 403 as well as the HTTP retries, reasoning that a
permission decision beats every transport. **The user disproved it: Firestorm renders `dda710d4`
correctly** (confirmed 2026-09-03). The asset exists and is servable — the generic `ViewerAsset`
cap simply refuses to serve it to us (`403 from asset-cdn.glb.agni.lindenlab.com/`, the generic
CDN, not a bake-style URL).

`FetchTextureDataAsync` now keeps two sets instead of one:

- `_httpDeniedTextures` — 403'd on HTTP. The caps are never asked again this session (that
  unwinnable retry loop was the original flicker), but the request still falls through to UDP.
- `_permanentlyDeniedTextures` — 403 on HTTP **and** nothing over UDP. Only this state sets
  `TextureFetchResult.Gone`, and it short-circuits every later request for the id.

So a CDN-refused texture costs exactly one UDP attempt through LibreMetaverse's legacy image
transfer — the same fallback the reference viewer uses — and the anti-flicker property survives,
because HTTP is never retried and one failed UDP round ends it for the session. Success logs
`HTTP 403 but UDP delivered N bytes -- the asset exists, the CDN just would not serve it`.

Open question if UDP *also* comes back empty: what Firestorm does differently on the HTTP request
itself (headers, a different cap, or a per-asset signed URL) — that would need the HTTP capture.

