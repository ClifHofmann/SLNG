using System;

namespace SLNG.Core;

/// <summary>
/// The rules for how big the interface is (FEAT-UI-42), kept free of Godot so they can be tested.
/// <para>Two inputs decide it: what the user chose (a number, or "automatic") and what the
/// operating system says the display's scale is. Automatic follows the display; a chosen number
/// wins and is the same on every screen.</para>
/// </summary>
/// <remarks>
/// <b>Why detection takes a DPI as well as a scale.</b> Godot's <c>DisplayServer.ScreenGetScale</c>
/// is only implemented on macOS, Wayland, Android and iOS -- on Windows it answers 1.0 at every
/// Windows scale setting (measured on a 150 % laptop panel: scale 1.0, DPI 144). Windows reports its
/// scale through the monitor DPI (96 = 100 %), so on that platform the DPI is the signal.
/// </remarks>
public static class UiScalePolicy
{
    /// <summary>Smallest scale offered. Below this, body text is no longer legible.</summary>
    public const float Min = 0.8f;

    /// <summary>Largest scale offered. Windows itself goes to 500 %; 400 % leaves room for the
    /// common 300 and 350 % laptop settings, so an OS scale is not silently clamped to something
    /// smaller than the display asked for.</summary>
    public const float Max = 4.0f;

    /// <summary>The scale of a display the operating system gives no information about.</summary>
    public const float Default = 1.0f;

    /// <summary>The DPI Windows treats as 100 %.</summary>
    public const float ReferenceDpi = 96f;

    /// <summary>Scale is kept to this granularity (the slider's step), so a DPI of 105.6 does not
    /// become 1.1000001 and "125 %" is exactly 1.25.</summary>
    public const float Step = 0.05f;

    /// <summary>A scale inside [<see cref="Min"/>, <see cref="Max"/>]; a value that is not a usable
    /// number (NaN, infinity, zero, negative -- what a half-written config or an unknown platform
    /// can produce) falls back to <see cref="Default"/> rather than being clamped to an extreme.</summary>
    public static float Clamp(float scale)
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) return Default;
        return Math.Clamp(scale, Min, Max);
    }

    /// <summary>The display's scale as the operating system sees it.</summary>
    /// <param name="reportedScale">What the engine's screen-scale call answered (1.0 where the
    /// platform does not implement it).</param>
    /// <param name="dpi">The screen's DPI, or 0 / negative when unknown.</param>
    /// <param name="dpiIsTrustworthy">True where the DPI is the OS scale setting (Windows), false
    /// where it is the panel's physical density and says nothing about how the user set up the
    /// desktop (X11).</param>
    public static float DetectOsScale(float reportedScale, int dpi, bool dpiIsTrustworthy)
    {
        // A platform that implements the call answers something above 1 on a scaled display.
        if (!float.IsNaN(reportedScale) && !float.IsInfinity(reportedScale) && reportedScale > 1.01f)
            return Quantize(reportedScale);

        if (dpiIsTrustworthy && dpi > 0)
            return Quantize(dpi / ReferenceDpi);

        return Default;
    }

    /// <summary>Whether the scale follows the display. A saved choice made before the "automatic"
    /// state existed (a <c>ui_scale</c> with no flag next to it) is a manual one: whoever moved the
    /// slider keeps what they set. With neither flag nor value -- a fresh install -- it is automatic.</summary>
    /// <param name="savedAutomatic">The stored flag, or null when there is none.</param>
    /// <param name="hasSavedScale">Whether a manual scale was ever stored.</param>
    public static bool IsAutomatic(bool? savedAutomatic, bool hasSavedScale) =>
        savedAutomatic ?? !hasSavedScale;

    /// <summary>The scale the interface is drawn at.</summary>
    public static float Resolve(bool automatic, float manualScale, float osScale) =>
        Clamp(automatic ? osScale : manualScale);

    private static float Quantize(float scale) => Clamp(MathF.Round(scale / Step) * Step);
}
