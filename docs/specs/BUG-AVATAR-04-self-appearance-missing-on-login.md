# [BUG-AVATAR-04] Self avatar renders grey / default shape on ~half of logins

- **Feature ID:** `BUG-AVATAR-04`
- **Track:** `net` / `render`
- **Status:** 🧪 Review — workarounds landed (`v0.20.107`–`v0.20.114`); `ArmAttachmentReconcile` confirmed in-world. In-world 2026-09-07 (Agni): inconclusive — several relogs, no "grey" login occurred, so the `SelfAppearanceCache` restore path still has not been exercised once.
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

## Reproduction trigger, reported live 2026-09-09

> *"Ich würde behaupten der Fehler tritt auf, wenn ich das Outfit gewechselt hab und danach neu einlogge."*

This is the most valuable fact about this bug so far, because it replaces *"roughly every second
login"* — untestable, and the reason the `SelfAppearanceCache` restore path had never once been
exercised in months of test logins — with a **deliberate repro**: change outfit → relog.

It also reframes the bug. The title says the simulator sends nothing; if the trigger is an outfit
change *we* performed, the cause may be self-inflicted. Two candidates, both in `GridSession`, both
consistent with the trigger and neither yet confirmed:

1. **The re-composite nudge is debounced by 1.8 s** (`ScheduleRebakeAfterWearableEdit`,
   `GridSession.cs:3812-3830`). Nothing tells the server to re-composite until 1.8 s after the last
   wearable edit settles, and the POST is fire-and-forget. Log out inside that window — or before
   the server finishes compositing — and the next login has no finished bake to be told about.
2. **The appearance cache only writes when an `AvatarAppearance` arrives**
   (`MaybeSaveSelfAppearanceCache`, called from the self branch at `GridSession.cs:2094`). If none
   arrives after the outfit change, the cache still holds the state from *before* it — so the
   restore path faithfully restores the **old outfit**, which reads as "broken" just as much as a
   default shape does.

### Next step

Do **not** build a fault-injection switch for this (considered 2026-09-09 and dropped): with a
deliberate repro, one line of instrumentation is enough. Emit a single login summary saying which
path produced the avatar — `appearance: from sim` / `restored from cache (N params, M bakes)` /
`reconciled N attachments` — then one outfit change plus one relog decides between the two
candidates above, and tells us whether the recovery even fires.

Until that line exists, "kam kaputt, baute sich dann sauber auf" cannot be told apart from ordinary
progressive loading.

## Repro captured, and it moves the diagnosis (2026-09-09, `v0.21.22`)

The login summary fired on its first failing login. Both candidates above are **wrong**:

```
[Appearance] login summary: shape RESTORED FROM CACHE (253 params) + 10 bake id(s)
  — the sim sent no AvatarAppearance for us this login · cache written 2026-09-09 05:26 UTC (2 min ago)
[Appearance] login summary: RECONCILED 1 missing Current-Outfit attachment(s)
```

- **Candidate 2 (stale cache) is dead.** The cache was **two minutes old** — written after the
  outfit change. The restore brings back the *current* outfit, not an old one. That is exactly what
  the timestamp was added to decide.
- **The simulator really does send nothing.** Previously an assumption; now measured.
- **The workarounds work.** The restore path executed for the first time since it was written, and
  produced a correct avatar.

### What the visible defect actually was

The sequence, from the same log: `NO BAKE AT ALL` (blank head, system hair as an uncut helmet) at
login → attachment reconciled at ~6 s → `RESTORED FROM CACHE` at **~12 s** → correct avatar. The
restore was armed with a flat `Task.Delay(12 s)`. **The twelve-second wait was the bug's visible
form** — precisely the reported *"kam kaputt, baute sich dann sauber auf"*.

Mitigated in `v0.21.23`: `EarlyRestoreDelay` (2.5 s) tries the cache early and the 12 s pass now
only reports the verdict. Safe by construction — a genuine relay overrides the cached shape through
the same event — and `_selfShapeFromCache` keeps the summary honest about what the *simulator* did
rather than reporting our own restore as "FROM SIM". A new line,
`self AvatarAppearance relay arrived N s after login`, exists to replace the 2.5 s estimate with a
measurement from healthy logins.

