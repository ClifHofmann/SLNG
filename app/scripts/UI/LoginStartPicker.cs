using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core.Login;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-72: the login screen's "Start at" row -- a dropdown (My last location, My home, the profile's Favorites,
/// Region...) with, when Region... is picked, a field for a region name, <c>Region/x/y/z</c> or a pasted SLURL.
///
/// <code>
///  Start at  [ My last location   v ]
///            [ Region/x/y/z or SLURL ]     &lt;- only while "Region..." is picked
///            Not a valid location          &lt;- only while that text is invalid
/// </code>
///
/// <para>A reusable control that knows nothing about <c>logins.cfg</c>, the network or the form around it: the owner
/// hands it the profile's favorites (<see cref="SetFavorites"/>) and the remembered choice (<see cref="SetChoice"/>),
/// reads the pick back with <see cref="Resolve"/>, and gates its Login button on <see cref="IsValid"/>. Everything it
/// shows comes from local data, so it never waits on anything.</para>
///
/// <para>Never a silent fall-back: an invalid typed region is said so in red and keeps <see cref="IsValid"/> false
/// (the owner disables Login), instead of quietly logging in at "last". A remembered favorite that is no longer in the
/// list is the one quiet case -- the dropdown shows My last location and <see cref="FallbackReason"/> says why, so the
/// owner can log it.</para>
/// </summary>
public partial class LoginStartPicker : VBoxContainer
{
    /// <summary>The choice or its validity changed (a pick, typing in the region field, or the owner setting it).</summary>
    public event Action? ChoiceChanged;

    /// <summary><c>Enter</c> was pressed in the region field. The owner decides whether a login may start.</summary>
    public event Action? Submitted;

    private static readonly Color HintColor = new(0.94f, 0.45f, 0.45f);
    private static readonly Color LabelColor = new(0.815686f, 0.835294f, 0.858824f);

    private const string MetaLast = "last";
    private const string MetaHome = "home";
    private const string MetaRegion = "region";
    private const string MetaFavoritePrefix = "fav:";

    private readonly Label _label = new();
    private readonly OptionButton _combo = new();
    private readonly Control _regionSpacer = new();
    private readonly LineEdit _regionEdit = new();
    private readonly Control _hintSpacer = new();
    private readonly Label _hint = new();

    private IReadOnlyList<LoginFavorite> _favorites = Array.Empty<LoginFavorite>();
    private IReadOnlyList<LoginOfflineFavorite> _offline = Array.Empty<LoginOfflineFavorite>();
    private bool _neverPopulated;
    private StartChoice _choice = StartChoice.Last;
    private string _typedRegion = "";
    private int _maxXY = StartTarget.LindenMaxXY;
    private bool _settingText;

    /// <summary>The current pick. For a region it carries the text as typed (possibly empty).</summary>
    public StartChoice Choice => _choice;

    /// <summary>Why the choice last handed to <see cref="SetChoice"/> was changed: a favorite that is not in the list
    /// is shown as My last location and reported as <see cref="StartFallback.FavoriteMissing"/>.</summary>
    public StartFallback FallbackReason { get; private set; } = StartFallback.None;

    /// <summary>X and Y limit applied to typed text (<see cref="StartTarget.LindenMaxXY"/> on a Linden grid, otherwise
    /// <see cref="StartTarget.OpenMaxXY"/>). The owner sets it from the grid selected on the form.</summary>
    public int RegionMaxXY
    {
        get => _maxXY;
        set
        {
            if (value == _maxXY) return;
            _maxXY = value;
            UpdateRegionRow();
            ChoiceChanged?.Invoke();
        }
    }

    /// <summary>True unless Region... is picked with text that is not a location. Empty text counts as invalid (there is
    /// nothing to log in at) but does not show the red line yet.</summary>
    public bool IsValid => _choice.Kind != StartKind.Region
        || LoginStart.TryParseTyped(_choice.Key, _maxXY, out _);

    /// <summary>The favorite picked, or null when another kind is.</summary>
    public LoginFavorite? SelectedFavorite
    {
        get
        {
            if (_choice.Kind != StartKind.Favorite) return null;
            Guid asset = _choice.FavoriteAssetId;
            foreach (var f in _favorites)
                if (f.AssetId == asset) return f;
            return null;
        }
    }

    public LoginStartPicker()
    {
        AddThemeConstantOverride("separation", 6);

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        AddChild(grid);

        _label.Text = L10n.Tr("ui.login.start_location");
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.AddThemeColorOverride("font_color", LabelColor);
        grid.AddChild(_label);

        _combo.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _combo.ClipText = true;
        _combo.FitToLongestItem = false;
        _combo.TooltipText = L10n.Tr("ui.login.start_location");
        _combo.ItemSelected += OnItemSelected;
        grid.AddChild(_combo);

        // The two rows below are a pair each (an empty cell under the label, then the content); a GridContainer skips
        // invisible children, so each pair is shown and hidden together to keep the columns lined up.
        grid.AddChild(_regionSpacer);
        _regionEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _regionEdit.PlaceholderText = L10n.Tr("ui.login.start_region_placeholder");
        _regionEdit.TextChanged += OnRegionTextChanged;
        _regionEdit.TextSubmitted += _ => Submitted?.Invoke();
        grid.AddChild(_regionEdit);

        grid.AddChild(_hintSpacer);
        _hint.Text = L10n.Tr("ui.login.start_region_invalid");
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _hint.AddThemeColorOverride("font_color", HintColor);
        _hint.AddThemeFontSizeOverride("font_size", 12);
        grid.AddChild(_hint);

        Rebuild();
        UpdateRegionRow();
    }

