using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>BUG-RENDER-48: the reference viewer's water fog for things seen THROUGH the surface from
/// above, and a C# MIRROR of the shader's formula (<c>slng_water_fog_height</c> in
/// app/materials/slng_water_fog.gdshaderinc) so tests can hold it against numbers worked out from the
/// viewer's own source. Change both together.
///
/// <para>Source, all in scratch/slviewer: <c>class1/environment/waterFogF.glsl:40-74</c>
/// (<c>getWaterFogViewNoClip</c> / <c>getWaterFogView</c>), the uniforms from
/// <c>llsettingsvo.cpp:1073-1136</c> and the haze pass <c>class3/deferred/waterHazeF.glsl</c> driven by
/// <c>pipeline.cpp:9032-9114</c>.</para>
///
/// <para>The result is a pair that the caller composites as <c>colour * Transmittance + Radiance</c>:
/// the in-scattered water colour, and how much of the surface's own colour survives the path through
/// the water. Everything is in LINEAR light; <c>colorLinear</c> is the fog colour already converted
/// from the sRGB the setting is stored in (the viewer does <c>srgb_to_linear(kc)</c> in the shader).</para>
///
/// <para>Only the camera-ABOVE-the-surface case is modelled: with the eye under the water the viewer
/// switches to <c>getModifiedWaterFogDensity(underwater)</c> and a different path-length term, and
/// SLNG has no underwater camera look yet (docs/eep-parity-probe-protocol.md, W7). There
/// <see cref="Compute"/> returns <see cref="Fog.None"/>.</para></summary>
public static class WaterFog
{
    /// <summary><c>F</c> in getWaterFogViewNoClip: the per-metre survival base of the density term.</summary>
    public const float FogBase = 0.98f;

    /// <summary><c>LLSettingsVOWater::WATER_FOG_LIGHT_CLAMP</c> (llsettingsvo.cpp:916): the lowest light
    /// elevation the fog's sun term divides by, so a low sun does not blow the scattering up.</summary>
    public const float LightClamp = 0.3f;

    /// <summary>The 1.7 that getWaterFogViewNoClip puts on the radiance (as <c>1/1.7</c>) and that
    /// waterHazeF.glsl puts on the transmittance when it fogs the opaque frame.</summary>
    public const float Gamma = 1.7f;

    /// <summary><c>max(depth, 0.1)</c> in getWaterFogViewNoClip: the thinnest slab of water it fogs.</summary>
    public const float MinThickness = 0.1f;

    /// <summary>The fog of one view ray: <see cref="Radiance"/> is the in-scattered water colour
    /// (linear RGB), <see cref="Transmittance"/> the fraction of the surface colour that survives.</summary>
    public readonly record struct Fog(Vector3 Radiance, float Transmittance)
    {
        /// <summary>The viewer's <c>vec4(0, 0, 0, 1)</c>: nothing added, nothing removed.</summary>
        public static Fog None => new(Vector3.Zero, 1f);
    }

    /// <summary><c>waterFogKS</c> (llsettingsvo.cpp:1123): <c>1 / max(light.z, WATER_FOG_LIGHT_CLAMP)</c>.
    /// <paramref name="lightHeight"/> is the Z of the direction to whichever body lights the scene (the
    /// sun, or the moon when the sun is down) in SL axes, i.e. its sine of elevation.</summary>
    public static float KsFromLightHeight(float lightHeight) => 1f / MathF.Max(lightHeight, LightClamp);

    /// <summary>Fog for a point behind a water surface, seen from above it.</summary>
    /// <param name="eyeHeight">Height of the eye above the water plane. Not above it (zero or less) means
    /// the underwater camera, which is not modelled: <see cref="Fog.None"/>.</param>
    /// <param name="pointHeight">Height of the seen point above the plane. Above it: no fog, exactly the
    /// viewer's <c>getWaterFogView</c> early-out.</param>
    /// <param name="distance">Straight-line distance from the eye to the point.</param>
    /// <param name="density"><c>waterFogDensity</c> (the water setting's fog density; the underwater
    /// modifier does not apply with the camera above water, llsettingswater.cpp:378).</param>
    /// <param name="ks"><see cref="KsFromLightHeight"/>.</param>
    /// <param name="colorLinear">The fog colour, linear.</param>
    public static Fog Compute(float eyeHeight, float pointHeight, float distance,
                              float density, float ks, Vector3 colorLinear)
    {
        // `!(x > 0)` rather than `x <= 0` so a NaN also lands on the neutral answer.
        if (!(eyeHeight > 0f) || pointHeight > 0f || !(distance > 0f)) return Fog.None;

        // es = -dot(view, waterPlane.xyz): how steeply the ray runs down. For a ray from an eye above
        // the plane to a point at or below it this is positive by construction.
        float es = (eyeHeight - pointHeight) / distance;

        // int_v = view * waterPlane.w / es: where the ray crosses the surface. The slab of water the
        // ray then travels through is what is left of the distance, at least MinThickness.
        float depth = distance - eyeHeight / es;
        float l = MathF.Max(depth, MinThickness);

        // e0 = max(-waterPlane.w, 0) = 0 with the eye above the surface, so t1 reduces to -kd.
        float t1 = -density;
        float t2 = density + ks * es;
        // A negative density can land t2 on zero, and 0 * inf is NaN. Keep it a hair off zero (either
        // side) so the product stays finite and the clamp below decides.
        if (MathF.Abs(t2) < 1e-4f) t2 = t2 < 0f ? -1e-4f : 1e-4f;
        float t3 = MathF.Pow(FogBase, t2 * l) - 1f;

        // Clamped on both sides: the viewer only caps it at 1, but a negative fog density (the
        // validator tries to keep it out; llsettingswater.cpp:380-386) would make this NaN, which a
        // GPU turns into black.
        float scatter = Math.Clamp(t1 / t2 * t3, 0f, 1f);
        float radiance = MathF.Pow(scatter, 1f / Gamma);

        float transmittance = MathF.Pow(FogBase, l * density);
        return new Fog(colorLinear * radiance, Math.Clamp(transmittance, 0f, 1f));
    }

