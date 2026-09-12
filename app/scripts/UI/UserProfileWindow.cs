using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-13: a Firestorm-style avatar profile window. For another resident it shows the
/// "2nd Life" / "1st Life" pages, their Picks (with per-pick image / text / teleport) and
/// Classifieds, and a local-only Notes field, plus the social actions (Add Friend, IM, Pay,
/// Offer Teleport, Block/Mute). For <b>your own</b> avatar the 2nd Life / 1st Life / Interests
/// fields become editable and a "Save Profile" button writes them back
/// (<c>AvatarPropertiesUpdate</c> / <c>AvatarInterestsUpdate</c>); the social actions are hidden.
///
/// One instance per avatar, keyed by agent id in <see cref="Boot"/> (same multi-instance pattern
/// as <see cref="ObjectEditWindow"/>). The tab/action layout depends on whether this is the local
/// agent, which is only known in <see cref="Initialize"/> — so the pages are built there, not in
/// <see cref="_Ready"/>. Boot marshals every <see cref="GridSession"/> profile event to the main
/// thread before calling the Apply* methods here. All user-facing strings come from
/// <c>ui.profile.*</c> in the i18n bundle.
/// </summary>
public partial class UserProfileWindow : SLNGWindow
{
    private const string NotesConfigPath = "user://preferences.cfg";
    private const string NotesSection = "avatar_notes";
    private const double NotesSaveDebounceSec = 1.0;

    private Guid _agentId;
    private string _agentName = "";
    private bool _isSelf;
    private GridSession? _session;
    private GpuCache? _gpuCache;
    private SLNG.Assets.AssetService? _assetService;

    private AvatarProfileProperties? _lastProps;
    private bool _editFieldsSeeded;

    // Which texture id is currently pinned (GpuCache.AddRef'd) in each picture slot -- see
    // RepinTexture's doc comment for why this exists.
    private Guid _pinnedProfilePicId = Guid.Empty;
    private Guid _pinnedFirstLifePicId = Guid.Empty;
    private Guid _pinnedPickSnapshotId = Guid.Empty;

    // Shell
    private VBoxContainer _root = null!;
    private Control _tabHost = null!;
    private HBoxContainer _tabStrip = null!;
    private VBoxContainer _actionHost = null!;
    private readonly List<(Button Btn, Control Page)> _tabs = new();

    // Header
    private TextureRect _profilePic = null!;
    private Label _nameLabel = null!;
    private Label _idLabel = null!;
    private Label _bornLabel = null!;

    // Shared
    private Label _accountLabel = null!;
    private Label _partnerLabel = null!;
    private Guid _partnerId;
    private VBoxContainer _groupsList = null!;
    private TextureRect _firstLifePic = null!;
    private VBoxContainer _classifiedsList = null!;
    private Label _statusLabel = null!;

    // Picks (view mode)
    private VBoxContainer _picksList = null!;
    private VBoxContainer _pickDetail = null!;
    private TextureRect _pickSnapshot = null!;
    private Label _pickTitleValue = null!;
    private RichTextLabel _pickDescValue = null!;
    private Label _pickLocationValue = null!;
    private Guid _selectedPickId;
    private AvatarPickDetail? _selectedPickDetail;
    private readonly Dictionary<Guid, Button> _pickButtons = new();

    // View mode (another resident)
    private RichTextLabel? _aboutView;
    private RichTextLabel? _firstLifeView;
    private Label? _webView;
    private Label? _interestsView;

    // Edit mode (your own profile)
    private TextEdit? _aboutEdit;
    private TextEdit? _firstLifeEdit;
    private LineEdit? _webEdit;
    private CheckBox? _allowPublishCheck;
    private CheckBox? _maturePublishCheck;
    private LineEdit? _langEdit;
    private LineEdit? _skillsEdit;
    private LineEdit? _wantToEdit;

    // Notes (view mode only)
    private TextEdit? _notesEdit;
    private double _notesSaveTimer = -1.0;
    private bool _notesLoaded;

    // Social actions (view mode only)
    private Button? _addFriendBtn;
    private Button? _muteBtn;
    private HBoxContainer? _payRow;
    private SpinBox? _paySpin;

    /// <summary>Wired by Boot to <c>ChatWindow.OpenOrFocusImTab</c> — the "IM" action button.</summary>
    public Action<Guid, string>? OnOpenImRequested;

