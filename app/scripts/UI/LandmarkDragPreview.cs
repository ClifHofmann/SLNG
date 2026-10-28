using System;
using Godot;
using SLNG.Core.Landmarks;

namespace SLNG.App.UI;

/// <summary>
/// The card at the mouse cursor while a landmark is dragged. It shows the landmark; while the cursor is over the favorites bar it
/// also says what a drop there will do -- "Drop to add to Favorites", "Already a favorite", or why nothing can be added -- and
/// takes the colour of that answer, so the person sees the destination before letting go. Anywhere else it is just the landmark.
///
/// <para>Godot only tells a drop TARGET about the drag, never the preview, and a target is only asked while the cursor is on it,
/// with no call for "left". So the card asks the bar itself every frame (<see cref="FavoritesBar.Current"/>), which is also why it
/// needs no hook in whichever list the drag started from.</para>
/// </summary>
public partial class LandmarkDragPreview : Control
{
    private static readonly Color Neutral = new(0.2f, 0.75f, 0.9f, 0.85f);

    private readonly Guid _itemId;
    private readonly Guid _assetId;
    private readonly StyleBoxFlat _style;
    private readonly Label _status;

    /// <param name="name">The landmark's name.</param>
    /// <param name="itemId">Its inventory item id.</param>
    /// <param name="assetId">Its landmark asset id when the source knows it (the same place may already be a favorite under another item).</param>
    public LandmarkDragPreview(string name, Guid itemId, Guid assetId)
    {
        _itemId = itemId;
        _assetId = assetId;
        MouseFilter = MouseFilterEnum.Ignore;
        Position = new Vector2(18, 18); // off the hotspot, so the cursor stays visible

        _style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.09f, 0.15f, 0.94f),
            BorderColor = Neutral,
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
        var card = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        card.AddThemeStyleboxOverride("panel", _style);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        var title = new Label { Text = $"📍 {name}", MouseFilter = MouseFilterEnum.Ignore };
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 1f));
        box.AddChild(title);

        _status = new Label { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _status.AddThemeFontSizeOverride("font_size", 10);
        box.AddChild(_status);

        card.AddChild(box);
        AddChild(card);
    }

    public override void _Process(double delta)
    {
        var bar = FavoritesBar.Current;
        if (bar == null || !bar.IsVisibleInTree() || !bar.GetGlobalRect().HasPoint(bar.GetGlobalMousePosition()))
        {
            Show(null);
            return;
        }
        Show(bar.DropStatusFor(_itemId, _assetId));
    }

    private void Show(FavoritesBar.DropStatus? status)
    {
        _status.Visible = status != null;
        if (status is not { } s)
        {
            _style.BorderColor = Neutral;
            return;
        }
        _status.Text = s.Text;
        _status.AddThemeColorOverride("font_color", s.TextColor);
        _style.BorderColor = s.Border;
    }
}
