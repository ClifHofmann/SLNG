# [BUG-INV-02] Wearing an outfit never changes clothing or body parts

- **Feature ID:** `BUG-INV-02`
- **Track:** `net/ui`
- **Status:** `✅ Done` (confirmed in-world 2026-09-09, `v0.21.34`)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Symptom

Reported live 2026-09-09:

> *"beim wechseln von einem skin über den wechsel des outfits passiert nix, wenn ich die manuell
> anlege gehts"*

Changing a **skin** by wearing a saved outfit does nothing at all. Wearing the same skin item by
hand works. The status line even reported success — it counted only the attachments it sent.

## Cause — a Phase-2 skip that outlived Phase 2

Both wear paths refused system wearables on purpose:

```csharp
// WearOutfitAttachmentsAsync
if (assetType is AssetType.Clothing or AssetType.Bodypart)
    continue;                       // "waits on FEAT-AVATAR-01 Phase 2"

// ReplaceWornWithOutfitAttachmentsAsync
var targetObjs = contents
    .Where(w => w.Category is WornCategory.Attachment or WornCategory.Hud)   // wearables dropped
```

That was correct when FEAT-INV-04 shipped: `AttachItemAsync` could not put a Clothing/Bodypart
layer on at all, and an `Attach` for one is a server-side no-op. FEAT-AVATAR-01 then made it
possible — `AttachItemAsync` routes a wearable to `WearWearableAsync` (COF link +
`AgentIsNowWearing` + a debounced rebake) — and the **take-off** half was updated for it
(`RemoveOutfitFromWornAsync` grew a wearable pass), but the two **wear** halves were not. So the
one direction nobody re-tested kept silently dropping every skin, shape, shirt and tattoo in the
outfit.

Nothing was broken, which is why it survived: the code did exactly what its name and its comment
said. It only became a bug when the feature it was waiting for landed.

## Fix

`AttachItemAsync` already classifies each item, so the paths hand it everything and stop
classifying themselves.

- **`WearOutfitAsync`** (was `WearOutfitAttachmentsAsync`) — wears every entry of the outfit.
- **`ReplaceWornWithOutfitAsync`** (was `ReplaceWornWithOutfitAttachmentsAsync`) — detaches worn
  attachments the outfit lacks, removes worn **clothing** it lacks, then puts on everything in the
  outfit that is not already on, and writes the COF folder-link as before. Returns `(Removed, Worn)`.
- Both were renamed because the old names now describe the bug rather than the method.

**Body parts are never taken off.** An avatar always has exactly one shape, skin, hair and eyes;
`RemoveWearableAsync` refuses to remove one (and would raise `WearableEditRefused` at the user for
each). An outfit that lists no skin means "keep the one you have". The outfit's own body parts
replace the worn ones *in place* as they go on — `WearWearableAsync` drops the old COF link of the
same wearable type first.

### Wear order is not arbitrary

`WearWearableAsync` gives a new layer the index *"however many of that type are already on"*, i.e.
the top of the stack — so **the order items are worn in becomes the avatar's layer stack**. Wearing
a saved outfit in whatever order AIS listed it would rebuild a five-layer tattoo outfit upside down,
which is the exact failure [`WearableLayerOrder`](file:///E:/Git/SLNG/src/SLNG.Net/WearableLayerOrder.cs)
was written for (two opaque head skins, wrong one on top, measured 2026-08-31).

`GridSession.OrderOutfitForWearing` (pure, 7 unit tests) sorts:

1. **Body parts first** — everything else composites over them.
2. **Clothing, per type, bottom layer first**, by the viewer's `build_order_string` token. That
   token lives on the outfit folder's **link**, not on the target item, so
   `OrderOutfitTargetsForWearing` reads the links out of the already-fetched store node.
3. **Attachments last**, together with any row whose target the store has not resolved — dropping
   those would silently lose an item, and `AttachItemAsync` re-classifies each one when it gets
   there.

Removal runs before the wear pass so a new layer's stack index counts only the layers the outfit
itself wants.

### Cost

One `AgentIsNowWearing` per wearable, as with a manual wear, but the rebake is debounced 1.8 s and
cancel-restarted (`ScheduleRebakeAfterWearableEdit`), so a whole outfit still costs exactly one
re-composite.

## Files

- `src/SLNG.Net/GridSession.cs` — `OutfitWearRow`, `OrderOutfitForWearing`,
  `OrderOutfitTargetsForWearing`, `WearOutfitAsync`, `ReplaceWornWithOutfitAsync`; stale
  `RemoveOutfitFromWornAsync` summary corrected (it has removed wearables since FEAT-AVATAR-01).
- `app/scripts/UI/InventoryPanel.cs` — call sites; the status lines that promised
  *"Kleidung & Körper folgen mit FEAT-AVATAR-01 Phase 2"* now report what actually happened.
- `tests/SLNG.Net.Tests/OutfitWearOrderTests.cs` — new, 7 tests.
- `tests/SLNG.Net.Tests/GridSessionTests.cs` — renamed call sites.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.21.33-alpha`.

## Acceptance criteria

- [x] Wearing an outfit containing a skin sends the wearable (unit-tested: ordering; the send path
      is `AttachItemAsync`, already covered).
- [x] Body parts are never removed by a "replace outfit".
- [x] A stacked multi-layer outfit is worn bottom-first by its saved order token.
- [x] **In-world:** changing the skin through an outfit changes the avatar — *"Funktioniert jetzt
      super!"*, 2026-09-09. It took **BUG-INV-03** as well: this change made the outfit route wear a
      skin, and immediately exposed that the wear itself was refused for a `#Library` item and left
      the avatar with none.
- [ ] **In-world:** replace one outfit with another and confirm nothing worn goes missing.

## Related

- **FEAT-AVATAR-01** — the criterion *"Swapping skin / shape updates the avatar"*. Manual swap was
  confirmed working here; this bug is why the outfit route was not.
- **FEAT-INV-04** — the outfits browser these actions belong to.