    /// <summary>Fired from the close button so Boot can drop this window from its per-agent map.</summary>
    public Action? Closed;

    /// <summary>Set by Boot before the first show so several open profiles cascade instead of
    /// stacking exactly on top of each other.</summary>
    public int CascadeIndex { get; set; }

    public Guid AgentId => _agentId;

    private static string Tr(string key) => L10n.Tr($"ui.profile.{key}");
    private static string TrF(string key, params object[] args) => L10n.TrFormat($"ui.profile.{key}", args);

    public override void _Ready()
    {
        base._Ready();

        Title = Tr("title");
        CustomMinimumSize = new Vector2(360, 430);
        Size = new Vector2(420, 560);
        OnCloseRequested = RequestClose;

        // SLNGWindow.ContentContainer has no inner padding of its own, so without this the header,
        // labels and list rows sit flush against the window's left edge.
        // Inset comes from SLNGWindow.ContentContainer now (FEAT-UI-26); this container is kept
        // only because the layout below hangs off it.
        var pad = new MarginContainer();
        ContentContainer.AddChild(pad);

        _root = new VBoxContainer();
        _root.AddThemeConstantOverride("separation", 8);
        pad.AddChild(_root);

        BuildHeader(_root);
        BuildTabStrip(_root);

        _tabHost = new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _root.AddChild(_tabHost);

        _root.AddChild(new HSeparator());
        _actionHost = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _actionHost.AddThemeConstantOverride("separation", 4);
        _root.AddChild(_actionHost);
    }

    public override void _ExitTree()
    {
        FlushNotes();
        // Give back every GpuCache ref this window pinned -- see RepinTexture's doc comment.
        // Without this, closing the window (or QueueFree on relogin) would leave these entries
        // pinned in GpuCache forever, since nothing else would ever call ReleaseRef for them.
        if (_gpuCache != null)
        {
            if (_pinnedProfilePicId != Guid.Empty) _gpuCache.ReleaseRef(_pinnedProfilePicId);
            if (_pinnedFirstLifePicId != Guid.Empty) _gpuCache.ReleaseRef(_pinnedFirstLifePicId);
            if (_pinnedPickSnapshotId != Guid.Empty) _gpuCache.ReleaseRef(_pinnedPickSnapshotId);
        }
        base._ExitTree();
    }

    /// <summary>Boot calls this once, right after construction, before requesting the profile.</summary>
    public void Initialize(Guid agentId, string initialName, GridSession session,
        GpuCache? gpuCache, SLNG.Assets.AssetService? assetService)
    {
        _agentId = agentId;
        _agentName = initialName ?? "";
        _session = session;
        _gpuCache = gpuCache;
        _assetService = assetService;
        _isSelf = session != null && Guid.TryParse(session.AgentId, out var me) && me == agentId;

        BuildTabs();
        BuildActions();

        ApplyCascade();
        UpdateNameHeader();
        _idLabel.Text = agentId.ToString();
        if (!_isSelf) LoadNotes();

        _session?.RequestAvatarProfile(agentId);
        _session?.RequestAvatarName(agentId);
    }

    // ---- GridSession event sinks (Boot marshals to the main thread first) --------------------

    public void ApplyProperties(AvatarProfileProperties p)
    {
        _lastProps = p;

        _accountLabel.Text = string.IsNullOrWhiteSpace(p.CharterMember) ? Tr("account_resident") : p.CharterMember;
        _bornLabel.Text = string.IsNullOrWhiteSpace(p.BornOn) ? "" : TrF("born", p.BornOn);

        _partnerId = p.PartnerId;
        if (p.PartnerId == Guid.Empty)
            _partnerLabel.Text = Tr("partner_none");
        else if (_session != null && _session.TryGetCachedName(p.PartnerId, out var pn))
            _partnerLabel.Text = TrF("partner_label", pn);
        else
        {
            _partnerLabel.Text = Tr("partner_loading");
            _session?.RequestAvatarName(p.PartnerId);
        }

        RepinTexture(ref _pinnedProfilePicId, p.ProfileImageId);
        LoadTextureInto(p.ProfileImageId, _profilePic);
        RepinTexture(ref _pinnedFirstLifePicId, p.FirstLifeImageId);
        LoadTextureInto(p.FirstLifeImageId, _firstLifePic);

        if (_isSelf)
        {
            // Seed the editable fields from the first server copy only, so a second reply landing
            // mid-edit doesn't wipe what the user just typed.
            if (!_editFieldsSeeded)
            {
                _editFieldsSeeded = true;
                if (_aboutEdit != null) _aboutEdit.Text = p.AboutText;
                if (_firstLifeEdit != null) _firstLifeEdit.Text = p.FirstLifeText;
                if (_webEdit != null) _webEdit.Text = p.ProfileUrl;
                if (_allowPublishCheck != null) _allowPublishCheck.ButtonPressed = p.AllowPublish;
                if (_maturePublishCheck != null) _maturePublishCheck.ButtonPressed = p.MaturePublish;
            }
            return;
        }

        if (_aboutView != null)
            _aboutView.Text = string.IsNullOrWhiteSpace(p.AboutText) ? Tr("no_description") : p.AboutText;
        if (_firstLifeView != null)
            _firstLifeView.Text = string.IsNullOrWhiteSpace(p.FirstLifeText) ? Tr("no_first_life") : p.FirstLifeText;
        if (_webView != null)
        {
            _webView.Text = p.ProfileUrl ?? "";
            _webView.Visible = !string.IsNullOrWhiteSpace(p.ProfileUrl);
        }
    }

