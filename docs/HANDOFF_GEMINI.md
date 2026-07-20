# HANDOFF — Avatar Rigging / Rigged-Mesh Skinning (for Antigravity/Gemini)

**Date:** 2026-06-30 (updated 2026-07-02) · **Branch:** `feat/sculpt-rendering` · **Author:** Claude

> **Full blow-by-blow attempt log now lives in
> `C:\Users\cid80\.gemini\antigravity-ide\brain\54dd6a30-7822-4b55-9b01-5db6ad7ef960\debugging_log.md`**
> (Gemini's 5 attempts + Claude Sessions 2/2b/3/4). Read/update THAT file for the moment-to-moment
> record; this doc stays the higher-level technical writeup. Session 3 (2026-07-02 pm) found and
> fixed a second, independent bug: overlapping redundant `UpdateAttachment` calls (LibreMetaverse
> fires `ObjectUpdate` several times per object during rez) raced and leaked duplicate/orphaned
> rigged-mesh instances stacked on the real one (z-fighting, doubled alpha blending). Fixed with
> an `_attachmentMeshIds` guard in `AvatarRenderer.cs`, same pattern as `UpdateVisual`'s existing
> texture/animation dedup. Session 4 (2026-07-02 evening) did a deep viewer-source dive per user
> request and found a THIRD independent bug: rigged-mesh normals were transformed by the raw
> `BindShapeMatrix` instead of its inverse-transpose (verified against `llface.cpp`'s
> `getGeometryVolume` — the viewer does exactly this for normals/tangents, plain matrix only for
> positions). Wrong for any mesh with non-uniform bind-shape scale (3 of 5 test meshes qualify,
> up to ~4:1) — causes incorrect lighting/shading independent of vertex-position correctness.
> Fixed in `BuildRiggedMeshInstance`. **Still untested in a clean client run** — see
> debugging_log.md "Status Quo" for exact verification steps before trying anything else.

This continues the avatar-rendering work. Read this top-to-bottom before touching code.
The hard part (root-cause of the "exploding attachment") is fully diagnosed below — you do
**not** need to re-derive it.

---

## 0. TL;DR

- **Body skin textures now render correctly.** Root cause was a missing **UV V-flip**
  (SL/OpenGL bottom-left origin vs Godot top-left). Fixed + committed (`b281ba2`).
- **3 of 4 worn/rigged meshes render correctly** with our existing skinning. Our skinning
  math is **verified correct** (matches the SL viewer). Do not rewrite it blindly.
- **1 mesh (`984717fb…`) flies off to z≈−171** ("the blob"). Root cause: it is rigged to
  **non-standard joint positions** and needs **joint-position overrides**, which we do not
  implement yet. This is the one real open problem. See §3–§4.
- **There are uncommitted changes in `app/scripts/AvatarRenderer.cs`** (worn-mesh V-flip +
  restored sanity guards). They are **correct and build-clean but UNVERIFIED in-world**,
  because the last test run used a **stale DLL** (see §5 — critical, this wasted a cycle).

---

## 0a. FINAL UPDATE — Session 2b (2026-07-02, Claude): §0b's "broken mesh" claim is WRONG

User reported the face mesh works in the official SL viewer → re-derived everything by
**numerically replaying the exact viewer chain** `dst = (v·BSM)·Σw(invBind·jointWorld)` with
standard joint worlds built from avatar_skeleton.xml (incl. collision volumes, scales). Result:
**ALL five worn meshes compute to correct on-body positions with the standard skeleton.**
`984717fb` = 3×8×6 cm at z≈1.68 (a small FACE attachment — the "orange thing at the mouth",
previously misread as a distorted head), `30430498` = earring, `22a033f2` = pelvis harness,
`3dff0962` = tail, `20037a41` = tiny head object.

The trick everyone missed: its `invBind[mPelvis]` 3×3 is **scale 0.01 + an axis permutation**
(invisible when only dumping translations!) which exactly cancels the huge bind shape
(+168 m, ×7.8). The mesh was likely rendering CORRECTLY all along; the AABB-based guards
(both Gemini's and mine) hid it. **The static bind-pose AABB says nothing about where a
skinned mesh renders — never reject on it.** Guard is now removed (diagnostic print only);
culling is covered by the generous CustomAabb.

So: no joint overrides needed for these meshes, nothing "broken". Remaining real issues to
verify in-world after a CLEAN rebuild (§5): correct texture on the yellow thing near the leg
(likely the tail with its tint/texture not resolving, NOT a geometry bug) and general worn-mesh
texture correctness after the V-flip. §0b below is kept for history but its "broken/inconsistent
mesh" conclusion and the extent/centre guard it describes are OBSOLETE.

---

## 0b. UPDATE — Session 2 (2026-07-02, Claude) — PARTIALLY OBSOLETE, see 0a

Continued after Gemini's `debugging_log.md` (Versuche 1–5). Key new findings + changes:

- **`AltInverseBindMatrices` is EMPTY for every worn mesh we can inspect** (verified by
  decoding the cached `.mesh` files with LibreMetaverse: `hasAlt=0 mats`). So the SL viewer's
  joint-override path (§3) has **no data to work with for these OpenSim meshes**. Gemini's
  Versuch 1 (using AltIBM as the inverse-bind) was doubly wrong: they're empty here anyway, and
  even when present they are *joint-position overrides*, not skinning matrices.
- **The correct skinning model is restored** (Gemini's Versuch 5 had removed the bind-shape
  application): bind-shape IS applied to the vertices; the regular `InverseBindMatrices` alone
  go into the Godot Skin bind via `C⁻¹·ibm·C`. This is the viewer model and renders the 3
  standard meshes correctly. All the `[RiggedMesh DEBUG]`/decode-log spam was removed.
- **Joint-position overrides implemented (correct but currently inert):**
  `AvatarRenderer.ApplyJointPositionOverrides` reads `AltInverseBindMatrices[j].translation` as
  the joint's overridden LOCAL position and writes it into the skeleton rest + a per-visual
  `JointPosOverrides` map that `ApplyShape` re-applies. This mirrors
  `LLVOAvatar::addAttachmentOverridesForObject` + `LLJoint::updatePos`. It is a **no-op for the
  current meshes** (no AltIBM) but is the right foundation for real SL mesh bodies that DO ship
  AltIBM. Guarded (only fires when `alt.Length==jointCount` and above the 0.1 mm threshold).
- **`984717fb…` / `20037a41…` (joints=70) are inconsistent/broken meshes:** bind-shape
  translation ≈ +168 m combined with a **standard** `mPelvis` inverse-bind (−1.07) and
  non-standard collision-volume inverse-binds (`PELVIS`=−6.09, `BUTT`=−9.67). Nothing cancels
  the +168 m, and there's no AltIBM to override joints, so no correct skinning is possible.
  These are now **cleanly hidden** by the guard instead of flying off as a blob.
- **Guard rewritten** to test the **bind-pose** extent/centre (post bind-shape), not the raw
  vertex AABB: `maxExtent > 50 m` OR `bind-pose centre dist > 20 m` → skip. Raw verts are a
  normalized ±0.5 (or huge ±50 "position domain") cube, so the old raw-AABB guard was meaningless.
- **Worn/rigged + static attachment UVs now V-flipped** (`1 - uv.Y`), same as the body parts.

**Net effect after a clean rebuild:** the 3 standard rigged meshes + tail render with correct
textures; the 2 broken meshes are hidden (no blob) → the system avatar shows through, correctly
textured. **This does NOT make `984717fb…` render** — that needs either upstream AltIBM data or
a decision that the mesh is junk. All Session-2 changes are **uncommitted and UNVERIFIED in-world**.

**Next:** clean rebuild via `run-client.ps1` (see §5!), confirm in the log
`[RiggedMesh] skipping broken mesh (…)` for the flyers and correct textures on the rest, then
screenshot. Open question for the user: are `984717fb`/`20037a41` their mesh body/head (worth
chasing AltIBM / a fitted-mesh collision-volume fix) or disposable OpenSim junk?

---

## 1. Git state

- HEAD = `b281ba2` `fix(avatars): V-flip baked-texture UVs so skin maps to correct body parts`
  - Contains: the **body-part UV V-flip** (verified working in-world) + a snapshot of the
    prior session's WIP (camera HUD, texture channel decode, prim UV repeat/offset,
    extended `FaceTexture`). This commit is good.
- **Uncommitted** (working tree, `app/scripts/AvatarRenderer.cs` only): two changes, both
  **build-clean (`dotnet build app/SLNG.App.csproj` → 0 errors) but NOT yet verified in-world:**
  1. **Worn-mesh UV V-flip** in `BuildRiggedMeshInstance` (~line 706): `uv.Y` → `1.0f - uv.Y`.
     Same fix as the body parts, so worn mesh (mesh head/clothing) textures aren't mirrored.
  2. **Restored two rigged-mesh sanity guards** in `BuildRiggedMeshInstance` that the previous
     (Gemini) session had removed:
     - bind-shape translation > 5 m → skip (catches corrupt uploads that fling the mesh away).
     - `centerDist > 8 m` (AABB centre far from avatar root) → skip. This is what catches the
       z=−171 blob. Extent limit kept generous at 50 m so large *correctly-placed* meshes pass.

  ⚠️ These guards are a **pragmatic band-aid** (they HIDE the bad mesh). The user pushed back
  on hiding it and wants the *correct interpretation* (§3). Decide with the user whether to
  keep the guards or replace them with real joint-override support before committing.

---

## 2. What was actually wrong with the body (SOLVED — for reference)

The `.llm` body meshes feed UVs in **SL/OpenGL convention** (origin bottom-left, V grows up),
but Godot/Vulkan samples top-left and Magick decodes row 0 = top. Result: baked skin landed
**vertically mirrored** (front-torso texture on the back, navel mid-spine). Fix = `1 - uv.Y`.

- Body parts: `BuildPartResources`, `app/scripts/AvatarRenderer.cs:839` (committed).
- Worn/rigged mesh: `BuildRiggedMeshInstance`, ~`:706` (uncommitted).
- **Prims / world objects** (`ObjectRenderer` / `PrimMeshService`) still do NOT flip V. They
  *probably* also need it but it's masked by tiled/symmetric textures. **Not yet verified.**
  If you confirm prims are mirrored, the clean fix is to flip **once in the texture decode**
  (`AssetService.DecodeTexture`) and remove the per-mesh `1 - uv.Y` flips — but only after
  verifying, because it touches the whole world.

Textures themselves are fine: the baked skin J2Cs decode to correct skin tones, fully opaque
(verified by decoding the cached bakes — see scratchpad `TexProbe`). It was never a decode/
alpha/colorspace problem.

---

## 3. The real open problem: rigged-mesh joint-position overrides

### Symptom
One worn mesh renders as a giant (~8 m) blob ~171 m from the avatar (Godot AABB
`pos (-3.9, 9.0, -171.4) size (7.8, 2.7, 6.0)`, logged as `[RiggedMesh] joints 70/70 …`).

### Our skinning is CORRECT (do not rewrite the matrix math)
The SL viewer (`LLSkinningUtil::initSkinningMatrixPalette` + `getPerVertexSkinMatrix`) does:
```
v_world = (v · bindShapeMatrix) · invBind[j] · jointWorld[j]      (row-vector convention)
palette[j] = invBind[j] · jointWorld[j]      (invBind FIRST)
```
bind_shape is pre-applied to vertices; there is **no** alternate-bind/override logic *inside*
`LLSkinningUtil` itself. We replicate this:
- We pre-apply `bindShape` to positions: `AvatarRenderer.cs:686`.
- We convert each `invBind` into a Godot Skin bind via change-of-basis
  `RowMatrixToTransform(SlToGodotInv * ibm * SlToGodot)` (`:660-665`, helpers `:889-911`).
- I verified this conversion by hand: it yields `B = C · ibm^T · C⁻¹` for **both** the 3×3
  rotation **and** the translation. Combined with Godot's `boneGlobal·B·v`, it equals the
  viewer's formula. **The math is right.** 3 of 4 meshes prove it (they render correctly).

### Why the 4th mesh breaks
`invBind·jointWorld` only cancels to identity at rest **if our skeleton's joint world
transforms equal the skeleton the mesh was rigged to.** Mesh `984717fb…` is rigged to
**non-standard joint positions** (≈6× the standard offsets — see data §4). Our skeleton stays
in standard rest, so the offset doesn't cancel and the mesh flies off.

### How the original viewer solves it
**`LLVOAvatar::addAttachmentOverridesForObject`** (in `indra/newview/llvoavatar.cpp`). Before
skinning, the viewer reads each rigged mesh's intended joint positions (= `invBind⁻¹`, or from
`mAlternateBindMatrix`) and **overrides the avatar skeleton's joint positions** via
`LLJoint::addAttachmentPosOverride`. Then `invBind·jointWorld` matches and the mesh sits right.
**We do not do this at all.** That is the missing feature.