    /// <summary>
    /// Copies the glass look of the form's own controls: <paramref name="dropdown"/>'s overridden styleboxes and font
    /// colour go to this dropdown, and <paramref name="input"/>'s to the region field. Only what the source overrides is
    /// copied, so a control the theme styles is left to the theme exactly like its neighbours.
    /// </summary>
    public void ApplyStylesFrom(OptionButton dropdown, LineEdit? input = null)
    {
        CopyOverrides(dropdown, _combo, new[] { "normal", "hover", "pressed", "focus", "disabled" }, new[] { "font_color" });
        if (input != null)
            CopyOverrides(input, _regionEdit, new[] { "normal", "focus", "read_only" }, new[] { "font_color", "font_placeholder_color" });
    }

    private static void CopyOverrides(Control from, Control to, string[] styleboxes, string[] colors)
    {
        foreach (string name in styleboxes)
            if (from.HasThemeStyleboxOverride(name))
                to.AddThemeStyleboxOverride(name, from.GetThemeStylebox(name));
        foreach (string name in colors)
            if (from.HasThemeColorOverride(name))
                to.AddThemeColorOverride(name, from.GetThemeColor(name));
    }

    /// <summary>
    /// Sets the profile's favorites and rebuilds the dropdown. <paramref name="neverPopulated"/> is true for a profile that
    /// exists but whose favorites were never written (the first login after the upgrade): the list then shows one disabled
    /// line saying they will appear after the next login. A choice that is a favorite no longer in the list becomes My last
    /// location (see <see cref="FallbackReason"/>).
    /// </summary>
    public void SetFavorites(IReadOnlyList<LoginFavorite> favorites, bool neverPopulated,
        IReadOnlyList<LoginOfflineFavorite>? offline = null)
    {
        _favorites = favorites ?? Array.Empty<LoginFavorite>();
        _offline = offline ?? Array.Empty<LoginOfflineFavorite>();
        _neverPopulated = neverPopulated && _favorites.Count == 0 && _offline.Count == 0;

        var before = _choice;
        if (_choice.Kind == StartKind.Favorite && SelectedFavorite == null)
        {
            _choice = StartChoice.Last;
            FallbackReason = StartFallback.FavoriteMissing;
        }

        Rebuild();
        UpdateRegionRow();
        if (before != _choice) ChoiceChanged?.Invoke();
    }

    /// <summary>Selects <paramref name="choice"/> (the remembered one). A favorite that is not in the current list selects My
    /// last location instead and sets <see cref="FallbackReason"/>; a region fills the text field.</summary>
    public void SetChoice(StartChoice choice)
    {
        FallbackReason = StartFallback.None;
        var before = _choice;

        switch (choice.Kind)
        {
            case StartKind.Favorite:
                _choice = choice;
                if (SelectedFavorite == null)
                {
                    _choice = StartChoice.Last;
                    FallbackReason = StartFallback.FavoriteMissing;
                }
                break;

            case StartKind.Region:
                _choice = choice;
                _typedRegion = choice.Key;
                SetRegionFieldText(_typedRegion);
                break;

            case StartKind.Home:
                _choice = StartChoice.Home;
                break;

            default:
                _choice = StartChoice.Last;
                break;
        }

        SyncSelection();
        UpdateRegionRow();
        if (before != _choice) ChoiceChanged?.Invoke();
    }

    /// <summary>What to send for the current pick; see <see cref="LoginStart.Resolve"/>.</summary>
    public StartResolution Resolve(int maxXY) => LoginStart.Resolve(_choice, _favorites, maxXY);

    /// <summary>Re-reads every string from the current language. The login screen is built once, and the language can be
    /// changed in Preferences while a session runs; the owner calls this when the screen comes back.</summary>
    public void RefreshTexts()
    {
        _label.Text = L10n.Tr("ui.login.start_location");
        _combo.TooltipText = L10n.Tr("ui.login.start_location");
        _regionEdit.PlaceholderText = L10n.Tr("ui.login.start_region_placeholder");
        _hint.Text = L10n.Tr("ui.login.start_region_invalid");
        Rebuild();
    }

    // ---- building ------------------------------------------------------------------------------------------------

