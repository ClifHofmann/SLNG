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

## Still open / verify

- **Not re-verified in-world.** Confirm the hair face stops flickering and the
  `Failed to fetch texture … Forbidden` spam is gone (one `[TextureGiveUp] … sim denied it` and
  then silence for that id).
- A permanently-untextured hair face still renders as flat albedo (hair tint). If that reads
  badly, a neutral hair fallback texture is a separate follow-up — but stable-and-plain beats
  flickering.
- Why the sim 403s `dda710d4` at all (no-transfer texture on the hair? an asset that outlived its
  permission?) is not investigated here — SLNG can't fetch it either way.