References (fetch raw from `github.com/secondlife/viewer`, branch `main`):
- `indra/newview/llskinningutil.cpp` — the per-vertex skin matrix math (we match it).
- `indra/newview/llvoavatar.cpp` — `addAttachmentOverridesForObject`, joint pos overrides.
- `indra/llmath/lljoint.cpp` — `addAttachmentPosOverride` / `getWorldMatrix`.
- Wiki: `wiki.secondlife.com/wiki/Mesh/Rigging_Fitted_Mesh` (collision-volume bones).

### Important caveat
`984717fb…`'s override would be **~6 m** — real fitted-mesh overrides are cm-scale. This mesh
is either a huge effect mesh or a broken/troll OpenSim upload; even with overrides it'd be a
7.83 m object. **Before building the override feature, confirm whether this mesh is worth it**
(check the item name in inventory / another viewer). The user was going to clarify this.

### What we DON'T have
Our `MeshSkin` DTO (`src/SLNG.Assets/MeshData.cs`) exposes `JointNames`,
`InverseBindMatrices`, `BindShapeMatrix`, `PelvisOffset` — but **not** the alternate bind
matrices. Joint positions can still be derived as `invBind[j]⁻¹`. If you implement overrides,
note the conflict problem: **one shared `Skeleton3D` drives the body + all attachments**, so
per-mesh overrides can fight each other and the body. The viewer resolves this with priority/
first-wins; design this with the user (or the `architect`/`asset-pipeline` agents).

