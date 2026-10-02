using System;
using System.Globalization;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// State of the developer "Material lab" window: live render knobs for A/B-ing the legacy
/// material path against the reference viewer. A dev tool -- nothing here is saved, every value
/// starts at today's behaviour on every launch, and with the window never opened nothing changes.
/// </summary>
public static class MaterialLab
{
    /// <summary>Multiplier on the SPECULAR a face with a specular map gives Godot (see
    /// <see cref="LegacyShadeMirror.LegacySpecularScaleUniform"/>). 1 = unchanged.</summary>
    public static float LegacySpecularScale { get; private set; } = LegacyShadeMirror.DefaultLegacySpecularScale;

    /// <summary>True once any knob is off its default; the diag lines only mention the lab then.</summary>
    public static bool Modified => Math.Abs(LegacySpecularScale - LegacyShadeMirror.DefaultLegacySpecularScale) > 1e-4f;

    public static void SetLegacySpecularScale(float value)
    {
        float v = Math.Clamp(value, 0f, LegacyShadeMirror.MaxLegacySpecularScale);
        LegacySpecularScale = v;
        RenderingServer.GlobalShaderParameterSet(LegacyShadeMirror.LegacySpecularScaleUniform, v);
        if (Diagnostics.Enabled) Console.Error.WriteLine($"[MaterialLab] {Describe()}");
    }

    public static void Reset() => SetLegacySpecularScale(LegacyShadeMirror.DefaultLegacySpecularScale);

    /// <summary>One-line state for the <c>[LightBalance]</c> and <c>[LiveMaterial]</c> diag lines.</summary>
    public static string Describe() =>
        "legacySpecularScale=" + LegacySpecularScale.ToString("0.##", CultureInfo.InvariantCulture)
        + (Modified ? " (CHANGED from default 1)" : " (default)");
}
