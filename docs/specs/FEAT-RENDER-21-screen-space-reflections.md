# [FEAT-RENDER-21] Screen-space reflections for shiny surfaces

- **Feature ID:** `FEAT-RENDER-21`
- **Track:** `render`
- **Status:** `✅ Done (code) — not yet confirmed in-world`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Split out of `FEAT-RENDER-20` (the real `ReflectionProbe`). With the probe working and sharpened,
the user asked the question that exposes its one structural limit:

> *"sollte ich mich vorn auf der kugel spiegeln oder? ich sehe mich aber eher an der seite"*

Correct on both counts. A `ReflectionProbe` captures the world from ONE point, and with
`BoxProjection = false` Godot treats that cubemap as infinitely distant. That is right for sky and
horizon and wrong for anything a few metres away — your own avatar included, which lands in the
wrong direction on the sphere. Confirmed in an isolated render: a bright box placed in FRONT-LEFT
of a shiny sphere appears as a thin sliver at the sphere's far-left LIMB, not where it actually is.

Screen-space reflections have the opposite trade: they reflect the frame that was actually
rendered, so their parallax is correct by construction, but they only know about what is on
screen.

## This is parity, not an extra

The user also guessed the reason Firestorm does better here might be PBR. It is not — it is SSR,
and the reference viewer applies it to ORDINARY legacy Shiny faces:

`sampleReflectionProbesLegacy` (`reflectionProbeF.glsl:867-885`):

```glsl
#if defined(SSR)
    if (cube_snapshot != 1)
    {
        vec4 ssr = vec4(0);
        ...
        tapScreenSpaceReflection(1, tc, pos, norm, ssr, sceneMap, glossiness);
        glossenv = mix(glossenv, ssr.rgb, ssr.a);
        legacyenv = mix(legacyenv, ssr.rgb, ssr.a);
    }
#endif
```

Note there is **no glossiness gate** in this path — SSR is mixed OVER the probe sample for any
Shiny face. The `glossiness >= 0.9` threshold that does exist (`:753`) guards the PBR path only.
So an SL "Shiny High" prim gets SSR in Firestorm today and got none here. (An earlier reading of
this file in-session concluded the opposite from the `:753` gate alone; reading the legacy function
itself corrected it.)

## What was built

`Boot.SetupEnvironment` enables Godot's built-in SSR on the world `Environment`:
`SsrEnabled = true`, `SsrMaxSteps = 32`, `SsrFadeIn = 0.15`, `SsrFadeOut = 2.0`.

Deliberately modest step count: SSR only ever reflects what is already on screen, so it fades at
the frame edge and behind occluders regardless; a longer trace costs real per-pixel time for rays
that mostly terminate early in an outdoor scene anyway.

Toggleable as `PostFxSsr` (`GraphicsSettings` / `DesignPreferencesPage`, "Screen-Space
Reflections" / "Spiegelungen (Bildschirmraum)"), kept **separate** from `FEAT-RENDER-20`'s
`PostFxReflectionProbe`. That separation is the point: the two cover different halves of the same
job and fail differently — the probe covers everything including off-screen content but misplaces
nearby objects, SSR places correctly but only knows the visible frame. The reference viewer runs
both and mixes one over the other; being able to switch them independently is what makes it
possible to attribute an artefact to one of them.

## Measured cost

900 shiny mesh instances + an active reflection probe, 1280x720, VSync disabled, 60 warmup frames
discarded, 240 measured frames, each configuration run twice to show the delta is not drift:

| configuration | avg frame |
|---|---|
| SSR off | 1.580 ms |
| SSR on | 1.741 ms |
| SSR off (repeat) | 1.601 ms |
| SSR on (repeat) | 1.824 ms |

≈ **+0.19 ms/frame, ~1.12x baseline**. Against the ~6.1 ms frame the live client reports on the
test region, that is a modest, bounded cost — and it is switchable per AGENTS.md's render-budget
non-negotiable if it ever is not.

## Acceptance Criteria

- [x] SSR enabled on the world environment, reaching prim faces through the existing shader path
- [x] Verified to change nearby-object placement, not just add brightness — isolated render with a
      stand-in "avatar" box shows the probe-only reflection putting it at the limb and SSR pulling
      it toward its true direction
- [x] Parity claim checked against the reference viewer's own source rather than assumed, and the
      earlier mis-reading of the `>= 0.9` gate corrected
- [x] Frame-time cost measured, both configurations run twice, numbers recorded above
- [x] Toggleable, separately from the reflection probe, and persisted
- [ ] Confirmed in-world against Firestorm — **not yet done**

## Technical Specs & Affected Files

- `app/scripts/Boot.cs` — `SetupEnvironment` SSR properties
- `app/scripts/UI/GraphicsSettings.cs` — `PostFxSsr`, load/save/apply
- `app/scripts/UI/DesignPreferencesPage.cs` — the checkbox
- `app/i18n/de-DE.json`, `app/i18n/en-US.json` — `ui.preferences.post_fx_ssr`
- Reference: `scratch/slviewer/.../class3/deferred/reflectionProbeF.glsl:867-885` (legacy path,
  ungated) and `:753` (PBR path, `glossiness >= 0.9`)

## Known limits and things to watch in-world

- SSR cannot reflect what is off screen or occluded; expect reflections to fade at frame edges.
  That is inherent to the technique and true of the reference viewer too.
- At a prim's 4% dielectric F0 the SSR contribution is *correctly placed but faint* — SSR fixes
  direction, not strength. The strength question is `FEAT-RENDER-20`'s probe intensity, and the
  curve-shape mismatch documented there still stands.
- **Water is worth a specific look.** SLNG's water shader does its own screen-space refraction and
  writes depth; Godot's SSR may interact with that. Nothing in the isolated tests covered water,
  so check it in-world before assuming it is fine.