---

## 4. Hard evidence (decoded the 4 cached attachment meshes)

Raw vertices for ALL meshes are normalized to `[-0.5, 0.5]`; real size/placement lives in
bindShape + invBind.

| Mesh (entity) | Joints | BindShape translation | BindShape scale | max \|invBind T\| | invBind[mPelvis] z | Verdict |
|---|---|---|---|---|---|---|
| `3dff0962…` (tail) | 72 | (0, 0.08, 0.36) | small | 55* | — | ✅ renders ok |
| `30430498…` | 14 | (0, −0.09, 1.69) | ~1 | 1.8 | — | ✅ ok |
| `22a033f2…` | 86 | (−0.02, 0.9, 0) | ~0.35 | 46* | −1.07 (standard) | ✅ ok |
| **`984717fb…`** | **70** | **(0, 168.4, 10.4)** | **(7.83, 6.02, 2.71)** | **28** | **−6.09 (≈6× off)** | 🔴 **the blob** |

\* the "max |invBind T|" can be inflated by collision-volume bones; the discriminator that
matters is `invBind[mPelvis].z` ≈ −1.07 (standard) vs −6.09 (non-standard) and the bindShape
translation magnitude.

Standard pelvis invBind z ≈ **−1.07**. `984717fb…` has **−6.09** → rigged to a ~6× skeleton.
That + bindShape Y=+168 is exactly why it lands at Godot z=−168/−171.

