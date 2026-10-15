# [FEAT-RENDER-26] Apply glTF material overrides

- **Feature ID:** `FEAT-RENDER-26`
- **Track:** `render` (+ `net`)
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

**What an override is.** A PBR face names a material *asset* (`FaceTexture.RenderMaterialId`). Second Life lets the
Build tool, and creators' scripts, change how one face of such an object looks **without** changing the asset: a
per-face *override* with a base-colour tint, roughness, metallic, emissive colour, alpha mode and cutoff,
double-sided, texture swaps, and texture transforms. The simulator sends overrides in their own message, separate
from the object update.

**What SLNG does today.** It renders the asset only. Nothing in `src/` or `app/` consumes overrides. A recoloured
PBR face shows its asset colour. LibreMetaverse (LMV) 3.1.6 does decode overrides into its own `Primitive`, but SLNG
never reads that, and LMV's parser *rejects some payloads*: 16 `Failed to parse GltfMaterialOverride payload` /
`Unknown type marker ','` warnings in one Sirens session on 2026-10-15 (`godot.log`, 15 of them in one burst while the
region loaded). Since v0.27.59 they are printed once per session (`GridSession.FilterLibreMetaverseLog`).

**Goal:** a PBR face with an override looks as it does in Firestorm. Replace, clear, re-send and "the object arrived
after its override" behave as in the reference viewer. Two faces with equal overrides produce equal material keys
(FEAT-PERF-20 A1).

**Not in this task** (see "Open questions"): worn attachments and animesh (`AvatarRenderer`), per-slot texture
transforms other than base colour, sending overrides (`ModifyMaterialParams`), editing UI.

TPV Policy: this is render-only. Nothing is exported, and no asset is fetched that the material did not already
reference.

## What the reference viewer does (read this before coding)

All paths below are in the vendored viewer, `scratch/slviewer/` (HEAD `4ef9f8f1`, 2026-08-25). It is a shallow
clone: never `git pull` it.

### 1. How an override reaches the viewer

| Route | What | Cite |
|---|---|---|
| **`GenericStreamingMessage`, method `0x4175`** (the only route to the viewer) | UDP message, `High` frequency number 31, trusted, unencoded. Block `MethodData` { `Method` U16 }, block `DataBlock` { `Data` Variable, **2-byte length prefix** }. `0x4175` = `METHOD_GLTF_MATERIAL_OVERRIDE`. Payload = **LLSD notation text**. Keep it under 7 KiB. | `scripts/messages/message_template.msg:5855-5872`; `indra/llmessage/llgenericstreamingmessage.h:37-41`; `.cpp:55-69` (7 KiB buffer, `:60`) |
| Dispatch | handler registered at startup, switches on the method id, anything else is "unknown method" once | `indra/newview/llstartup.cpp:3057`; `indra/newview/llviewergenericmessage.cpp:97-111` |
| Applied by | `LLGLTFMaterialList::applyOverrideMessage(msg, data)` | `indra/newview/llgltfmateriallist.cpp:171-253` |
| **Objects that arrive later** | The message is **always cached** per region, keyed by the object's local id, even when the object is not known yet ("obj may be null if the viewer hasn't heard about the object yet, cache update in any case"). When the object's drawable is rebuilt, `applyQueuedOverrides` replays the cache onto it. | `llgltfmateriallist.cpp:191, 248`; `llgltfmateriallist.cpp:269-326`; `llviewerregion.cpp:2787-2798` (store), `:3849-3870` (`applyCacheMiscExtras`: replay); called from `llvovolume.cpp:5750` |
| Disk cache | the viewer also writes the override cache to disk with the object cache and replays it on the next login. SLNG has no object cache for this. | `llviewerregion.cpp:796, 826`; `indra/newview/llvocache.cpp:63-153, 1928-1960` |
| `ModifyMaterialParams` capability | **viewer → simulator only**: a POST that edits overrides (`object_id`, `side`, `gltf_json` / `asset_id`). The server then "echoes back" the change as the streaming message above. SLNG does not need it. | `llgltfmateriallist.cpp:432-506, 722-759`; echo comment `llviewerobject.cpp:7797` |
| `RenderMaterials` capability | fetches legacy Blinn-Phong materials by id (SLNG already uses it, `GridSession.Assets.cs`). Not involved. | |

### 2. The message shape

Payload (LLSD notation). **This example is built from the viewer's parser, not captured: no real payload has been
logged yet.** The viewer accepts any key order and any whitespace; the keys below are what its code reads:

```
{'id':i4242,'od':[{'bc':[r0.8,r0.1,r0.1,r1]},{'rf':r0.35,'tex':[u11111111-2222-3333-4444-555555555555]}],'te':[i1,i3]}
```

| Key | Type | Meaning | Cite |
|---|---|---|---|
| `id` | integer | the object's **local id** in the sending region (not a UUID) | `llgltfmateriallist.cpp:186` |
| `te` | array of integers | face indices, 0..44 | `:199, :205` |
| `od` | array of maps | `od[i]` is the override of face `te[i]`; arrays are parallel | `:200, :216` |

The viewer reads at most **45** faces (`MAX_TES`, `:202`). It applies `od[i]` to `te[i]`.

**This message replaces the object's whole override set** (`:234-246`): every face of the object that is *not* listed in
`te`, and has an override, loses it. **A message with an empty `te` array clears all of the object's overrides**
(`:205` comment: "null out all overrides will come in as an empty te array"), and then the cache entry is erased
(`llviewerregion.cpp:2790-2797`). A message **without** a `te` array is malformed and ignored.

### 3. One face's override map (`od[i]`)

Parsed by `LLGLTFMaterial::applyOverrideLLSD` (`indra/llprimitive/llgltfmaterial.cpp:756-861`). **A key that is absent
means "not overridden".** Types matter: a value of the wrong LLSD type is ignored.

| Key | LLSD type required | Meaning | Default (= not overridden) |
|---|---|---|---|
| `tex` | array of UUID, index = texture slot | replaces that slot's texture. Slots: **0 base colour, 1 normal, 2 metallic-roughness (= occlusion), 3 emissive** (`llgltfmaterial.h`, enum `TextureInfo`). The all-F UUID `ffffffff-ffff-ffff-ffff-ffffffffffff` means "**remove** this texture"; the null UUID (or an `!` element) means "leave as is" (`llgltfmaterial.cpp:48, 597-610`) | slot unchanged |
| `bc` | array of 4 reals (r,g,b,a) | base colour factor, **replaces** the asset's (no multiply) | asset value |
| `ec` | array of 3 reals | emissive colour | asset value |
| `mf` | **real** | metallic factor | asset value |
| `rf` | **real** | roughness factor | asset value |
| `am` | **integer** | alpha mode: **0 OPAQUE, 1 BLEND, 2 MASK** (`enum AlphaMode`, `llgltfmaterial.h`; this is NOT the order of `SLNG.Assets.PbrAlphaMode`, which is Opaque, Mask, Blend) | asset value |
| `ac` | **real** | alpha cutoff (MASK only) | asset value |
| `ds` | **boolean** | double-sided | asset value |
| `ti` | array of up to 4 maps, index = texture slot | texture transform of that slot: `o` = offset [x,y], `s` = scale [x,y], `r` = rotation (real, radians). Each key is optional. Sparse arrays carry `!` for unused slots. | offset (0,0), scale (1,1), rotation 0 |

`mf`, `rf`, `ac`, `r` and `am` are only applied when the LLSD value has exactly the right type (`isReal()`,
`isInteger()`, `isBoolean()`): `llgltfmaterial.cpp:790-835, 854`. A map entry that says `'mf':i1` is ignored.

**How the viewer merges it** (`LLGLTFMaterial::applyOverride`, `llgltfmaterial.cpp:612-682`): the render material is a
copy of the base material, then each field is replaced **only if the override's value differs from the default**,
with two exceptions (alpha mode and double-sided also apply when their `mOverride…` flag is set, which `am` / `ds`
always set). Because "equal to the default" would then mean "not overridden", the receiver *nudges* a received value
that equals the default by `FLT_EPSILON` (`:772-775, :783-787, :794-798, :805-809, :823-827`) so that "override to
exactly 1.0" survives. **SLNG must not copy this hack.** The parser records *presence* explicitly (nullable fields),
which gives the same result without the epsilon.

### 4. When the viewer re-applies or drops an override

