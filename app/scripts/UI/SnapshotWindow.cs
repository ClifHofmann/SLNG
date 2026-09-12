using System;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// MVP3-4 Phase 1: take a world-only photo of the current 3D view and save it as a PNG.
///
/// Capture hides every CanvasLayer for one rendered frame — windows, toolbar, menu bar, dialogs,
/// avatar name tags and worn HUD attachments are all CanvasLayers, the 3D world is not — then
/// reads the main viewport’s texture back, so the shot carries the world and nothing else.
///
/// FEAT-RENDER-07 adds the depth-of-field controls below the capture buttons. They belong here
/// rather than in Preferences because DoF is framing, not a quality setting: you set the focal
/// plane while looking at the shot you are about to take. The effect itself is applied by
/// <see cref="SLNG.App.DepthOfFieldController"/> to the live view, so the preview and the saved
/// PNG show exactly what the sliders did.
///
/// A resolution multiplier and environment presets are a later phase (FEAT-UI-22).
/// </summary>
public partial class SnapshotWindow : SLNGWindow
{
    private const string DefaultSnapshotDir = "user://snapshots";

    private CanvasLayer? _hudLayer;

    // Nothing registers chrome layers any more -- see HideEveryCanvasLayer. The registration
    // approach shipped once and the menu bar was still in the photo: SetupHud() builds this
    // window BEFORE SetupTopMenu() creates the menu, so the registration handed over a null and
    // silently did nothing. Enumerating the tree at capture time cannot be out of order, and
    // cannot miss a layer somebody adds later either.

    private Viewport? _vp;
    private TextureRect _preview = null!;
    private Label _resolutionLabel = null!;
    private Label _statusLabel = null!;
    private Button _captureButton = null!;
    private Button _saveButton = null!;

    private Image? _lastImage;
    private bool _capturing;

    // --- Output folder (FEAT-UI-17, partial) -----------------------------------------------
    private SnapshotSettings? _settings;
    private Label _folderLabel = null!;
    private FileDialog _folderDialog = null!;

    // --- Live HUD hide ------------------------------------------------------------------
    // Capture already hides the whole HUD for the single frame it reads back (see the class
    // doc comment) -- this is a separate, manual toggle so the user can compose the shot with
    // a clean view instead of only seeing the HUD-free result after pressing Capture.

    // --- Depth of field (FEAT-RENDER-07) ---------------------------------------------------
    private DofSettings? _dof;
    // Null until login builds the camera. The controls still work before that: they write to
    // DofSettings, and DepthOfFieldController.Initialize applies whatever it finds there.
    private SLNG.App.DepthOfFieldController? _dofController;

    private CheckButton _dofEnable = null!;
    private VBoxContainer _dofControls = null!;
    private CheckBox _dofAutoFocus = null!;
    private CheckBox _dofShowMarker = null!;
    private CheckBox _dofNearBlur = null!;
    private HSlider _dofFocusSlider = null!;
    private HSlider _dofRangeSlider = null!;
    private HSlider _dofFalloffSlider = null!;
    private HSlider _dofBlurSlider = null!;
    private Label _dofFocusValue = null!;
    private Label _dofRangeValue = null!;
    private Label _dofFalloffValue = null!;
    private Label _dofBlurValue = null!;
    private Label _dofAutoFocusReadout = null!;

    // Guards the ValueChanged/Toggled handlers while Refresh pushes settings INTO the controls,
    // so restoring the UI does not read back as a user edit.
    private bool _refreshingDof;

    public override void _Ready()
    {
        PersistId = "snapshot";
        base._Ready();

        Title = L10n.Tr("ui.snapshot.title");
        CustomMinimumSize = new Vector2(340, 390);
        Size = new Vector2(400, 650);
        Position = new Vector2(120, 120);
        Visible = false;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        ContentContainer.AddChild(vbox);

        _preview = new TextureRect
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 150),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        var previewFrame = new PanelContainer();
        var frameStyle = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.35f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        previewFrame.AddThemeStyleboxOverride("panel", frameStyle);
        previewFrame.SizeFlagsVertical = SizeFlags.ExpandFill;
        previewFrame.AddChild(_preview);
        vbox.AddChild(previewFrame);

        _resolutionLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _resolutionLabel.AddThemeFontSizeOverride("font_size", 11);
        _resolutionLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        vbox.AddChild(_resolutionLabel);

