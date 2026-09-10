# [BUG-RENDER-16] High-frequency foliage: soft edge vs. flicker

- **Feature ID:** `BUG-RENDER-16`
- **Track:** `render`
- **Status:** `✅ Done` — confirmed in-world 2026-09-10 at `v0.22.28-alpha`: grass, bush and pine all calm while walking ("passt jetzt alles"); the fix is three deterministic tie-breaks in Godot's per-instance transparent sort, see the v0.22.26–28 sections
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Symptom

Reported live 2026-09-10 on SL region *Millenium*, dense ornamental grass (pink
plumes + green blades), Firestorm side-by-side.

- With BUG-RENDER-11's shipped behaviour (undeclared alpha → hard `Scissor` cutout),
  the grass renders as flat, hard-edged, blocky pink shapes. The thin low-alpha
  green blades are cut away entirely, so the field reads far too pink and the
  feathered silhouette is gone. Firestorm shows soft, translucent blade tips and
  keeps the green.
- Routing the same faces to `Blend` restores the Firestorm look exactly — but the
  grass then **flickers under camera motion**: chunks swap draw order as the camera
  orbits. This is BUG-RENDER-11's original complaint, for the subset it deliberately
  traded away.

## Root cause

Two separate limits, and no shader mode escapes both:

1. **Depth-writing / opaque-queue modes never flicker but cannot make a soft edge.**
   `Scissor` is a binary test; `alpha_to_coverage` over the project's 4× MSAA gives
   only ~4 coverage levels, so a wispy tip stair-steps. `Hash` (BUG-RENDER-09's
   stochastic cutout) writes depth like opaque so it never sorts, but resolves the
   gradient as a per-pixel dither that reads as crunchy speckle.