| Event | What happens | Cite |
|---|---|---|
| A new override message for the object | replaces the set (above); faces not listed lose theirs | `llgltfmateriallist.cpp:199-250` |
| Empty `te` | clears everything, cache entry erased | `:205`, `llviewerregion.cpp:2790-2797` |
| The object's render-material id changes (a new `RenderMaterial` ExtraParams block from the sim) | the render material is rebuilt from the **new** base and the **stored** override is applied again once the new base has loaded | `llviewerobject.cpp:6844-6851`, `:7819-7831`, `:7680-7812` (`:7727` `material_changed`, `:7750-7766` re-apply) |
| An ordinary object update (TE message) | does not touch the override | (the override is not in the TE message) |
| The override arrives before the object or before the base material has loaded | `setTEGLTFMaterialOverride` returns "no change"; the cache keeps it and replays it at the next drawable rebuild | `llviewerobject.cpp:5870-5914` (`:5877, :5885-5897`); `llgltfmateriallist.cpp:269-326` |
| The object is killed / leaves the cache | its cache entry is erased | `llviewerregion.cpp:1165` |
| The face has **no base material** | an override cannot apply (needs a base) | `lltextureentry.cpp:562-573` (`llassert`), `llviewerobject.cpp:5885-5891` |
| Number of faces grows | the last face's override is copied to the new faces | `llviewerobject.cpp:5247-5270` |
| A local edit with the Build tool | `setBaseMaterial()` clears every override field **except the transforms**, then re-sends | `llviewerobject.cpp:7729-7738`; `lltextureentry.cpp:575-594` (SLNG has no editing) |

### 5. What the viewer does with the result (and where SLNG differs)

- **Which material a face uses:** `LLTextureEntry::getGLTFRenderMaterial()` returns the render material (base +
  override) if there is one, otherwise the base material (`lltextureentry.cpp:596-605`).
- **Colour:** the face's vertex colour **is** the material's base colour factor; the face's own TE colour is
  ignored for a PBR face (`indra/newview/llface.cpp:1392-1401`). SLNG multiplies the TE colour in
  (`pbr.BaseColorFactor * colorTint`, `ObjectRenderer.cs` ~4376). See Open question 1.
- **Alpha:** the cutoff is only used for MASK (`llfetchedgltfmaterial.cpp:76-83`).
- **Texture placement:** for a PBR face the legacy face repeat/offset/rotation are **not** applied
  (`llface.cpp:1474-1475` "For GLTF, transforms will be applied later"). Each texture slot is placed by its own
  `KHR_texture_transform` in the vertex shader (`indra/newview/app_settings/shaders/class1/deferred/pbropaqueV.glsl:85-88`,
  `textureUtilV.glsl:36-79`):

  ```glsl
  // texcoord.y is flipped to 1-y first, then:
  transform = offset_mat * rotation_mat * scale_mat;      // scale first, then rotate, then offset
  rotation_mat = mat3(cos(r), -sin(r), 0,  sin(r), cos(r), 0,  0, 0, 1);   // GLSL columns
  // result.x =  cos(r)*sx*u + sin(r)*sy*v + ox
  // result.y = -sin(r)*sx*u + cos(r)*sy*v + oy        (then y is flipped back)
  ```

  The rotation is about the **top-left corner** (there is no centring) and clockwise on screen
  (`llgltfmaterial.cpp:950-954`). The legacy-to-PBR conversion function (`:955-1004`) is for the editor only; the
  streaming message already carries PBR-convention values.
- Transforms exist **only in overrides**: asset materials have theirs reset (`sanitizeAssetMaterial`,
  `llgltfmaterial.cpp:372-375`).

## Why LibreMetaverse fails on some payloads

**Pinned version:** `LibreMetaverse` 3.1.6 (`src/SLNG.Net/SLNG.Net.csproj`), and its `LibreMetaverse.StructuredData`
3.1.6. (`scratch/libremetaverse_src` is v3.0.2 and **stale**: do not use it.) Decompiled with `ilspycmd` 8.2 from the
NuGet packages.

**What LMV does:** `Client.Network.GenericStreamingMessage` raises an event with the raw bytes. `ObjectManager.
GenericStreamingMessageHandler` calls `OSDParser.DeserializeLLSDNotation(Utils.BytesToString(e.Data))`, catches the
exception and logs `Failed to parse GltfMaterialOverride payload`. When it does parse, it builds `AssetMaterial`s and
stores them on `Primitive.Textures[i].MaterialOverride` and in `Simulator.GLTFMaterialOverrides`. It raises
`ObjectManager.GLTFMaterialOverrideReceived`.

**The bug** (in `OSDParser.DeserializeLLSDNotationArray` and `DeserializeLLSDNotationMap`): both loop
`while (Peek() != closer) { read element; read the delimiter }`. **When the container is EMPTY, the loop body never
runs, so the closing `]` / `}` is never consumed.** The *parent* then reads that closer as its "delimiter" (it
accepts any character there), and the next thing it sees is the real `,`, which it tries to read as the start of an
element: `Unknown type marker ','`. The reference viewer's parser consumes the closer in every case
(`indra/llcommon/llsdserialize.cpp:736-836`), and skips stray commas and spaces between entries.

**Reproduced** with a throwaway probe against the pinned DLLs (source kept in the session notes, not in the repo):

| Payload | LMV 3.1.6 |
|---|---|
| `{'id':i4242,'od':[{'bc':[r0.5,r0.25,r0.25,r1]}],'te':[i0]}` | parses |
| `{'id':i4242,'te':[]}` (empty array is **last**) | parses |
| `{'id':i4242,'od':[],'te':[]}` (the clear-all message) | **`Unknown type marker ','`** |
| `{'id':i4242,'te':[],'od':[]}` | **`Unknown type marker ','`** |
| `{'id':i4242,'od':[{}],'te':[i0]}` (an empty override entry) | **`Unknown type marker ','`** |
| `{'id':i4242,'od':[{},{'bc':[r1,r0,r0,r1]}],'te':[i0,i1]}` | **`Unknown type marker ','`** |
| `'ti':[!,!,{'o':[r0.1,r0]}]`, `'ds':1`, `'ds':true`, `'rf':r1e-05` | all parse |

The real stack in `godot.log` is `Element <- Map <- Element <- GenericStreamingMessageHandler`. Every shape in the
table gives exactly that stack, so the log cannot tell which shape the simulator sent. **The raw payload was never
logged.** The two likely ones are the *clear-all* message (empty `od` and `te`) and an *empty override entry*. Step 4
therefore logs the first rejected payloads.

**Consequence:** LMV never applies a clear-all, so a face whose override was removed would keep it forever, and LMV's
own handler also bypasses SLNG's `ObjectUpdate` pipeline (it mutates `Primitive.Textures` and raises its own event).

**Decision: SLNG parses the message itself.**

- Subscribe to the **public** `Client.Network.GenericStreamingMessage` event (`LibreMetaverse.
  GenericStreamingMessageEventArgs`: `Simulator`, `Method`, `Data` bytes). No private packet hook is needed; the
  event carries the bytes of `DataBlock.Data`. (`RegisterCallback(PacketType.GenericStreamingMessage, ...)` would
  also work; do not use it.)
- An engine-neutral parser in `SLNG.Core` (Steps 1 and 2) with fixtures from real payload shapes.
- **Rejected:** reading LMV's result (`GLTFMaterialOverrideReceived` / `TextureEntryFace.MaterialOverride`). It is an
  LMV type (`AssetMaterial`: breaks the boundary rule), it misses exactly the clear-all message, and the event is
  raised separately from `ObjectUpdate`, so the renderer would not notice a change.
- LMV's own handler keeps running and keeps failing on those payloads. That is harmless; the existing filter keeps
  the log quiet (Step 7 changes its wording).

## How SLNG renders PBR faces today, and where the override merges

(`app/scripts/ObjectRenderer.cs`, `BuildFaceMaterialAsync`, ~4092-4513; `app/materials/prim/prim_common.gdshaderinc`.)

- One `ShaderMaterial` per face, built from a `SLNG.Core.FaceTexture` record. `ApplyFaceMaterialsAsync` (~3930-4040)
  resolves the face as `prim.Faces[faceIdx]` or the default face.
- With `ft.RenderMaterialId != Guid.Empty` it awaits `_assetService.GetMaterialAsync` (a `SLNG.Assets.PbrMaterialData`:
  four texture ids, base colour, metallic, roughness, emissive, alpha mode, cutoff, double-sided; **no transforms**).
  It sets `albedo_color`, `metallic_factor`, `roughness_factor`, `emission_*`, swaps the shader for BLEND / MASK
  (`PrimShaderFamily.Blend` / `Scissor`) and for double-sided (`DoubleSidedTwin`), and requests the textures.
- **Placement:** all four PBR textures are sampled with the **one** vertex UV from `slng_transform_uv`, which is the
  *legacy* face placement (`uv_scale`, `uv_offset`, `uv_rotation`). Only the legacy normal and specular maps have their
  own coordinate (`normal_uv_*`).
- The renderer re-applies a prim's materials when `prim.Faces` changes: the gate at `ObjectRenderer.cs` ~3178-3187
  compares `FaceTexture[]` by value (`FacesEqual`). `FaceSurfaceMerge` merges equal consecutive faces, and
  `PrimMaterialKey` (FEAT-PERF-20) is built **from the `FaceTexture`**. So: **if the override is a value-equal field
  of `FaceTexture`, then re-apply on change, surface merging and the future shared-material key all work with no
  further change.** That is the central decision of this spec.
- `MaterialFingerprint.Of` (`ObjectInstanceGroups.cs:416-448`) enumerates **every** shader uniform, so new uniforms are
  part of the instancing-group key automatically.

