using System;

namespace SLNG.Core.Camera;

/// <summary>
/// The field-of-view limits of the Linden viewer, as pure arithmetic (FEAT-UI-73). The viewer allows far more
/// than SLNG's old 50-100 degrees: a narrow field of view is its "zoom in" (View > Zoom level), and the only way
/// to look closely at a detail of an outfit from a normal distance.
///
/// <para>All angles are the VERTICAL field of view in degrees, which is what Godot's <c>Camera3D.Fov</c> is
/// under its default KeepHeight mode, and what the viewer's <c>mView</c> is. Source, in the vendored clone
/// <c>scratch/slviewer/indra</c>:</para>
/// <list type="bullet">
///   <item><c>llmath/llcamera.h:51-52</c> - <c>MIN_FIELD_OF_VIEW</c> 5 degrees and <c>MAX_FIELD_OF_VIEW</c> 175
///     degrees, "for square views".</item>
///   <item><c>llmath/llcamera.cpp:80-94</c> - <c>getMinView</c> / <c>getMaxView</c> adjust them to the window's
///     aspect (width / height): narrow views raise the minimum (5 / aspect), wide views lower the maximum
///     (175 / aspect).</item>
///   <item><c>newview/llviewercamera.cpp:783-790</c> (<c>setView</c>) and <c>:817-819</c> (<c>setDefaultFOV</c>)
///     clamp to that range; <c>LLCamera::setView</c> (<c>llcamera.cpp:115-119</c>) then clamps to the absolute
///     5-175. <c>LLAgentCamera::updateCamera</c> (<c>llagentcamera.cpp:1481</c>) calls <c>setView</c> every
///     frame, so the field of view actually rendered always honours the CURRENT window shape while the stored
///     setting does not change.</item>
///   <item><c>newview/llviewermenu.cpp:9947-9959</c> (<c>LLZoomer</c>, registered at :10006-10008) - View >
///     Zoom out / in multiply the stored field of view by 1.2 or 1/1.2, clamp it, and store the clamped value
///     in the persisted <c>CameraAngle</c> setting (<c>app_settings/settings.xml:1307-1316</c>, Persist 1).</item>
///   <item><c>newview/llfloaterpreference.cpp:502-503</c> and <c>:618-619</c> - the Preferences slider
///     <c>camera_fov</c> is given exactly this aspect-dependent range (its XML bounds 0.17 and 2.97 rad,
///     <c>skins/default/xui/en/panel_preferences_move.xml:23-39</c>, are overridden).</item>
/// </list>
/// </summary>
public static class CameraFovLimits
{
    /// <summary>The viewer's default and SLNG's: <c>DEFAULT_FIELD_OF_VIEW</c>, llcamera.h:36.</summary>
    public const float DefaultDegrees = 60f;

    /// <summary>Narrowest field of view for a square view: <c>MIN_FIELD_OF_VIEW</c>, llcamera.h:51.</summary>
    public const float AbsoluteMinDegrees = 5f;

    /// <summary>Widest field of view for a square view: <c>MAX_FIELD_OF_VIEW</c>, llcamera.h:52.</summary>
    public const float AbsoluteMaxDegrees = 175f;

    /// <summary>Smallest aspect ratio the camera accepts: <c>MIN_ASPECT_RATIO</c>, llcamera.h:46.</summary>
    public const float MinAspect = 0.02f;

    /// <summary>Largest aspect ratio the camera accepts: <c>MAX_ASPECT_RATIO</c>, llcamera.h:41.</summary>
    public const float MaxAspect = 50f;

    /// <summary>The aspect the viewer starts with before it knows the window: <c>DEFAULT_ASPECT_RATIO</c>
    /// (640 x 480), llcamera.h:37.</summary>
    public const float DefaultAspect = 640f / 480f;

    /// <summary>One View > Zoom in / out step: the field of view is multiplied by this, or by its inverse
    /// (<c>LLZoomer(1.2f)</c> and <c>LLZoomer(1/1.2f)</c>, llviewermenu.cpp:10006-10007).</summary>
    public const float ZoomStep = 1.2f;

    /// <summary>The aspect the camera works with: finite and within the viewer's accepted range
    /// (llcamera.cpp:131). Anything unusable (a minimised window, NaN) counts as the default 4:3.</summary>
    public static float ClampAspect(float aspect)
        => float.IsFinite(aspect) ? Math.Clamp(aspect, MinAspect, MaxAspect) : DefaultAspect;

    /// <summary>The smallest vertical field of view for a window of this shape: 5 degrees, or 5 / aspect in a
    /// narrow (taller than wide) window - <c>LLCamera::getMinView</c>, llcamera.cpp:80-86. Never above the
    /// absolute maximum.</summary>
    public static float MinDegrees(float aspect)
    {
        aspect = ClampAspect(aspect);
        float min = aspect > 1f ? AbsoluteMinDegrees : AbsoluteMinDegrees / aspect;
        return Math.Min(min, AbsoluteMaxDegrees);
    }

    /// <summary>The largest vertical field of view for a window of this shape: 175 degrees, or 175 / aspect in a
    /// wide window - <c>LLCamera::getMaxView</c>, llcamera.cpp:88-94. At 16:9 that is 98.4 degrees, at 21:9 75.
    /// Never below <see cref="MinDegrees"/>, which only matters for absurd shapes (aspect above 35).</summary>
    public static float MaxDegrees(float aspect)
    {
        aspect = ClampAspect(aspect);
        float max = aspect > 1f ? AbsoluteMaxDegrees / aspect : AbsoluteMaxDegrees;
        return Math.Max(max, MinDegrees(aspect));
    }

    /// <summary>The field of view the camera ends up with when asked for <paramref name="verticalFovDegrees"/>
    /// in a window of this <paramref name="aspect"/> (width / height). A value that is not a number counts as
    /// the default.</summary>
    public static float Clamp(float verticalFovDegrees, float aspect)
    {
        if (!float.IsFinite(verticalFovDegrees)) verticalFovDegrees = DefaultDegrees;
        return Math.Clamp(verticalFovDegrees, MinDegrees(aspect), MaxDegrees(aspect));
    }

    /// <summary>View > Zoom in: narrows the field of view by one <see cref="ZoomStep"/>, down to the minimum.</summary>
    public static float ZoomIn(float currentDegrees, float aspect)
        => Clamp(Clamp(currentDegrees, aspect) / ZoomStep, aspect);

    /// <summary>View > Zoom out: widens the field of view by one <see cref="ZoomStep"/>, up to the maximum.</summary>
    public static float ZoomOut(float currentDegrees, float aspect)
        => Clamp(Clamp(currentDegrees, aspect) * ZoomStep, aspect);

    /// <summary>View > Zoom default: 60 degrees, kept inside the range for this window shape.</summary>
    public static float Default(float aspect) => Clamp(DefaultDegrees, aspect);
}
