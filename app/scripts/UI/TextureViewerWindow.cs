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

        _infoLabel = new Label
        {
            Text = L10n.Tr("ui.texture_viewer.loading"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _infoLabel.AddThemeFontSizeOverride("font_size", 11);
        _infoLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        box.AddChild(_infoLabel);
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

    private void ShowImage(Image image)
    {
        var tex = ImageTexture.CreateFromImage(image);
        _image.Texture = tex;
        int w = image.GetWidth(), h = image.GetHeight();
        _infoLabel.Text = $"{w} × {h}";

        // Open at the picture's own shape (and no larger than the window can sensibly be), so a
        // wide image is not squeezed into the default box. Never shrink below what is showing.
        float chrome = 90f; // title bar + insets + the info line
        float scale = Mathf.Min(1f, Mathf.Min(MaxStartWidth / w, (MaxStartHeight - chrome) / h));
        Size = new Vector2(Mathf.Max(CustomMinimumSize.X, w * scale + 2 * DefaultContentMarginH),
            Mathf.Max(CustomMinimumSize.Y, h * scale + chrome));
        ClampToViewport();
    }
}