    /// <summary>How much of what lies under flat water survives the surface itself: <c>1 - df2.x</c> of
    /// waterF.glsl's calculateFresnelFactors (:162-179, <c>color = mix(fb, radiance, min(1, df2.x))</c>,
    /// :327) with all three wave normals pointing straight up. The viewer's water writes the screen as
    /// <c>fb * (1 - df2.x) + reflection</c>, so anything drawn BEFORE the water -- which for a blended face
    /// below the surface it is, there -- is seen through that weight. SLNG draws such a face after the
    /// water instead (BUG-RENDER-05), so the prim shader applies the weight itself, using the flat-water
    /// value in place of the per-fragment ripple normals (they average out to near that).
    /// <paramref name="viewDotUp"/> is dot(eye-to-fragment direction, world up): negative looking down.</summary>
    public static float FlatSurfaceTransmission(float viewDotUp, float fresnelScale, float fresnelOffset)
    {
        float d = MathF.Max(0f, viewDotUp * fresnelScale + fresnelOffset);
        // df3 = d*d for each of the three wave sets; df2.x = min(1, df3.x + df3.y + df3.z) = min(1, 3 d^2)
        return 1f - MathF.Min(1f, 3f * d * d);
    }

    /// <summary>The weight of the surface's reflection over flat water, <c>df2.x * df2.y</c> of
    /// calculateFresnelFactors: the sheen of reflected sky is <c>radiance * ReflectionWeight</c>, on top of
    /// what <see cref="FlatSurfaceTransmission"/> lets through. df2.y is the same bracket as each df3
    /// component before squaring.</summary>
    public static float FlatSurfaceReflectionWeight(float viewDotUp, float fresnelScale, float fresnelOffset)
    {
        float d = MathF.Max(0f, viewDotUp * fresnelScale + fresnelOffset);
        return MathF.Min(1f, 3f * d * d) * d;
    }

    /// <summary>What a BLENDED face below the surface shows for its own colour: the face fogs itself
    /// (<see cref="ApplyToSurface"/>) and the water surface then weighs the whole result by
    /// <paramref name="transmission"/> (<see cref="FlatSurfaceTransmission"/>), exactly as the viewer's
    /// water does to the frame the face was drawn into.</summary>
    public static Vector3 ApplyToBlendedSurface(Vector3 colour, Fog fog, float transmission) =>
        ApplyToSurface(colour, fog) * transmission;

    /// <summary>What the viewer's haze pass does to an OPAQUE pixel behind the water before the water is
    /// drawn over it: <c>dst * pow(D, 1.7) + radiance</c> (waterHazeF.glsl:70-72 with the
    /// <c>(ONE, SRC_ALPHA)</c> blend of pipeline.cpp:9059). This is the colour the water shader's
    /// refracted sample starts from.</summary>
    public static Vector3 ApplyHaze(Vector3 behind, Fog fog) =>
        behind * MathF.Pow(fog.Transmittance, Gamma) + fog.Radiance;

    /// <summary>What an alpha-blended surface below the water does to its OWN colour before it is
    /// blended over the frame: <c>colour * D + radiance</c> (waterFogF.glsl:90-96, reached through
    /// <c>applySkyAndWaterFog</c>, :115-144). Note the transmittance is NOT raised to 1.7 here, unlike
    /// <see cref="ApplyHaze"/> -- the viewer is inconsistent between its two paths and this follows
    /// each of them.</summary>
    public static Vector3 ApplyToSurface(Vector3 colour, Fog fog) =>
        colour * fog.Transmittance + fog.Radiance;
}
