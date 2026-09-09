# [BUG-INV-03] A refused Current-Outfit link is reported as success — and strips the avatar

- **Feature ID:** `BUG-INV-03`
- **Track:** `net`
- **Status:** `✅ Done` (cause proven in-world 2026-09-09 — the skins are `#Library` items)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Symptom

Reported live 2026-09-09, right after BUG-INV-02 shipped:

> *"wenn ich über das inventar die skin anziehe sieht der avi kaputt aus. wenn ich 'den gleichen'
> skin über das outfit anziehe gehts wieder"*

Wearing a skin from the **Inventar** tab leaves a washed-out, default-looking body. Wearing the same
skin from the **Outfits** tab restores it. The status line reported success both times.

## Evidence — the log answers this outright

One session, `v0.21.33-alpha`, `godot.log`. Two bake sets alternate:

| set | channels 8 / 9 / 10 | look |
|---|---|---|
| **A** | `d639c582 6f6d65cf 465b0b02` | the login bake — correct |
| **B** | `441fcff6 12aafeb3 c6fb6045` | washed out |

and one warning appears in the session, seven times, always immediately before
`[Appearance] wore "…" (Bodypart)`:

```
warn: SLNG[0] Create inventory in 0ddfec4f-…: Bad Request (400): Bad Request
```

| line | 400? | next `[SelfBake]` |
|---|---|---|
| 8731 | yes | **B** |
| 9061 / 9238 / 9438 | yes | — |
| 9463 | **no** | **A** |
| 9526 | yes | **B** |
| 9715 | **no** | **A** |

**A refused link ⇒ the broken bake. No refusal ⇒ the good bake.** Twice each way, no exceptions.
The 400s are the Inventar wears; the two clean ones are the Outfits wears — exactly what was
reported. The attachment path's own `AIS refused COF link for …` diagnostic never fired once in any
log: only the **wearable** path is refused.

## Cause — three failures stacked, all in `WearWearableAsync`

**1. The refusal was invisible.** `InventoryManager.CreateLinkAsync` returns `Task<InventoryItem?>`
and signals an AIS rejection by returning **null** — it does not throw (`InventoryManager.cs:1663`:
the AIS call's `(success, created)` goes through `WrapItemCreatedCallback`, and a failure lands on
`tcs.TrySetResult(null)`). `WearWearableAsync` discarded the return value.

**2. So it went on to lie to the simulator.** It sent `AgentIsNowWearing` including the skin, then
nudged a server re-composite (`ScheduleRebakeAfterWearableEdit` → `SendServerAppearanceUpdateAsync`,
`cof_version` to the `UpdateAvatarAppearance` cap). The server re-composites **from the Current
Outfit Folder**, which never received the link.

**3. And it had already thrown away the old skin.** `RemoveCofLinksOfWearableTypeAsync` ran *first*,
so on a refusal the COF is left with **no skin of that type at all** and the server bakes the
default. That is the washed-out body — not a wrong skin, an absent one. It also explains why only
body parts looked catastrophic: for a clothing layer a refusal merely does nothing.

## Fix

**Create the link first, verify it, and only then remove the old one.** A refusal now leaves the
avatar exactly as it was, tells the user (`WearableEditUnavailable`), and sends nothing.

The COF-link logic the attachment path grew during BUG-INV-01 is extracted and now shared:

- `ResolveCofLinkTargetAsync` — walk a link chain up to 4 hops down to the base item (AIS rejects a
  `linked_id` that is itself a link, and one hop is not always enough), then copy a `#Library` item
  into our own inventory and link the copy, as `LLAppearanceMgr::wearItemsOnAvatar` does.
- `CreateCofLinkAsync` — writes the link, checks the return, and on null logs the full identity of
  what was refused: `assetType`, `invType`, `isLink`, `owner`, `mine`, `perms`, `parentFolder`.
- `FindCofLinkTo` — a wear of something already worn no longer piles up a duplicate COF link (which
  `CollectWornWearablesFromCof` would then report to the simulator twice); it nudges the
  re-composite and stops.

`EnsureCofLinkForItemAsync` (attachments) is rewritten on top of the same three, unchanged in
behaviour. `WearWearableAsync` (wearables) gets all of it for the first time — **the hardening was
written for one path in BUG-INV-01 and only ever applied there**, which turned out to be exactly why
the identical item links from an outfit and not from the inventory (see below).

Also fixed in passing: the layer-order token for a body part is now always index 0. It used to be
computed from "how many of this type are worn", which, with the removal reordered, would have made
a replacing body part `@101` on top of a stack that cannot exist.

## The cause, confirmed (`v0.21.34`, same day)

The next session settled which of the two it was, in one line and with **zero** `Bad Request` in the
whole log:

```
[Appearance] copied Library item 'VELOUR & leLAPEAU - Lupita Skin' into your inventory (c373dc25-…)
```

**The skins are `#Library` items.** Second Life's modern starter avatars ship the Legacy mesh body,
the LeLutka head and the VELOUR skins in the Library — which is exactly what the reported inventory
list was full of, every row marked *(no modify)*. A Library item is owned by the Library account, so
AIS refuses to link it into your COF; the reference viewer copies it into your inventory first and
links the copy. SLNG has done that since BUG-INV-01 — in `EnsureCofLinkForItemAsync`, i.e. **for
attachments only**. `AttachItemAsync` returns into `WearWearableAsync` *before* reaching its Library
branch, so a Library **wearable** never got the copy.

That also explains the outfit-versus-inventory split precisely: the outfit's link already pointed at
an owned copy made earlier, the Inventar row pointed at the Library original.

Two skin changes in that session, each `replaced 1 worn Skin`, each followed by its own distinct
bake set. **Reported in-world: *"Funktioniert jetzt super!"***

## Files

- `src/SLNG.Net/GridSession.cs` — `WearWearableAsync` reordered + return checked;
  `ResolveCofLinkTargetAsync`, `CreateCofLinkAsync`, `FindCofLinkTo` extracted;
  `EnsureCofLinkForItemAsync` rebuilt on them.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.21.34-alpha`.

## Acceptance criteria

- [x] A refused COF link changes nothing: no `AgentIsNowWearing`, no re-composite nudge, the old
      body part stays on.
- [x] The user is told (`WearableEditUnavailable`) instead of seeing a silent success.
- [x] The refusal logs the item's full identity.
- [x] Wearing an already-worn item writes no second COF link.
- [x] **In-world:** wearing a skin from the Inventar tab changes the avatar correctly
      (2026-09-09, `v0.21.34` — zero refusals, two clean skin swaps).

## Related

- **BUG-INV-01** — where `ResolveCofLinkTargetAsync` / `CreateCofLinkAsync` come from; they were
  written for attachments and never reached the wearable path.
- **BUG-INV-02** — the change that made this reachable at all: before it, an outfit could not wear a
  skin, so the two paths could not be compared.
- **FEAT-AVATAR-01** — the *"Swapping skin / shape updates the avatar"* criterion.