---

## 5. ⚠️ CRITICAL GOTCHA: stale assembly (cost us a full test cycle this session)

The Godot client **runs a stale C# DLL** unless you do a **clean** rebuild.
- The running DLL is `app/.godot/mono/temp/bin/Debug/SLNG.App.dll`.
- An **incremental** build (Godot editor Play button, or plain `godot --path app`) can write a
  **fresh-timestamp DLL that is missing your latest edits.** Observed today: DLL mtime was 1 min
  *after* the source edit, yet the new code was not in it.
- **Fix:** delete `app/.godot/mono` first, then `dotnet build app/SLNG.App.csproj`. This is
  exactly what **`tools/run-client.ps1`** does. **Always test via `run-client.ps1`**, and close
  any running client first (it locks the DLL).
- **Verify which build actually ran via LOG BEHAVIOUR, not byte-grep** (grep on the DLL gives
  false negatives — .NET stores string literals as UTF-16). E.g. for the guard fix, a correct
  build logs `[RiggedMesh] skipping absurd mesh (extent 7.8, dist 169)`. If that line is absent
  and the `z=-171` AABB line is still present, you're on the old DLL.
- Logs: `%APPDATA%\Godot\app_userdata\SLNG\logs\godot*.log` (newest = current run; UTF-8).