**Merge point:** per face, in `BuildFaceMaterialAsync`, between `GetMaterialAsync` and writing the uniforms:
`var effective = PbrOverrideMerge.Apply(pbr, ft.PbrOverride)`. Everything after that uses `effective`.

## Decisions

1. **The override lives in `FaceTexture.PbrOverride`** (a value-equal, immutable record, or `null`).
2. **`WorldSimulation` owns the store** (`(regionHandle, localId)` -> faces) and **stamps** the overrides onto
   `prim.Faces` whenever a message arrives *and* after every `ObjectUpdate` assigns `prim.Faces`. This is the viewer's
   region cache, in the single thread that mutates the world (drained in `Pump()`, never from the network thread).
3. **The merge is a pure function in `SLNG.Assets`** (`PbrOverrideMerge`), next to `PbrMaterialData`.
4. **Presence is explicit** (nullable fields). No epsilon nudging.
5. **Texture transforms (v1): only the base-colour slot's transform is used, for the whole face** (it replaces the
   face's legacy placement, as in the viewer, via two new uniforms `pbr_uv_a` / `pbr_uv_b`). The other three slots are
   parsed and stored (so equality is right) but not rendered. Reason: creators and the Build tool's "All" mode set all
   four equal; per-slot placement needs two more varyings.
