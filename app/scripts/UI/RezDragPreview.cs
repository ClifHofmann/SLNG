using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Drag-and-drop preview attached to the mouse cursor when dragging a rezzable inventory object (FEAT-INV-15).
/// Shows the item's icon and name and live feedback on whether a drop at the cursor would rez. Only used for
/// objects -- every other inventory drag keeps the plain label preview.
/// </summary>
public partial class RezDragPreview : Control
{
    public static RezDragPreview? Active { get; private set; }

    private readonly PanelContainer _card;
    private readonly StyleBoxFlat _styleBox;
    private readonly Label _statusLabel;

    /// <param name="name">The item's name.</param>
    /// <param name="assetType">The item's asset type: the icon is the one its inventory row shows.</param>
    public RezDragPreview(string name, int assetType)
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Active = this;
        TreeExited += () =>
        {
            if (Active == this) Active = null;
        };

        // Offset from mouse cursor hotspot so the cursor arrow/cross remains clearly visible
        Position = new Vector2(18, 18);

        _card = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };

        _styleBox = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.09f, 0.15f, 0.94f),
            BorderColor = new Color(0.2f, 0.75f, 0.9f, 0.85f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            ShadowColor = new Color(0, 0, 0, 0.55f),
            ShadowSize = 8,
            ShadowOffset = new Vector2(0, 2),
        };
        _card.AddThemeStyleboxOverride("panel", _styleBox);

        var vbox = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddThemeConstantOverride("separation", 2);

        var topRow = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
        };
        topRow.AddThemeConstantOverride("separation", 6);

        var iconLabel = new Label
        {
            Text = InventoryIcons.ForAssetType(assetType),
            MouseFilter = MouseFilterEnum.Ignore,
        };

        var titleLabel = new Label
        {
            Text = name,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        titleLabel.AddThemeFontSizeOverride("font_size", 13);
        titleLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 1f));

        topRow.AddChild(iconLabel);
        topRow.AddChild(titleLabel);
        vbox.AddChild(topRow);

        _statusLabel = new Label
        {
            Text = L10n.Tr("ui.rez.drag_hint"),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 10);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.35f, 0.8f, 1f));
        vbox.AddChild(_statusLabel);

        _card.AddChild(vbox);
        AddChild(_card);
    }

    /// <summary>
    /// Updates the preview's status text and border colour from what a drop at the cursor would do.
    /// </summary>
    public void UpdateStatus(string status, Color textColor, Color borderColor)
    {
        _statusLabel.Text = status;
        _statusLabel.AddThemeColorOverride("font_color", textColor);
        _styleBox.BorderColor = borderColor;
    }
}
