# [FEAT-PERF-16] Texture animation in the shader

- **Feature ID:** `FEAT-PERF-16`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), plan [MVP6-1](MVP6-1-performance-program.md)
  (ADR 0004 P5: per-frame animation runs on the GPU)

## Overview & Goal

**Measured at Sirens Beach on 2026-10-15 (main, v0.27.55):** the main thread spends about 105 ms per second in
`obj-ticks.texanim`, roughly 4.6 ms per frame at 22 fps. That is about 1,600 animated faces, such as the waves
on the beach.

**How it works today** (`app/scripts/ObjectRenderer.cs`):

- `TickTextureAnimations()` runs every frame.
- For every object in `_texAnims`, it evaluates `SLNG.Core.TextureAnimator.Evaluate`.
- It writes `uv_scale`, `uv_offset` and `uv_rotation` onto each affected face material, through
  `ApplyTextureAnimFrame` and `ApplyAnimatedPlacement`.

That means one to three `SetShaderParameter` calls per face per frame.

**Goal:** the shader computes the animation from a time value. The main thread writes the animation
parameters **once**, when an animation starts, changes or stops, or when a face's material is rebuilt. It does
nothing per frame except set one global time value. The result on screen must be identical: every mode, the
same frames.

**Second goal, for FEAT-PERF-20 A1 (shared materials):** a looping animation uses a common start time (0), so
two faces with the same texture and the same animation get identical uniform values and can share a material
later.

## Rules for this task

- Work in `E:/Git/SLNG`. Create the branch `feature/FEAT-PERF-16-texanim-shader` from `main`. Do not use
  `git worktree`.
- In `docs/ROADMAP.md`, set the FEAT-PERF-16 row to Owner `gemini` and status 🚧, and to 🧪 when done.
- Set `AppVersion` in `app/scripts/Boot.cs` to `v0.27.57-alpha`, or one patch above whatever is there.
- **No `using Godot;` in `src/`.**
- Do not delete `SLNG.Core.TextureAnimator` or its tests. It stays the reference that the new code is tested
  against.
- Do not change what is animated. Today only the **diffuse** placement (`uv_scale` / `uv_offset` /
  `uv_rotation`) is driven, together with the alpha depth pass (`prim_depth_core.gdshader`). The normal-map
  and specular-map placements are not. Keep exactly that.
- Animated objects stay out of MultiMesh groups. That behaviour is unchanged: `_instanceGroups?.Leave` in
  `UpdateTextureAnimRegistration`, and the `_texAnims.ContainsKey` check in the cull sweep.
- Commit: `feat(render): [FEAT-PERF-16] texture animation evaluated in the prim shaders (v0.27.57-alpha)`. Do
  not merge, push or release.

## Technical Specs & Affected Files

### Step 1: pure packing and a C# mirror of the shader formula (tests first)

**New file `src/SLNG.Core/TextureAnimShader.cs`:**

```csharp
using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>FEAT-PERF-16: what the prim shaders need to run an llSetTextureAnim themselves, and a C#
/// MIRROR of the shader's formula (slng_tex_anim_* in app/materials/prim/prim_common.gdshaderinc) so tests
/// can hold it against <see cref="TextureAnimator.Evaluate"/>. Change both together.</summary>
public static class TextureAnimShader
{
    /// <summary>The two vec4 uniforms of an animated face.
    /// a = (mode bits, SizeX, SizeY, Start); b = (Length, Rate, start time, 1 = animated).
    /// A LOOP animation starts at time 0 (a shared epoch): its phase is not observable, and equal
    /// animations then pack to equal values, so faces can share a material (FEAT-PERF-20 A1). A
    /// non-looping one plays once from when it arrived, so it keeps its own start time.</summary>
    public static (Vector4 A, Vector4 B) Pack(in TextureAnimation anim, float nowSeconds)
    {
        float start = (anim.Mode & TextureAnimFlags.Loop) != 0 ? 0f : nowSeconds;
        return (new Vector4((byte)anim.Mode, anim.SizeX, anim.SizeY, anim.Start),
                new Vector4(anim.Length, anim.Rate, start, 1f));
    }

    /// <summary>The uniforms of a face that is NOT animated.</summary>
    public static readonly Vector4 Off = Vector4.Zero;

    /// <summary>C#'s float % (truncated, keeps the sign of the dividend). GLSL's mod() is floored, so the
    /// shader must use this, not mod().</summary>
    public static float Fmod(float a, float b) => a - b * MathF.Truncate(a / b);

    /// <summary>MathF.Round(x, MidpointRounding.AwayFromZero).</summary>
    public static float RoundAway(float x) => MathF.Sign(x) * MathF.Floor(MathF.Abs(x) + 0.5f);

    /// <summary>Line-for-line what the shader does. Returns the same frame TextureAnimator.Evaluate does,
    /// computed only from the packed uniforms and the time.</summary>
    public static TextureAnimFrame Evaluate(Vector4 a, Vector4 b, float timeSeconds)
    {
        // Implement exactly as the GLSL in Step 2 (same order, same helpers: Fmod, RoundAway,
        // MathF.Floor, MathF.Truncate). Return TextureAnimFrame.Idle when b.W < 0.5.
        throw new NotImplementedException();
    }
}
```

