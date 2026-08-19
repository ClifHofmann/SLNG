using System.Numerics;

namespace SLNG.Core;

/// <summary>One water state — an EEP water frame, a legacy Windlight water setting, or the
/// interpolation of two day-cycle keyframes (FEAT-ENV-01).
///
/// Defaults are the viewer's own, from <c>LLSettingsWater::defaults()</c>
/// (llsettingswater.cpp). <see cref="NormalScale"/>'s default varies with day-cycle position in
/// the viewer (it offsets by <c>position * 0.5 - 0.25</c>); the value here is the mid-track case,
/// which is what a region with no water settings should look like.</summary>
public record WaterSettings
{
    /// <summary>Underwater fog colour. Viewer default (0.0156, 0.1490, 0.2509).</summary>
    public Vector3 FogColor { get; init; } = new(0.0156f, 0.1490f, 0.2509f);

    /// <summary>Underwater fog density exponent. Viewer default 2.0.</summary>
    public float FogDensity { get; init; } = 2.0f;

    /// <summary>Fog modifier applied while the camera is underwater. Viewer default 0.25.</summary>
    public float UnderwaterFogMod { get; init; } = 0.25f;

    /// <summary>Fresnel offset — how reflective the surface is head-on. Viewer default 0.5.</summary>
    public float FresnelOffset { get; init; } = 0.5f;

    /// <summary>Fresnel scale — how reflectivity grows toward grazing angles. Viewer default
    /// 0.3999.</summary>
    public float FresnelScale { get; init; } = 0.3999f;

    /// <summary>Reflection blur. Viewer default 0.04.</summary>
    public float BlurMultiplier { get; init; } = 0.04f;

    /// <summary>Normal-map tiling. Viewer default 2.0 on each axis at mid-track.</summary>
    public Vector3 NormalScale { get; init; } = new(2.0f, 2.0f, 2.0f);

    /// <summary>Refraction scale above the surface. Viewer default 0.0299.</summary>
    public float ScaleAbove { get; init; } = 0.0299f;

    /// <summary>Refraction scale below the surface. Viewer default 0.2.</summary>
    public float ScaleBelow { get; init; } = 0.2f;

    /// <summary>First wave direction. Viewer default (1.04999, -0.42).</summary>
    public Vector2 Wave1Direction { get; init; } = new(1.04999f, -0.42f);

    /// <summary>Second wave direction. Viewer default (1.10999, -1.16).</summary>
    public Vector2 Wave2Direction { get; init; } = new(1.10999f, -1.16f);

    /// <summary>Water normal-map asset, or <see cref="Guid.Empty"/> for the viewer default.</summary>
    public Guid NormalMapId { get; init; }

    /// <summary>The viewer's own default water.</summary>
    public static WaterSettings Default { get; } = new();

    /// <summary>Blends two day-cycle keyframes. <see cref="NormalMapId"/> does not interpolate —
    /// an asset id has no midpoint, so the nearer keyframe's map wins.</summary>
    public static WaterSettings Lerp(WaterSettings a, WaterSettings b, float t) => new()
    {
        FogColor = Vector3.Lerp(a.FogColor, b.FogColor, t),
        FogDensity = float.Lerp(a.FogDensity, b.FogDensity, t),
        UnderwaterFogMod = float.Lerp(a.UnderwaterFogMod, b.UnderwaterFogMod, t),
        FresnelOffset = float.Lerp(a.FresnelOffset, b.FresnelOffset, t),
        FresnelScale = float.Lerp(a.FresnelScale, b.FresnelScale, t),
        BlurMultiplier = float.Lerp(a.BlurMultiplier, b.BlurMultiplier, t),
        NormalScale = Vector3.Lerp(a.NormalScale, b.NormalScale, t),
        ScaleAbove = float.Lerp(a.ScaleAbove, b.ScaleAbove, t),
        ScaleBelow = float.Lerp(a.ScaleBelow, b.ScaleBelow, t),
        Wave1Direction = Vector2.Lerp(a.Wave1Direction, b.Wave1Direction, t),
        Wave2Direction = Vector2.Lerp(a.Wave2Direction, b.Wave2Direction, t),
        NormalMapId = t < 0.5f ? a.NormalMapId : b.NormalMapId,
    };
}
