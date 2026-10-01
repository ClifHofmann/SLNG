using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>The radar's zoom: how many metres the shorter side of the map shows (FEAT-UI-39).</summary>
public static class RadarZoom
{
    public const float MinMetres = 16f;
    public const float MaxMetres = 512f;
    public const float DefaultMetres = 64f;

    /// <summary>The menu presets, very close to far, in metres across the shorter side of the map.</summary>
    public static IReadOnlyList<float> PresetsMetres { get; } = new[] { 32f, 64f, 128f, 256f };

    /// <summary>The range kept within <see cref="MinMetres"/> to <see cref="MaxMetres"/>. A value that is
    /// not a finite number (a hand-edited preferences file) becomes the default rather than wedging the
    /// map at an extreme.</summary>
    public static float Clamp(float metres)
        => float.IsFinite(metres) ? Math.Clamp(metres, MinMetres, MaxMetres) : DefaultMetres;

    /// <summary>The index of the preset closest to <paramref name="metres"/>, which is what the zoom
    /// menu ticks. Compared as a ratio, because zooming is multiplicative (the wheel scales by a
    /// factor): the halfway point between 64 and 128 is 90, not 96. No usable value gives the
    /// default's preset.</summary>
    public static int NearestPresetIndex(float metres)
    {
        if (!(metres > 0f) || !float.IsFinite(metres)) metres = DefaultMetres;

        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < PresetsMetres.Count; i++)
        {
            float distance = MathF.Abs(MathF.Log(metres / PresetsMetres[i]));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }

        return best;
    }
}