Write `Evaluate` so it mirrors the GLSL in Step 2 one operation at a time.

**New test file `tests/SLNG.Core.Tests/TextureAnimShaderTests.cs`:**

1. **Parity.** For every combination of the bits Loop, Reverse, PingPong, Smooth, Rotate and Scale (with On
   always set; 64 combinations), and a few grids and values:
   - SizeX/SizeY: (1,1), (4,4), (8,2), and (0,0) when Smooth is set;
   - Length: 0 and 5;
   - Start: 0 and 2;
   - Rate: 2.5, -3 and 0.5;
   - times t = 0, 0.013, 0.5, 1, 3.7, 10, 59.9 and 600 seconds.

   For each one, pack with `nowSeconds = 100` and evaluate the mirror at `timeSeconds = startTime + t`, where
   startTime is 0 for Loop and 100 otherwise. Compare with `TextureAnimator.Evaluate(anim, t)`:
   - `Driven` must be equal;
   - OffsetS, OffsetT, ScaleS, ScaleT and Rotation must be within 1e-3.

   For Loop cases, compare `TextureAnimator.Evaluate(anim, 100 + t)` with the mirror at time `100 + t`,
   because the epoch is 0.
2. **Pack.** Loop gives start time 0; non-loop gives `nowSeconds`; `B.W == 1`.
3. **Off.** `B.W == 0` returns `TextureAnimFrame.Idle`.
4. **Shader text.** Read `app/materials/prim/prim_common.gdshaderinc` (find the repo root the way
   `LegacyShadeMirrorTests.RepoRoot()` does). Assert that it contains `uniform vec4 tex_anim_a`,
   `uniform vec4 tex_anim_b`, `global uniform float slng_anim_time;` and `float slng_fmod(`.

### Step 2: the shader

In `app/materials/prim/prim_common.gdshaderinc`, add the following next to `uniform float uv_rotation`:

```glsl
// FEAT-PERF-16: llSetTextureAnim, evaluated here instead of by ObjectRenderer every frame.
// a = (mode bits, SizeX, SizeY, Start), b = (Length, Rate, start time, 1 = animated).
// Packed by SLNG.Core.TextureAnimShader.Pack; mirrored by TextureAnimShader.Evaluate (tests).
uniform vec4 tex_anim_a = vec4(0.0);
uniform vec4 tex_anim_b = vec4(0.0);
// Seconds since the client started; ObjectRenderer sets it once per frame.
global uniform float slng_anim_time;
```

Add these functions above `slng_transform_uv`. They are a port of `TextureAnimator.Evaluate`; keep the order:

```glsl
// C#'s float % (truncated). GLSL mod() is floored and gives a different result for negative rates.
float slng_fmod(float a, float b) { return a - b * trunc(a / b); }
// MathF.Round(x, MidpointRounding.AwayFromZero)
float slng_round_away(float x) { return sign(x) * floor(abs(x) + 0.5); }

// Replaces the driven parts of a face's placement. repeat/offset are SL's raw values
// (offset NOT packed with the centring term); rotation in radians.
void slng_tex_anim(inout vec2 repeat_st, inout vec2 offset_st, inout float rotation) {
    int mode = int(tex_anim_a.x + 0.5);
    bool is_loop = (mode & 2) != 0;
    bool is_reverse = (mode & 4) != 0;
    bool is_ping = (mode & 8) != 0;
    bool is_smooth = (mode & 16) != 0;
    float size_x = tex_anim_a.y;
    float size_y = tex_anim_a.z;
    float start = tex_anim_a.w;
    float len = tex_anim_b.x;
    float rate = tex_anim_b.y;
    float elapsed = slng_anim_time - tex_anim_b.z;

    float num_frames = len != 0.0 ? len : max(1.0, size_x * size_y);
    float full_length = num_frames;
    if (is_ping) {
        if (is_smooth) full_length = 2.0 * num_frames;
        else if (is_loop) full_length = max(1.0, 2.0 * num_frames - 2.0);
        else full_length = max(1.0, 2.0 * num_frames - 1.0);
    }

    float fc = elapsed * rate;
    if (is_loop) fc = slng_fmod(fc, full_length);
    else fc = min(full_length - 1.0, fc);

    if (!is_smooth) {
        fc = floor(fc + 0.01);
        fc = min(full_length - 1.0, fc);
    }
    if (is_ping && fc >= num_frames) {
        fc = is_smooth ? num_frames - (fc - num_frames) : (num_frames - 1.99) - (fc - num_frames);
    }
    if (is_reverse) {
        fc = is_smooth ? num_frames - fc : (num_frames - 0.99) - fc;
    }
    fc += start;
    if (!is_smooth) fc = slng_round_away(fc);

    if ((mode & 32) != 0) { rotation = fc; return; }                  // ROTATE
    if ((mode & 64) != 0) { repeat_st = vec2(fc); return; }           // SCALE
    if (size_x != 0.0 && size_y != 0.0) {                              // frame grid
        float ss = 1.0 / size_x;
        float st = 1.0 / size_y;
        float x_frame = slng_fmod(fc, size_x);
        float y_frame = trunc(fc / size_x);
        repeat_st = vec2(ss, st);
        offset_st = vec2((-0.5 + 0.5 * ss) + x_frame * ss, (0.5 - 0.5 * st) - y_frame * st);
        return;
    }
    offset_st = vec2(fc, 0.0);                                         // plain slide
}
```