    private void Rebuild()
    {
        _combo.Clear();

        AddOption(L10n.Tr("ui.login.start_last"), MetaLast);
        AddOption(L10n.Tr("ui.login.start_home"), MetaHome);

        if (_neverPopulated)
        {
            int pending = _combo.ItemCount;
            _combo.AddItem(L10n.Tr("ui.login.start_favorites_pending"));
            _combo.SetItemDisabled(pending, true);
        }
        else if (_favorites.Count > 0 || _offline.Count > 0)
        {
            _combo.AddSeparator(L10n.Tr("ui.login.start_favorites_heading"));
            foreach (var f in _favorites)
            {
                int index = _combo.ItemCount;
                AddOption(f.Name, MetaFavoritePrefix + f.AssetId.ToString("N"));
                _combo.SetItemTooltip(index, L10n.TrFormat("ui.login.start_favorite_tooltip",
                    f.Name, f.Target.Region, f.Target.X, f.Target.Y, f.Target.Z));
            }

            // FEAT-UI-77: favorites whose region was not answering at the last session. Listed so they are not missed, greyed
            // out because nothing says where they point (no region name), so there is nothing to start at.
            foreach (var o in _offline)
            {
                int index = _combo.ItemCount;
                _combo.AddItem(L10n.TrFormat("ui.login.start_favorite_offline", o.Name));
                _combo.SetItemDisabled(index, true);
                _combo.SetItemTooltip(index, L10n.TrFormat("ui.login.start_favorite_offline_tooltip", o.Name));
            }
        }

        _combo.AddSeparator();
        AddOption(L10n.Tr("ui.login.start_region"), MetaRegion);

        SyncSelection();
    }

    private void AddOption(string text, string meta)
    {
        int index = _combo.ItemCount;
        _combo.AddItem(text);
        _combo.SetItemMetadata(index, meta);
    }

    private static string MetaOf(StartChoice choice) => choice.Kind switch
    {
        StartKind.Home => MetaHome,
        StartKind.Favorite => MetaFavoritePrefix + choice.Key,
        StartKind.Region => MetaRegion,
        _ => MetaLast,
    };

    /// <summary>Selects the dropdown item that belongs to the current choice.</summary>
    private void SyncSelection()
    {
        string meta = MetaOf(_choice);
        for (int i = 0; i < _combo.ItemCount; i++)
        {
            if (_combo.IsItemSeparator(i)) continue;
            var itemMeta = _combo.GetItemMetadata(i);
            if (itemMeta.VariantType == Variant.Type.String && itemMeta.AsString() == meta)
            {
                _combo.Select(i);
                return;
            }
        }
        _combo.Select(0);
    }

    private void UpdateRegionRow()
    {
        bool region = _choice.Kind == StartKind.Region;
        bool showHint = region && !IsValid && _choice.Key.Trim().Length > 0;

        _regionSpacer.Visible = region;
        _regionEdit.Visible = region;
        _hintSpacer.Visible = showHint;
        _hint.Visible = showHint;
    }

    private void SetRegionFieldText(string text)
    {
        if (_regionEdit.Text == text) return;
        _settingText = true;
        try { _regionEdit.Text = text; }
        finally { _settingText = false; }
    }

    // ---- input ---------------------------------------------------------------------------------------------------

    private void OnItemSelected(long index)
    {
        var before = _choice;
        FallbackReason = StartFallback.None;

        var meta = _combo.GetItemMetadata((int)index);
        string key = meta.VariantType == Variant.Type.String ? meta.AsString() : MetaLast;

        if (key == MetaHome) _choice = StartChoice.Home;
        else if (key == MetaRegion) _choice = StartChoice.Region(_typedRegion);
        else if (key.StartsWith(MetaFavoritePrefix, StringComparison.Ordinal)
                 && Guid.TryParseExact(key[MetaFavoritePrefix.Length..], "N", out var asset))
            _choice = StartChoice.Favorite(asset);
        else _choice = StartChoice.Last;

        UpdateRegionRow();
        if (_choice.Kind == StartKind.Region && IsInsideTree()) _regionEdit.GrabFocus();
        if (before != _choice) ChoiceChanged?.Invoke();
    }

    private void OnRegionTextChanged(string text)
    {
        if (_settingText) return;
        _typedRegion = text;
        if (_choice.Kind != StartKind.Region) return;
        _choice = StartChoice.Region(text);
        UpdateRegionRow();
        ChoiceChanged?.Invoke();
    }

    // ---- test hooks (SelfTest.LoginStart) ------------------------------------------------------------------------

    internal int OptionCount => _combo.ItemCount;
    internal string OptionText(int index) => _combo.GetItemText(index);
    internal bool OptionIsSeparator(int index) => _combo.IsItemSeparator(index);
    internal bool OptionIsDisabled(int index) => _combo.IsItemDisabled(index);
    internal int SelectedOption => _combo.Selected;
    internal bool HintVisible => _hint.Visible;
    internal bool RegionFieldVisible => _regionEdit.Visible;

    /// <summary>Does what a click on item <paramref name="index"/> of the popup does.</summary>
    internal void PickOption(int index)
    {
        _combo.Select(index);
        OnItemSelected(index);
    }

    /// <summary>Does what typing <paramref name="text"/> into the region field does.</summary>
    internal void TypeRegion(string text)
    {
        SetRegionFieldText(text);
        OnRegionTextChanged(text);
    }

    /// <summary>Does what <c>Enter</c> in the region field does.</summary>
    internal void PressEnterInRegionField() => Submitted?.Invoke();
}