## Root cause — why there is nothing to fall back on in the first place

The mitigation above shortens the window; it does not answer *why the login is broken after an
outfit change*. That answer is in our own constructor, not in the simulator:

`GridSession.cs:397` — **`_client.Settings.Agent.SendAppearance = false;`**

That flag gates LibreMetaverse's entire appearance/wearables workflow. With it off:

- `GetWearables()` is empty → `[Appearance] no worn wearables returned`
- LibreMetaverse decodes no wearable assets → `[VisualParams] LibreMetaverse holds NO visual parameters`
- so **SLNG has no independent source for its own shape at all.** The ~253 visual params can only
  arrive as the simulator's unprompted echo of *our own* `AvatarAppearance`.

The reference viewer never depends on that echo. It derives its own appearance from the **wearable
assets** it downloads after `AgentWearablesUpdate` — the path this flag disables. Second Life does
not owe us the echo, and after an outfit change (server re-composite in flight) it is least likely
to arrive. Hence: the failure is not the grid withholding data, it is SLNG having switched off the
mechanism a viewer normally uses to know what it is wearing.

The flag is off deliberately and the comment states the gate: the correction path
(`SendCorrectedAppearance`) had a hole on the login bake, and *"this flag stays off until that
fallback has been verified in-world, because with it on EVERY login writes to the account before a
human can react."* That risk is real and this is **not** a flip-the-flag fix.

### Two ways forward, and they are not equivalent

1. **Verify the correction fallback in-world, then re-enable the flag.** Restores viewer-standard
   behaviour, and would also close FEAT-AVATAR-01's four unchecked criteria and BUG-INV-01's
   "worn state not visible" — all three are the same root. Carries the stated write-to-account
   risk; needs Aditi (the beta grid) before Agni.
2. **Read the worn shape asset ourselves.** Fetch the COF's shape wearable and decode its visual
   params directly, without letting LibreMetaverse send anything. That gives the independent source
   the viewer has, with **zero** write risk, and leaves the flag off. Strictly more work, strictly
   safer.

Option 2 is the recommendation: it removes the dependency on the sim's echo without touching the
one thing that has historically written a bad appearance to the account.

## Implemented — option 2 (`v0.21.24`)

`TryDeriveSelfShapeFromWearablesAsync`: when the simulator still has not echoed our appearance at
the 12 s mark, read the worn wearables and take the visual params out of the **assets**, the way the
reference viewer does. Tried **before** the on-disk cache, because the assets are the current truth
and the cache is only the last thing we happened to see.

Almost all of it already existed for the bake path and was simply never wired into login:

| piece | already there for |
|---|---|
| `CollectWornWearablesForBakeAsync` — reads the COF, downloads each asset | the bake |
| `OrderWearablesAsTheViewerDoes` — layer order from the COF link description | the bake |
| `AgentAppearanceParams.BuildWireArray` — resolve to the 253-param wire array | the corrected send |

Nothing is sent. That is the whole point of choosing this over re-enabling
`Settings.Agent.SendAppearance`: it gives the independent shape source without the flag's risk of
writing an appearance to the account on every login.

The derived shape is also written to `SelfAppearanceCache`, so a later login that cannot reach the
assets in time still has it.

### Login paths, in the order they are now tried

1. Simulator relay (`FROM SIM`) — normal.
2. Cached shape at 2.5 s (`no relay yet …`) — provisional, overridden by a late relay.
3. **Derived from worn wearables at 12 s (`DERIVED FROM WORN WEARABLES`)** — the fix.
4. Cached shape as the verdict (`RESTORED FROM CACHE`) — when the COF yields nothing usable.
5. `NO shape` — nothing worked.

### First live run: the mitigation worked, the fix did not (`v0.21.24`)

```
[Appearance] no relay yet after 2,5 s — showing the cached shape (253 params, 10 bake id(s))
[Appearance] login summary: shape RESTORED FROM CACHE (253 params) + 10 bake id(s)
```