Change `slng_transform_uv` so that its last line runs the animation first:

```glsl
    // FEAT-PERF-16: an animated face. uv_offset is packed as (0.5 - 0.5*repeatU + offsetU,
    // 0.5 - 0.5*repeatV - offsetV) (ObjectRenderer.ApplyAnimatedPlacement / BuildFaceMaterialAsync);
    // unpack, let the animation replace what it drives, pack again with the FINAL repeat.
    if (tex_anim_b.w > 0.5) {
        vec2 repeat_st = uv_scale;
        vec2 offset_st = vec2(uv_offset.x - 0.5 + 0.5 * uv_scale.x, 0.5 - 0.5 * uv_scale.y - uv_offset.y);
        float rot = uv_rotation;
        slng_tex_anim(repeat_st, offset_st, rot);
        vec2 packed = vec2(0.5 - 0.5 * repeat_st.x + offset_st.x, 0.5 - 0.5 * repeat_st.y - offset_st.y);
        return slng_place_uv(uv, repeat_st, packed, rot);
    }
    return slng_place_uv(uv, uv_scale, uv_offset, uv_rotation);
```

**Check:** `prim_depth_core.gdshader` includes `prim_common.gdshaderinc` and calls `slng_transform_uv`, so it is
covered automatically. Grep every `.gdshader` under `app/materials/prim/` that calls `slng_transform_uv` and
confirm it includes the common file. List them in your report.

**Register the global:** in `app/project.godot`, section `[shader_globals]`, add an entry like the existing
`slng_legacy_specular_scale` one:

```
slng_anim_time={
"type": "float",
"value": 0.0
}
```

Then run `python tools/check_shader_globals.py`. It must print `consistent`.

### Step 3: C#, set the values once instead of every frame

In `app/scripts/PrimShaderFamily.cs`, next to `UvRotation`:

```csharp
/// <summary>FEAT-PERF-16: the packed llSetTextureAnim of a face (see SLNG.Core.TextureAnimShader).</summary>
public static readonly StringName TexAnimA = "tex_anim_a";
public static readonly StringName TexAnimB = "tex_anim_b";
```

In `app/scripts/ObjectRenderer.cs`:

1. **`TexAnimState`.** Add `public Godot.Vector4 A, B;` (the packed uniforms). The `LastFrame` field is no
   longer needed; remove it and every use.
2. **`UpdateTextureAnimRegistration`.** Whenever an animation is created or changes:
   - compute `var (a, b) = SLNG.Core.TextureAnimShader.Pack(running, AnimNowSeconds());`;
   - store them, converted to `Godot.Vector4`;
   - set `TexAnimNeedsReapply = true` on the visual, if it exists in `_visuals`.

   When the animation stops (the branch that does `_texAnims.Remove(entityId)`), call
   `ApplyTexAnimUniforms(state, prim, null)` first, if the visual and its `PrimitiveComponent` exist, so the
   faces go back to static.

   Add:

   ```csharp
   /// <summary>FEAT-PERF-16: the clock the shaders animate by (global uniform slng_anim_time), in seconds since
   /// the client started. Float on the GPU: precise to well under a frame for many hours.</summary>
   private static float AnimNowSeconds() => Godot.Time.GetTicksMsec() / 1000f;
   ```

