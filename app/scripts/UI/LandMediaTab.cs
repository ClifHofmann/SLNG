using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>The text rows of the Media tab, in Firestorm's order.</summary>
internal enum LandMediaRow { Type, HomePage, Description, Texture, Size }

/// <summary>
/// FEAT-LAND-02: the read-only "Media" tab of the Land-Info window (Firestorm's About Land -> Media).
/// Shows the parcel's media URL and how it is presented. Which controls apply to which MIME type
/// (looping for movie/audio, size for web content) is <see cref="LandInfoFormat.ClassifyMime"/>.
/// The tab does not play anything; a parcel with no media says so in one line above the (empty) form.
/// </summary>
public partial class LandMediaTab : ScrollContainer, ILandInfoTab
{
    private readonly Dictionary<LandMediaRow, Control> _rows = new();
    private Label _noMedia = null!;
    private LandReadOnlyCheck _autoScale = null!;
    private LandReadOnlyCheck _loop = null!;

    string ILandInfoTab.TabTitle => L10n.Tr("ui.land.tab_media");

    public override void _Ready()
    {
        var column = LandTabPage.Prepare(this);

        _noMedia = LandTabPage.Note(L10n.Tr("ui.land.media_none"));
        column.AddChild(_noMedia);

        var grid = LandTabPage.NewGrid(column);
        AddRow(grid, LandMediaRow.Type, "ui.land.media_type", LandTabPage.ValueLabel());
        AddRow(grid, LandMediaRow.HomePage, "ui.land.media_home", LandTabPage.SelectableValue()); // a URL is something to copy
        AddRow(grid, LandMediaRow.Description, "ui.land.media_description", LandTabPage.ValueLabel());
        AddRow(grid, LandMediaRow.Texture, "ui.land.media_texture", LandTabPage.SelectableValue());
        AddRow(grid, LandMediaRow.Size, "ui.land.media_size", LandTabPage.ValueLabel());

        var flags = new VBoxContainer();
        flags.AddThemeConstantOverride("separation", 4);
        column.AddChild(flags);
        _autoScale = new LandReadOnlyCheck(L10n.Tr("ui.land.media_autoscale"));
        _loop = new LandReadOnlyCheck(L10n.Tr("ui.land.media_loop"));
        flags.AddChild(_autoScale);
        flags.AddChild(_loop);

        Clear();
    }

    /// <inheritdoc/>
    public void ShowParcel(ParcelInfo p)
    {
        var m = p.Media;
        _noMedia.Visible = !m.HasMedia;
        LandTabPage.SetText(_rows[LandMediaRow.Type], LandInfoFormat.MimeText(m.MimeType));
        LandTabPage.SetText(_rows[LandMediaRow.HomePage], LandInfoFormat.OrDash(m.Url));
        LandTabPage.SetText(_rows[LandMediaRow.Description], LandInfoFormat.OrDash(m.Description));
        LandTabPage.SetText(_rows[LandMediaRow.Texture], LandInfoFormat.TextureIdText(m.TextureId));
        LandTabPage.SetText(_rows[LandMediaRow.Size], LandInfoFormat.SizeText(m));
        _autoScale.Show(m.AutoScale);
        _loop.Show(LandInfoFormat.LoopTicked(m), LandInfoFormat.LoopApplies(m));
    }

    /// <inheritdoc/>
    public void RefreshNames() { } // nothing here is a name

    // --- for the selftest ----------------------------------------------------------------------------

    internal string RowText(LandMediaRow row) => LandTabPage.Text(_rows[row]);

    internal bool NoMediaVisible => _noMedia.Visible;

    internal string NoMediaText => _noMedia.Text;

    internal LandReadOnlyCheck AutoScale => _autoScale;

    internal LandReadOnlyCheck Loop => _loop;

    private void Clear()
    {
        foreach (var row in _rows.Values) LandTabPage.SetText(row, LandInfoFormat.Unknown);
        _autoScale.Show(false, false);
        _loop.Show(false, false);
    }

    private void AddRow(GridContainer grid, LandMediaRow row, string keyId, Control value)
    {
        _rows[row] = value;
        LandTabPage.AddRow(grid, L10n.Tr(keyId), value);
    }
}