The avatar looked right, but only because the 2.5 s early restore did its job. `DERIVED FROM WORN
WEARABLES` never appeared: `TryDeriveSelfShapeFromWearablesAsync` ran and returned **false in
silence**, and the log could not say which of its two exits was taken.

The suspected cause — the lazy per-folder inventory — is **ruled out**:
`CollectWornWearablesForBakeAsync` does not read the store, it calls `FolderContentsAsync` on the
COF precisely because "at bake time nothing has opened the inventory yet".

`v0.21.25` makes both exits explain themselves, the same fix the healthy path needed before the
login summary existed:

- `cannot derive a shape: Current Outfit Folder gave N wearable(s), M with a downloaded asset` —
  distinguishes an empty COF from assets that did not download in time.
- `cannot derive a shape: N wearable(s) resolved to L params but D of them are default (0/128)` —
  the resolved array failed `VisualParamsHealthy` (needs ≥ 200 params and at least one non-default).

One more relog after an outfit change now says which it is. **Lesson, twice over in this bug: a
`return false` with no reason costs a whole round trip.**

## Second live run: the reason, in one line (`v0.21.25` → fix in `v0.21.26`)

```
[Appearance] cannot derive a shape: Current Outfit Folder gave 9 wearable(s), 0 with a downloaded asset
```

The diagnostic added in `v0.21.25` settled it on the first relog after an outfit change:

- The **COF reads fine** — 9 wearables resolved. The lazy-inventory suspicion is dead for good.
- **No asset was downloaded.** `.Asset` was null on every one.

Cause: `CollectWornWearablesForBakeAsync` resolves the COF links and fills in
`ItemID` / `AssetID` / `WearableType` — it does **not** fetch the assets. The bake path downloads
them itself in the loop immediately after calling it (`RequestAssetAsync` per wearable,
`AssetWearable.Decode()`). `TryDeriveSelfShapeFromWearablesAsync` called the collector and went
straight to reading `.Asset`, so it always found nothing.

`v0.21.26` adds the same download loop, restricted to `Bodypart` / `Clothing` (the COF also holds
attachments, which carry no visual params). Wearables already fetched for a bake this session are
cache hits.

**Method note, third time in this bug:** every step here was decided by one log line rather than by
reading code and guessing — the login summary, then the failure reason, then this. Each cost one
relog. The two guesses made along the way (stale cache, lazy inventory) were both wrong.

## It works (`v0.21.26`, in-world 2026-09-09)

```
[Appearance] derived our own shape from 9 worn wearable(s) (253 params) — no AvatarAppearance needed
[Appearance] login summary: shape DERIVED FROM WORN WEARABLES — the sim sent no AvatarAppearance
  for us this login · cache written 2026-09-09 05:52 UTC (0 min ago)
```

All nine worn wearables downloaded and decoded, the full 253-param array resolved from them, and the
result written back to the cache (age 0 min) as designed. **SLNG no longer depends on the
simulator echoing our own appearance.**

### How a login now composes the avatar

| what | from where |
|---|---|
| shape (253 visual params) | **the worn wearable assets** — independent, this change |
| bake texture ids | the cache / `TryPublishSelfBakesFromScene`, as before |
| missing attachments | `ArmAttachmentReconcile`, as before |

Splitting shape from bake ids is deliberate: the wearables define the body, the bakes are server
composites we can only recover, never compute (`SendAppearance` stays off).

### Still open

- **Visual confirmation.** The log proves the mechanism ran and produced 253 params; it does not
  prove the avatar looks right. Earlier rounds of this bug are a standing reminder that those are
  different claims.
- **The 12 s ladder.** Derivation runs at the 12 s mark, after the 2.5 s cached shape. Fine while
  the cache is fresh, and it costs 9 asset fetches, so moving it earlier is a trade rather than an
  improvement. Revisit only if a first login on a machine with no cache looks wrong.
- `[Appearance] no worn wearables returned` still appears at login — that is
  `AgentWearablesRequest` (LibreMetaverse's own list, dead with `SendAppearance` off) and now only
  affects the Worn tab, not the avatar. It is FEAT-AVATAR-01 / BUG-INV-01 territory.
