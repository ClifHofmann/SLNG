# [BUG-RENDER-07] `LegacyMaterialId` was silently lost for every object's FIRST load

- **Feature ID:** `BUG-RENDER-07`
- **Track:** `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `main`
- **Version:** `v0.20.11-alpha`

## Overview & Goal

Follow-up to the still-open BUG-RENDER-06 conifer flicker: the user sent a Firestorm Build-tool
Texture-tab screenshot of the SAME reported tree — **Blinn-Phong** material selected (not PBR),
`Alpha-Modus: Alpha-Masking`, cutoff `100`, a real normal map attached. That flatly contradicts
what this app's own `[LegacyMaterial]` diagnostic log showed for that exact object (BUG-RENDER-06's
follow-up, previous entry): zero lines, for either tree, ever. A face cannot have a real legacy
material AND never trigger that log line if the code is reading its data correctly — so the
material clearly exists on the sim/asset but SLNG was never seeing it.

## Root cause

`SLNG.Core.Components.PrimitiveComponent`'s constructor never had a `legacyMaterialId` parameter
at all, despite `LegacyMaterialId` being a real, declared property on the class. `WorldSimulation
.ApplyObjectUpdate` has two branches for handling an incoming object event:

- **First time this entity is seen** (`prim == null`): builds a brand new `PrimitiveComponent` via
  the constructor — which had no way to receive `e.LegacyMaterialId` at all, so the property stayed
  at its default `Guid.Empty` no matter what the incoming event carried.
- **Every later update to the same entity** (the `else` branch): sets every field individually,
  including `prim.LegacyMaterialId = e.LegacyMaterialId;` — correctly, but only reachable once the
  object has already been created once.

So `LegacyMaterialId` was ONLY ever populated on a *second or later* update to an object — never on
its first load. A tree that has been standing since before the session started, loads once via its
initial `ObjectUpdate`, and never happens to receive an incidental full resend afterward keeps
`LegacyMaterialId == Guid.Empty` for the rest of the session — silently falling through to
`ObjectRenderer.ApplyAlphaCutout`'s pixel-content `DetectAlpha()` guess (a hardcoded 0.5 cutoff
instead of the creator's real declared value, no normal/specular map ever applied) rather than the
real, richer legacy material the creator actually authored.

`RenderMaterialId` (the PBR sibling) was NOT affected — it was already a real constructor
parameter and correctly passed at the one call site. This bug was specific to the legacy-material
plumbing.

## Acceptance Criteria

- [x] `PrimitiveComponent`'s constructor accepts `legacyMaterialId` and sets `LegacyMaterialId`
      from it, so an object's very first load carries the same data its second update already did.
- [x] The one call site (`WorldSimulation.ApplyObjectUpdate`'s `new PrimitiveComponent(...)`
      branch) passes `e.LegacyMaterialId` through.
- [x] The new parameter is appended at the end of the (already long, all-optional) parameter list
      rather than inserted logically after `renderMaterialId`, since there is exactly one call site
      in the whole repo and appending costs nothing while eliminating any risk of silently
      reordering an existing positional call elsewhere.
- [x] `dotnet build SLNG.sln` / `app/SLNG.App.csproj`: 0 warnings. `dotnet test`: 563/563.
      `dotnet format` clean, shader-globals clean, `--selftest` 29/29 (nothing render-mode related
      touched).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Core/Components/PrimitiveComponent.cs` | Constructor gains `Guid legacyMaterialId = default`, sets `LegacyMaterialId = legacyMaterialId;`. |
| `src/SLNG.Core/WorldSimulation.cs` | `ApplyObjectUpdate`'s object-creation branch passes `e.LegacyMaterialId` into the constructor call. |

### Design notes

- **Audited the rest of the `else` branch against the constructor while here.** Every other field
  the `else` branch sets (`Scale`, `ProfileCurve`, `IsMesh`, `MeshId`, `TextureId`,
  `RenderMaterialId`, `ColorTint`, `RepeatU/V`, `OffsetU/V`, `Rotation`, `Shape`, `IsSculpt`,
  `SculptId`, `SculptType`, `TexGen`, `Fullbright`, `Faces`) already had a matching constructor
  parameter — `LegacyMaterialId` was the one exception, not a symptom of a wider pattern across
  this component.
- **Likely broader impact than just this one tree.** Any object with a legacy Blinn-Phong material
  that loads once and is never incidentally re-sent by the sim afterward would have silently lost
  its normal map, specular map, and real alpha-mask cutoff for the rest of the session — this is
  not scoped to trees or to this one report, just first found there.

## What the tests guarantee

Nothing new pins this specific field — `PrimitiveComponent`/`WorldSimulation` sit in `SLNG.Core`,
in `tests-rules`' scope, but no existing test constructs a `PrimitiveComponent` and asserts
`LegacyMaterialId` survives a fresh vs. a repeat `ApplyObjectUpdate`. Worth a follow-up unit test
(`WorldSimulation` already likely has fixtures for the create/update branches to extend). The full
563-test suite passing unmodified confirms nothing else regressed.

## Still open

- **Not yet re-verified in-world** — the next test should show a real `[LegacyMaterial]` log line
  for this exact tree, with `alphaMode=Mask` and the real `100`-based cutoff instead of the
  DetectAlpha() guess.
- **This fix likely does NOT resolve BUG-RENDER-06's flicker on its own.** A legacy material's
  culling is untouched either way — `ObjectRenderer.cs`'s own comment on the legacy branch says
  the real viewer back-face culls legacy-materialed alpha faces exactly like everything else
  (same `lldrawpoolalpha.cpp` rule already re-verified for BUG-RENDER-06: culling lifts only for
  particles and explicitly double-sided *GLTF* materials — legacy Blinn-Phong has no equivalent
  flag at all). This fix corrects the alpha threshold and adds the missing normal/specular maps,
  which is worth having regardless, but the flicker's actual cause is still open.
- **A missing unit test** for the create-vs-update `PrimitiveComponent` field parity noted above.
