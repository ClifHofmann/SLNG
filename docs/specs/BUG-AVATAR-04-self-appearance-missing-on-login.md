# [BUG-AVATAR-04] Self avatar renders grey / default shape on ~half of logins

- **Feature ID:** `BUG-AVATAR-04`
- **Track:** `net` / `render`
- **Status:** 🚧 In Progress — first fix landed (`v0.20.107`), not yet re-verified in-world.
- **Owner:** `claude`
- **Reported:** live, Agni, 2026-09-07 — screenshot of a grey-skinned, featureless avatar;
  *"bei gefühlt jedem 2. Login sieht der Avatar so aus … der Shape ist dann auch nach dem Bake
  noch falsch."*

## Symptom

Roughly every second login the local avatar comes up grey (system body/head textures
unbaked) with the **default shape**. The existing self-bake watchdog recovers the baked
*texture* ids a few seconds later, but the **shape stays wrong** — it is never corrected for
the rest of the session.

## Root cause

The simulator does not reliably send the local agent its own `AvatarAppearance` packet after
login (other avatars are unaffected — theirs arrives with their `ObjectUpdate`). SLNG already
has two mitigations, and neither covers the shape:

- `TryPublishSelfBakesFromScene()` — reads the bake **texture ids** out of our own
  `ObjectUpdate` TextureEntry. Works, but TextureEntry carries no visual parameters.
- `SendServerAppearanceUpdateAsync()` (the `UpdateAvatarAppearance` cap nudge) — measured live
  to return HTTP 200 and change nothing when the server has already served the current
  `cof_version`.

The ~253 **visual parameters** have exactly one source: the `AvatarAppearance` packet
(`_lastSelfRelayVisualParams`, consumed by `AvatarShapeService`). Miss it and there is no
second chance this session — `_lastSelfRelayVisualParams` stays empty and the renderer uses
the default shape.

`_client.Settings.Agent.SendAppearance = false` (deliberate — FEAT-AVATAR-01: turning it on
lets LibreMetaverse's broken param encoder overwrite the account's stored shape) means we also
cannot ask LMV to recompute the params locally.

## Fix so far (`v0.20.107-alpha`)

`SelfAppearanceCache` — a small binary file at
`%LocalAppData%/SLNG/self-appearance/<agentId>.bin` holding the last **healthy** self
appearance: the wire-order visual-param array, the per-slot bake ids, the hover offset.

- **Write:** `OnAvatarAppearance` → `MaybeSaveSelfAppearanceCache()` on every healthy self
  relay, only when the param array changed.
- **Restore:** `ArmSelfAppearanceRestore()` (armed once per session next to the bake
  watchdog) waits 12 s; if `_lastSelfRelayVisualParams` is still empty and a cache file loads,
  it sets `_lastSelfRelayVisualParams` / `_lastSelfRelayBakes` / `_lastSelfHoverOffsetZ` and
  publishes an `AvatarAppearanceReceived` event so the renderer applies the cached shape.

Read-only with respect to the grid — nothing is sent. A real `AvatarAppearance` arriving
later overrides it through the same event, so a stale cache self-heals. Log lines:
`[Appearance] restored last-known shape (N params) + M bake id(s) from cache` /
`… no cached shape to fall back on`.

## Missing Current-Outfit attachment on login (`v0.20.108-alpha`)

Same session type, second symptom: the avatar comes up fine except **one** worn attachment is
missing — boots one login, the DOUX hair the next. The Angezogen tab lists the missing item as
**"(nicht aktiv)"** (in the COF, not in the scene), and there is no SLNG log line for it — the
simulator simply did not rez that attachment this login. It is always one of the freshly-made
`#Library` copies, whose newer inventory/asset records lose the login COF/asset race more often.

**Fix (`v0.20.114`, confirmed in-world):** `ArmAttachmentReconcile()` (armed once per session
next to the other appearance watchdogs) runs `ReattachMissingCofAttachmentsAsync()` at
6 / 12 / 22 / 45 / 80 s: it **fetches the COF into the store first** (lazy per-folder inventory
means nothing pulls it on login — this was why earlier versions were a silent no-op), then for
every COF attachment link whose target is not in the **scene** (the only reliable signal —
LibreMetaverse's `GetAttachmentsByItemId()` cache lags and gave false "worn" hits) **and was
never seen worn this session** (`_attachmentsSeenWornThisSession`, so it never re-adds something
the user took off), it re-sends `Appearance.Attach(…, Default, replace: false)` — the reference
viewer's `LLAttachmentsMgr` re-request behaviour. Stops early once a pass finds nothing.

Live 2026-09-07: `Mellow Elie / Camden Boots` missing on login → re-attached on the 6 s pass,
`[Appearance] 1 Current-Outfit attachment(s) the sim did not rez on login — re-attaching …`,
boots back. The one-shot per-link `[Reconcile]` dump used to find this has been removed.

## Still open

- Whether the sim can be *asked* to (re)send our appearance on login rather than only cached
  around — the reference viewer's login appearance flow.
- The first login on a machine (no cache yet) still shows the default shape until a relay
  arrives.
- Not re-verified in-world: needs a run through several logins to confirm the grey/default
  case now recovers the shape.