        // There is no "hide HUD" switch here any more. CaptureAsync takes every overlay out of the
        // captured frame by itself -- viewer chrome, name tags and worn HUD attachments alike --
        // so a switch could only ever change the LIVE view, which is not what a photo control is
        // for and is not what the user wanted it to do. Hiding the interface on purpose is still
        // View -> Toggle HUD in the top menu.

        var buttonRow = new HBoxContainer();
        buttonRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(buttonRow);

        _captureButton = new Button
        {
            Text = $"📷  {L10n.Tr("ui.snapshot.capture")}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };
        _captureButton.Pressed += () => _ = CaptureAsync();
        buttonRow.AddChild(_captureButton);

        _saveButton = new Button
        {
            Text = $"💾  {L10n.Tr("ui.snapshot.save")}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            Disabled = true,
        };
        _saveButton.Pressed += Save;
        buttonRow.AddChild(_saveButton);

        BuildFolderRow(vbox);

        BuildDofSection(vbox);

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

    /// <summary>Hides every CanvasLayer in the scene and returns what was visible, so the caller
    /// can put it all back.
    ///
    /// A CanvasLayer is, in this app, exactly "2D drawn over the world": the HUD, the top menu
    /// bar, the dialog layer, the teleport overlay, the avatar name tags, the worn-HUD viewport.
    /// The 3D world itself renders through the root viewport and owns no CanvasLayer, so sweeping
    /// all of them is precisely "photograph the world and nothing else" -- and unlike a list of
    /// registered layers it cannot be defeated by construction order or by a layer added later.
    /// </summary>
    private static System.Collections.Generic.List<CanvasLayer> HideEveryCanvasLayer(Node root)
    {
        var hidden = new System.Collections.Generic.List<CanvasLayer>();
        Walk(root);
        return hidden;

        void Walk(Node node)
        {
            if (node is CanvasLayer { Visible: true } layer)
            {
                layer.Visible = false;
                hidden.Add(layer);
            }

            foreach (var child in node.GetChildren()) Walk(child);
        }
    }


    /// <summary>FEAT-UI-17 (partial): the persisted output-folder choice. Available from startup,
    /// same split as <see cref="InitializeDof"/>.</summary>
    public void InitializeSettings(SnapshotSettings settings)
    {
        _settings = settings;
        UpdateFolderLabel();
    }

    /// <summary>FEAT-RENDER-07: the persisted DoF settings. Available from startup, unlike the
    /// controller — see <see cref="SetDofController"/>.</summary>
    public void InitializeDof(DofSettings settings)
    {
        _dof = settings;
        RefreshDof();
    }

    /// <summary>FEAT-RENDER-07: the live controller, which only exists once login has built the
    /// camera. Until then the controls edit <see cref="DofSettings"/> alone and take effect the
    /// moment the controller initializes.</summary>
    public void SetDofController(SLNG.App.DepthOfFieldController controller)
    {
        _dofController = controller;
        _dofController.Apply();
    }

    public void Toggle() => Visible = !Visible;

    private void UpdateResolutionLabel()
    {
        var size = GetViewport().GetVisibleRect().Size;
        _resolutionLabel.Text = $"{(int)size.X} × {(int)size.Y}";
    }

    // --- Output folder (FEAT-UI-17, partial) ------------------------------------------------

    /// <summary>The folder snapshots are saved to right now: the user's chosen folder if one is
    /// set, otherwise <see cref="DefaultSnapshotDir"/>. Always returned as a globalized OS path,
    /// since that is what <see cref="FileDialog"/> and <see cref="OS.ShellShowInFileManager"/>
    /// both need, and what <see cref="Save"/> and <see cref="Image.SavePng"/> accept just as well
    /// as a Godot user:// path.</summary>
    private string ResolvedSnapshotDir() =>
        string.IsNullOrEmpty(_settings?.OutputDir)
            ? ProjectSettings.GlobalizePath(DefaultSnapshotDir)
            : _settings.OutputDir;

    private void BuildFolderRow(VBoxContainer parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        parent.AddChild(row);

        _folderLabel = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            TooltipText = L10n.Tr("ui.snapshot.folder_tooltip"),
        };
        _folderLabel.AddThemeFontSizeOverride("font_size", 11);
        _folderLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(_folderLabel);

        var chooseButton = new Button
        {
            Text = L10n.Tr("ui.snapshot.choose_folder"),
            FocusMode = FocusModeEnum.None,
        };
        chooseButton.Pressed += () =>
        {
            _folderDialog.CurrentDir = ResolvedSnapshotDir();
            _folderDialog.PopupCentered(new Vector2I(560, 420));
        };
        row.AddChild(chooseButton);

        var openButton = new Button
        {
            Text = L10n.Tr("ui.snapshot.open_folder"),
            FocusMode = FocusModeEnum.None,
        };
        openButton.Pressed += OpenFolder;
        row.AddChild(openButton);

        _folderDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = L10n.Tr("ui.snapshot.choose_folder"),
            // Godot's own built-in file browser has no relation to SLNGWindow's hand-drawn
            // translucent/rounded look (it is a completely separate engine dialog, generic grey
            // chrome, English-only button captions) -- so instead of trying to reskin it, use the
            // real OS folder picker. That looks native everywhere, the way every app's file
            // dialogs do, instead of half-matching ours.
            UseNativeDialog = true,
        };
        _folderDialog.DirSelected += dir =>
        {
            _settings?.SetOutputDir(dir);
            UpdateFolderLabel();
        };
        AddChild(_folderDialog);

