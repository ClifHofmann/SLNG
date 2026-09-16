# [BUG-RENDER-24] A mirror is a flag the sim sends, not a material we can recognise

- **Feature ID:** `BUG-RENDER-24`
- **Track:** `render/net`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-14 (Agni, *Millenium*): a framed wall mirror renders as a flat light-grey
panel with a faint ghost in it. Follow-up from the user, which is what redirected this:
*„der spiegel hat kein PBR“*.

`FEAT-RENDER-22` built the real-time hero probe — the only thing in SLNG that can render a
mirror, because it captures *from the mirror* rather than from the camera — and decided which
surface deserves it by **guessing from the material**: a glTF PBR material with roughness ≤ 0.25.

That guess matches nothing Second Life actually transmits. Measured in the reported session:

- **0** `[PbrMaterial]` lines — the region contains no glTF material at all
- **0** `[HeroProbe]` lines — the probe never armed once, with a mirror in frame the whole time
- **1289** distinct legacy Blinn-Phong materials, of which the strongest Environment Intensity
  anywhere is 100/255 ≈ 0.39

## How the reference viewer identifies a mirror

It does not look at materials. A mirror is an **explicit creator flag**, in the Reflection Probe
ExtraParams block:

```cpp
// llheroprobemanager.cpp:141 -- the hero probe's candidate list
if (vo && !vo->isDead() && vo->mDrawable.notNull()
    && vo->isReflectionProbe() && vo->getReflectionProbeIsBox()) { ... }

// llvovolume.cpp:4536 -- what puts an object on that list in the first place
else if (mReflectionProbe.isNull() && getReflectionProbeIsMirror())
{   if (!mIsHeroProbe) mIsHeroProbe = gPipeline.mHeroProbeManager.registerViewerObject(this); }

// llprimitive.h:186-191
FLAG_BOX_VOLUME = 0x01,  FLAG_DYNAMIC = 0x02,
FLAG_MIRROR     = 0x04,  // "This probe is used for reflections on realtime mirrors."
```

And once a mirror exists, the viewer feeds it into the **legacy** reflection at full weight —
which is exactly the content type this region is made of:

```glsl
// reflectionProbeF.glsl:887-888, inside sampleReflectionProbesLegacy
tapHeroProbe(glossenv,  pos, norm, glossiness);   // PBR path: faded in by glossiness
tapHeroProbe(legacyenv, pos, norm, 1.0);          // legacy env: full weight, no gate
```

So the two halves were wrong in the same direction: we looked for mirrors among glTF materials,
and SL puts them among ordinary flagged prims whose faces are Blinn-Phong.

## The protocol gap

**LibreMetaverse does not parse this block.** `ExtraParamType.ReflectionProbe = 0x90` is in its
enum (`EnumsPrimitive.cs:242`), but `Primitive.SetExtraParamsFromBytes` has branches only for
Flexible / Light / LightImage / Sculpt / Mesh / ExtendedMesh and steps over everything else by its
declared length. The high-level API therefore cannot express "this object is a mirror" at all.

SLNG already reads raw ExtraParams bytes for one other thing — the Light block's presence, because
`Primitive.Light` latches — so this rides the same scan.

Wire layout (`LLReflectionProbeParams::pack`, llprimitive.cpp:1837): F32 ambiance, F32 clip
distance, U8 flags, little-endian, inside the usual envelope of one count byte then per entry a
UInt16 type and a UInt32 length.

## Acceptance Criteria

- [ ] An object whose Reflection Probe block carries `FLAG_MIRROR` is what the hero probe renders
      from, whatever its faces are made of. *(Unexercised: no flagged mirror has been found in-world
      yet — the reported one carries no probe block.)*
- [x] A flagged mirror outranks a material-guessed one regardless of distance.
- [x] The click dump reports the block (`probe=[MIRROR box ambiance=… clip=…]`), so its ABSENCE is
      as visible as its presence.
- [x] No mirror flag anywhere in the scene ⇒ behaviour is unchanged from `FEAT-RENDER-22`.
- [x] ~~The reported wall mirror reflects the room~~ — moved to `BUG-RENDER-25`, which is its
      actual cause.

## Technical Specs & Affected Files

- `src/SLNG.Core/ReflectionProbeParams.cs` *(new)* — the block, its flags and its wire size.
- `src/SLNG.Net/GridSession.cs` — `ExtraParamsReflectionProbe` reads block `0x90` out of the raw
  ObjectUpdate bytes in the existing scan; the result rides `ObjectUpdateEvent`.
- `src/SLNG.Core/GridEvents.cs`, `WorldSimulation.cs`, `Components/PrimitiveComponent.cs` — carried
  to the world model, applied under the same `IsFullUpdate` gate as the other ExtraParams-derived
  fields (a terse update carries no ExtraParams; applying its null would un-mirror every mirror
  the moment something near it moved).
- `app/scripts/ObjectRenderer.cs` — `_flaggedMirrors` maintained where the component is already in
  hand, so the per-visual cull sweep stays one hash lookup; a flagged mirror outranks the glTF
  roughness guess before distance is considered. The guess is kept as a fallback, not deleted.
- `tests/SLNG.Net.Tests/ReflectionProbeExtraParamsTests.cs` *(new, 6 tests)* — values, a block
  behind other blocks, absence, non-mirror flags, a truncated block (refused rather than reading a
  flag byte out of whatever followed), and a longer-than-known block (read, not skipped).
- `app/scripts/Boot.cs` — `AppVersion` `v0.22.129-alpha`.

### Known limitation: which mirror gets the one probe

SL affords exactly one hero probe, so a room with two mirrors makes the selection rule visible.
The selection here is "nearest flagged mirror that is visible and not resource-released". The
reference viewer additionally requires the object to be inside the view frustum and weighs how far
along the view axis it sits (`center_distance`, llheroprobemanager.cpp:146-158), so standing
between two mirrors it follows the one being looked at. Worth porting once the flag itself is
confirmed to arrive; doing it first would be building on an unverified assumption.

## Open

**Measured 2026-09-14, and the answer is no.** The click dump on `v0.22.129-alpha` reports no
`probe=` clause on any of the five parts of the reported mirror — it carries no Reflection Probe
block at all. That object is a fullbright + Shiny HIGH fake mirror; see `BUG-RENDER-25`, which is
what actually fixes it.

This entry therefore stands as parity work that is **correct but unexercised**: the decode, the
model and the candidate selection are in and tested, and the first real flagged mirror SLNG meets
will use them, but nothing in-world has confirmed the path end to end yet. The diagnostic is the
lasting part — the absence of the block is now as visible as its presence, which is how this was
settled in one click instead of another round of guessing.

What Firestorm shows for the same object is now captured, and it is the room *behind the camera*:
a window, furniture, floorboards. No probe sitting at the camera can
produce that, so the object is being rendered by Firestorm's hero probe — which it only grants to
an object carrying `FLAG_MIRROR`. That makes the flag's presence very likely but not measured;
the click is what measures it. If it turns out to be absent, the premise above is wrong and the
next question is which OTHER object in that room is the flagged one.

## Sub-tasks / Progress

- [x] Root-caused against llheroprobemanager.cpp / llvovolume.cpp / llprimitive.h.
- [x] Protocol gap confirmed by reading LibreMetaverse's own parser, not assumed.
- [x] Decode + model + candidate selection + tests + diagnostic.
- [x] Confirmed in-world (2026-09-16).