3. **Replace `ApplyTextureAnimFrame` and `ApplyAnimatedPlacement`** with one method:

   ```csharp
   /// <summary>FEAT-PERF-16: writes the packed animation (or "off") onto the face materials of one object.
   /// Faces the animation does not cover get "off". Runs when an animation starts, changes or stops, and
   /// after a material rebuild -- never per frame.</summary>
   private void ApplyTexAnimUniforms(VisualState state, PrimitiveComponent prim, TexAnimState? anim)
   ```

   Walk the surfaces exactly as `ApplyTextureAnimFrame` does today: `_meshFaceIndices` with the
   `MaterialOverride` fallback, and the `animFace >= 0 && faceIdx != animFace` test. For each material:
   - if the face is covered: set `TexAnimA` to `anim.A` and `TexAnimB` to `anim.B`;
   - otherwise: set `TexAnimB` to `Godot.Vector4.Zero`.

   Do the same on `mat.NextPass` when it is the DepthCore material (copy the `ReferenceEquals(core.Shader,
   PrimShaderFamily.DepthCore)` test from `ApplyAnimatedPlacement`).

   Do **not** write `uv_scale`, `uv_offset` or `uv_rotation` any more. The material keeps the face's static
   placement, and the shader derives the animated one from it.
4. **`TickTextureAnimations`** becomes:
   - set the clock once:
     `RenderingServer.GlobalShaderParameterSet("slng_anim_time", AnimNowSeconds());` (put this first, even when
     `_texAnims.Count == 0`, so a later animation starts from a current clock);
   - then loop over `_texAnims`, and for each entry handle only visuals with `state.TexAnimNeedsReapply ==
     true`: skip `ResourcesReleased` (keep the flag), look up the `PrimitiveComponent`, call
     `ApplyTexAnimUniforms(state, prim, anim)`, set the flag to false.

   Keep the `MainThreadPhase.Enter("obj-ticks.texanim")` label where it is, so `[PhaseCost]` shows the drop.

   Do not call `TextureAnimator.Evaluate` here any more.
5. Leave the places that set `TexAnimNeedsReapply = true` after a material rebuild unchanged. They are what
   triggers the one-time re-write.

### Step 4: selftest

Add `CheckTextureAnimInShader(SceneTree tree)` (new file `app/scripts/ObjectRenderer.TexAnim.SelfTest.cs`,
`partial class ObjectRenderer`, registered in `app/scripts/SelfTest.cs`). Build a test visual the way the
existing ObjectRenderer selftests do, with two surfaces / faces. Then:

1. Register a looping 4x4 flipbook on face 1 only.
2. Run one `TickTextureAnimations()`. Check:
   - face 1's material has `tex_anim_b.w == 1` and `tex_anim_b.z == 0` (Loop epoch);
   - face 0's material has `tex_anim_b == (0,0,0,0)`;
   - `uv_scale`, `uv_offset` and `uv_rotation` on both are the static values they had before;
   - `TexAnimNeedsReapply == false`.
3. Change `tex_anim_a` on face 1's material by hand to a sentinel, then run 5 more ticks. The sentinel is still
   there. This proves nothing writes per frame.
4. Set `TexAnimNeedsReapply = true`, tick once, and check the sentinel is replaced by the packed value.
5. Stop the animation. Face 1 has `tex_anim_b == (0,0,0,0)`.
6. Read back `RenderingServer.GlobalShaderParameterGet("slng_anim_time")` and check it is > 0. If that call is
   a synchronous round trip that is not allowed in the selftest's render-thread mode, skip this item and say
   so.

## Acceptance Criteria

- [ ] `TextureAnimShaderTests` pass, including the 64-combination parity test. Existing `TextureAnimatorTests`
      pass unchanged.
- [ ] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test`,
      `dotnet format SLNG.sln --verify-no-changes`, and `python tools/check_shader_globals.py` are all clean.
- [ ] `godot --headless --path app --log-file user://logs/selftest/godot.log -- --selftest` prints `PASS`, with the
      new check `ok`. Afterwards run `git status` and undo any change the headless run made to `app/project.godot`
      **except** your new `slng_anim_time` entry.
- [ ] Report:
  - the list of shaders covered (Step 2 check);
  - any mode where the mirror differed from `TextureAnimator` and how you resolved it;
  - the verify output.
- [ ] In-world (the user does this), at Sirens Beach:
  - the waves and other animated textures look and move as before;
  - `obj-ticks.texanim` in `[PhaseCost]` drops from ~100 ms/s to near 0.

## Sub-tasks / Progress

- [ ] Step 1 TextureAnimShader + tests
- [ ] Step 2 shader + global
- [ ] Step 3 ObjectRenderer
- [ ] Step 4 selftest
- [ ] Roadmap, version, commit
