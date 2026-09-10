using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-RENDER-07: depth-of-field settings, persisted to user://preferences.cfg under a "dof"
/// section -- same ConfigFile file and pattern as <see cref="CameraSettings"/> / GraphicsSettings,
/// so all of them coexist without knowing about each other.
///
/// The values here are a photographer's model (a focal plane plus the depth that stays sharp
/// around it), not Godot's. <see cref="SLNG.App.DepthOfFieldController"/> owns the translation
/// into <c>CameraAttributesPractical</c>'s four near/far distance+transition knobs, because that
/// mapping is a rendering detail and a settings holder should not carry it.
///
/// <see cref="Enabled"/> defaults to OFF on purpose: DoF is a photography tool, it costs a
/// full-screen blur pass, and with auto-focus on it also costs a physics raycast -- none of which
/// ordinary play should pay for. See the controller's raycast comment for why "only while
/// actually enabled" matters more here than it looks.
/// </summary>
public sealed class DofSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "dof";

    public const float MinFocusDistance = 0.5f;
    public const float MaxFocusDistance = 200f;
    public const float DefaultFocusDistance = 8f;

    // The sharp band, centred on the focal plane. The floor is deliberately not 0: a zero-width
    // band means near and far blur meet at exactly one depth, which reads as "everything is
    // blurred" rather than as a shallow focus.
    public const float MinFocusRange = 0.2f;
    public const float MaxFocusRange = 50f;
    public const float DefaultFocusRange = 4f;

    // Godot's dof_blur_amount is a 0..1 intensity. Its own default is 0.1; 0.15 is a visible but
    // still photographic starting point rather than a smeared one.
    public const float MinBlurAmount = 0f;
    public const float MaxBlurAmount = 1f;
    public const float DefaultBlurAmount = 0.15f;

    public bool Enabled { get; private set; }

    /// <summary>Track the focal plane with a raycast through the centre of the viewport, instead
    /// of using <see cref="FocusDistance"/> verbatim.</summary>
    public bool AutoFocus { get; private set; } = true;

    /// <summary>Manual focal-plane distance in metres. Also where auto-focus starts from, and what
    /// it falls back to when the centre ray hits nothing.</summary>
    public float FocusDistance { get; private set; } = DefaultFocusDistance;

    /// <summary>Metres of depth that stay sharp, centred on the focal plane.</summary>
    public float FocusRange { get; private set; } = DefaultFocusRange;

    /// <summary>Blur intensity, 0..1.</summary>
    public float BlurAmount { get; private set; } = DefaultBlurAmount;

    /// <summary>Blur the foreground too (everything nearer than the sharp band). Off gives the
    /// "sharp subject, blurred background only" look; on is the full cinematic one.</summary>
    public bool NearBlur { get; private set; } = true;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        Enabled = (bool)cfg.GetValue(Section, "enabled", false);
        AutoFocus = (bool)cfg.GetValue(Section, "auto_focus", true);
        FocusDistance = ClampFocus((float)cfg.GetValue(Section, "focus_distance", DefaultFocusDistance));
        FocusRange = ClampRange((float)cfg.GetValue(Section, "focus_range", DefaultFocusRange));
        BlurAmount = ClampBlur((float)cfg.GetValue(Section, "blur_amount", DefaultBlurAmount));
        NearBlur = (bool)cfg.GetValue(Section, "near_blur", true);
    }

    public void SetEnabled(bool value) => Persist("enabled", value, () => Enabled = value);
    public void SetAutoFocus(bool value) => Persist("auto_focus", value, () => AutoFocus = value);
    public void SetNearBlur(bool value) => Persist("near_blur", value, () => NearBlur = value);

    // The three sliders take a `persist` flag the checkboxes don't need. An HSlider raises
    // ValueChanged on every step of a drag, and every one of those would otherwise be a
    // ConfigFile.Save() -- a synchronous file write on the Godot main thread, up to once a frame,
    // for the whole duration of a drag. The UI therefore applies live with persist:false while
    // dragging and writes once on DragEnded.

    public void SetFocusDistance(float value, bool persist = true)
    {
        float clamped = ClampFocus(value);
        Persist("focus_distance", clamped, () => FocusDistance = clamped, persist);
    }

    public void SetFocusRange(float value, bool persist = true)
    {
        float clamped = ClampRange(value);
        Persist("focus_range", clamped, () => FocusRange = clamped, persist);
    }

    public void SetBlurAmount(float value, bool persist = true)
    {
        float clamped = ClampBlur(value);
        Persist("blur_amount", clamped, () => BlurAmount = clamped, persist);
    }

    /// <summary>Restores every value to its shipped default and writes them out.</summary>
    public void ResetToDefaults()
    {
        SetEnabled(false);
        SetAutoFocus(true);
        SetFocusDistance(DefaultFocusDistance);
        SetFocusRange(DefaultFocusRange);
        SetBlurAmount(DefaultBlurAmount);
        SetNearBlur(true);
    }

    private static void Persist(string key, Variant clamped, Action assign, bool write = true)
    {
        assign();
        if (!write) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (UiSettings, CameraSettings, ...)
        cfg.SetValue(Section, key, clamped);
        cfg.Save(ConfigPath);
    }

    private static float ClampFocus(float v) => Mathf.Clamp(v, MinFocusDistance, MaxFocusDistance);
    private static float ClampRange(float v) => Mathf.Clamp(v, MinFocusRange, MaxFocusRange);
    private static float ClampBlur(float v) => Mathf.Clamp(v, MinBlurAmount, MaxBlurAmount);
}
