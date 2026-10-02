using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// The one place the interface scale lives (FEAT-UI-42). It is applied to the ROOT window as
/// <see cref="Window.ContentScaleFactor"/> with <see cref="Window.ContentScaleModeEnum.Disabled"/> --
/// the mechanism the Godot editor uses for its own UI -- so every Control and CanvasLayer, popups and
/// tooltips included, scales together, while the 3D render stays at the window's physical pixels.
/// <para>Consequences every caller has to know:</para>
/// <list type="bullet">
/// <item>Control coordinates, <see cref="Viewport.GetVisibleRect"/>, <see cref="Viewport.GetMousePosition"/>,
/// <c>Camera3D.ProjectRayOrigin</c> and <c>UnprojectPosition</c> are all in LOGICAL units
/// (physical / scale) and agree with each other. Nothing needs converting between them.</item>
/// <item>Anything that must match the real render resolution -- a SubViewport that mirrors the main
/// view, an image of the frame, a pixels-per-radian LOD figure -- must use <see cref="RenderSize"/>,
/// not the visible rect.</item>
/// <item><c>Input.WarpMouse</c> and <see cref="InputEventMouseMotion.ScreenRelative"/> are PHYSICAL;
/// use <see cref="Viewport.WarpMouse"/> and multiply a logical mouse delta by <see cref="Current"/>.</item>
/// <item>SLNGWindow does NOT scale itself any more. It used to set its own Scale, which together
/// with this would scale twice.</item>
/// </list>
/// The pure rules (automatic vs. chosen, clamping, what the OS scale is) are
/// <see cref="UiScalePolicy"/>, tested without an engine.
/// </summary>
public static class UiScale
{
    /// <summary>The scale the interface is drawn at right now.</summary>
    public static float Current { get; private set; } = 1.0f;

    /// <summary>The operating system's scale for the window's screen as last measured. Shown in the
    /// preferences page next to the automatic setting.</summary>
    public static float OsScale { get; private set; } = 1.0f;

    /// <summary>Raised BEFORE the root window changes, with the old and new scale. The one subscriber
    /// that needs it is SLNGWindow, which moves a floating window so it keeps its place on the
    /// physical screen; it has to run before the viewport resizes, because the resize makes every
    /// window re-clamp itself against the new bounds.</summary>
    public static event System.Action<float, float>? Changing;

    /// <summary>Raised after the root window has the new scale.</summary>
    public static event System.Action<float>? Changed;

    /// <summary>Asks the OS for the scale of the screen the main window is on. Windows reports its
    /// scale through the DPI and not through ScreenGetScale (see <see cref="UiScalePolicy"/>).</summary>
    public static float DetectOsScale()
    {
        int screen = DisplayServer.WindowGetCurrentScreen();
        if (screen < 0) screen = DisplayServer.GetPrimaryScreen();
        return UiScalePolicy.DetectOsScale(
            DisplayServer.ScreenGetScale(screen),
            DisplayServer.ScreenGetDpi(screen),
            dpiIsTrustworthy: OS.GetName() == "Windows");
    }

    /// <summary>Re-reads the OS scale. Returns true if it differs from the last reading.</summary>
    public static bool RefreshOsScale()
    {
        float measured = DetectOsScale();
        if (Mathf.IsEqualApprox(measured, OsScale)) return false;
        OsScale = measured;
        return true;
    }

    /// <summary>Puts <paramref name="scale"/> on the root window. Idempotent. In-memory only -- the
    /// choice is persisted by UiSettings, never here, so a self-test can apply any scale without
    /// touching the user's preferences.</summary>
    public static void Apply(float scale)
    {
        scale = UiScalePolicy.Clamp(scale);
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root == null) { Current = scale; return; }

        // Disabled mode is what keeps the 3D view at physical pixels. Set every time: it is the
        // project default too, but a stale stretch mode would silently turn this into a blur.
        root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;

        if (Mathf.IsEqualApprox(scale, Current) && Mathf.IsEqualApprox(root.ContentScaleFactor, scale)) return;

        float old = Current;
        Changing?.Invoke(old, scale);
        Current = scale;
        root.ContentScaleFactor = scale;
        Changed?.Invoke(scale);
    }

    /// <summary>The size, in physical pixels, that a surface must have to match a viewport's render
    /// resolution. For the main window that is the window's own size; for anything else (a
    /// SubViewport) the visible rect already is pixels.</summary>
    public static Vector2 RenderSize(Viewport? viewport)
    {
        if (viewport == null) return Vector2.Zero;
        return viewport is Window window ? new Vector2(window.Size.X, window.Size.Y) : viewport.GetVisibleRect().Size;
    }
}
