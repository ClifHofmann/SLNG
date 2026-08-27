using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// MVP3-4 Phase 1: take a world-only photo of the current 3D view and save it as a PNG.
///
/// Capture hides the whole <see cref="_hudLayer"/> CanvasLayer for one rendered frame so the
/// shot carries no windows, toolbar or overlay — then reads the main viewport's texture back.
/// FOV, depth-of-field, a resolution multiplier and environment presets are later phases (see
/// the spec); this window is deliberately just capture + preview + save first.
/// </summary>
public partial class SnapshotWindow : SLNGWindow
{
    private const string SnapshotDir = "user://snapshots";

    private CanvasLayer? _hudLayer;
    private Viewport? _vp;
    private TextureRect _preview = null!;
    private Label _resolutionLabel = null!;
    private Label _statusLabel = null!;
    private Button _captureButton = null!;
    private Button _saveButton = null!;

    private Image? _lastImage;
    private bool _capturing;

    public override void _Ready()
    {
        PersistId = "snapshot";
        base._Ready();

        Title = "Snapshot";
        CustomMinimumSize = new Vector2(320, 300);
        Size = new Vector2(380, 360);
        Position = new Vector2(120, 120);
        Visible = false;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(vbox);

        _preview = new TextureRect
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 170),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        var previewFrame = new PanelContainer();
        var frameStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.35f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        };
        previewFrame.AddThemeStyleboxOverride("panel", frameStyle);
        previewFrame.SizeFlagsVertical = SizeFlags.ExpandFill;
        previewFrame.AddChild(_preview);
        vbox.AddChild(previewFrame);

        _resolutionLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _resolutionLabel.AddThemeFontSizeOverride("font_size", 11);
        _resolutionLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        vbox.AddChild(_resolutionLabel);

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(buttonRow);

        _captureButton = new Button
        {
            Text = "📷  Capture",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };
        _captureButton.Pressed += () => _ = CaptureAsync();
        buttonRow.AddChild(_captureButton);

        _saveButton = new Button
        {
            Text = "💾  Save PNG",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            Disabled = true,
        };
        _saveButton.Pressed += Save;
        buttonRow.AddChild(_saveButton);

        _statusLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 30),
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 11);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        vbox.AddChild(_statusLabel);

        _vp = GetViewport();
        UpdateResolutionLabel();
        if (_vp != null) _vp.SizeChanged += UpdateResolutionLabel;
    }

    public override void _ExitTree()
    {
        if (_vp != null) _vp.SizeChanged -= UpdateResolutionLabel;
        base._ExitTree();
    }

    /// <summary>Boot hands us the HUD CanvasLayer so a capture can blank every overlay for one
    /// frame. Without it, capture still works but the shot includes the UI.</summary>
    public void Initialize(CanvasLayer hudLayer) => _hudLayer = hudLayer;

    public void Toggle() => Visible = !Visible;

    private void UpdateResolutionLabel()
    {
        var size = GetViewport().GetVisibleRect().Size;
        _resolutionLabel.Text = $"{(int)size.X} × {(int)size.Y}";
    }

    private async System.Threading.Tasks.Task CaptureAsync()
    {
        if (_capturing) return;
        _capturing = true;
        _captureButton.Disabled = true;
        _statusLabel.Text = "Capturing…";

        bool hudWasVisible = _hudLayer?.Visible ?? false;
        if (_hudLayer != null) _hudLayer.Visible = false;

        try
        {
            // One full frame with the overlay hidden, then wait for the GPU to have drawn it
            // before reading the target back. ProcessFrame lets the visibility change take
            // effect; FramePostDraw is the point the frame's pixels exist.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            var image = GetViewport().GetTexture().GetImage();
            if (image == null || image.IsEmpty())
            {
                _statusLabel.Text = "Capture failed: the viewport returned no image.";
                return;
            }

            _lastImage = image;
            _preview.Texture = ImageTexture.CreateFromImage(image);
            _saveButton.Disabled = false;
            _statusLabel.Text = $"Captured {image.GetWidth()} × {image.GetHeight()}. Save it, or capture again.";
        }
        finally
        {
            if (_hudLayer != null) _hudLayer.Visible = hudWasVisible;
            _captureButton.Disabled = false;
            _capturing = false;
        }
    }

    private void Save()
    {
        if (_lastImage == null) return;

        var dirErr = DirAccess.MakeDirRecursiveAbsolute(SnapshotDir);
        if (dirErr != Error.Ok && dirErr != Error.AlreadyExists)
        {
            _statusLabel.Text = $"Could not create {ProjectSettings.GlobalizePath(SnapshotDir)} ({dirErr}).";
            return;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = $"{SnapshotDir}/snapshot_{stamp}.png";
        for (int n = 1; FileAccess.FileExists(path); n++)
            path = $"{SnapshotDir}/snapshot_{stamp}_{n}.png";

        var err = _lastImage.SavePng(path);
        _statusLabel.Text = err == Error.Ok
            ? $"Saved: {ProjectSettings.GlobalizePath(path)}"
            : $"Save failed ({err}).";
    }
}
