using System;
using System.Globalization;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// State of the developer "Material lab" window: live render knobs for A/B-ing the legacy
/// material path against the reference viewer. A dev tool -- nothing here is saved; every launch
/// starts at the defaults (reflection scale 1, viewer sun highlight on), which are the shipped rendering.
/// </summary>
public static class MaterialLab
{
    /// <summary>Multiplier on the environment reflection of a face with a specular map (see
    /// <see cref="LegacyShadeMirror.LegacySpecularScaleUniform"/>): the viewer's gloss reflection on a
    /// viewer-specular twin, Godot's own F0 on the stock Opaque. 1 = viewer-faithful / unchanged.</summary>
    public static float LegacySpecularScale { get; private set; } = LegacyShadeMirror.DefaultLegacySpecularScale;

    /// <summary>Default ON: faces with a specular map on a world-prim variant (opaque, alpha-mask,
    /// alpha-blend) take the reference viewer's own sun highlight and gloss reflection (the *_vspec
    /// twins) instead of Godot's GGX lobe.</summary>
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