        UpdateFolderLabel();
    }

    private void UpdateFolderLabel()
    {
        _folderLabel.Text = ResolvedSnapshotDir();
    }

    /// <summary>Ensures the folder exists, then hands it to the OS file manager. Creating it
    /// first means "Open folder" works even before the first capture, same as most viewers.</summary>
    private void OpenFolder()
    {
        var dir = ResolvedSnapshotDir();
        var err = DirAccess.MakeDirRecursiveAbsolute(dir);
        if (err != Error.Ok && err != Error.AlreadyExists)
        {
            _statusLabel.Text = L10n.TrFormat("ui.snapshot.dir_failed", dir, err);
            return;
        }
        OS.ShellShowInFileManager(dir);
    }

    // --- Depth of field ---------------------------------------------------------------------

    private void BuildDofSection(VBoxContainer parent)
    {
        parent.AddChild(new HSeparator());

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        parent.AddChild(header);

        var heading = new Label
        {
            Text = L10n.Tr("ui.snapshot.dof_heading"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        heading.AddThemeFontSizeOverride("font_size", 12);
        header.AddChild(heading);

        _dofEnable = new CheckButton
        {
            Text = L10n.Tr("ui.snapshot.dof_enable"),
            TooltipText = L10n.Tr("ui.snapshot.dof_enable_tooltip"),
            FocusMode = FocusModeEnum.None,
        };
        _dofEnable.Toggled += on =>
        {
            if (_refreshingDof || _dof == null) return;
            _dof.SetEnabled(on);
            _dofControls.Visible = on;
            _dofController?.Apply();
        };
        header.AddChild(_dofEnable);

        _dofControls = new VBoxContainer { Visible = false };
        _dofControls.AddThemeConstantOverride("separation", 4);
        parent.AddChild(_dofControls);

        // --- Auto focus, with a live readout of what it currently resolves to -----------------
        var autoRow = new HBoxContainer();
        autoRow.AddThemeConstantOverride("separation", 8);
        _dofControls.AddChild(autoRow);

        _dofAutoFocus = new CheckBox
        {
            Text = L10n.Tr("ui.snapshot.dof_auto_focus"),
            TooltipText = L10n.Tr("ui.snapshot.dof_auto_focus_tooltip"),
            FocusMode = FocusModeEnum.None,
        };
        _dofAutoFocus.Toggled += on =>
        {
            if (_refreshingDof || _dof == null) return;
            _dof.SetAutoFocus(on);
            _dofController?.Apply();
        };
        autoRow.AddChild(_dofAutoFocus);

        _dofAutoFocusReadout = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _dofAutoFocusReadout.AddThemeFontSizeOverride("font_size", 11);
        _dofAutoFocusReadout.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        autoRow.AddChild(_dofAutoFocusReadout);

        // --- The three sliders ---------------------------------------------------------------
        // The focus slider stays editable even while auto focus is on: _Process tracks the live
        // focal plane onto its handle, and grabbing it is a manual override that switches auto
        // focus off (so the value the user just set actually holds, with no jump on the handoff).
        _dofFocusSlider = AddSliderRow(
            L10n.Tr("ui.snapshot.dof_focus"), L10n.Tr("ui.snapshot.dof_focus_tooltip"),
            DofSettings.MinFocusDistance, DofSettings.MaxFocusDistance, 0.1,
            DofSettings.DefaultFocusDistance, out _dofFocusValue,
            v => $"{v:0.0} m",
            (v, persist) =>
            {
                if (_dof is { AutoFocus: true })
                {
                    _dof.SetAutoFocus(false);
                    _refreshingDof = true;
                    _dofAutoFocus.ButtonPressed = false;
                    _refreshingDof = false;
                }
                _dof?.SetFocusDistance((float)v, persist);
            });

        _dofRangeSlider = AddSliderRow(
            L10n.Tr("ui.snapshot.dof_range"), L10n.Tr("ui.snapshot.dof_range_tooltip"),
            DofSettings.MinFocusRange, DofSettings.MaxFocusRange, 0.1,
            DofSettings.DefaultFocusRange, out _dofRangeValue,
            v => $"{v:0.0} m",
            (v, persist) => _dof?.SetFocusRange((float)v, persist));

        _dofFalloffSlider = AddSliderRow(
            L10n.Tr("ui.snapshot.dof_falloff"), L10n.Tr("ui.snapshot.dof_falloff_tooltip"),
            DofSettings.MinFalloff, DofSettings.MaxFalloff, 0.01,
            DofSettings.DefaultFalloff, out _dofFalloffValue,
            v => $"{v * 100:0} %",
            (v, persist) => _dof?.SetFalloff((float)v, persist));

        _dofBlurSlider = AddSliderRow(
            L10n.Tr("ui.snapshot.dof_blur"), null,
            DofSettings.MinBlurAmount, DofSettings.MaxBlurAmount, 0.01,
            DofSettings.DefaultBlurAmount, out _dofBlurValue,
            v => $"{v:0.00}",
            (v, persist) => _dof?.SetBlurAmount((float)v, persist));

        // --- Foreground blur + reset ----------------------------------------------------------
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        _dofControls.AddChild(footer);

        _dofNearBlur = new CheckBox
        {
            Text = L10n.Tr("ui.snapshot.dof_near_blur"),
            TooltipText = L10n.Tr("ui.snapshot.dof_near_blur_tooltip"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };
        _dofNearBlur.Toggled += on =>
        {
            if (_refreshingDof || _dof == null) return;
            _dof.SetNearBlur(on);
            _dofController?.Apply();
        };
        footer.AddChild(_dofNearBlur);

        // BUG-RENDER-22: the diagnostic toggle. Lives with the DoF controls rather than in the
        // Developer menu because it is only meaningful while DoF is on, and _dofControls is
        // already hidden whenever it is off.
        _dofShowMarker = new CheckBox
        {
            Text = L10n.Tr("ui.snapshot.dof_show_marker"),
            TooltipText = L10n.Tr("ui.snapshot.dof_show_marker_tooltip"),
            FocusMode = FocusModeEnum.None,
        };
        _dofShowMarker.Toggled += on =>
        {
            if (_refreshingDof || _dof == null) return;
            _dof.SetShowFocusMarker(on);
        };
        _dofControls.AddChild(_dofShowMarker);

        var reset = new Button
        {
            Text = L10n.Tr("ui.snapshot.dof_reset"),
            FocusMode = FocusModeEnum.None,
        };
        reset.Pressed += () =>
        {
            if (_dof == null) return;
            _dof.ResetToDefaults();
            RefreshDof();
            _dofController?.Apply();
        };
        footer.AddChild(reset);
    }

    /// <summary>One "label — slider — value" row inside the DoF block. <paramref name="onChanged"/>
    /// is called with <c>persist:false</c> for every step of a drag and once with
    /// <c>persist:true</c> when the drag ends — see the note on <see cref="DofSettings"/>'s
    /// setters for why the write is not on every step.</summary>
    private HSlider AddSliderRow(
        string label, string? tooltip,
        double min, double max, double step, double initial,
        out Label valueLabel,
        Func<double, string> format,
        Action<double, bool> onChanged)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _dofControls.AddChild(row);

        var name = new Label { Text = label, CustomMinimumSize = new Vector2(92, 0) };
        name.AddThemeFontSizeOverride("font_size", 11);
        if (tooltip != null) { name.TooltipText = tooltip; name.MouseFilter = Control.MouseFilterEnum.Stop; }
        row.AddChild(name);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = initial,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(120, 0),
        };
        if (tooltip != null) slider.TooltipText = tooltip;
        row.AddChild(slider);

        var value = new Label
        {
            Text = format(initial),
            CustomMinimumSize = new Vector2(52, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        value.AddThemeFontSizeOverride("font_size", 11);
        value.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        row.AddChild(value);
        valueLabel = value;

        var capturedLabel = value;
        slider.ValueChanged += v =>
        {
            capturedLabel.Text = format(v);
            if (_refreshingDof) return;
            onChanged(v, false);
            _dofController?.Apply();
        };
        slider.DragEnded += _ =>
        {
            if (_refreshingDof) return;
            onChanged(slider.Value, true);
        };

        return slider;
    }

    /// <summary>Pushes <see cref="DofSettings"/> back into the controls. Used on wiring and after
    /// the reset button.</summary>
    private void RefreshDof()
    {
        if (_dof == null) return;

        _refreshingDof = true;
        try
        {
            _dofEnable.ButtonPressed = _dof.Enabled;
            _dofControls.Visible = _dof.Enabled;
            _dofAutoFocus.ButtonPressed = _dof.AutoFocus;
            _dofNearBlur.ButtonPressed = _dof.NearBlur;
            _dofShowMarker.ButtonPressed = _dof.ShowFocusMarker;
            _dofFocusSlider.Value = _dof.FocusDistance;
            _dofRangeSlider.Value = _dof.FocusRange;
            _dofFalloffSlider.Value = _dof.Falloff;
            _dofBlurSlider.Value = _dof.BlurAmount;
        }
        finally
        {
            _refreshingDof = false;
        }
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        // Only the auto-focus readout is live, and only while it can actually be seen.
        if (!Visible || _dof == null || !_dof.Enabled || !_dof.AutoFocus)
        {
            if (_dofAutoFocusReadout.Text.Length > 0) _dofAutoFocusReadout.Text = "";
            return;
        }

        if (_dofController == null) { _dofAutoFocusReadout.Text = ""; return; }

        _dofAutoFocusReadout.Text = _dofController.AutoFocusHasTarget
            ? $"→ {_dofController.CurrentFocusDistance:0.0} m"
            : L10n.Tr("ui.snapshot.dof_no_target");

        // Track the live focal plane onto the slider handle so switching to manual (by grabbing
        // it) has no jump. Guarded so this write is not read back as a user edit.
        _refreshingDof = true;
        _dofFocusSlider.Value = _dofController.CurrentFocusDistance;
        _refreshingDof = false;
    }

    // --- Capture ----------------------------------------------------------------------------

    private async System.Threading.Tasks.Task CaptureAsync()
    {
        if (_capturing) return;
        _capturing = true;
        _captureButton.Disabled = true;
        _statusLabel.Text = L10n.Tr("ui.snapshot.capturing");

        // FEAT-UI-25: the shot is the WORLD and nothing else -- no windows, no menu bar, no name
        // tags, no worn HUD attachments. Every one of those is a CanvasLayer, so one sweep covers
        // them all and the finally below puts back exactly what was visible.
        var hiddenLayers = HideEveryCanvasLayer(GetTree().Root);

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
                _statusLabel.Text = L10n.Tr("ui.snapshot.capture_failed");
                return;
            }

            _lastImage = image;
            _preview.Texture = ImageTexture.CreateFromImage(image);
            _saveButton.Disabled = false;
            _statusLabel.Text = L10n.TrFormat("ui.snapshot.captured", image.GetWidth(), image.GetHeight());
        }
        finally
        {
            foreach (var layer in hiddenLayers)
            {
                if (IsInstanceValid(layer)) layer.Visible = true;
            }
            _captureButton.Disabled = false;
            _capturing = false;
        }
    }

    private void Save()
    {
        if (_lastImage == null) return;

        var dir = ResolvedSnapshotDir();
        var dirErr = DirAccess.MakeDirRecursiveAbsolute(dir);
        if (dirErr != Error.Ok && dirErr != Error.AlreadyExists)
        {
            _statusLabel.Text = L10n.TrFormat("ui.snapshot.dir_failed", dir, dirErr);
            return;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = $"{dir}/snapshot_{stamp}.png";
        for (int n = 1; FileAccess.FileExists(path); n++)
            path = $"{dir}/snapshot_{stamp}_{n}.png";

        var err = _lastImage.SavePng(path);
        _statusLabel.Text = err == Error.Ok
            ? L10n.TrFormat("ui.snapshot.saved", path)
            : L10n.TrFormat("ui.snapshot.save_failed", err);
    }
}