6. **Colour:** a face whose override sets `bc` uses that colour **alone** (the face's TE colour is not multiplied in),
   which is what the viewer does for every PBR face. Faces without a `bc` override keep today's behaviour.
7. Override applies only to faces that have a `RenderMaterialId`; otherwise it is ignored (the viewer needs a base).

## Rules for this task

- Work in `E:/Git/SLNG`. Create the branch `feature/FEAT-RENDER-26-gltf-material-overrides` from `main`. Do not use
  `git worktree`. Check `git branch --show-current` and `git status` first: another session may hold uncommitted
  work in the shared tree. If it does, stop and ask.
- In `docs/ROADMAP.md`, set the FEAT-RENDER-26 row to Owner `gemini` and status 🚧, and to 🧪 when done.
- Set `AppVersion` in `app/scripts/Boot.cs` to `v0.27.60-alpha`, or one patch above whatever is there.
- **No `using Godot;` in `src/`.** No LibreMetaverse type in a **public** member of `SLNG.Net` or `SLNG.Assets`
  (`GenericStreamingMessageEventArgs` and `GenericStreamingMethod` are LMV types: use them only inside `GridSession`).
- **Tests first** for Steps 1-4: write the test file, watch it fail, then write the code.
- One public type per file, named after the file. `dotnet format SLNG.sln` must be clean.
- Do not touch: `AvatarRenderer` and `AvatarRenderer.*` (worn attachments, animesh), the texture-animation code
  (`TextureAnimShader`, `tex_anim_*`), `PrimMaterialKey`, `FaceSurfaceMerge`, the legacy normal/specular path.
- Commit: `feat(render): [FEAT-RENDER-26] apply glTF material overrides to world prim faces (v0.27.60-alpha)`. Do not
  merge, push or release.

## Technical Specs & Affected Files

### Step 1: an LLSD notation parser in `SLNG.Core` (tests first)

New files:

- `src/SLNG.Core/LlsdValue.cs`
- `src/SLNG.Core/LlsdNotation.cs`
- `tests/SLNG.Core.Tests/LlsdNotationTests.cs`

**`LlsdValue`**: an immutable tree node with `Kind` (`Undefined`, `Boolean`, `Integer`, `Real`, `String`, `Uuid`, `Map`,
`Array`, `Other`) and the semantics of C++ `LLSD`. In particular **indexing never throws**: a missing map key or an
out-of-range index gives `LlsdValue.Undefined`, exactly as `LLSD::operator[] const` does.

```csharp
namespace SLNG.Core;

public enum LlsdKind { Undefined, Boolean, Integer, Real, String, Uuid, Map, Array, Other }

public sealed class LlsdValue
{
    public static readonly LlsdValue Undefined = new(LlsdKind.Undefined);
    public LlsdKind Kind { get; }
    public bool IsDefined => Kind != LlsdKind.Undefined;
    public bool IsBoolean => Kind == LlsdKind.Boolean;
    public bool IsInteger => Kind == LlsdKind.Integer;   // strictly Integer: 'i1', not 'r1', not '1'
    public bool IsReal => Kind == LlsdKind.Real;         // strictly Real
    public bool IsUuid => Kind == LlsdKind.Uuid;
    public bool IsMap => Kind == LlsdKind.Map;
    public bool IsArray => Kind == LlsdKind.Array;
    public int Count { get; }                            // map or array size, else 0
    public LlsdValue this[string key] { get; }           // Undefined when absent or not a map
    public LlsdValue this[int index] { get; }            // Undefined when out of range or not an array
    public IEnumerable<KeyValuePair<string, LlsdValue>> Entries { get; }
    public bool AsBoolean();                             // Boolean, else Integer != 0, Real != 0, else false
    public int AsInteger();                              // Integer; Real truncated; Boolean 0/1; String parsed or 0; else 0
    public double AsReal();                              // Real; Integer widened; Boolean 0/1; else 0
    public Guid AsUuid();                                // Uuid, else Guid.Empty
    public string AsString();                            // String, else ""
    // plus internal factories: FromBoolean, FromInteger, FromReal, FromString, FromUuid, FromMap, FromArray, Other
}
```

**`LlsdNotation`**:

```csharp
public static class LlsdNotation
{
    /// <summary>The viewer reads at most 7 KiB of a streaming payload (llgenericstreamingmessage.cpp:60); more than
    /// 16 KiB is refused without parsing.</summary>
    public const int MaxBytes = 16 * 1024;
    public const int MaxDepth = 32;

    public static bool TryParse(ReadOnlySpan<byte> utf8, out LlsdValue value, out string error);
    public static bool TryParse(string text, out LlsdValue value, out string error);
}
```

Behaviour (a tolerant mirror of `indra/llcommon/llsdserialize.cpp:476-836`):

- **Trailing bytes after the top-level value are ignored** (the payload may end in a NUL). Leading and trailing
  whitespace is ignored. Empty input is a failure.
- Map: `{` then entries `key : value`, separated by `,`. **Commas and whitespace between entries are skipped, and a
  trailing comma is allowed.** A key is a string (`'..'`, `".."` or `s(n)"…"`); any other key is a failure. A map
  needs a closing `}`.
- Array: `[` elements `]`, same tolerance.
- **The closing `]` / `}` of an EMPTY container is consumed.** This is the line LMV lacks. The sketch below is the
  whole trick:

```csharp
private LlsdValue ParseMap(int depth)            // the '{' has been consumed
{
    var map = new Dictionary<string, LlsdValue>();
    while (true)
    {
        SkipWhiteAndCommas();
        if (_i >= _s.Length) throw Fail("unterminated map");
        if (_s[_i] == '}') { _i++; return LlsdValue.FromMap(map); }   // also reached first for '{}'
        var key = ParseValue(depth + 1);
        if (key.Kind != LlsdKind.String) throw Fail("map key is not a string");
        SkipWhite();
        if (_i >= _s.Length || _s[_i++] != ':') throw Fail("expected ':' after a map key");
        map[key.AsString()] = ParseValue(depth + 1);
    }
}
// ParseArray is the same with ']' and no key.
```

- Scalars, by the first character (case as in the viewer, `llsdserialize.cpp:476-734`):

| Text | Value |
|---|---|
| `!` | undefined |
| `0`, `1` | **boolean** false / true (a bare digit; an integer is `i0`) |
| `t`, `T`, `true`, `TRUE`, `f`, `F`, `false`, `FALSE` | boolean |
| `i` + optional sign + digits | integer (int32; out of range is a failure) |
| `r` + token | real. Token = chars of `[0-9+-.eE]` and letters; accept `nan`, `inf`, `-inf`, `+inf` (case-insensitive) and everything `double.TryParse(NumberStyles.Float, InvariantCulture)` accepts. A bad token is a failure |
| `u` + 36 characters | UUID (`Guid.TryParse`); failure otherwise |
| `'..'`, `".."` | string. A backslash escapes the next character; `\n`, `\t`, `\r` and `\xHH` are understood |
| `s(n)"…"` | string of exactly *n* characters |
| `l"…"`, `d"…"`, `b…` | skipped as a token, value `Kind.Other` (not an error) |
| anything else | failure `unknown type marker 'c' at <offset>` |

- Nesting deeper than `MaxDepth` is a failure. Never throw out of `TryParse`: catch your own `Fail` exception inside
  and return `false` with the message.

**`LlsdNotationTests`** (assert values, not "did not throw"):

1. Every scalar row of the table, including `r1e-05`, `r-0.5`, `rnan`, `r-inf`, `i-7`, the bare `1` / `0`, `true`,
   `T`, a UUID, `'it\'s'`, `"x"`, `s(3)"abc"`.
2. **Empty containers**, each followed by a comma, a closer, and end of input: `[]`, `{}`, `[[]]`, `[{},{}]`,
   `{'a':[],'b':{}, 'c':i1}` (check `c == 1`), `[[],i5]` (check element 1 is the integer 5).
3. **The four payloads LMV rejects** (see the table above) parse and give the expected maps.
4. Tolerance: whitespace and newlines everywhere (a pretty-printed payload), a trailing comma, a missing comma
   between entries, a trailing NUL byte, a UTF-8 BOM.
5. Failures with a message: empty input, `{`, `[i1`, `{'a'}`, `{i1:i2}`, `{'a':?}`, `u1234`, `i99999999999`,
   `r` with a bad token, 40 nested `[`, a 20,000 byte input.
6. `LlsdValue` semantics: an `Undefined` index never throws; `IsReal` is false for `i1`; `AsInteger` of `r2.9` is 2.

### Step 2: the override DTOs and the wire parser (tests first)

New files in `src/SLNG.Core/`:

- `GltfAlphaMode.cs`
- `GltfTextureTransform.cs`
- `GltfMaterialOverride.cs`
- `GltfOverrideMessage.cs`
- `GltfOverrideParser.cs`

New tests: `tests/SLNG.Core.Tests/GltfOverrideParserTests.cs`, `tests/SLNG.Core.Tests/GltfTextureTransformTests.cs`.

```csharp
namespace SLNG.Core;

/// <summary>glTF alpha mode in the WIRE numbering (llgltfmaterial.h: OPAQUE 0, BLEND 1, MASK 2). NOT the numbering of
/// SLNG.Assets.PbrAlphaMode (Opaque, Mask, Blend): never cast between them, map explicitly.</summary>
public enum GltfAlphaMode { Opaque = 0, Blend = 1, Mask = 2 }

/// <summary>One slot's KHR_texture_transform, in the viewer's convention (see FEAT-RENDER-26).</summary>
public readonly record struct GltfTextureTransform(Vector2 Offset, Vector2 Scale, float Rotation)
{
    public static readonly GltfTextureTransform Identity = new(Vector2.Zero, Vector2.One, 0f);

    /// <summary>The viewer's khr_texture_transform (textureUtilV.glsl:36-49), written out: scale, then rotate, then
    /// offset, about the top-left corner. <paramref name="uv"/> is SLNG's mesh UV (top-origin V); the result is the
    /// coordinate to sample at. A C# MIRROR of slng_khr_uv in prim_common.gdshaderinc: change both together.</summary>
    public Vector2 Apply(Vector2 uv)
    {
        var sc = uv * Scale;
        float s = MathF.Sin(Rotation), c = MathF.Cos(Rotation);
        return new Vector2(c * sc.X + s * sc.Y + Offset.X, -s * sc.X + c * sc.Y + Offset.Y);
    }
}

/// <summary>What one face's override changes. A null member means "not overridden". Value equality, so equal
/// overrides compare equal inside FaceTexture (re-apply gate, FaceSurfaceMerge, PrimMaterialKey).</summary>
public sealed record GltfMaterialOverride
{
    // Texture per slot: null = unchanged, Guid.Empty = REMOVE the texture (the wire's all-F UUID), else replace.
    public Guid? BaseColorTexture { get; init; }
    public Guid? NormalTexture { get; init; }
    public Guid? MetallicRoughnessTexture { get; init; }
    public Guid? EmissiveTexture { get; init; }
    public Vector4? BaseColor { get; init; }          // r,g,b,a
    public Vector3? EmissiveColor { get; init; }
    public float? Metallic { get; init; }
    public float? Roughness { get; init; }
    public GltfAlphaMode? AlphaMode { get; init; }
    public float? AlphaCutoff { get; init; }
    public bool? DoubleSided { get; init; }
    // Per slot; null = no transform override for that slot. Only BaseColorTransform is rendered in v1.
    public GltfTextureTransform? BaseColorTransform { get; init; }
    public GltfTextureTransform? NormalTransform { get; init; }
    public GltfTextureTransform? MetallicRoughnessTransform { get; init; }
    public GltfTextureTransform? EmissiveTransform { get; init; }

    /// <summary>True when nothing is overridden (all 15 members null). Expression-bodied on purpose: a property
    /// with no backing field does not take part in the record's value equality.</summary>
    public bool IsEmpty => BaseColorTexture is null && NormalTexture is null && MetallicRoughnessTexture is null
        && EmissiveTexture is null && BaseColor is null && EmissiveColor is null && Metallic is null
        && Roughness is null && AlphaMode is null && AlphaCutoff is null && DoubleSided is null
        && BaseColorTransform is null && NormalTransform is null && MetallicRoughnessTransform is null
        && EmissiveTransform is null;
}

/// <summary>One GenericStreamingMessage 0x4175, parsed. REPLACES the object's whole override set.</summary>
public sealed record GltfOverrideMessage(uint LocalId, IReadOnlyDictionary<int, GltfMaterialOverride> Faces);
```

`Faces` contains **only faces with a non-empty override**. An empty `Faces` means "this object has no overrides now".

**Parser** (`GltfOverrideParser`):

```csharp
public static class GltfOverrideParser
{
    public const int MaxFaces = 45;                    // MAX_TES, llgltfmateriallist.cpp:202
    public static readonly Guid RemoveTextureMarker = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public static bool TryParse(ReadOnlySpan<byte> payload, out GltfOverrideMessage? message, out string? error);
    public static bool TryFromLlsd(LlsdValue root, out GltfOverrideMessage? message, out string? error);
    internal static GltfMaterialOverride ParseOverride(LlsdValue od);
}
```

`TryFromLlsd`, following `applyOverrideMessage` (`llgltfmateriallist.cpp:171-253`):

1. `root` must be a map, and `root["id"]` must be an integer, else failure.
2. `root["te"]` must be an **array** (even an empty one), else failure `no 'te' array` (the viewer: "malformed").
3. For `i` in `0 .. min(te.Count, 45) - 1`: `face = te[i].AsInteger()`; skip when `face < 0 || face >= 45`;
   `o = ParseOverride(od[i])` (`od[i]` is `Undefined` when `od` is shorter or absent: all unset). If `o.IsEmpty`,
   remove `face` from the result, else `result[face] = o`. A later duplicate of the same face wins.
4. Return `new GltfOverrideMessage((uint)id, result)`.

`ParseOverride(od)` (`llgltfmaterial.cpp:756-861`; **strict types**):

- `tex`: for `i` in 0..3, if `tex[i].IsUuid`: `Guid g = tex[i].AsUuid()`; `g == Guid.Empty` -> unchanged;
  `g == RemoveTextureMarker` -> `Guid.Empty`; else `g`.
- `bc`: present if `od["bc"].IsArray` with at least 4 elements: `(AsReal x4)`. `ec`: at least 3 elements.
  A non-finite component (NaN, infinity) means the key is treated as **absent**.
- `mf`, `rf`, `ac`: present only if `.IsReal` (and finite).
- `am`: present only if `.IsInteger` and `0 <= v <= 2`. `ds`: only if `.IsBoolean`.
- `ti`: if `od["ti"].IsArray`, for slot `i` in 0..3: `m = ti[i]`; if `m["o"]`, `m["s"]` or `m["r"]` is defined: start
  from `GltfTextureTransform.Identity`; `o` = `(m["o"][0].AsReal(), m["o"][1].AsReal())` when `m["o"].IsArray`;
  `s` the same; `r` only if `m["r"].IsReal`. The slot's transform is that value (even if it equals the identity).

**Fixtures** (`GltfOverrideParserTests`; write them as `const string`, parse via `TryParse(Encoding.UTF8.GetBytes(...))`).
Every one must be **asserted by value**:

| # | Payload | Expected |
|---|---|---|
| F1 recolour | `{'id':i4242,'od':[{'bc':[r0.8,r0.1,r0.1,r1]}],'te':[i1]}` | `LocalId 4242`; `Faces` = {1: `BaseColor (0.8,0.1,0.1,1)`, all else null} |
| F2 two faces | `{'id':i77,'od':[{'bc':[r1,r1,r1,r0.5],'mf':r0,'rf':r0.35,'tex':[u11111111-2222-3333-4444-555555555555]},{'am':i2,'ac':r0.4,'ds':1}],'te':[i0,i2]}` | face 0: colour (1,1,1,0.5), Metallic 0, Roughness 0.35, `BaseColorTexture 11111111-…`; face 2: `Mask`, cutoff 0.4, `DoubleSided true` |
| F3 texture removed | `{'id':i9,'od':[{'tex':[!,!,!,uffffffff-ffff-ffff-ffff-ffffffffffff]}],'te':[i3]}` | face 3: `EmissiveTexture == Guid.Empty`; the other three texture members null |
| F4 transform | `{'id':i5,'od':[{'ti':[{'o':[r0.25,r-0.5],'s':[r2,r3],'r':r0.5236}]}],'te':[i0]}` | face 0 `BaseColorTransform` = offset (0.25,-0.5), scale (2,3), rotation 0.5236; other slots null |
| F4b sparse | `{'id':i5,'od':[{'ti':[!,!,{'s':[r4,r4]}]}],'te':[i0]}` | `MetallicRoughnessTransform` = offset 0, scale (4,4), rot 0; `BaseColorTransform` null |
| F5 clear-all (**LMV rejects**) | `{'id':i4242,'od':[],'te':[]}` | success, `Faces` empty |
| F5b | `{'id':i4242,'te':[],'od':[]}` and `{'id':i4242,'te':[]}` | success, `Faces` empty |
| F6 empty entry (**LMV rejects**) | `{'id':i4242,'od':[{}],'te':[i0]}` | success, `Faces` empty (face 0 has nothing to override) |
| F7 mixed (**LMV rejects**) | `{'id':i4242,'od':[{},{'bc':[r1,r0,r0,r1]}],'te':[i0,i1]}` | `Faces` = {1: red} |
| F8 default values are overrides | `{'id':i1,'od':[{'bc':[r1,r1,r1,r1],'rf':r1,'mf':r1,'ac':r0.5,'am':i0,'ds':0}],'te':[i0]}` | every member **non-null** with the default value: BaseColor (1,1,1,1), Roughness 1, Metallic 1, cutoff 0.5, `Opaque`, `DoubleSided false` |
| F9 wrong types ignored | `{'id':i1,'od':[{'mf':i1,'rf':'0.2','am':r2.0,'ds':i1}],'te':[i0]}` | `Faces` empty (viewer ignores each) |
| F10 face range | `te` = `[i-1,i44,i45,i3]` with four distinct `bc`s | faces 44 and 3 present; -1 and 45 skipped |
| F11 `od` shorter | `{'id':i1,'od':[{'bc':[r1,r0,r0,r1]}],'te':[i0,i2]}` | `Faces` = {0: red}; face 2 has nothing |
| F12 order & layout | `{'te':[i1],'od':[{'bc':[r0,r1,r0,r1]}],'id':i2}` pretty-printed over several lines, with a trailing NUL | same result as the compact form |
| F13 duplicate face | `te` = `[i1,i1]`, `od` = `[{'bc':red},{}]` | face 1 absent (the later, empty entry wins) |
| F14 non-finite | `{'id':i1,'od':[{'bc':[rnan,r0,r0,r1],'rf':rinf}],'te':[i0]}` | `Faces` empty |
| Failures | not a map; no `id`; `id` is `r1`; no `te`; `te` is a map; truncated `{'id':i1,'te':[i0`; unknown marker | `TryParse` returns false and `error` is non-empty |

`GltfTextureTransformTests` (known answers from the viewer's own shader maths):

| Transform | `Apply(uv)` |
|---|---|
| `Identity` | `(0.3,0.7)` -> `(0.3,0.7)` |
| scale (2,3) | `(0.5,0.5)` -> `(1,1.5)` |
| offset (0.25,-0.5) | `(0,0)` -> `(0.25,-0.5)` |
| rotation π/2 | `(1,0)` -> `(0,-1)`; `(0,1)` -> `(1,0)` (tolerance 1e-5) |
| **scale (2,1), rotation π/2** | `(1,0)` -> `(0,-2)` (pins "scale first, then rotate"; the other order gives `(0,-1)`) |
| scale (2,2), rotation π, offset (1,1) | `(0.25,0.5)` -> `(0.5,0)` (tolerance 1e-5) |

### Step 3: the merge, in `SLNG.Assets` (tests first)

New file `src/SLNG.Assets/PbrOverrideMerge.cs`; new tests `tests/SLNG.Assets.Tests/PbrOverrideMergeTests.cs`.

```csharp
namespace SLNG.Assets;

public static class PbrOverrideMerge
{
    /// <summary>The material a face renders with: <paramref name="baseMaterial"/> with the override's present fields
    /// applied (LLGLTFMaterial::applyOverride, llgltfmaterial.cpp:612-682, with explicit presence instead of the
    /// viewer's "differs from default" test). Returns the same instance when there is nothing to apply.</summary>
    public static PbrMaterialData Apply(PbrMaterialData baseMaterial, GltfMaterialOverride? o)
    {
        if (o is null || o.IsEmpty) return baseMaterial;
        return baseMaterial with
        {
            BaseColorTextureId = o.BaseColorTexture ?? baseMaterial.BaseColorTextureId,
            NormalTextureId = o.NormalTexture ?? baseMaterial.NormalTextureId,
            MetallicRoughnessTextureId = o.MetallicRoughnessTexture ?? baseMaterial.MetallicRoughnessTextureId,
            EmissiveTextureId = o.EmissiveTexture ?? baseMaterial.EmissiveTextureId,
            BaseColorFactor = o.BaseColor is { } bc ? Clamp01(bc) : baseMaterial.BaseColorFactor,
            EmissiveFactor = o.EmissiveColor is { } ec ? Clamp01(ec) : baseMaterial.EmissiveFactor,
            MetallicFactor = o.Metallic is { } m ? Math.Clamp(m, 0f, 1f) : baseMaterial.MetallicFactor,
            RoughnessFactor = o.Roughness is { } r ? Math.Clamp(r, 0f, 1f) : baseMaterial.RoughnessFactor,
            AlphaMode = o.AlphaMode is { } am ? Map(am) : baseMaterial.AlphaMode,
            AlphaCutoff = o.AlphaCutoff is { } ac ? Math.Clamp(ac, 0f, 1f) : baseMaterial.AlphaCutoff,
            DoubleSided = o.DoubleSided ?? baseMaterial.DoubleSided,
        };
    }

    private static PbrAlphaMode Map(GltfAlphaMode m) => m switch
    {
        GltfAlphaMode.Blend => PbrAlphaMode.Blend,
        GltfAlphaMode.Mask => PbrAlphaMode.Mask,
        _ => PbrAlphaMode.Opaque,
    };
    // Clamp01(Vector4) / Clamp01(Vector3): component-wise. The viewer clamps on SET (setBaseColorFactor ...), not
    // on receive; clamping here only protects the shader's hint_range from a bad payload.
}
```

**Tests** (`PbrOverrideMergeTests`), with a base such as: base-colour texture A, normal B, ORM C, emissive D, colour
(0.2,0.4,0.6,1), metallic 0.1, roughness 0.9, emissive (0,0,0), `Mask` cutoff 0.5, double-sided true:

1. `null` override and an empty override return the **same instance** (`Assert.Same`).
2. Each member alone changes exactly its own `PbrMaterialData` field and nothing else (one test per member, 11).
3. Textures: a replacement id replaces; `Guid.Empty` **clears** (`EmissiveTextureId == Guid.Empty`); null leaves.
4. **Alpha-mode mapping:** wire `Blend` (1) -> `PbrAlphaMode.Blend`; wire `Mask` (2) -> `PbrAlphaMode.Mask`; wire
   `Opaque` (0) -> `PbrAlphaMode.Opaque`. (This is the trap: the two enums are numbered differently.)
5. **Default-valued overrides apply:** an override with `BaseColor (1,1,1,1)`, `Roughness 1`, `DoubleSided false`,
   `AlphaMode Opaque` on the base above gives white, roughness 1, not double-sided, opaque (the viewer's epsilon
   hack exists for exactly this).
6. Clamping: `BaseColor (2,-1,0.5,9)` -> `(1,0,0.5,1)`; `Roughness 5` -> 1; `Metallic -1` -> 0; cutoff 3 -> 1.
7. Transforms are **not** part of `PbrMaterialData`: an override with only a `BaseColorTransform` returns the base
   unchanged field-wise.

### Step 4: events, the world store, stamping, and the network hook

#### 4a. `FaceTexture.PbrOverride`

In `src/SLNG.Core/FaceTexture.cs`, append **one trailing optional parameter** after `HasMedia`:

```csharp
    bool HasMedia = false,
    /// <summary>FEAT-RENDER-26: the per-face glTF material override the simulator sent for this face (message
    /// 0x4175), or null. Part of the face's value identity on purpose: the renderer's re-apply gate, FaceSurfaceMerge
    /// and PrimMaterialKey all compare FaceTexture by value, so a changed override re-applies the face, splits a
    /// surface run, and gives a different material key, and equal overrides give equal keys. Only meaningful when
    /// <see cref="RenderMaterialId"/> is not empty. Never set by the network layer's face build: it is stamped by
    /// WorldSimulation (see GltfOverrideStamp).</summary>
    GltfMaterialOverride? PbrOverride = null)
```

Existing calls are positional and short, so nothing else changes. `PrimMaterialKey.Create` uses `face with {…}`, which
keeps the new member.

In `src/SLNG.Core/Components/PrimitiveComponent.cs` add:

```csharp
/// <summary>The face a surface without its own record is built from: the same fields ObjectRenderer's own default
/// face uses (ApplyFaceMaterialsAsync), so a stamped face grown from it renders identically.</summary>
public FaceTexture DefaultFace() => new(TextureId, RenderMaterialId, LegacyMaterialId, ColorTint,
    RepeatU, RepeatV, OffsetU, OffsetV, Rotation, TexGen, Fullbright);

/// <summary>FEAT-RENDER-26: true while <see cref="Faces"/> carries stamped overrides. Lets WorldSimulation skip the
/// per-update re-stamp in O(1) for the 99% of prims that never had one.</summary>
public bool HasPbrOverrides { get; set; }
```

**Cost note.** The network layer builds `Faces` from LMV's fixed `TextureEntry.FaceTextures` array, which has
**45 entries** (`MAX_FACES = 45`, LMV 3.1.6), so *every decoded prim already carries 45 `FaceTexture`s*. The new
reference member adds 8 bytes to each: about +360 bytes per prim (a few percent). That is accepted here; shrinking
the array is a separate performance item. It also means a stamped override never needs to grow the array for a
decoded prim: growth only happens for a prim whose `Faces` is null.

#### 4b. `GltfOverrideStamp` (pure, in `SLNG.Core`)

New file `src/SLNG.Core/GltfOverrideStamp.cs`, tests `tests/SLNG.Core.Tests/GltfOverrideStampTests.cs`:

```csharp
public static class GltfOverrideStamp
{
    /// <summary>Returns <paramref name="faces"/> with each face's PbrOverride set to the entry of
    /// <paramref name="overrides"/> for its index (null where there is none). Returns the SAME array when nothing
    /// would change, so a caller can tell by reference whether to notify. When an override names a face beyond the
    /// array (or the array is null), the array grows; the new faces are copies of <paramref name="defaultFace"/>,
    /// which is what the renderer builds a surface without its own record from anyway.</summary>
    public static FaceTexture[]? Apply(FaceTexture[]? faces, in FaceTexture defaultFace,
                                       IReadOnlyDictionary<int, GltfMaterialOverride>? overrides)
    {
        // Plain loops, no LINQ: this runs on every object update of a prim that has (or had) overrides.
        if (overrides is not { Count: > 0 })
        {
            if (faces == null) return faces;
            bool any = false;
            for (int i = 0; i < faces.Length; i++) if (faces[i].PbrOverride != null) { any = true; break; }
            if (!any) return faces;
            var cleared = (FaceTexture[])faces.Clone();
            for (int i = 0; i < cleared.Length; i++) cleared[i] = cleared[i] with { PbrOverride = null };
            return cleared;
        }

        int max = 0;
        foreach (var k in overrides.Keys) if (k > max) max = k;
        int length = Math.Max(faces?.Length ?? 0, max + 1);
        var result = new FaceTexture[length];
        bool changed = faces == null || faces.Length != length;
        for (int i = 0; i < length; i++)
        {
            var f = faces != null && i < faces.Length ? faces[i] : defaultFace;
            overrides.TryGetValue(i, out var o);
            if (!Equals(f.PbrOverride, o)) { f = f with { PbrOverride = o }; changed = true; }
            result[i] = f;
        }
        return changed ? result : faces;
    }
}
```

Tests: a face gets its override; others stay null; replacing the set removes the old ones; the same input twice returns
the same reference; a null/short array grows with default-face copies (check `Length`, and that a grown face equals
the default face apart from the override); an empty or null `overrides` strips every override; stamping then
`FaceSurfaceMerge.Plan` gives a **run break** between two faces that were equal but have different overrides, and
**no break** when the overrides are equal; `PrimMaterialKey.Create` is equal for two faces with equal overrides and
different for different ones (including different `BaseColorTransform`).

#### 4c. The event and the world store

In `src/SLNG.Core/GridEvents.cs`, next to `ObjectMediaEvent`:

```csharp
/// <summary>FEAT-RENDER-26: a GenericStreamingMessage 0x4175, parsed. Addressed by LOCAL id (the message carries no
/// UUID), so it can arrive before its object. REPLACES the object's whole override set; empty = none.</summary>
public record GltfMaterialOverrideEvent(
    ulong RegionHandle, uint LocalId, IReadOnlyDictionary<int, GltfMaterialOverride> Faces) : IWorldEvent;
```

`src/SLNG.Core/IWorldEventSource.cs`: add `event EventHandler<GltfMaterialOverrideEvent>? GltfMaterialOverrideReceived;`
(only `GridSession` implements the interface).

`src/SLNG.Core/WorldSimulation.cs`:

1. Subscribe next to `ObjectAnimationReceived` (line ~87) and unsubscribe next to the other `-=` (line ~1806). The
   handler only enqueues: `private void OnGltfMaterialOverride(object? sender, GltfMaterialOverrideEvent e) => _pending.Enqueue(e);`
2. Add `"GltfOverride"` to `DrainKinds` **before** `"other"`; in `Pump()` add
   `case GltfMaterialOverrideEvent e: kind = 14; ApplyGltfOverride(e); break;` (the `default` still uses the last index).
3. The store (Pump thread only) and its methods:

```csharp
/// <summary>Past this many held entries (objects that have overrides), NEW entries for objects that have no entity
/// yet are dropped with one log line. Far above any real region; entries of live objects are never evicted.</summary>
public const int GltfOverrideLimit = 65536;

// FEAT-RENDER-26: the viewer's per-region override cache (llviewerregion.cpp:2787, :3849), keyed the same way.
private readonly Dictionary<(ulong Region, uint LocalId), IReadOnlyDictionary<int, GltfMaterialOverride>> _gltfOverrides = new();

private void ApplyGltfOverride(GltfMaterialOverrideEvent e)
{
    var key = (e.RegionHandle, e.LocalId);
    var entity = _world.GetEntity(e.RegionHandle, e.LocalId);

    if (e.Faces.Count == 0) _gltfOverrides.Remove(key);                      // clear-all
    else if (entity != null || _gltfOverrides.Count < GltfOverrideLimit)
        _gltfOverrides[key] = new Dictionary<int, GltfMaterialOverride>(e.Faces); // the store owns its copy

    var prim = entity?.GetComponent<PrimitiveComponent>();
    if (prim == null) return;                                                 // held until the object arrives
    if (StampGltfOverrides(prim, key)) _world.NotifyComponentUpdated(entity!, prim);
}

/// <summary>Re-stamps prim.Faces from the store. True when prim.Faces changed. O(1) for a prim that has no override
/// and never had one (the flag): this runs after EVERY object update, terse ones included.</summary>
private bool StampGltfOverrides(PrimitiveComponent prim, (ulong, uint) key)
{
    _gltfOverrides.TryGetValue(key, out var set);
    bool hasSet = set is { Count: > 0 };
    if (!hasSet && !prim.HasPbrOverrides) return false;

    var stamped = GltfOverrideStamp.Apply(prim.Faces, prim.DefaultFace(), set);
    prim.HasPbrOverrides = hasSet;
    if (ReferenceEquals(stamped, prim.Faces)) return false;
    prim.Faces = stamped;
    return true;
}
```

4. **In `ApplyObjectUpdate`**, directly after the `if (prim == null) { … } else { … }` block that creates or updates
   the `PrimitiveComponent` (it assigns `Faces` from the event on **every** update, terse ones too) and before
   `prim.AttachmentPoint = …`, add one line: `StampGltfOverrides(prim, (e.RegionHandle, e.LocalId));`. Do not add a
   second notification: the method's own `NotifyComponentUpdated` at its end covers it. (Skip the dictionary lookup
   cost concern: it is one hash lookup per update.)
5. **Drop rules** (the viewer's `killCacheEntry`): in `OnEntityRemoved` (line ~1530; `e.Entity.RegionHandle` /
   `e.Entity.LocalId` are available) remove `(RegionHandle, LocalId)` from the store, **before** its early return for
   a missing object id. In the `RegionDisconnectedEvent` branch of `Pump()` call a new
   `DropGltfOverrides(ulong region)` that removes every entry of that region (the entities' removal does not cover
   entries whose object never arrived).

Tests (`tests/SLNG.Core.Tests/GltfOverrideWorldTests.cs`, same pattern as `FaceTexGenTests`:
`new World()`, `new GridSession()`, `new WorldSimulation(world, session)`, `session.RaiseObjectUpdate(...)`,
`Pump()`, and `session.RaiseGltfMaterialOverride(...)`, see 4d):

1. **Object first, override after:** object with two faces carrying `RenderMaterialId`; override for face 1;
   `Pump()`; `prim.Faces[1].PbrOverride` is the override, face 0's is null; `World.ComponentUpdated` fired once for it.
2. **Override first, object after:** the override is held; the object's first update arrives; its `Faces[1]` carries it
   *in that same notification*.
3. **Replace:** a second message for face 0 only removes face 1's override.
4. **Clear-all:** an empty `Faces` removes every override and notifies; the store is empty (use a second object update
   to confirm nothing is re-stamped).
5. **A later `ObjectUpdate` (full and terse) keeps the override** and does **not** fire a change that makes the
   renderer re-apply: `prim.Faces` is `SequenceEqual` to before (value equality).
6. **An object update that changes the render-material id keeps the override.**
7. **Kill** (`ObjectRemovedEvent`) drops it: a new object with the same local id has none.
8. **Region disconnect** drops held entries of that region only.
9. A prim with `Faces == null` (no per-face data) gets a grown array whose other faces equal `DefaultFace()`.
10. **A prim that never had an override** keeps the event's own array: after an `ObjectUpdate`,
    `ReferenceEquals(prim.Faces, theEvent.Faces)` (no copy, no allocation: the O(1) early-out). After its override is
    cleared and the next update arrives, `HasPbrOverrides` is false again and the array is again the event's own.
11. `GltfOverrideLimit`: with the limit reached, a new entry for an unknown object is dropped, an entry for a live
    object is kept (set the constant low through an `internal` test hook or test with a count just above it; if that is
    awkward, skip this test and say so in the report).

#### 4d. The network hook

New file `src/SLNG.Net/GridSession.GltfOverride.cs` (`partial class GridSession`):

```csharp
public sealed partial class GridSession
{
    public event EventHandler<GltfMaterialOverrideEvent>? GltfMaterialOverrideReceived;
    internal void RaiseGltfMaterialOverride(GltfMaterialOverrideEvent e) => GltfMaterialOverrideReceived?.Invoke(this, e);

    private int _gltfOverrideMessages;      // parsed
    private int _gltfOverrideRejected;      // failed to parse

    /// <summary>Network thread. Parse only, raise the neutral event; never touch the world.</summary>
    private void OnGenericStreamingMessage(object? sender, LibreMetaverse.GenericStreamingMessageEventArgs e)
    {
        if (e.Method != LibreMetaverse.GenericStreamingMethod.GltfMaterialOverride) return;

        if (!GltfOverrideParser.TryParse(e.Data, out var message, out var error))
        {
            int n = Interlocked.Increment(ref _gltfOverrideRejected);
            // The raw payload has never been seen (BUG: LibreMetaverse rejected it): print the first three.
            if (n <= 3) Console.Error.WriteLine($"[GltfOverride] rejected a payload ({error}): {Preview(e.Data)}");
            return;
        }

        Interlocked.Increment(ref _gltfOverrideMessages);
        if (Diag.Verbose)
            Console.Error.WriteLine($"[GltfOverride] region={e.Simulator.Handle} local={message!.LocalId} faces={message.Faces.Count}" +
                                    (message.Faces.Count == 0 ? " (clear)" : ""));
        RaiseGltfMaterialOverride(new GltfMaterialOverrideEvent(e.Simulator.Handle, message!.LocalId, message.Faces));
    }

    // Printable ASCII only, at most 240 characters, so a binary payload cannot garble the log.
    private static string Preview(byte[] data) { … }
}
```

- Subscribe in the `GridSession` constructor next to `_client.Objects.ObjectAnimation += OnObjectAnimation;`
  (`GridSession.cs` ~782): `_client.Network.GenericStreamingMessage += OnGenericStreamingMessage;`. Unsubscribe in
  `Dispose` next to `_client.Objects.ObjectAnimation -= OnObjectAnimation;` (~930).
- Put the conversion and the preview in an `internal static` helper (for example `GltfOverrideEvents`) so it can be
  tested without a client. Tests in `tests/SLNG.Net.Tests/GltfOverrideHookTests.cs`: a payload becomes the right
  event; a rejected payload is counted and previewed (non-printable bytes replaced, length capped).
- **A regression test that documents why SLNG parses itself:** in the same file, feed the four LMV-rejected payloads
  to `LibreMetaverse.StructuredData.OSDParser.DeserializeLLSDNotation` and assert it **throws** `OSDException`, and
  that `GltfOverrideParser` accepts each. Comment: "if an LMV upgrade makes this test fail, LMV fixed its parser;
  keep SLNG's own (replace semantics, no LMV type) and delete only this test."

#### 4e. The existing log filter

`GridSession.FilterLibreMetaverseLog` (`GridSession.cs` ~309-318) still counts and silences LMV's own warning, but its
text now lies ("SLNG does not apply overrides yet"). Change the once-per-session line to:
`[GltfOverride] LibreMetaverse's own parser rejected an override payload (it mishandles an empty [] or {}); SLNG parses them itself, nothing is lost.`

### Step 5: the shader and the renderer

#### 5a. Shader (`app/materials/prim/prim_common.gdshaderinc`)

Next to `uniform float uv_rotation`:

```glsl
// FEAT-RENDER-26: a glTF face with a texture-transform override (base-colour slot). a = (scale.xy, offset.xy),
// b = (rotation in radians, 1 = in use). Replaces the face's legacy placement, as in the viewer (llface.cpp:1475).
uniform vec4 pbr_uv_a = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 pbr_uv_b = vec4(0.0);
```

Above `slng_transform_uv`:

```glsl
// KHR_texture_transform exactly as the viewer's shader does it (textureUtilV.glsl:36-49): scale, then rotate, then
// offset, about the top-left corner. uv is SLNG's mesh UV (top-origin V); the viewer flips V before and after, and
// SLNG's flipV meshes make those two flips cancel, so the same formula applies directly.
// C# mirror: GltfTextureTransform.Apply. Change both together.
vec2 slng_khr_uv(vec2 uv) {
    float s = sin(pbr_uv_b.x);
    float c = cos(pbr_uv_b.x);
    vec2 sc = uv * pbr_uv_a.xy;
    return vec2(c * sc.x + s * sc.y, -s * sc.x + c * sc.y) + pbr_uv_a.zw;
}
```

In `slng_transform_uv`, directly after the `if (uv_texgen == 1) { … }` block:

```glsl
    // FEAT-RENDER-26: an animated face keeps the animation (it already replaces the placement); otherwise a face
    // with a texture-transform override uses it instead of the legacy placement.
    if (pbr_uv_b.y > 0.5 && tex_anim_b.w <= 0.5) {
        return slng_khr_uv(uv);
    }
```

In `app/scripts/PrimShaderFamily.cs`, next to `TexAnimA`:
`public static readonly StringName PbrUvA = "pbr_uv_a"; public static readonly StringName PbrUvB = "pbr_uv_b";`

**Check:** the other `.gdshader` files under `app/materials/prim/` include `prim_common.gdshaderinc`, so the uniforms
exist in every variant; list the ones that call `slng_transform_uv` in your report. No global uniform is added, so
`check_shader_globals.py` is unaffected (still run it).

Add `tests/SLNG.Core.Tests/PbrShaderTextTests.cs`: read `prim_common.gdshaderinc` (find the repo root the way
`LegacyShadeMirrorTests.RepoRoot()` does) and assert it contains `uniform vec4 pbr_uv_a`, `uniform vec4 pbr_uv_b`,
`vec2 slng_khr_uv(`, and the two lines `c * sc.x + s * sc.y` and `-s * sc.x + c * sc.y`.

#### 5b. Renderer (`app/scripts/ObjectRenderer.cs`, `BuildFaceMaterialAsync`)

1. **Extract** the part of the PBR branch that only writes uniforms and picks the shader (today: the `AlbedoColor`,
   `MetallicFactor`, `RoughnessFactor` lines, the two `Emission*` lines, and the `Blend` / `Scissor` / `DoubleSidedTwin`
   block, ~4376-4422) into

   ```csharp
   /// <summary>FEAT-RENDER-26: writes a face's PBR factors, emission, alpha-mode shader and double-sided twin onto
   /// <paramref name="material"/>, plus the base-colour texture transform of the override (pbr_uv_a/b), if any.
   /// Pure uniform writes, so a selftest can call it without an asset service. <paramref name="pbr"/> is already
   /// merged.</summary>
   internal static void ApplyPbrFactors(ShaderMaterial material, PbrMaterialData pbr,
                                        GltfMaterialOverride? o, Godot.Color colorTint)
   ```

   Move the code **verbatim** (same order, same comments that explain alpha/double-sided). Then add at its end:

   ```csharp
   if (o?.BaseColorTransform is { } t)
   {
       material.SetShaderParameter(PrimShaderFamily.PbrUvA, new Godot.Vector4(t.Scale.X, t.Scale.Y, t.Offset.X, t.Offset.Y));
       material.SetShaderParameter(PrimShaderFamily.PbrUvB, new Godot.Vector4(t.Rotation, 1f, 0f, 0f));
   }
   ```

   **Verify the extraction is a pure move:** the face material of an object with no override must be bit-for-bit the
   same as before (the selftest in Step 6 and the fingerprint comparison cover this).
2. **At the top**, after `var colorTint = …`: a face whose override sets `bc` ignores the face's TE colour
   (Decision 6):

   ```csharp
   if (ft.RenderMaterialId != Guid.Empty && ft.PbrOverride?.BaseColor != null)
       colorTint = new Godot.Color(1f, 1f, 1f, 1f);
   ```

   This must run **before** `tintIsTranslucent` is computed.
3. **Texel area:** where `texelArea` is computed from `ft.RepeatU/RepeatV`, use the override's scale when it has a
   base-colour transform (`ft.PbrOverride?.BaseColorTransform is { } t ? t.Scale : (ft.RepeatU, ft.RepeatV)`), so a
   4x tiling picks the right mip.
4. **In the `pbr != null` branch:** `var eff = PbrOverrideMerge.Apply(pbr, ft.PbrOverride);`, then use `eff` (not
   `pbr`) for: `ApplyPbrFactors(material, eff, ft.PbrOverride, colorTint)`, the `LogFaceAlpha` text, the four
   `used.Add(...)` / `GetOrCreateGpuTextureAsync(...)` texture blocks (so a swapped texture is requested and
   ref-counted). **Keep `pbr`** (the base) for the `[PbrMaterial]` first-seen line and for the `_mirrorMaterials` test
   (both are keyed by material id, not by face), and only run the mirror test when `ft.PbrOverride?.Roughness == null`.
5. A `Diag.Verbose` line once per face that has an override:
   `[PbrOverride] mat=<8> bc=<yes/no> rf=<v|-> mf=<v|-> am=<v|-> tex=<count swapped> ti=<yes/no>`. Use the existing
   `LogFaceAlpha`-style helper; do not print when `Diag.Verbose` is off.
6. Nothing else in `ObjectRenderer` changes: the re-apply gate (`FacesEqual`), `FaceSurfaceMerge`, and instancing
   (`MaterialFingerprint` reads every uniform) already see the override through `FaceTexture`.

### Step 6: selftest

Add `CheckPbrOverrideApply` (new file `app/scripts/ObjectRenderer.PbrOverride.SelfTest.cs`, registered in
`app/scripts/SelfTest.cs` after `CheckTextureAnimInShader`), static, no scene needed. Use `ObjectRenderer.ApplyPbrFactors`
on `new ShaderMaterial { Shader = PrimShaderFamily.Opaque }` with a made-up `PbrMaterialData`:

1. **No override:** `albedo_color` = base factor × tint, `roughness_factor`, `metallic_factor` = base, emission
   off, shader still `Opaque`, and `pbr_uv_b` is unset or has `.Y == 0` (treat a `Nil` read as unset).
2. **Factors:** after `PbrOverrideMerge.Apply` with colour (0.8,0.1,0.1,1), roughness 0.35, metallic 0, emissive
   (0.2,0,0): the uniforms carry exactly those; emission is on.
3. **Alpha MASK, cutoff 0.4:** shader is `PrimShaderFamily.Scissor` and `alpha_scissor_threshold == 0.4`.
4. **BLEND:** shader is `PrimShaderFamily.Blend`. **Double-sided:** the shader is the double-sided twin of whatever
   was selected.
5. **Transform:** scale (2,3), offset (0.25,-0.5), rotation 0.5: `pbr_uv_a == (2,3,0.25,-0.5)`, `pbr_uv_b == (0.5,1,0,0)`.
6. **Equal in, equal out:** two materials built from equal overrides have an identical `MaterialFingerprint.Of`; a
   different roughness or transform gives a different one.

Report the check line. Afterwards run `git status` and undo any change the headless run made to `app/project.godot`
(see `slng-verify`).

### Step 7: roadmap, version, manual

- `app/scripts/Boot.cs`: `AppVersion`.
- `docs/ROADMAP.md`: the FEAT-RENDER-26 row (state, and a one-line "Built …" summary when done).
- `docs/BENUTZERHANDBUCH.md`: grep for statements about PBR / glTF materials that say tints or overrides are not
  shown; none were found when this spec was written (only the hardware line and the mirror line). Change nothing
  unless a statement is now wrong.

## Acceptance Criteria

- [ ] `LlsdNotationTests`, `GltfOverrideParserTests`, `GltfTextureTransformTests`, `PbrOverrideMergeTests`,
      `GltfOverrideStampTests`, `GltfOverrideWorldTests`, `GltfOverrideHookTests`, `PbrShaderTextTests` pass. **The
      four payloads LMV rejects parse** and the LMV regression test shows LMV still rejects them.
- [ ] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test`,
      `dotnet format SLNG.sln --verify-no-changes`, and `python tools/check_shader_globals.py` are all clean.
- [ ] `godot --headless --path app --log-file user://logs/selftest/godot.log -- --selftest` prints `PASS` with the new
      check `ok`. Then `git status`; undo headless changes to `app/project.godot`.
- [ ] Report: the list of shaders that include `prim_common.gdshaderinc` and call `slng_transform_uv`; whether the limit test
      (the last one in 4c) was written; the verify output; any fixture whose behaviour differs from the table and why.
- [ ] **In-world (the user does this)**, on Second Life with a full-perm object that has a PBR material (Firestorm
      Build > Texture > PBR; each change below writes an override). Compare SLNG against Firestorm side by side, same
      camera and time of day:
  - [ ] **Tint:** change the base colour. SLNG shows the same colour.
  - [ ] **Roughness and metallic** sliders: same highlight and reflection.
  - [ ] **Emissive** colour: glows as in Firestorm.
  - [ ] **Alpha mode** (blend / mask / opaque) and cutoff; **double sided**.
  - [ ] **Texture swap** on the base-colour slot; **remove** a texture.
  - [ ] **Texture transform:** repeat 2 x 2, an offset, and a rotation (about the top-left corner, clockwise).
  - [ ] **Reset the overrides in Firestorm while SLNG is watching:** SLNG returns to the asset look without a relog
        (the clear-all message, the one LMV could not parse).
  - [ ] **Relog / leave and return** to the region: the overrides come back (see Open question 4).
  - [ ] A region full of PBR content (Sirens, where the 16 warnings came from): no `[GltfOverride] rejected` lines
        (or, if there are, the first three payloads are in the log), and `obj-ticks` / `[PhaseCost]` do not rise.

## Open questions (left for the maintainer)

1. **TE colour on PBR faces.** The viewer ignores it for every PBR face (`llface.cpp:1397-1400`); SLNG multiplies it
   into the base colour. This task changes that only for faces whose override sets `bc`. Whether to follow the
   viewer for all PBR faces (a behaviour change; the build floater's per-face alpha edit touches the same path,
   `ObjectRenderer.cs` ~3170-3177) is a separate decision.
2. **Per-slot texture transforms.** Only the base-colour slot's transform is rendered. If normal / ORM / emissive
   differ (rare), they use the base-colour placement. Rendering them needs `normal`/`orm`/`emission` varyings next to
   `v_normal_uv`; the data is already parsed and stored.
3. **Texture animation on a PBR face with a transform override:** the animation wins (the viewer applies the animation
   first and the transform on top: `textureUtilV.glsl:62-79`). Composing them needs the animation evaluated before
   `slng_khr_uv`.
4. **Does the simulator re-send overrides when SLNG re-requests objects** (`[Rerequest]`, `ObjectUpdateCached`)? The
   viewer persists them in its object cache and does not need to be told again. If, after a relog or a teleport back,
   a face loses its override while Firestorm keeps it, SLNG needs its own persistence (the viewer's format is
   `llvocache.cpp:63-153`). Compare the `[GltfOverride]` counts (`--diag`) with the number of overridden faces.
5. **The exact shapes of the 16 rejected payloads are unknown** (never logged). The mechanism and four reproducing
   shapes are certain; which of them the simulator sends is not. The first three rejected payloads are now logged,
   but SLNG's parser should accept all of them.
6. **Mirror detection** (`_mirrorMaterials`, FEAT-RENDER-22) is keyed by material id and uses the base roughness, so
   an override that makes a face a mirror (or not) is not seen.
7. **Worn attachments and animesh** use `AvatarRenderer`'s own PBR path. `prim.Faces` already carries the stamped
   override for them, and `PbrOverrideMerge` / `ApplyPbrFactors`-style code is reusable: a follow-up.
8. **Planar texture mapping + transform override:** the transform is applied to the planar coordinate. The viewer does
   the same order, but it was not compared in-world.
9. **Every decoded prim carries 45 `FaceTexture`s** (LMV's fixed `TextureEntry.FaceTextures`, `MAX_FACES = 45`), about
   5 KB per prim, most of them copies of the default face. This task adds 8 bytes to each. Trimming the array to the
   faces a prim really has would save far more memory and per-update work, but touches every reader of `Faces`.
10. **Editing.** `ModifyMaterialParams` (`llgltfmateriallist.cpp:432-506, 722-759`) is the way SLNG would one day
    write overrides; nothing here depends on it.

## Sub-tasks / Progress

- [ ] Step 1 `LlsdValue` / `LlsdNotation` + tests
- [ ] Step 2 override DTOs, `GltfOverrideParser`, fixtures F1-F14, transform tests
- [ ] Step 3 `PbrOverrideMerge` + tests
- [ ] Step 4a `FaceTexture.PbrOverride`, `PrimitiveComponent.DefaultFace`
- [ ] Step 4b `GltfOverrideStamp` + tests
- [ ] Step 4c event, `IWorldEventSource`, `WorldSimulation` store, drop rules + tests
- [ ] Step 4d `GridSession` hook, conversion helper, LMV regression test
- [ ] Step 4e log filter wording
- [ ] Step 5a shader + `PrimShaderFamily`, shader text test
- [ ] Step 5b `ApplyPbrFactors` extraction and the `BuildFaceMaterialAsync` changes
- [ ] Step 6 selftest
- [ ] Step 7 roadmap, version, commit
- [ ] In-world check by the user
