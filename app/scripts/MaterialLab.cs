using System;
using System.Globalization;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// State of the developer "Material lab" window: live render knobs for A/B-ing the legacy
/// material path against the reference viewer. A dev tool -- nothing here is saved; every launch
/// starts at the defaults (veil 0, viewer sun highlight on), which are the shipped rendering.
/// </summary>
public static class MaterialLab
{
    /// <summary>Multiplier on the SPECULAR a face with a specular map gives Godot (see
    /// <see cref="LegacyShadeMirror.LegacySpecularScaleUniform"/>). 1 = unchanged.</summary>
    public static float LegacySpecularScale { get; private set; } = LegacyShadeMirror.DefaultLegacySpecularScale;

    /// <summary>Default ON: faces with a specular map on the Opaque variant take the
    /// reference viewer's own sun highlight (prim_opaque_vspec.gdshader) instead of Godot's GGX lobe.</summary>
    public static bool ViewerSunSpecular { get; private set; } = LegacyShadeMirror.DefaultViewerSunSpecular;

    /// <summary>Raised on the main thread when <see cref="ViewerSunSpecular"/> changes; ObjectRenderer
    /// re-applies the shader choice to every live surface.</summary>
    public static event Action? ViewerSunSpecularChanged;

    /// <summary>True once any knob is off its default; the diag lines only mention the lab then.</summary>
    public static bool Modified =>
        Math.Abs(LegacySpecularScale - LegacyShadeMirror.DefaultLegacySpecularScale) > 1e-4f
        || ViewerSunSpecular != LegacyShadeMirror.DefaultViewerSunSpecular;

    public static void SetLegacySpecularScale(float value)
    {
        float v = Math.Clamp(value, 0f, LegacyShadeMirror.MaxLegacySpecularScale);
        LegacySpecularScale = v;
        RenderingServer.GlobalShaderParameterSet(LegacyShadeMirror.LegacySpecularScaleUniform, v);
        if (Diagnostics.Enabled) Console.Error.WriteLine($"[MaterialLab] {Describe()}");
    }

    public static void SetViewerSunSpecular(bool on)
    {
        if (ViewerSunSpecular == on) return;
        ViewerSunSpecular = on;
        ViewerSunSpecularChanged?.Invoke();
        if (Diagnostics.Enabled) Console.Error.WriteLine($"[MaterialLab] {Describe()}");
    }

    public static void Reset()
    {
        SetLegacySpecularScale(LegacyShadeMirror.DefaultLegacySpecularScale);
        SetViewerSunSpecular(LegacyShadeMirror.DefaultViewerSunSpecular);
    }

    /// <summary>One-line state for the <c>[LightBalance]</c> and <c>[LiveMaterial]</c> diag lines.</summary>
    public static string Describe() =>
        "legacySpecularScale=" + LegacySpecularScale.ToString("0.##", CultureInfo.InvariantCulture)
        + " viewerSunSpecular=" + (ViewerSunSpecular ? "ON" : "off")
        + (Modified ? " (CHANGED from default)" : " (default)");
}