    public void ApplyInterests(AvatarProfileInterests i)
    {
        if (_isSelf)
        {
            // Seed each field only while still empty, so a duplicate/late reply can't overwrite
            // something the user has started typing.
            if (_langEdit != null && string.IsNullOrEmpty(_langEdit.Text)) _langEdit.Text = i.LanguagesText;
            if (_skillsEdit != null && string.IsNullOrEmpty(_skillsEdit.Text)) _skillsEdit.Text = i.SkillsText;
            if (_wantToEdit != null && string.IsNullOrEmpty(_wantToEdit.Text)) _wantToEdit.Text = i.WantToText;
            return;
        }

        if (_interestsView == null) return;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(i.WantToText)) parts.Add(TrF("interests_wants_to", i.WantToText));
        if (!string.IsNullOrWhiteSpace(i.SkillsText)) parts.Add(TrF("interests_skills", i.SkillsText));
        if (!string.IsNullOrWhiteSpace(i.LanguagesText)) parts.Add(TrF("interests_languages", i.LanguagesText));
        _interestsView.Text = parts.Count == 0 ? "" : string.Join("\n", parts);
        _interestsView.Visible = parts.Count > 0;
    }

    public void ApplyGroups(IReadOnlyList<AvatarProfileGroup> groups)
    {
        ClearChildren(_groupsList);
        if (groups.Count == 0)
        {
            _groupsList.AddChild(MutedLine(Tr("no_groups")));
            return;
        }
        foreach (var g in groups)
            _groupsList.AddChild(BodyLine(string.IsNullOrWhiteSpace(g.Name) ? g.GroupId.ToString() : g.Name));
    }

    public void ApplyPicks(IReadOnlyList<AvatarPickInfo> picks)
    {
        ClearChildren(_picksList);
        _pickButtons.Clear();
        _pickDetail.Visible = false;
        _selectedPickId = Guid.Empty;

        if (picks.Count == 0)
        {
            _picksList.AddChild(MutedLine(Tr("no_picks")));
            return;
        }

        foreach (var pick in picks)
        {
            var id = pick.PickId;
            var btn = new Button
            {
                Text = string.IsNullOrWhiteSpace(pick.Name) ? Tr("unnamed") : pick.Name,
                Flat = true,
                ClipText = true,
                FocusMode = FocusModeEnum.None,
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () => SelectPick(id);
            _picksList.AddChild(btn);
            _pickButtons[id] = btn;
        }

        _picksList.AddChild(MutedLine(Tr("pick_select_hint")));

        if (picks.Count == 1) SelectPick(picks[0].PickId);
    }

    private void SelectPick(Guid pickId)
    {
        _selectedPickId = pickId;
        _selectedPickDetail = null;
        foreach (var (id, btn) in _pickButtons)
            btn.AddThemeColorOverride("font_color",
                id == pickId ? new Color(1, 1, 1) : new Color(0.8f, 0.8f, 0.8f));

        _pickDetail.Visible = true;
        _pickSnapshot.Texture = null;
        // Release the previous pick's snapshot ref now that we're done showing it -- ApplyPickDetail
        // pins the new one once its id is known.
        RepinTexture(ref _pinnedPickSnapshotId, Guid.Empty);
        _pickTitleValue.Text = Tr("loading");
        _pickDescValue.Text = "";
        _pickLocationValue.Text = "";
        _session?.RequestAvatarPickInfo(_agentId, pickId);
    }

    public void ApplyPickDetail(AvatarPickDetail d)
    {
        if (d.PickId != _selectedPickId) return; // a pick this window didn't ask for
        _selectedPickDetail = d;
        _pickTitleValue.Text = string.IsNullOrWhiteSpace(d.Name) ? Tr("unnamed") : d.Name;
        _pickDescValue.Text = d.Description ?? "";
        _pickLocationValue.Text = string.IsNullOrWhiteSpace(d.SimName)
            ? ""
            : $"{d.SimName} ({d.GlobalX % 256:0}, {d.GlobalY % 256:0}, {d.GlobalZ:0})";
        RepinTexture(ref _pinnedPickSnapshotId, d.SnapshotId);
        LoadTextureInto(d.SnapshotId, _pickSnapshot);
    }

    public void ApplyClassifieds(IReadOnlyList<AvatarClassifiedInfo> ads) =>
        FillNameList(_classifiedsList, Tr("no_classifieds"), ads.Count, i => ads[i].Name);

    /// <summary>Boot relays GridSession.NameResolved / DisplayNameResolved here so the header and
    /// the partner line fill in once their UUIDs resolve.</summary>
    public void OnNameResolved(Guid id, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (id == _agentId)
        {
            _agentName = name;
            UpdateNameHeader();
        }
        else if (id == _partnerId)
        {
            _partnerLabel.Text = TrF("partner_label", name);
        }
    }

    // ---- Header -----------------------------------------------------------------------------

    private void BuildHeader(Control parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        parent.AddChild(row);

        var picFrame = new PanelContainer { CustomMinimumSize = new Vector2(76, 76) };
        picFrame.AddThemeStyleboxOverride("panel", RoundedPanel(new Color(0, 0, 0, 0.35f)));
        _profilePic = new TextureRect
        {
            CustomMinimumSize = new Vector2(72, 72),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        picFrame.AddChild(_profilePic);
        row.AddChild(picFrame);

        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 2);
        row.AddChild(col);

        _nameLabel = new Label { Text = "…", ClipText = true };
        _nameLabel.AddThemeFontSizeOverride("font_size", 16);
        _nameLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.95f));
        col.AddChild(_nameLabel);

        _idLabel = new Label { Text = "" };
        _idLabel.AddThemeFontSizeOverride("font_size", 9);
        _idLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        col.AddChild(_idLabel);

        _bornLabel = new Label { Text = "" };
        _bornLabel.AddThemeFontSizeOverride("font_size", 10);
        _bornLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        col.AddChild(_bornLabel);
    }

    private void UpdateNameHeader()
    {
        _nameLabel.Text = string.IsNullOrWhiteSpace(_agentName) ? Tr("unknown_avatar") : _agentName;
        if (!string.IsNullOrWhiteSpace(_agentName))
            Title = _agentName.ToUpperInvariant();
    }

    // ---- Tabs ------------------------------------------------------------------------------

    private void BuildTabStrip(Control parent)
    {
        var stripPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, 34) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.03f),
            BorderWidthBottom = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
            ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        stripPanel.AddThemeStyleboxOverride("panel", style);
        parent.AddChild(stripPanel);

        _tabStrip = new HBoxContainer();
        _tabStrip.AddThemeConstantOverride("separation", 2);
        stripPanel.AddChild(_tabStrip);
    }

    private void BuildTabs()
    {
        AddTab(Tr("tab_2nd_life"), _isSelf ? BuildSecondLifeEditPage() : BuildSecondLifeViewPage());
        AddTab(Tr("tab_1st_life"), _isSelf ? BuildFirstLifeEditPage() : BuildFirstLifeViewPage());
        AddTab(Tr("tab_picks"), BuildPicksPage());
        AddTab(Tr("tab_classifieds"), BuildListPage(out _classifiedsList));
        if (!_isSelf) AddTab(Tr("tab_notes"), BuildNotesPage());
        SelectTab(0);
    }

    private void AddTab(string name, Control page)
    {
        int index = _tabs.Count;
        page.Visible = index == 0;
        page.SetAnchorsPreset(LayoutPreset.FullRect);
        _tabHost.AddChild(page);

        var btn = new Button
        {
            Text = name,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 24),
            ClipText = true,
        };
        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.Pressed += () => SelectTab(index);
        _tabStrip.AddChild(btn);

        _tabs.Add((btn, page));
    }

    private void SelectTab(int index)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            bool sel = i == index;
            _tabs[i].Page.Visible = sel;
            _tabs[i].Btn.AddThemeColorOverride("font_color",
                sel ? new Color(1, 1, 1) : new Color(0.68f, 0.68f, 0.68f));
        }
    }

    private static (ScrollContainer scroll, VBoxContainer box) ScrollPage()
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        return (scroll, box);
    }

    private Control BuildSecondLifeViewPage()
    {
        var (scroll, box) = ScrollPage();

        _aboutView = BodyRichText();
        _aboutView.Text = Tr("loading");
        box.AddChild(_aboutView);

        box.AddChild(new HSeparator());

        _accountLabel = BodyLine(Tr("account_resident"));
        box.AddChild(_accountLabel);
        _partnerLabel = BodyLine(Tr("partner_loading"));
        box.AddChild(_partnerLabel);
        _webView = BodyLine("");
        _webView.Visible = false;
        box.AddChild(_webView);

        _interestsView = BodyLine("");
        _interestsView.Visible = false;
        box.AddChild(_interestsView);

        box.AddChild(new HSeparator());
        box.AddChild(SectionLabel(Tr("groups")));
        _groupsList = GroupsListBox();
        box.AddChild(_groupsList);

        return scroll;
    }

    private Control BuildSecondLifeEditPage()
    {
        var (scroll, box) = ScrollPage();

        box.AddChild(SectionLabel(Tr("edit_about")));
        _aboutEdit = MultilineEdit(90, Tr("edit_about_placeholder"));
        box.AddChild(_aboutEdit);

        box.AddChild(SectionLabel(Tr("edit_web_url")));
        _webEdit = new LineEdit { PlaceholderText = "https://…" };
        _webEdit.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_webEdit);

        _allowPublishCheck = new CheckBox { Text = Tr("edit_show_in_search") };
        _allowPublishCheck.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_allowPublishCheck);
        _maturePublishCheck = new CheckBox { Text = Tr("edit_mature") };
        _maturePublishCheck.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_maturePublishCheck);

        box.AddChild(new HSeparator());
        box.AddChild(SectionLabel(Tr("edit_interests")));
        _langEdit = LabelledLine(box, Tr("edit_languages"));
        _skillsEdit = LabelledLine(box, Tr("edit_skills"));
        _wantToEdit = LabelledLine(box, Tr("edit_want_to"));

        box.AddChild(new HSeparator());
        box.AddChild(SectionLabel(Tr("groups")));
        _groupsList = GroupsListBox();
        box.AddChild(_groupsList);

        box.AddChild(new HSeparator());
        _accountLabel = BodyLine(Tr("account_resident"));
        box.AddChild(_accountLabel);
        _partnerLabel = BodyLine(Tr("partner_loading"));
        box.AddChild(_partnerLabel);

        return scroll;
    }

    private Control BuildFirstLifeViewPage()
    {
        var (scroll, box) = ScrollPage();
        box.AddChild(FirstLifePicFrame());
        _firstLifeView = BodyRichText();
        _firstLifeView.Text = Tr("loading");
        box.AddChild(_firstLifeView);
        return scroll;
    }

    private Control BuildFirstLifeEditPage()
    {
        var (scroll, box) = ScrollPage();
        box.AddChild(FirstLifePicFrame());
        box.AddChild(MutedLine(Tr("edit_picture_unavailable")));
        box.AddChild(SectionLabel(Tr("edit_1st_life")));
        _firstLifeEdit = MultilineEdit(120, Tr("edit_first_life_placeholder"));
        box.AddChild(_firstLifeEdit);
        return scroll;
    }

    private Control BuildPicksPage()
    {
        var (scroll, box) = ScrollPage();

        _picksList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _picksList.AddThemeConstantOverride("separation", 2);
        _picksList.AddChild(MutedLine(Tr("loading")));
        box.AddChild(_picksList);

        _pickDetail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        _pickDetail.AddThemeConstantOverride("separation", 4);
        box.AddChild(_pickDetail);

        _pickDetail.AddChild(new HSeparator());

        var picFrame = new PanelContainer { CustomMinimumSize = new Vector2(0, 130) };
        picFrame.AddThemeStyleboxOverride("panel", RoundedPanel(new Color(0, 0, 0, 0.35f)));
        _pickSnapshot = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(0, 126),
        };
        picFrame.AddChild(_pickSnapshot);
        _pickDetail.AddChild(picFrame);

        _pickDetail.AddChild(SectionLabel(Tr("pick_title")));
        _pickTitleValue = BodyLine("");
        _pickDetail.AddChild(_pickTitleValue);

        _pickDetail.AddChild(SectionLabel(Tr("pick_description")));
        _pickDescValue = BodyRichText();
        _pickDetail.AddChild(_pickDescValue);

        _pickDetail.AddChild(SectionLabel(Tr("pick_location")));
        _pickLocationValue = MutedLine("");
        _pickDetail.AddChild(_pickLocationValue);

        var tp = new Button
        {
            Text = Tr("teleport"),
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 26),
        };
        tp.AddThemeFontSizeOverride("font_size", 11);
        tp.Pressed += OnPickTeleportPressed;
        _pickDetail.AddChild(tp);

        return scroll;
    }

    private Control FirstLifePicFrame()
    {
        var picFrame = new PanelContainer { CustomMinimumSize = new Vector2(0, 160) };
        picFrame.AddThemeStyleboxOverride("panel", RoundedPanel(new Color(0, 0, 0, 0.35f)));
        _firstLifePic = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(0, 156),
        };
        picFrame.AddChild(_firstLifePic);
        return picFrame;
    }

    private static VBoxContainer GroupsListBox()
    {
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 2);
        list.AddChild(MutedLine(Tr("loading")));
        return list;
    }

    private static Control BuildListPage(out VBoxContainer list)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 2);
        list.AddChild(MutedLine(Tr("loading")));
        scroll.AddChild(list);
        return scroll;
    }

    private Control BuildNotesPage()
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);

        box.AddChild(MutedLine(Tr("notes_hint")));

        _notesEdit = new TextEdit
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = Tr("notes_placeholder"),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        _notesEdit.AddThemeFontSizeOverride("font_size", 12);
        _notesEdit.TextChanged += () => _notesSaveTimer = NotesSaveDebounceSec;
        box.AddChild(_notesEdit);

        return box;
    }

    // ---- Action bar ---------------------------------------------------------------------

    private void BuildActions()
    {
        _statusLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 18),
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 10);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));

        if (_isSelf)
        {
            var save = new Button
            {
                Text = "💾  " + Tr("save"),
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 28),
            };
            save.Pressed += OnSaveProfilePressed;
            _actionHost.AddChild(save);
            _actionHost.AddChild(_statusLabel);
            return;
        }

        _payRow = new HBoxContainer { Visible = false };
        _payRow.AddThemeConstantOverride("separation", 6);
        _paySpin = new SpinBox
        {
            MinValue = 1, MaxValue = 100000, Step = 1, Value = 10,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _payRow.AddChild(new Label { Text = "L$" });
        _payRow.AddChild(_paySpin);
        var paySend = new Button { Text = Tr("action_send"), FocusMode = FocusModeEnum.None };
        paySend.Pressed += OnPaySendPressed;
        _payRow.AddChild(paySend);
        _actionHost.AddChild(_payRow);

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        _actionHost.AddChild(grid);

        _addFriendBtn = ActionButton(Tr("action_add_friend"), OnAddFriendPressed);
        _muteBtn = ActionButton(Tr("action_mute"), OnMutePressed);
        grid.AddChild(_addFriendBtn);
        grid.AddChild(ActionButton(Tr("action_im"), OnImPressed));
        grid.AddChild(ActionButton(Tr("action_pay"), () => _payRow.Visible = !_payRow.Visible));
        grid.AddChild(ActionButton(Tr("action_offer_tp"), OnOfferTeleportPressed));
        grid.AddChild(_muteBtn);

        _actionHost.AddChild(_statusLabel);

        RefreshActionButtons();
    }

    private void RefreshActionButtons()
    {
        if (_session == null || _addFriendBtn == null || _muteBtn == null) return;

        bool alreadyFriend = false;
        foreach (var f in _session.GetFriends())
            if (f.Id == _agentId) { alreadyFriend = true; break; }
        _addFriendBtn.Disabled = alreadyFriend;
        _addFriendBtn.Text = alreadyFriend ? Tr("action_friend_added") : Tr("action_add_friend");

        _muteBtn.Text = _session.IsAvatarMuted(_agentId) ? Tr("action_unmute") : Tr("action_mute");
    }

    private void OnSaveProfilePressed()
    {
        if (_session == null) return;
        _session.UpdateOwnProfile(
            _aboutEdit?.Text ?? "",
            _firstLifeEdit?.Text ?? "",
            _webEdit?.Text ?? "",
            _lastProps?.ProfileImageId ?? Guid.Empty,
            _lastProps?.FirstLifeImageId ?? Guid.Empty,
            _allowPublishCheck?.ButtonPressed ?? false,
            _maturePublishCheck?.ButtonPressed ?? false);
        _session.UpdateOwnInterests(
            _langEdit?.Text ?? "", _skillsEdit?.Text ?? "", _wantToEdit?.Text ?? "");
        _statusLabel.Text = Tr("status_saved");
    }

    private void OnAddFriendPressed()
    {
        _session?.OfferFriendship(_agentId);
        if (_addFriendBtn != null) _addFriendBtn.Disabled = true;
        _statusLabel.Text = Tr("status_friend_sent");
    }

    private void OnImPressed() =>
        OnOpenImRequested?.Invoke(_agentId, string.IsNullOrWhiteSpace(_agentName) ? "" : _agentName);

    private void OnPaySendPressed()
    {
        int amount = (int)(_paySpin?.Value ?? 0);
        _session?.PayAvatar(_agentId, amount);
        if (_payRow != null) _payRow.Visible = false;
        _statusLabel.Text = TrF("status_paid", amount);
    }

    private void OnOfferTeleportPressed()
    {
        _session?.OfferTeleport(_agentId);
        _statusLabel.Text = Tr("status_tp_sent");
    }

    private void OnMutePressed()
    {
        if (_session == null || _muteBtn == null) return;
        bool nowMuted = !_session.IsAvatarMuted(_agentId);
        _session.SetAvatarMuted(_agentId, _agentName, nowMuted);
        _muteBtn.Text = nowMuted ? Tr("action_unmute") : Tr("action_mute");
        _statusLabel.Text = nowMuted ? Tr("status_muted") : Tr("status_unmuted");
    }

    private void OnPickTeleportPressed()
    {
        if (_session == null || _selectedPickDetail is not { } d || string.IsNullOrWhiteSpace(d.SimName)) return;
        _session.TeleportToGlobalPosition(d.SimName, d.GlobalX, d.GlobalY, d.GlobalZ);
        _statusLabel.Text = TrF("status_teleporting", d.SimName);
    }

    // ---- Notes persistence ---------------------------------------------------------------

    private void LoadNotes()
    {
        if (_notesEdit == null) return;
        var cfg = new ConfigFile();
        cfg.Load(NotesConfigPath); // fine if it doesn't exist yet
        var key = _agentId.ToString();
        if (cfg.HasSectionKey(NotesSection, key))
            _notesEdit.Text = (string)cfg.GetValue(NotesSection, key);
        _notesLoaded = true;
    }

    public override void _Process(double delta)
    {
        if (_notesSaveTimer <= 0) return;
        _notesSaveTimer -= delta;
        if (_notesSaveTimer <= 0) FlushNotes();
    }

    private void FlushNotes()
    {
        _notesSaveTimer = -1.0;
        if (!_notesLoaded || _notesEdit == null || _agentId == Guid.Empty || !IsInstanceValid(_notesEdit)) return;

        var cfg = new ConfigFile();
        cfg.Load(NotesConfigPath); // preserve window_geometry / other sections
        var key = _agentId.ToString();
        var text = _notesEdit.Text;
        if (string.IsNullOrEmpty(text))
        {
            if (cfg.HasSectionKey(NotesSection, key)) cfg.EraseSectionKey(NotesSection, key);
        }
        else
        {
            cfg.SetValue(NotesSection, key, text);
        }
        cfg.Save(NotesConfigPath);
    }

    // ---- Helpers -----------------------------------------------------------------------

    private void RequestClose()
    {
        FlushNotes();
        Visible = false;
        Closed?.Invoke();
        QueueFree();
    }

    private void ApplyCascade()
    {
        var vp = GetViewportRect().Size;
        var start = new Vector2(Mathf.Max(0f, (vp.X - Size.X) / 2f), Mathf.Max(0f, (vp.Y - Size.Y) / 3f));
        Position = start + new Vector2(CascadeIndex * 28, CascadeIndex * 28);
    }

    /// <summary>Pins <paramref name="newId"/> in <see cref="GpuCache"/> and releases whatever this
    /// "slot" had pinned before, so a texture <see cref="LoadTextureInto"/> is still displaying
    /// can never be evicted (and disposed) out from under its <see cref="TextureRect"/> --
    /// <see cref="GpuCache.GetOrUploadTextureAsync"/> itself starts an entry at RefCount 0
    /// (nothing here passes <c>initialRefCount</c>), and this class previously called it without
    /// ever pinning the result at all. <see cref="WorldMapWindow"/> hit the exact same bug class
    /// live (a disposed <c>Godot.ImageTexture</c> crashing every subsequent redraw) -- this
    /// follows its fix, matching <c>ObjectRenderer</c>'s own established convention: <see
    /// cref="GpuCache.GetOrUploadTextureAsync"/> itself stays ref-neutral, and explicit
    /// <see cref="GpuCache.AddRef"/>/<see cref="GpuCache.ReleaseRef"/> is the sole ref-counting
    /// mechanism, called BEFORE the async fetch even starts (not after it completes) -- safe even
    /// if the fetch is still in flight or ultimately fails, since <c>GpuCache</c>'s own
    /// <c>_pendingRefDelta</c> buffers a ref for an id that hasn't been <c>Put</c> into the cache
    /// yet.</summary>
    private void RepinTexture(ref Guid pinnedId, Guid newId)
    {
        if (_gpuCache == null || pinnedId == newId) return;
        if (pinnedId != Guid.Empty) _gpuCache.ReleaseRef(pinnedId);
        if (newId != Guid.Empty) _gpuCache.AddRef(newId);
        pinnedId = newId;
    }

    private async Task LoadTextureIntoAsync(Guid texId, TextureRect target)
    {
        if (texId == Guid.Empty || _gpuCache == null || _assetService == null) return;
        var tex = await _gpuCache.GetOrUploadTextureAsync(texId, _assetService, generateMipmaps: true)
            .ConfigureAwait(false);
        if (tex == null) return;
        // No SynchronizationContext on the Godot main thread — marshal via CallDeferred on this
        // node (safe, unlike Callable.From(lambda).CallDeferred from a worker thread).
        CallDeferred(MethodName.ApplyTexture, target, tex);
    }

    private void LoadTextureInto(Guid texId, TextureRect target) => _ = LoadTextureIntoAsync(texId, target);

    private void ApplyTexture(TextureRect target, Texture2D tex)
    {
        if (IsInstanceValid(target)) target.Texture = tex;
    }

    private static void FillNameList(VBoxContainer list, string emptyText, int count, Func<int, string> nameAt)
    {
        ClearChildren(list);
        if (count == 0) { list.AddChild(MutedLine(emptyText)); return; }
        for (int i = 0; i < count; i++)
        {
            var n = nameAt(i);
            list.AddChild(BodyLine(string.IsNullOrWhiteSpace(n) ? Tr("unnamed") : n));
        }
    }

    private static void ClearChildren(Node n)
    {
        foreach (Node c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); }
    }

    private static TextEdit MultilineEdit(int minHeight, string placeholder)
    {
        var t = new TextEdit
        {
            PlaceholderText = placeholder,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, minHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        t.AddThemeFontSizeOverride("font_size", 12);
        return t;
    }

    private static LineEdit LabelledLine(Control parent, string label)
    {
        parent.AddChild(SectionLabel(label));
        var e = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        e.AddThemeFontSizeOverride("font_size", 12);
        parent.AddChild(e);
        return e;
    }

    private static Label BodyLine(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", 12);
        l.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
        return l;
    }

    private static Label MutedLine(string text)
    {
        var l = BodyLine(text);
        l.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
        return l;
    }

    private static Label SectionLabel(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 10);
        l.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        return l;
    }

    private static RichTextLabel BodyRichText()
    {
        var r = new RichTextLabel
        {
            BbcodeEnabled = false, // bio text is shown literally
            FitContent = true,
            ScrollActive = false,
            SelectionEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        r.AddThemeFontSizeOverride("normal_font_size", 12);
        return r;
    }

    private static Button ActionButton(string text, Action onPressed)
    {
        var b = new Button
        {
            Text = text,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 26),
            ClipText = true,
        };
        b.AddThemeFontSizeOverride("font_size", 11);
        b.Pressed += onPressed;
        return b;
    }

    private static StyleBoxFlat RoundedPanel(Color bg) => new()
    {
        BgColor = bg,
        CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
    };
}
