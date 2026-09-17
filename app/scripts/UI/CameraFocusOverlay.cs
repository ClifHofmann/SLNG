using Godot;

namespace SLNG.App.UI;

/// <summary>
/// 2D viewport overlay that renders the camera's 3D Look-At / focus point marker.
/// Rendered on the 2D HUD CanvasLayer after all 3D post-processing passes,
/// so it remains 100% crisp and is structurally immune to Depth-of-Field blur.
/// Unprojects the 3D subject coordinate (the Alt-clicked target or avatar follow point)
/// to 2D screen coordinates, so it stays firmly anchored to the 3D world subject
/// even when the camera is panned or orbited.
/// </summary>
public sealed partial class CameraFocusOverlay : Control
{
    private readonly DofSettings _settings;
    private AvatarController? _camera;

    public CameraFocusOverlay(DofSettings settings)
    {
        _settings = settings;
        Name = "CameraFocusOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public void SetCamera(AvatarController camera) => _camera = camera;

    public override void _Process(double delta)
    {
        if (!_settings.ShowFocusMarker)
        {
            QueueRedraw();
            return;
        }

        if (_camera == null || !GodotObject.IsInstanceValid(_camera))
        {
            _camera = GetTree().Root.FindChild("AvatarController", true, false) as AvatarController;
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_settings.ShowFocusMarker) return;
        if (_camera == null || !GodotObject.IsInstanceValid(_camera)) return;

        var worldPos = _camera.FocusSubjectPoint != Vector3.Zero ? _camera.FocusSubjectPoint : _camera.CameraTargetPoint;
        if (worldPos == Vector3.Zero || !worldPos.IsFinite()) return;

        // View depth check: must be in front of camera
        var camXform = _camera.GlobalTransform;
        var toMarker = worldPos - camXform.Origin;
        if (toMarker.Dot(-camXform.Basis.Z) <= 0.1f) return;

        var screenPos = _camera.UnprojectPosition(worldPos);
        var vpRect = GetViewportRect();
        if (!vpRect.HasPoint(screenPos)) return;

        // SL-style Look-At Reticle (subtle, discreet hairline indicator):
        // Small central ring with 4 subtle orthogonal hairline ticks
        float ringRadius = 3.5f;
        float gap = 4.5f;
        float tickLen = 5.5f;

        // 1. Soft dark drop shadow (1.8px width, 0.5 alpha)
        var shadow = new Color(0f, 0f, 0f, 0.5f);
        DrawArc(screenPos, ringRadius, 0, Mathf.Tau, 24, shadow, 1.8f, true);
        DrawLine(screenPos + new Vector2(-gap - tickLen, 0), screenPos + new Vector2(-gap, 0), shadow, 1.8f);
        DrawLine(screenPos + new Vector2(gap, 0), screenPos + new Vector2(gap + tickLen, 0), shadow, 1.8f);
        DrawLine(screenPos + new Vector2(0, -gap - tickLen), screenPos + new Vector2(0, -gap), shadow, 1.8f);
        DrawLine(screenPos + new Vector2(0, gap), screenPos + new Vector2(0, gap + tickLen), shadow, 1.8f);

        // 2. Subtle foreground hairline (soft cyan, 1.0px width, 0.75 alpha)
        var reticleColor = new Color(0.3f, 0.85f, 0.95f, 0.75f);
        DrawArc(screenPos, ringRadius, 0, Mathf.Tau, 24, reticleColor, 1.0f, true);
        DrawLine(screenPos + new Vector2(-gap - tickLen, 0), screenPos + new Vector2(-gap, 0), reticleColor, 1.0f);
        DrawLine(screenPos + new Vector2(gap, 0), screenPos + new Vector2(gap + tickLen, 0), reticleColor, 1.0f);
        DrawLine(screenPos + new Vector2(0, -gap - tickLen), screenPos + new Vector2(0, -gap), reticleColor, 1.0f);
        DrawLine(screenPos + new Vector2(0, gap), screenPos + new Vector2(0, gap + tickLen), reticleColor, 1.0f);
    }
}
