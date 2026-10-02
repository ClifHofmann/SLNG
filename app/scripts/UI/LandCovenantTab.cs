using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-LAND-05: the read-only "Covenant" tab of the Land-Info window (Firestorm's About Land ->
/// Covenant). The estate covenant is the same for every parcel of a region, so the tab does not draw
/// anything itself: it hosts the shared <see cref="CovenantView"/> and tells it which region the window
/// is showing. The parcel itself is not used beyond its region.
/// </summary>
public partial class LandCovenantTab : MarginContainer, ILandInfoTab
{
    private readonly CovenantView _view = new();

    string ILandInfoTab.TabTitle => L10n.Tr("ui.land.tab_covenant");

    /// <summary>The shared view, for the selftest.</summary>
    internal CovenantView View => _view;

    /// <summary>Sets how the estate owner's id becomes a name.</summary>
    internal void Initialize(LandInfoFormat.NameLookup lookup) => _view.Initialize(lookup);

    /// <summary>Hands the session to the view; it asks for the covenant when a region is shown.</summary>
    internal void Bind(GridSession? session) => _view.Bind(session);

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            AddThemeConstantOverride("margin_" + side, 6);
        AddChild(_view);
    }

    /// <inheritdoc/>
    public void ShowParcel(ParcelInfo parcel) => _view.Follow(parcel.RegionHandle);

    /// <inheritdoc/>
    public void RefreshNames() => _view.RefreshNames();
}
