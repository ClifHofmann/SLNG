using System;
using System.Threading.Tasks;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// A floating image viewer for one texture: opened by a click on a profile picture and by a
/// double-click on a texture in the inventory. It only ever shows the picture -- there is no
/// save/export here, the same way the reference viewer's preview does not hand out a texture
/// the owner never allowed to be copied (TPV Policy).
///
/// It decodes the asset itself (<see cref="FullResImage"/>, not <see cref="GpuCache"/>) so it
/// never shows less than the asset has, and owns the resulting texture, which dies with the
/// window.
/// </summary>
public partial class TextureViewerWindow : SLNGWindow
{
    private const float MaxStartWidth = 720f;
    private const float MaxStartHeight = 620f;

    private SLNG.Assets.AssetService? _assetService;
    private Guid _textureId;

    private TextureRect _image = null!;
    private Label _infoLabel = null!;

    /// <summary>Fired from the close button so the owner can drop its reference.</summary>
    public Action? Closed;

    public override void _Ready()
    {
        base._Ready();

        Title = L10n.Tr("ui.texture_viewer.title");
        CustomMinimumSize = new Vector2(240, 200);
        Size = new Vector2(480, 400);
        OnCloseRequested = () =>
        {
            Closed?.Invoke();
            QueueFree();
        };

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        ContentContainer.AddChild(box);

        var frame = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.35f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        });
        box.AddChild(frame);

        _image = new TextureRect
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            // A big picture shown in a smaller window: sample the mip chain instead of aliasing.
            TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
        };
        frame.AddChild(_image);

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        box.AddChild(footer);

        _infoLabel = new Label
        {
            Text = L10n.Tr("ui.texture_viewer.loading"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _infoLabel.AddThemeFontSizeOverride("font_size", 11);
        _infoLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        footer.AddChild(_infoLabel);

        // Off: the picture keeps its own shape and the window shows bars where it is not that shape.
        // On: the picture takes the window's shape, stretched -- for someone who wants to size it freely.
        var free = new CheckBox { Text = L10n.Tr("ui.texture_viewer.free_transform"), FocusMode = FocusModeEnum.None };
        free.AddThemeFontSizeOverride("font_size", 11);
        free.Toggled += on => _image.StretchMode = on
            ? TextureRect.StretchModeEnum.Scale
            : TextureRect.StretchModeEnum.KeepAspectCentered;
        footer.AddChild(free);
    }

    /// <summary>Call once, right after the window is in the tree.</summary>
    public void Initialize(Guid textureId, string name, SLNG.Assets.AssetService? assetService)
    {
        _textureId = textureId;
        _assetService = assetService;
        if (!string.IsNullOrWhiteSpace(name)) Title = name;

        if (assetService == null || textureId == Guid.Empty)
        {
            _infoLabel.Text = L10n.Tr("ui.texture_viewer.failed");
            return;
        }

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // Pixel work on this worker thread; only the GPU upload happens on the main thread.
            var image = await FullResImage.LoadAsync(_assetService!, _textureId).ConfigureAwait(false);
            if (image == null)
            {
                CallDeferred(MethodName.ShowFailed);
                return;
            }
            // Worker thread: marshal by method name (BUG-RENDER-01), never Callable.From(lambda).
            CallDeferred(MethodName.ShowImage, image);
        }
        catch (Exception ex)
        {
            Logger.Info($"[TextureViewer] {_textureId.ToString()[..8]} failed: {ex.Message}");
            CallDeferred(MethodName.ShowFailed);
        }
    }

    private void ShowFailed() => _infoLabel.Text = L10n.Tr("ui.texture_viewer.failed");

    public void ShowImage(Image image) =>
        ShowImageOn(image, GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720));

    /// <summary>Shows the picture on a screen of the given size (the window is sized to it, shrunk to fit).
    /// Split out so a test can state the screen instead of inheriting a headless 64 x 64 one.</summary>
    internal void ShowImageOn(Image image, Vector2 viewport)
    {
        var tex = ImageTexture.CreateFromImage(image);
        _image.Texture = tex;
        int w = image.GetWidth(), h = image.GetHeight();
        _infoLabel.Text = $"{w} × {h}";

        // Open with the picture area at the picture's own size (shrunk, keeping its shape, only when it would not
        // fit the screen): the window is made exactly as large as the picture needs, whatever the title bar, the
        // insets and the footer take, instead of a guessed chrome height that left bars around it.
        float maxW = Mathf.Min(MaxStartWidth, viewport.X - 80f);
        float maxH = Mathf.Min(MaxStartHeight, viewport.Y - 160f);
        float scale = Mathf.Min(1f, Mathf.Min(maxW / w, maxH / h));
        _image.CustomMinimumSize = new Vector2(Mathf.Round(w * scale), Mathf.Round(h * scale));
        Size = Vector2.Zero; // clamps up to the minimum size: exactly picture + chrome
        ClampToViewport();

        // The picture area asked for that size only to size the window; give the freedom to shrink back.
        CallDeferred(MethodName.ReleaseImageMinimum);
    }

    private void ReleaseImageMinimum() => _image.CustomMinimumSize = new Vector2(0, 120);
}