**Immediate next action regardless of direction:** close the client, run `tools/run-client.ps1`,
confirm the uncommitted guard/flip actually execute (check the log line above).

---

## 6. Reproduction tools (scratchpad, not in repo)

Two standalone probes were written to the session scratchpad (safe to recreate anywhere):
- **`TexProbe`** — decodes cached baked-skin J2Cs with `Magick.NET-Q8-AnyCPU 14.14.0`, prints
  mean RGB + alpha. Proved textures are fine (skin-toned, opaque). 5-channel SL bakes; decode
  reads ch 0,1,2 = RGB, ch 3 = alpha (correct).
- **`MeshProbe`** — decodes cached `.mesh` files via `LibreMetaverse 3.0.0` +
  `LibreMetaverse.Rendering.MeshFoundry 3.0.0` (namespaces: `LibreMetaverse`,
  `LibreMetaverse.Assets`, `LibreMetaverse.Rendering`). Produced the §4 table. Re-run it to
  inspect any mesh's bindShape/invBind/positions.

Caches:
- Meshes: `%APPDATA%\Godot\app_userdata\SLNG\cache\assets\<uuid>.mesh`
- Textures: `…\cache\assets\<uuid>_v5.j2c`

---

## 7. Recommended next steps (in order)

1. **Clean-rebuild via `run-client.ps1`** and confirm the uncommitted worn-flip + guards run
   (log check per §5). Take a front + behind screenshot.
2. If the blob is gone and the 3 good meshes + worn-mesh textures look right → **commit** the
   uncommitted change: `fix(avatars): V-flip worn-mesh UVs; restore rigged-mesh sanity guards`.
3. **Decide with the user** on `984717fb…`: (a) leave it guarded/hidden, or (b) implement
   **joint-position overrides** (§3) — the proper viewer feature. If (b): start by deriving
   joint positions from `invBind⁻¹`, design shared-skeleton conflict resolution, likely need to
   surface `mAlternateBindMatrix` through `MeshSkin` (check what LibreMetaverse `MeshSkinData`
   exposes). Consider the `architect` + `asset-pipeline` agents.
4. **Separately verify prim/world UV V-flip** (§2) and, if mirrored, move the flip into the
   texture decode and drop the per-mesh flips.

## 8. Key file/line map

- `app/scripts/AvatarRenderer.cs`
  - `BuildSkinnedMeshInstance` / `BuildPartResources` (`:772`, `:796`) — body parts; UV flip at `:839`.
  - `BuildRiggedMeshInstance` (`:642`) — worn/rigged mesh; invBind change-of-basis `:660-665`;
    bindShape applied `:686`; UV flip ~`:706`; sanity guards (uncommitted) near `:672` and `:725`.
  - Helpers: `SlToGodot`/`SlToGodotInv` `:889-893`, `RowMatrixToTransform` `:904`,
    `ComputeGlobalRestTransform` `:917`.
  - Baked-texture apply + bake→meshpart map: `LoadAndApplyTextureAsync` `:317`, map ~`:372`.
- `src/SLNG.Assets/AssetService.cs` — mesh decode `Decode` `:170`, `ConvertSkin` `:230`,
  `ToMatrix` `:252` (LMV matrices = row-major float[16], row-vector). Texture decode
  `DecodeTexture` `:467` (V-flip candidate spot).
- `src/SLNG.Assets/MeshData.cs` — `MeshSkin` DTO (no alternate-bind matrices yet).

---
*Conventions reminder (AGENTS.md): `src/` stays engine-agnostic (no `using Godot;`); the SL→Godot
V-flip and axis swap belong in `app/`, not `src/`. Conventional Commits with task id.*