2. **Blended modes make the correct soft edge but flicker.** Godot's transparent
   queue sorts **per surface** by AABB-centre distance — far coarser than the
   viewer's per-draw sort — so dense overlapping foliage at similar distance swaps
   order on tiny camera moves. `BuildArrayMesh` emits **one Godot surface per SL
   face**, so a multi-blade grass mesh hands the sorter N independently-reorderable
   pieces (same shape as BUG-RENDER-12's hair: "one Godot surface per SL face handed
   the sorter 18 reorderable pieces of one authored hair stream").

Firestorm is stable because its sort granularity is finer, which — per BUG-RENDER-11
and BUG-RENDER-12 — **no shader mode fixes**.

## What was tried (all in-world, `--foliage-alpha=<mode>`, v0.22.9–v0.22.16)

| mode | edge | flicker | verdict |
|---|---|---|---|
| `scissor` (BUG-RENDER-11) | hard, blocky, eats the green | none | rejected: wrong look |
| `blend` | **soft, matches Firestorm** | **yes** | correct look, flickers |
| `hash` (`ALPHA_HASH_SCALE` 1→4) | crunchy dither / "zu scharf" | none | rejected: grainy |
| `prepass` (`depth_prepass_alpha`) | soft | yes | Godot's prepass α-threshold is ~0.99 — mid-alpha grass never enters the depth prepass, so it still sorts |
| `edge` (`ALPHA_ANTIALIASING_EDGE`) | still hard | none | still 4 MSAA coverage levels; the built-in doesn't help enough |
| `blenddepth` (`depth_draw_always`) | soft | **strong** | render order = depth-write order, so ties flip harder — worse |
| + **TAA** (`use_taa`) on `hash` | still grainy | none | Godot's alpha hash is stable per surface point, not temporally jittered, so TAA has nothing to average when static |
| + **FXAA** (`screen_space_aa=1`) on `hash` | still grainy | none | softens a little, not enough |

## Interim decision (this branch, `v0.22.16-alpha`)

- **`RenderConfig.HighFrequencyFoliageAlpha` defaults to `Blend`.** It is the only
  mode whose texture the user accepted as matching Firestorm; the flicker is a known
  Godot-sort limitation and shipping the correct look with it beats a permanently
  degraded edge. `Scissor` for the same subset (BUG-RENDER-11) was rejected in-world.
- The other five modes stay wired, reachable via `--foliage-alpha=` /
  `tools/run-client.ps1 -FoliageAlpha`, as the A/B harness the real fix needs.
- `--foliage-hash-scale=N` tunes `ALPHA_HASH_SCALE` for the `hash` mode.
- `project.godot` gains `anti_aliasing/quality/screen_space_aa=1` (FXAA) — kept as a
  reasonable default; it is cheap and has no ghosting, unlike TAA (which was tried
  and reverted).
- Clean cutouts (`maskable == true`: fences, sharp leaf cards) and `fullbright`
  faces (BUG-RENDER-14 flames) are **never** re-routed — unchanged.

## The real fix — same-material surface merge (shipped, `v0.22.17-alpha`)

### The cheap spike was answered by reading Godot, not by running it

The planned first step was to test whether letting a mesh's same-material faces share ONE
`ShaderMaterial` object would make Godot sort and batch them as a unit. It cannot, and the
reason is two lines of Godot 4.7-stable — `render_forward_clustered.h:722-726`, the comparator
the alpha list is actually sorted with:

```cpp
struct SortByReverseDepthAndPriority {
    bool operator()(const GeometryInstanceSurfaceDataCache *A, const GeometryInstanceSurfaceDataCache *B) const {
        return (A->sort.priority == B->sort.priority) ? (A->owner->depth > B->owner->depth)
                                                      : (A->sort.priority < B->sort.priority);
    }
};
```

Three facts follow, and together they close the whole shader-mode search:

1. **The material never enters the comparison.** Only `sort.priority` — the material's
   *render priority* — and `owner->depth`. Texture, shader, uniforms, object identity: none of
   it is consulted. So sharing a material object changes nothing about order.
2. **`depth` is per INSTANCE, not per surface.** `render_forward_clustered.cpp:961-966` assigns
   it once per `GeometryInstance` from the instance AABB centre. **Every surface of one prim
   therefore compares exactly equal**, and `SortArray` is an unstable median-of-3 introsort
   (`core/templates/sort_array.h:202-235`): the tied group is permuted according to the
   surrounding array contents, which change whenever the camera moves. *That* is the flicker,
   named precisely.
3. **`render_priority` cannot be used as an intra-object tiebreak.** It is compared *before*
   depth and is scene-global, so numbering a plant's faces 0..N would order a near plant's
   face 0 ahead of a far plant's face 1 and break inter-object sorting outright.

The reference viewer's equivalent — keep worn faces in authored order and let depth resolve them
(`llvovolume.cpp:6332-6335`) — has no Godot analogue *except* the one below: triangles **within**
one surface are drawn in index order and are never sorted against each other.

### What shipped

Consecutive submeshes whose resolved `FaceTexture` compares **equal** are committed as **one**
Godot surface, appended into a single `SurfaceTool` with their indices shifted past the vertices
already in it. Identical in shape to the BUG-RENDER-12 fix that ended the hair bug, and safe for
the same reason: `FaceTexture` is a `readonly record struct`, so equality covers every field a
material is built from — texture id, both material ids, tint, repeats, offsets, rotation, texgen,
fullbright. Two faces that compare equal cannot produce different materials, and the alpha *kind*
is itself a function of that record plus per-texture cached properties, so a merged run cannot
straddle two shader kinds either.

The planner is pure and lives in `SLNG.Core.FaceSurfaceMerge`, so the part that has to be right —
which faces may share a material — is unit-tested (26 tests) rather than only observable in-world.

Four things this had to solve that the avatar path did not:

- **World prim meshes are SHARED between instances**, keyed by geometry; the merge depends on the
  *instance's* texturing. The GpuCache key is therefore `(geometry, run-boundary bitmask)`, an
  EXACT mask rather than a hash — a collision would render the wrong geometry. A field of
  identically-textured grass still shares one upload; a re-textured copy gets its own. **A plan
  that merges nothing returns the geometry key unchanged**, so every object the merge cannot help
  keeps bit-identical caching, sharing and surface layout.
  A safety valve bounds the trade: past **8 distinct merge patterns for one geometry** that
  geometry stops merging and every further variant shares the single un-merged upload. An asset
  re-textured many different ways would otherwise get one `ArrayMesh` per pattern, and "render
  budgets over fidelity" (AGENTS.md) outranks the merge; the content this fix is for — one plant
  re-used verbatim across a field — sits at one or two patterns. The cap rewrites the *plan* to
  identity, not merely the key: re-keying alone would have built merged geometry under the
  un-merged key and handed the wrong surface layout to every sharer of it.
- **Collision shapes stay on the geometry key.** A merge regroups triangles into surfaces without
  changing a triangle, so all merge variants share one trimesh shape — which matters, since
  building one was measured at 6.12 ms and 94 % of all mesh work.
- **A re-texture re-plans.** The grouping is a function of the face records, and the three
  mesh-assignment paths are all gated on a shape/asset change, so a texture edit alone would have
  left an object wearing a grouping computed for its previous texturing.
- **A per-face texture animation is a merge barrier.** A merged run wears one material, so an
  individually animated face must stand alone or it would drag identically-textured static faces
  along with it. An all-faces block (wire 255 → -1) needs no barrier. A script that *starts* an
  animation after the mesh was built re-plans too.

`_meshFaceIndices` is consequently no longer surface ⇔ face 1:1: an entry now holds the run's
FIRST face number, which is a valid representative precisely because every face in the run
compares equal. Both consumers (`ApplyFaceMaterialsAsync`, the per-frame texanim reapply) already
index it by surface and needed no change; the other two `GetSurfaceCount()` users iterate the real
surface count and map nothing.

### The diagnostic decides what happens next

`[PrimMesh] mesh=… N submeshes -> M surface(s): <verdict>` (behind `--diag`), once per distinct
mesh. **Read this before drawing any conclusion from the screen** — the three outcomes need three
different follow-ups and only the log separates them:

| log | meaning | next step |
|---|---|---|
| `6 -> 1: MERGED` | the flicker was N reorderable surfaces inside one object | fixed here |
| `6 -> 6: no same-material consecutive runs` | the faces genuinely differ | find out *why* (tint? repeats?) before blaming the sort |
| `1 -> 1: single surface already` | the reorder is **between objects** | see below — this fix cannot reach it |

The third case is the one this change deliberately does not attempt, and it is worth stating
plainly rather than discovering twice: if dense grass is many separate prims rather than one prim
with many faces, then Godot is tying *instances* whose AABB centres are near-equidistant, and
merging surfaces has nothing to merge. The researched lever for that case is the reference
viewer's `ALPHA_DIRTY` hysteresis — it re-sorts unrigged alpha only once the view angle has moved
past `0.64` (`llspatialpartition.cpp:667-674`) — which has no Godot equivalent and would be a
separate task. It is not bundled here: the history in BUG-RENDER-12 is explicit about what
changing two variables at once costs.

## The merge was not enough — measured, not guessed (`v0.22.19`)

The merge shipped and fired: of 1848 distinct meshes on *Millenium*, **195 merged** (`8 -> 1`,
`6 -> 1`, `8 -> 3` …). The flicker was unchanged, so the `[PrimMesh]` verdict table above was
read, and then a second census was added because the first question it raised — *are the
flickering surfaces even on multi-surface objects?* — it could not answer.

`[AlphaSort]`, over every visible object within 96 m:

```
1310 objects / 1821 surfaces  multiSurface=373 singleSurface=937 within32m=565
```

**937 of 1310 transparent objects have exactly ONE sorted surface.** Godot gives every surface of
one instance the same depth, so a single-surface object has no internal tie left to remove — the
surface merge is structurally incapable of touching 71 % of the problem. What reorders them is the
sort *between* objects, and Godot re-runs it from scratch every frame over all 1821 surfaces.

### Two diagnostics were wrong before this number was trustworthy

Worth recording, because both cost a round trip:

1. **The `[PrimMesh]` line was `Logger.Debug`**, so the run that was supposed to decide the next
   step produced no line at all. A diagnostic whose whole purpose is to settle a question must not
   be behind the flag the user then does not pass. `[AlphaSort]` is `Logger.Info`.
2. **The first `[AlphaSort]` counted the shader at material-build time and always got zero.**
   `ApplyAlphaCutout` — which decides Blend vs Scissor — runs inside a fire-and-forget
   `_ = GetOrCreateGpuTextureAsync(...).ContinueWith(...)` marshalled onto the Visual lane, i.e.
   *after* `BuildFaceMaterialAsync` has already returned the material. At `Task.WhenAll` time every
   shader is still `Opaque`. The census now reads `GetSurfaceOverrideMaterial(i).Shader` off the
   live surfaces instead: it looks at the settled state rather than predicting it.

## The between-object fix: the viewer's alpha-sort hysteresis, ported (`v0.22.20`, opt-in)

`llspatialpartition.cpp:657-676`. Two details matter and both are easy to get wrong from memory:

```cpp
eye.normalize3fast();
...
diff.setSub(view_angle, group->mLastUpdateViewAngle);
if (diff.getLength3().getF32() > 0.64f) { ... group->setState(LLSpatialGroup::ALPHA_DIRTY); }
```

- `eye` is **normalised first**, so `0.64` is a **chord on the unit sphere — about 37° of direction
  change** — not a distance and not 0.64 radians.
- The re-sort is **per spatial-octree group**, and between re-sorts that group's alpha order is
  simply *reused*.

That is the whole reason Firestorm is stable on this content. Its sort proxy is no better than
Godot's; it just stops re-deciding. AABB-centre distance cannot express the true per-pixel order of
interpenetrating grass cards, so re-deciding every frame swaps between orderings that are each
wrong in different pixels.

**The port.** Godot's sorter cannot be told to keep a previous order, but it can be told what depth
to sort at — `inst->depth = distance(cam, centre) - sorting_offset`
(`render_forward_clustered.cpp:961-966`). Writing

```
SortingOffset = currentDepth - depthWhenLastFrozen
```

makes an object sort at the depth it had when its direction was last frozen, so Godot's own
unmodified sort reproduces the frozen order. Each object keeps its own frozen direction and
re-freezes when the direction to the camera moves past the threshold — finer-grained than the
viewer's per-group rule, and it needs no spatial structure SLNG does not have.

Scoped and cheap: only objects the census found to carry a sorted-transparent surface are touched
(`HasSortedTransparent`, refreshed on a 1 s scan), and an object whose offset would not change is
not written at all — every write is a Godot interop call and this runs every frame.

**The trade, stated up front:** this does not make the order *right*, because no single order is.
It stops it being re-decided. Instead of continuous shimmer the order snaps once per ~37° of orbit
— exactly the behaviour the reference viewer ships.

**Opt-in**, like the six `--foliage-alpha` modes, so it can be A/B'd in one session:
`--alpha-sort-hysteresis` (takes the viewer's 0.64) or `--alpha-sort-hysteresis=N`, and
`tools/run-client.ps1 -AlphaSortHysteresis N`. Default off — this changes global transparent
render behaviour and has not been confirmed in-world.

## What is being sorted BY — the parity gap the first three rounds all missed (`v0.22.22`)

Three rounds aimed at the wrong layer, and it is worth being explicit about the pattern: the
surface merge attacked *how many* sortable pieces there are, the hysteresis attacked *when*
re-sorting is allowed. Neither asked what the sort key actually **is**.

The observation that settled it came from in-world and could not have come from the log: **the
grass is steady standing still AND while only turning the camera; it flickers when the avatar
moves.**

That is a very specific signature. Godot's sort key is a RADIAL distance —
`inst->depth = cam_transform.origin.distance_to(center)`
(`render_forward_clustered.cpp:961-966`) — and a radial distance is **invariant under camera
rotation**, which is exactly why turning is calm. But when you walk, it changes by completely
different amounts for an object straight ahead and one off to the side, so the order churns
continuously.

The reference viewer does not sort alpha radially. `pipeline.cpp:3732` orders alpha groups with
`CompareDepthGreater` on `mDepth` (`llspatialpartition.h:236`), and `mDepth` is built **only for
groups containing `PASS_ALPHA`** (`llspatialpartition.cpp:684-692`):

```cpp
LLVector4a v = eye;                  // eye = groupCentre - cameraOrigin
...
LLVector3 at = camera.getAtAxis();
t = ata; t.mul(0.25f); t.mul(group->mObjectBounds[1]);   // a quarter of the extents, toward the front
v.sub(t);
group->mDepth = v.dot3(ata).getF32();                    // PROJECTION on the view axis
```

The `else` branch immediately below keeps `eye.getLength3()` — radial — for everything that is not
alpha. So the switch to planar depth is deliberate and alpha-only.

**Why planar depth is stable where radial is not:** walking along the view axis decreases every
object's planar depth by the *same* amount, so the ordering is preserved exactly. Radial distance
has no such property. This is not a hysteresis or a granularity question — it is the wrong metric.

**The port.** Same lever as before, because Godot subtracts it:
`SortingOffset = radialDistance - planarDepth` makes Godot's own sorter order by planar depth,
including the viewer's quarter-extent bias toward the front of the bounding box. Independent
opt-in switch, so all four combinations can be A/B'd in one session:
`--alpha-sort-planar` / `tools/run-client.ps1 -AlphaSortPlanar`, and the `[AlphaSort]` line
reports `planar` or `radial`.

### Still open after this, whatever the A/B says

- **Worn hair** was reported flickering too ("das Gras und bei manchen Haaren"). That is
  `AvatarRenderer`'s path, which this change does not touch — and the viewer does not depth-sort
  rigged alpha at all: `pipeline.cpp:3734` uses `CompareRenderOrder` (avatar attachment order),
  not `CompareDepthGreater`. Different mechanism, separate task.
- **Multi-material multi-surface objects** (474 meshes, `N -> N`) still hand Godot several
  surfaces at one identical instance depth. The merge may not join them (different materials) and
  a per-instance `SortingOffset` moves them all together, so the tie survives. Only reachable by
  giving each transparent surface its own instance — the granularity the viewer sorts at.
  Not attempted; the in-world evidence says this is not what is being seen (it would flicker with
  a completely stationary camera, and it does not).


## Re-reading the freeze test — the order is not re-decided, it is WRONG (`v0.22.24`)

The `--alpha-sort-freeze` result was read as "the sort order is not the cause". It proves less
than that. Freezing removes the *re-ordering*; it does nothing about the order being *wrong*,
and a wrong-but-frozen order is exactly what parallax makes visible:

- A blended far blade composited over a near one is a static picture while the camera stands.
- **Rotating the camera about its own eye point preserves every occlusion relation** — the ray
  through a blade point stays the same ray, so nothing behind it changes. Calm.
- **Translating the camera slides every wrongly-ordered crossing across the blade in front of
  it.** Hundreds of such crossings, all moving, is the "zappeln". Only real per-pixel depth
  removes it, which is why the depth-writing modes were always calm.

That is the one signature every observation shares, including the four fixes that changed the
sort *key* or its *timing* and changed nothing — none of them could make the order right, because
no single per-instance order is.

### Ruled out against the engine source before building, so nobody re-derives them

- **SSAO / SSIL / SSR do not touch the blend pass.** The transparent pass is set up with
  `_setup_environment(..., p_opaque_render_buffers = false)` (`render_forward_clustered.cpp:2415`),
  and that flag is what gates `ss_effects_flags` (`:759-772`), so
  `scene_forward_clustered.glsl:2028` never samples `ao_buffer` for a transparent surface. The
  parallax-shaped hypothesis "blended grass inherits the AO of the terrain behind it" is dead;
  the user's SSAO-on setting is not a factor.
- **Depth of Field was OFF** in the session (`preferences.cfg [dof] enabled=false`), so the
  viewer's own DoF depth pass (`lldrawpoolalpha.cpp:212-227`) is not what was missing. It is,
  however, the construction reused below.
- **A plain blend material casts no shadow and is in no prepass** — `uses_alpha_pass()` without
  `uses_depth_in_alpha_pass()` sets neither `FLAG_PASS_SHADOW` nor `FLAG_PASS_DEPTH`
  (`render_forward_clustered.cpp:4191-4200`). Self-shadowing is not what differs between the
  modes either.
- **Godot's `depth_prepass_alpha` threshold is a hard 0.99** —
  `p_render_data->scene_data->opaque_prepass_threshold = 0.99f`
  (`render_forward_clustered.cpp:1823`), tested at `scene_forward_clustered.glsl:1428`. Not a
  project setting, not a uniform. That is the precise reason `--foliage-alpha=prepass` still
  flickered: nothing below 0.99 alpha ever entered the prepass.
- **A transparent-queue material writes depth iff `depth_draw_always`** —
  `scene_shader_forward_clustered.cpp:443-445` disables the write for `depth_draw_opaque` only,
  and the compare op is `GREATER_OR_EQUAL` (reversed Z, `:356`), so a second pass over the same
  geometry lands on equal depth and passes.

### What the reference viewer does — a depth pass over the alpha pool

`lldrawpoolalpha.cpp` has the construction twice:

```cpp
// :212-227 — after blending, depth only, for DoF
simple_shader->setMinimumAlpha(0.33f);
gGL.setColorMask(false, false);          // depth buffer only
renderAlpha(..., true);                   // "discard mostly transparent faces"

// :240-248 — worn (rigged) alpha writes depth for every fragment, drawn BEFORE unrigged alpha
bool write_depth = rigged || ...;
LLGLDepthTest depth(GL_TRUE, write_depth ? GL_TRUE : GL_FALSE);
```

The threshold is the interesting part. `depth_draw_always` (`blenddepth`) was worse because a
fully transparent texel wrote depth and punched a hole through everything behind it; Godot's
prepass was useless because 0.99 let nothing through. The viewer's 0.33 sits where a blade's
core writes depth and its fringe does not.

### The port — `--foliage-alpha=blendcore` (default from `v0.22.24`)

`prim_depth_core.gdshader`, chained onto the ordinary `prim_blend` material as its `next_pass`:

- `blend_mix, depth_draw_always, unshaded`; fragment: `if (alpha < core_alpha_threshold)
  discard; ALPHA = 0.0;` — depth for the cores, the colour buffer untouched.
- `RenderPriority = -2`. The alpha comparator sorts by priority **before** depth
  (`render_forward_clustered.h:722-726`), so the whole bucket — every core of every foliage face
  — is in the depth buffer before water (-1) or any default-priority blend pass is drawn. That
  is the viewer's "depth pass over the whole pool", not a per-object prepass.
- The blend pass is unchanged. A fringe behind another blade's core is now rejected in **every**
  draw order; a core's own colour pass hits equal depth and blends as before; the only pairs the
  sort still decides are fringe-over-fringe, both under the threshold and low-contrast.
- Built by `Duplicate()` of the finished blend material, so every placement uniform the two
  passes must agree on (texture, tint, `uv_*`, `uv_texgen`, `prim_scale`) is copied by
  construction; textures are shared by reference so a later in-place sharpen reaches both.
  `ApplyAnimatedPlacement` forwards the animated UV uniforms to the pass.
- Scope is exactly the BUG-RENDER-16 subset (`!maskable && !fullbright`, legacy undeclared
  alpha). Fences, flames, glTF/legacy-material alpha and tint-translucent faces are untouched.
- Threshold: `RenderConfig.FoliageCoreAlpha = 0.33`, `--foliage-core-alpha=N`,
  `tools/run-client.ps1 -FoliageCoreAlpha N`.

**The trade, stated up front:** a core at alpha 0.33–1.0 occludes what is behind it but still
blends its own (1 − α) with the *background*, not with the far blade it hid — a static halo where
plumes overlap, never a moving one. Lower `N` = more occlusion, more halo; higher `N` = softer, more
fringe left to the sort. 0.33 is the viewer's number, not a tuning result.

### v0.22.25 — the core must sit a hair behind its own colour pass

First in-world run: a pine's needle clumps rendered as sky-coloured silhouettes while the
neighbouring clumps were correct (Firestorm side-by-side, 2026-09-10). Diagnosis from the picture
alone: the silhouette showed the *sky*, not the water behind the tree — so the water had been
depth-rejected by the needle's core, and then the needle's own colour pass was rejected too.
The two passes are different shader variants (the depth pass is `unshaded`), the driver may
contract or reorder the position maths differently in each, and `GREATER_OR_EQUAL` on a
coin-flip ulp fails the colour pass against its own core on roughly half the triangles. The
depth pass now writes `DEPTH = FRAGCOORD.z * (1.0 - 1e-4)` (reversed Z, so slightly farther):
~1000× the ulp noise, 1 cm at 100 m, 0.1 mm at 1 m. The grass had hidden the same defect because
a hole in grass shows more grass.

### v0.22.25 — BlendCore hazes dense canopies; it is opt-in again, Blend is the default

Second in-world run: the Millenium grass was calm, but a pine (Firestorm side-by-side) rendered
its needle clumps as a pale haze. `--foliage-alpha=blend` at the same tree: correct dark canopy,
flicker back ("nicht extrem"); `scissor`: coarse, subtle flicker.

**Measured, not guessed.** The 229 textures the session routed to BlendCore were pulled from the
client's own cache (`scratch/AlphaVerdict`, Magick.NET; `scratch/` is git-ignored, so the tool and the probes below live on the dev machine only) and the pine's needle family identified —
~20 textures with one signature: 25 % of texels visible, 80 % of those ≥ 0.33 alpha, 57 % ≥ 0.9.
Solid needles at mip 0, not a soft texture. `scratch/probes/probe_motion.gd` then rebuilt a
24-card canopy from the REAL needle texture with the REAL shaders: Blend = dense and dark, Core@0.33
= exactly the in-world haze, Core@0.9 = still visibly lighter than Blend. The mechanism: at the mip
level a canopy is viewed at, a one-texel needle averages to ~0.5 alpha. Such a texel writes depth,
hides every card behind it, and itself covers half the pixel — Blend accumulates N such layers into
1 − 0.5^N, the depth pass leaves a single 50 % layer over the sky. Depth-based occlusion is wrong
by construction for minified thin foliage; the reference viewer applies it only to rigged alpha
and to the DoF depth, never to unrigged foliage, and this is why.

The pink plume texture (`f04d8802`: only 27 % of visible texels ≥ 0.33, 3 % ≥ 0.9) is the
opposite kind of content, which is why the grass looked right. No single threshold serves both, so
`RenderConfig.HighFrequencyFoliageAlpha` is `Blend` again and `blendcore` stays an opt-in mode with
the trade documented. The depth-pass shader keeps the v0.22.25 bias (`DEPTH = FRAGCOORD.z *
(1 − 1e-4)`): without it the colour pass lost a coin-flip ulp against its own core and rendered
holes (the first pine screenshot); the probe with and without bias confirms both halves.

**Found along the way, not fixed here (separate task):** the alpha-mask verdict is computed on the
FIRST decode, which on this region was a 64×64 image for 2931 of 3861 1024² assets
(`[GpuUpload] decoded=64`), and `ApplyAlphaCutout` never re-runs after a `[GpuSharpen]`. Of the
229 BlendCore textures, 19 are masks by the viewer's own `analyzeAlphaData` at full resolution and
only 1 at 64 px — 18 faces are wearing a verdict made on a blur. The viewer re-analyses on every
discard level it uploads (`LLImageGL::setImage` → `analyzeAlpha`) and `canRenderAsMask()` reads
the current answer per frame (llface.cpp:1194). Port: re-run the verdict on sharpen, and do not
trust one made below ~256 px.

### The remaining question, now sharper

For single-surface grass objects the reference viewer's order is the same per-object order Godot
produces, it is frozen between `ALPHA_DIRTY` rebuilds, and SLNG's frozen order still flickered —
so for that content the sort is not the difference either way. The pine's `scissor` run flickered
"subtil" too, with no sorting involved at all, which points at a source that is not transparency:
shadow-map re-fit under camera motion on high-frequency cutouts, or the `[GpuSharpen]` pops (2368
in one session, each swapping a texture's mip chain in place while the avatar walks). Next round
should A/B `shadows=false` and a sharpen-freeze before touching the alpha path again.

### v0.22.26 — the tie INSIDE an object: per-surface instances (viewer per-face granularity)

The user named a flickering object by its Firestorm UUID. SLNG's logs carry only LocalIds, so
the click diagnostic now prints `uuid=` too — but the same session's `[FaceParams]` blocks
already held the answer. The clicked grass is a 23-part linkset; every part wears mesh
`77204967` (4 submeshes, `no same-material consecutive runs`) with this face set:

```
[0] 458b205a            [1] 458b205a mat=0774b985   [2] 84ed20c0            [3] 84ed20c0 mat=0774b985 ...
```

Material `0774b985` is `mode=Mask cutoff=90` → those faces are `Scissor` (opaque queue, never
sorted). Faces 0 and 2 carry **no material** (LibreMetaverse's `MaterialID` already falls back to
the TE default, so this is authored, not lost) → the undeclared-alpha path → `Blend`. So each part
hands the transparent queue **two sorted surfaces at one identical instance depth**
(`render_forward_clustered.cpp:961-966`), and the unstable introsort orders that pair by whatever
the surrounding render list happens to be — which changes with every object that enters or
leaves the frustum. That is a flicker that:

- survives `--alpha-sort-freeze`, hysteresis and planar depth — all three act per INSTANCE and
  cannot reach a tie inside one (the census had said it: `multiSurface=402`);
- the surface merge could not remove — the two faces are different textures;
- `blendcore` did remove — its depth pass made the order irrelevant;
- the reference viewer never has — it sorts alpha **per face** (`LLFace::CompareDistanceGreater`
  in `genDrawInfo`, `llvovolume.cpp`), so its two faces get their own depths.

**The port.** Godot's sort key is per instance and nothing below it (no per-surface offset, no
material property, no AABB override) can split a tie — and the ArrayMesh is SHARED by every object
of the same geometry, so a surface cannot be removed from it either. `SplitSortedSurfaces`
(`ObjectRenderer.cs`) therefore gives each sorted-transparent surface of a multi-surface object its
own child `MeshInstance3D` holding a one-surface copy of the mesh (its own AABB → its own depth),
cached per `(mesh, surface)` in a `ConditionalWeakTable` so a field of identical plants still
shares, and the parent draws the original surface with `prim_hidden.gdshader` (every vertex
collapsed, every fragment discarded: nothing rasterised, no shadow pass). The material OBJECT is the
same one, so `ApplyAlphaCutout`, sharpen and texture-animation writes reach it unchanged;
`SurfaceMaterial()` routes per-surface uniform writes to the child. It runs inside the census walk
— the one place that reads the SETTLED shader — and `UnsplitSurfaces` restores the parent before
any material re-apply or mesh replacement (`ApplyFaceMaterialsAsync`, `ReleaseMeshRef`).
Verified with `scratch/probes/probe_split.gd`: split and unsplit frames are pixel-identical.

Default on; `--alpha-split=off` / `tools/run-client.ps1 -AlphaSplit off` is the A/B control.
`[AlphaSort] … split=<n>` counts the surfaces living on their own instance; `multiSurface` should
read 0 once every tie is split.

### What to read in the log

- `[AlphaSort] … depthCore=<n>@0.33 foliage=BlendCore` — `n` counts sorted surfaces that actually
  carry the depth pass. `depthCore=0` with `foliage=BlendCore` means the mechanism is not live
  (stale assembly, or the faces went down another branch) and the screen must not be judged.
- `[FaceAlpha] … -> Blend (SORTED transparent pass) [BUG-RENDER-16 --foliage-alpha=BlendCore]
  +DepthCore@0.33` (`--diag`) per texture.

### A/B

```
pwsh tools/run-client.ps1 -Diag                        # blendcore (default)
pwsh tools/run-client.ps1 -Diag -FoliageAlpha blend    # control: the flickering v0.22.16-23 look
pwsh tools/run-client.ps1 -Diag -FoliageCoreAlpha 0.5  # softer cores, more sort-decided fringe
```

The hysteresis and planar-depth switches remain independent and default off.

### Still open

- **Worn hair.** The viewer's answer for rigged alpha is the second citation above: every worn
  alpha fragment writes depth, in attachment order, before unrigged alpha
  (`lldrawpoolalpha.cpp:240-248`). That is `AvatarRenderer`'s path and a separate task; the same
  `DepthCore` next-pass (at a `cull_disabled` twin) is the obvious port.

## Acceptance Criteria

- [x] Dense grass renders with Firestorm-soft edges **and** no flicker while walking, A/B'd
      on *Millenium* against `-AlphaSplit off` (v0.22.26) and confirmed at v0.22.28.
- [x] No regression reported on fences / sharp leaf cards (`maskable`) or flames
      (`fullbright`) in the confirming session.
- [x] Per-face texture animation and prim-scale writes are routed to the split child's
      material (`SurfaceMaterial`); LOD/mesh swaps unsplit first (`ReleaseMeshRef`).
- [x] Build + `dotnet test` + `dotnet format` + shader-globals + selftest green
      (both builds; 683 tests; selftest 35/35).

## Affected Files

- `src/SLNG.Core/FaceSurfaceMerge.cs` — **new.** The pure merge planner: which consecutive
  submeshes may share a surface, the exact run-pattern signature, and the Godot-source
  citations for why the merge is the fix and no shader mode substitutes.
- `tests/SLNG.Core.Tests/FaceSurfaceMergeTests.cs` — **new.** 26 tests, the load-bearing ones
  being material-key equality: every field a material is built from must break a run, plus a
  reflection guard that fails if a field is added to `FaceTexture` without a case here.
- `app/scripts/ObjectRenderer.cs` — `PlanSurfaceMerge`, `MergedMeshKey`, `RePlanSurfaceMerge`,
  `AnimBarrierFace`; `BuildArrayMesh` commits runs; the merged-vs-geometry key split
  (`VisualState.LoadedGeometryKey`, `LoadedMeshData`, `LoadedMeshFlipV`,
  `LoadedAnimBarrierFace`); the `MaxMergePatternsPerGeometry` cap; collision cached on the
  geometry key; the `[PrimMesh]` diagnostic.
- `app/scripts/ObjectRenderer.cs` (v0.22.19-20) — `TickAlphaSortCensus` (`[AlphaSort]`, Info, 1 s
  scan / 15 s print), `IsSortedTransparent`, `TickAlphaSortHysteresis`, `ClearAlphaSortOffsets`.
- `app/scripts/RenderConfig.cs` — `AlphaSortHysteresis`, `ViewerAlphaSortHysteresis` (0.64).
- `app/scripts/Diagnostics.cs` — `--alpha-sort-hysteresis[=N]`.
- `tools/run-client.ps1` — `-AlphaSortHysteresis`.
- `app/scripts/RenderConfig.cs` — `AlphaSortPlanarDepth` (v0.22.22).
- `app/scripts/Boot.cs` — `AppVersion` → `v0.22.22-alpha`.
- `app/materials/prim/prim_depth_core.gdshader` (v0.22.24) — **new.** The alpha depth pass.
- `app/scripts/PrimShaderFamily.cs` (v0.22.24) — `DepthCore`, `DepthCoreRenderPriority`,
  `CoreAlphaThreshold`.
- `app/scripts/ObjectRenderer.cs` (v0.22.24) — `AttachDepthCore` / `DetachDepthCore` /
  `HasDepthCore`, the `BlendCore` branch in `ApplyAlphaCutout`, the `depthCore=` census field,
  `ApplyAnimatedPlacement` forwarding.
- `app/scripts/RenderConfig.cs` (v0.22.24) — `FoliageAlpha.BlendCore` (default),
  `FoliageCoreAlpha`.
- `app/scripts/Diagnostics.cs`, `tools/run-client.ps1` (v0.22.24) — `blendcore`,
  `--foliage-core-alpha=N` / `-FoliageCoreAlpha`.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.22.25-alpha`.
- `app/materials/prim/prim_hidden.gdshader` (v0.22.26) — **new.** Draws nothing; worn by a shared
  mesh surface whose geometry moved to a child instance.
- `app/scripts/ObjectRenderer.cs` (v0.22.26) — `SplitSortedSurfaces`, `UnsplitSurfaces`,
  `SurfaceMaterial`, `_splitMeshes`; census split + `split=` field; `VisualState.SplitChildren`;
  `uuid=` on `[FaceParams]`.
- `app/scripts/RenderConfig.cs` (v0.22.26) — `SplitSortedSurfaces`; `Diagnostics.cs` /
  `tools/run-client.ps1` — `--alpha-split=off` / `-AlphaSplit off`.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.22.26-alpha`.

### Unchanged on purpose

`RenderConfig.HighFrequencyFoliageAlpha` stays `Blend` and all six `--foliage-alpha=` modes stay
wired. The merge does not replace the mode choice — it makes `Blend` (the only mode whose look
was accepted in-world) survivable, exactly as BUG-RENDER-12 kept `Kind.Blend` for hair. The
harness is also what the A/B needs.

## Sub-tasks / Progress

- [x] Diagnose (both limits), build the 6-mode A/B harness, run it in-world.
- [x] Interim: ship `Blend` default + FXAA; document the dead ends.
- [x] Settle the shared-material spike against Godot 4.7 source instead of running it.
- [x] Real fix: same-material foliage face → single surface merge, with a diagnostic that
      distinguishes intra-object from inter-object reordering.
- [x] Census the transparent set instead of inferring it: 937 of 1310 objects are single-surface,
      so the merge structurally cannot reach 71 % of the problem.
- [x] Port the viewer's alpha-sort hysteresis per object, opt-in behind
      `--alpha-sort-hysteresis`.
- [x] A/B the hysteresis in-world: it works as designed (`refreezes` falls to 0-3 per 15 s once
      the scene is loaded, i.e. the order really is frozen) and the flicker persisted — which is
      what ruled the between-object sort ORDER out as the cause.
- [x] Establish the symptom's timing in-world: steady when still and when turning, flickers when
      the avatar moves. That signature is what identified the sort METRIC as the gap.
- [x] Port the viewer's planar (view-axis) alpha depth, opt-in behind `--alpha-sort-planar`.
- [x] A/B `-AlphaSortPlanar` and `-AlphaSortFreeze` in-world: the flicker survived a completely
      frozen order, which re-framed the problem — the order is not re-decided, it is wrong, and
      parallax while walking is what makes a wrong order visible.
- [x] Rule out the parallax-shaped alternatives against the Godot 4.7 source (SSAO in the blend
      pass, DoF, shadow flags, the 0.99 prepass threshold) instead of running them.
- [x] Port the viewer's alpha depth pass (`lldrawpoolalpha.cpp:212-227`, threshold 0.33) as a
      `next_pass` at RenderPriority -2; ship as `blendcore`, default, `blend` as the control.
- [x] First in-world run of `blendcore` on Millenium (2026-09-10): "sieht schon gut aus". The log
      proves the pass was live — `[AlphaSort] … depthCore=858@0.33 foliage=BlendCore` over 2187
      sorted surfaces, 232 `+DepthCore` face verdicts. If a static overlap halo shows up, tune
      `-FoliageCoreAlpha` (higher = softer); if anything still flickers with `depthCore` > 0, the
      remaining pairs are fringe-over-fringe and the lever is a lower threshold, not the sort.
- [x] Second in-world run: a pine canopy hazed under `blendcore`. Reproduced offline with the real
      needle texture (`scratch/probes/probe_motion.gd`), mechanism identified (minified thin
      needles are ~0.5 alpha and occlude as if solid). Default reverted to `Blend`; `blendcore`
      stays opt-in. Bias in the depth pass kept (fixes the hole rendering of the first run).
- [ ] Separate task: alpha-mask verdict is made on the first (64 px) decode and never re-run on
      sharpen; 18 of 229 textures on Millenium wear a wrong verdict.
- [x] The reported grass object (Firestorm UUID) traced through the click diagnostic: 23 parts,
      each with TWO blended faces at one instance depth — an intra-instance tie no per-instance
      lever can reach. Ported the viewer's per-face granularity as per-surface child instances
      (`--alpha-split`, default on). Click diagnostic now prints the object UUID.
- [x] A/B'd in-world 2026-09-10: with the split the reported grass is calm, with
      `-AlphaSplit off` it flickers again. `[AlphaSort] … multiSurface=0 split=487`.
- [x] Next report, a bush (mesh `2377c336`: faces 616249d1 Blend, 72ccfcce Scissor, 1a6c10a1
      Blend, one part): both leaf surfaces span the same volume, so their one-surface copies have
      the SAME AABB centre and tie again at the child level. v0.22.27: each child gets
      `SortingOffset = (surface + 1) mm` -- lower surface index draws first, the authored order
      the viewer's std::sort keeps for equal face distances between rebuilds -- so siblings never
      tie. The click block now prints `[Blend*,Scissor,Blend*] split=2` per part.
- [x] The bush re-tested: still flickering, and the click block named the real object
      (`c25bf00f`): a THREE-part linkset of sculpts at one position/rotation/scale, two of them
      `[Blend]` single-surface parts (`57fbf391`, `224b2535`) plus an opaque trunk. Two separate
      INSTANCES with identical AABB centres — the inter-object tie. v0.22.28: every
      sorted-transparent object gets a deterministic `SortingOffset` tie-break of
      `(LocalId % 16) cm` (folded into the hysteresis/freeze writes and into the split children's
      millimetre steps), so no two co-located objects ever tie; a co-located pair draws in a
      fixed order, which is what the viewer's once-per-rebuild std::sort gives too.
- [x] Bush re-tested at v0.22.28: calm. User confirms the whole scene ("passt jetzt alles").
- [ ] If a flicker remains with `split>0` and `multiSurface=0`: `shadows=false` and a
      sharpen-freeze next, since `scissor` flickers subtly on the pine with no sorting involved.
- [ ] Re-verify in-world; then this can go `✅ Done`.
