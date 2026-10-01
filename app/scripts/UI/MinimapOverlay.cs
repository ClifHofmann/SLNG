using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// The radar window (MVP2-3, rebuilt for FEAT-UI-39 as Firestorm's People &gt; Nearby): a filter
/// row, the zoomable north-up map of the CURRENT region, and under it a column table of everyone
/// nearby, the two separated by a draggable divider. NOT the same tool as <see cref="WorldMapWindow"/>
/// -- see that class's doc comment for the 2026-08-28 clarification that these are two separate
/// windows (grid-wide sim search vs. per-region avatar radar), not one feature with two entry points.
///
/// An <see cref="SLNGWindow"/> (draggable/resizable, per the UI Standard).
///
/// Nearby avatars come from two independent sources merged by agent id:
/// <see cref="GridSession.NearbyAvatarsUpdated"/> (LibreMetaverse's <c>CoarseLocationUpdate</c> --
/// every avatar the SIMULATOR knows about, region-wide, including ones outside draw distance) and
/// the <see cref="ECS.World"/>'s own <see cref="AvatarComponent"/> entities (avatars actually
/// streamed to us, with live per-frame positions AND names). CoarseLocationUpdate-only avatars
/// (outside draw distance) have no name yet -- resolved via <see cref="GridSession.RequestAvatarName"/>,
/// same lazy-resolve pattern <see cref="FriendsPanel"/> uses.
///
/// The roster is rebuilt every frame because the map moves every frame; the table is not. It is
/// refreshed about twice a second, in place (see <see cref="RadarTableView"/>), and not built at all
/// while the window is not showing -- only who is nearby, and since when, is still noted. What is shown, sorted and filtered is all decided by the tested pure
/// logic in SLNG.Core (<see cref="RadarTable"/>, <see cref="RadarColumnSettings"/>); this class only
/// gathers the rows and puts them on screen.
/// </summary>
public partial class MinimapOverlay : SLNGWindow
{
    private const float DefaultVisibleRangeMeters = 64f;
    private const float MinVisibleRangeMeters = 16f;
    private const float MaxVisibleRangeMeters = 512f;
    // Double-clicking a table row "jumps" the radar to them -- pan AND zoom, not just pan, so a
    // far-off avatar (found only via CoarseLocationUpdate, easily hundreds of metres away at the
    // default 64 m view) is actually visible afterward rather than still off the edge.
    private const float FocusVisibleRangeMeters = 32f;

    /// <summary>How often the table is refreshed, in seconds. Positions are not what it shows (the
    /// map does that every frame); ages, ranges and notes change slowly, and Firestorm refreshes
    /// at one second.</summary>
    private const float TableRefreshSeconds = 0.5f;

    /// <summary>While the window is not showing, who is nearby is still noted this often.</summary>
    private const float HiddenTrackSeconds = 1f;

    // Chat ranges for the range colouring and the in-range count. Fixed for now; a region's own
    // values (OpenSim's say-range / shout-range) can replace these once they are parsed.
    private const float SayRangeMetres = 20f;
    private const float ShoutRangeMetres = 100f;

    // A coarse location carries its height in a byte of 4 m steps, so 1020 m is the pinned "no
    // idea" value: an avatar reported there has no usable height.
    private const float UnknownCoarseHeight = 1020f;

    private static readonly Vector2 DefaultSize = new(460, 560);

    // The ids the buttons' popups use. Columns use their enum value (0..10), so these stay clear of it.
    private const int SortAscendingId = 100;
    private const int SortDescendingId = 101;
    private const int ResetColumnsId = 102;

    private World? _world;
    private GridSession? _session;
    private MapTileTextures? _tileTextures;

    private readonly List<RadarCanvas.Tile> _tiles = new();
    private readonly List<RadarCanvas.Dot> _dots = new();

    private Label _regionLabel = null!;
    private RadarCanvas _canvas = null!;
    private LineEdit _filterEdit = null!;
    private MenuButton _gearButton = null!;
    private MenuButton _sortButton = null!;
    private PopupMenu _gearColumnsMenu = null!;
    private PopupMenu _columnMenu = null!;
    private VSplitContainer _split = null!;
    private RadarTableView _table = null!;

    private RadarColumnSettings _settings = RadarColumnSettings.CreateDefault();
    private bool _hadSavedPreferences;
    private bool _firstOpenHandled;

    // Set from network threads (NearbyAvatarsUpdated, NameResolved, BriefProfileUpdated) and only
    // ever drained on the main thread in _Process -- see Boot.cs's _pendingRegionEnvironment for
    // why a plain reference assignment (not Callable.From(...).CallDeferred()) is the safe pattern.
    private volatile NearbyAvatarsEvent? _pendingNearby;
    private NearbyAvatarsEvent? _lastNearby;
    private int _dataChanged;

    private float _visibleRangeMeters = DefaultVisibleRangeMeters;
    private Guid? _selectedAgentId;

    // Set by double-clicking a table row -- while non-null, the radar centres on THIS avatar
    // instead of the local one (own position/heading are still drawn, just no longer necessarily
    // at the canvas centre). Cleared automatically once they're no longer in range (see _Process),
    // or by double-clicking the same row again.
    private Guid? _focusAgentId;

    /// <summary>Double-click on a table row: fired with the target avatar's GODOT-space
    /// position (already converted via <see cref="RenderConfig.ToGodot"/> -- this overlay has no
    /// business handing out raw SL-region-local coordinates) and, when known, their facing
    /// direction (also already converted to Godot space) -- null when the avatar is only known
    /// via <c>CoarseLocationUpdate</c> (outside draw distance), which carries no orientation at
    /// all. Boot.cs wires this to <c>AvatarController.FocusOnAvatarFrontal</c>.</summary>
    public Action<Vector3, Vector3?>? OnFocusAvatarRequested;

    /// <summary>Right-click on a table row: fired with the click's screen position plus the
    /// target avatar's id/name, so Boot.cs can show the SAME shared avatar context menu
    /// (Profile/IM/Offer Teleport/Mute) the in-world right-click-an-avatar gesture uses, rather
    /// than this window building its own.</summary>
    public Action<Vector2, Guid, string>? OnAvatarContextMenuRequested;

    /// <summary>Double-click on the radar itself (FEAT-UI-39): fired with the current region's
    /// handle and the clicked spot as a full region-local position. Boot.cs wires this to
    /// <c>GridSession.TeleportToAsync</c>, which already owns the in-flight guard and drives the
    /// teleport overlay -- so a failure is shown there, not here. A single click does nothing,
    /// as in Firestorm.</summary>
    public Action<ulong, System.Numerics.Vector3>? OnTeleportRequested;

    /// <summary>Where the Voice column gets an avatar's activity level from (0..1), or null for
    /// "unknown". Left null until the voice subsystem exists (MVP5-1) -- the column is shown and
    /// stays empty, so turning voice on later changes no table code.</summary>
    public Func<Guid, float?>? VoiceLevelSource;

    // The local avatar's height at the last frame it was known -- the Z of a teleport click when
    // the terrain under the click has not arrived yet (RadarTeleportTarget).
    private float? _ownZ;
    private System.Numerics.Vector3? _ownPosition;

    /// <summary>One avatar in the current region, as the map and the table both need it. Rotation is
    /// only ever known for World-tracked avatars (within draw distance) -- a CoarseLocationUpdate-only
    /// entry (outside draw distance) has no orientation in the packet at all, hence nullable.</summary>
    private readonly record struct RosterEntry(
        Guid AgentId, System.Numerics.Vector3 Position, string Name,
        System.Numerics.Quaternion? Rotation, bool InWorld, bool IsSitting);

    // Rebuilt every frame in _Process; feeds the map and, twice a second, the table.
    private readonly List<RosterEntry> _roster = new();
    private readonly Dictionary<Guid, RosterEntry> _byAgent = new();

    // Table state, all main thread. _firstSeen is when each listed avatar entered the list (the Seen
    // column) and is dropped the moment one leaves, so coming back starts the clock again, as in
    // Firestorm. _relations and _friendIds are refreshed with the table, not per frame.
    private readonly Dictionary<Guid, DateTime> _firstSeen = new();
    private readonly Dictionary<Guid, RadarRelation> _relations = new();
    private readonly HashSet<Guid> _friendIds = new();
    private readonly Dictionary<Guid, RadarRow> _rowById = new();
    private readonly HashSet<Guid> _present = new();
    private readonly List<Guid> _departed = new();
    private float _tableTimer;
    private float _hiddenTimer;

    public override void _Ready()
    {
        PersistId = "minimap";
        base._Ready();

        // The minimap is a radar that should fill its window edge to edge, so it opts out of
        // the standard content inset (FEAT-UI-26).
        SetContentMargin(0, 0);

        Title = L10n.Tr("ui.minimap.title");
        CustomMinimumSize = new Vector2(380, 380);
        Visible = false;

        _hadSavedPreferences = RadarPreferences.Load(out _settings, out int splitOffset);
        BuildLayout();
        _table.Configure(_settings.VisibleColumns);
        _table.SetSort(_settings.SortColumn, _settings.SortAscending);
        _split.SplitOffsets = new[] { splitOffset };
    }

    private void BuildLayout()
    {
        var root = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 4);
        ContentContainer.AddChild(root);

        // Filter row: the name filter, the options gear and the sort button. The content margin is
        // 0 so the map reaches the frame, which means the controls around it carry their own.
        var filterMargin = new MarginContainer();
        filterMargin.AddThemeConstantOverride("margin_left", 8);
        filterMargin.AddThemeConstantOverride("margin_right", 8);
        filterMargin.AddThemeConstantOverride("margin_top", 6);
        root.AddChild(filterMargin);

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 4);
        filterMargin.AddChild(filterRow);

        _filterEdit = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.radar.filter_placeholder"),
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _filterEdit.AddThemeFontSizeOverride("font_size", 12);
        _filterEdit.TextChanged += _ => RefreshTable();
        filterRow.AddChild(_filterEdit);

        _gearButton = MakeMenuButton(RadarIcons.Gear, L10n.Tr("ui.radar.gear"));
        _gearButton.AboutToPopup += RebuildGearMenu;
        filterRow.AddChild(_gearButton);

        _sortButton = MakeMenuButton(RadarIcons.SortMenu, L10n.Tr("ui.radar.sort_by"));
        _sortButton.AboutToPopup += RebuildSortMenu;
        _sortButton.GetPopup().IdPressed += OnSortMenuId;
        filterRow.AddChild(_sortButton);

        // The columns submenu has to be a child of the popup it hangs from; the right-click menu on a
        // column title is a second copy of the same menu, shown at the mouse.
        _gearColumnsMenu = MakeColumnMenu();
        _gearButton.GetPopup().AddChild(_gearColumnsMenu);
        _columnMenu = MakeColumnMenu();
        AddChild(_columnMenu);
        RebuildGearMenu();

        _regionLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _regionLabel.AddThemeFontSizeOverride("font_size", 11);
        _regionLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 0.95f));
        root.AddChild(_regionLabel);

        // Map on top, table below, a draggable divider between. The map gets a little more of the
        // default height; SplitOffset moves it from there and is saved when the drag ends.
        _split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _split.AddThemeConstantOverride("separation", 8);
        _split.AddThemeConstantOverride("autohide", 0); // keep the grab handle visible, it is the affordance
        _split.DragEnded += SavePreferences;
        root.AddChild(_split);

        _canvas = new RadarCanvas
        {
            CustomMinimumSize = new Vector2(200, 140),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.2f,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = L10n.Tr("ui.minimap.zoom_hint"),
            ClipContents = true, // a region tile can reach past the canvas edge
        };
        _canvas.OnZoom = Zoom;
        _canvas.OnTeleportClick = HandleTeleportClick;
        _canvas.OnDotClicked = OnDotClicked;
        _split.AddChild(_canvas);

        // The table sits inside the window's rounded corners, so it gets a small inset of its own.
        var tableMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        tableMargin.AddThemeConstantOverride("margin_left", 6);
        tableMargin.AddThemeConstantOverride("margin_right", 6);
        tableMargin.AddThemeConstantOverride("margin_bottom", 8);
        _split.AddChild(tableMargin);

        _table = new RadarTableView();
        _table.SelectionChanged += id => _selectedAgentId = id;
        _table.RowActivated += OnRowActivated;
        _table.RowContextMenu += OnRowContextMenu;
        _table.TitleClicked += OnTitleClicked;
        tableMargin.AddChild(_table);
    }

    private static MenuButton MakeMenuButton(string glyph, string tooltip)
    {
        var button = new MenuButton
        {
            Text = glyph,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(30, 0),
        };
        button.AddThemeFontSizeOverride("font_size", 14);
        return button;
    }

    /// <summary>A column chooser: the menu stays open while several columns are ticked, which is why
    /// it does not hide on a checkable item and keeps its own check marks up to date.</summary>
    private PopupMenu MakeColumnMenu()
    {
        var menu = new PopupMenu { HideOnCheckableItemSelection = false };
        menu.IdPressed += OnColumnMenuId;
        return menu;
    }

    /// <summary>Only the FIRST-ever open needs this. Done when the window is first shown rather than
    /// in _Ready, because by then RestorePersistedGeometry (deferred out of base._Ready()) has long
    /// run, so <see cref="SLNGWindow.GeometryRestored"/> is meaningful -- see WorldMapWindow's identical
    /// fix/comment for why a synchronous "Position == Vector2.Zero" check in _Ready was wrong -- and
    /// because merely starting the client (or a self-test) must not write the player's settings.</summary>
    private void ApplyFirstOpenDefaults()
    {
        if (_firstOpenHandled) return;
        _firstOpenHandled = true;

        // A size saved by the old map-beside-list layout is far too small for a map over a table, so
        // the first time this layout is shown the window takes the new default. Saving at once is
        // what makes it "first time" only: the marker is the saved section, so a later resize sticks.
        if (!_hadSavedPreferences)
        {
            Size = DefaultSize;
            SavePreferences();
        }

        if (!GeometryRestored) Position = new Vector2(900, 60);
    }

    /// <summary>Boot hands over the world/session once after both exist. Both are read-only from
    /// here on -- the overlay never mutates world state.</summary>
    public void Initialize(World world, GridSession session, MapTileTextures tileTextures)
    {
        DetachSession();

        _world = world;
        _session = session;
        _tileTextures = tileTextures;
        // A new session can mean a different grid/region entirely -- a carried-over focus lock
        // or selection would point at an agent id that means nothing there.
        _focusAgentId = null;
        _selectedAgentId = null;
        _lastNearby = null;
        _pendingNearby = null;
        _ownZ = null;
        _ownPosition = null;
        _visibleRangeMeters = DefaultVisibleRangeMeters;
        _roster.Clear();
        ClearTracking();

        session.NearbyAvatarsUpdated += OnNearbyAvatarsUpdated;
        // A name or profile that arrives after the row was built (both requests are fire-and-forget)
        // should show up soon rather than at the next regular refresh. These are network-thread
        // events: they only raise a flag, and _Process does the refreshing.
        session.NameResolved += OnNameResolved;
        session.DisplayNameResolved += OnNameResolved;
        session.BriefProfileUpdated += OnBriefProfileUpdated;
    }

    public override void _ExitTree()
    {
        DetachSession();
        base._ExitTree();
    }

    private void DetachSession()
    {
        if (_session == null) return;
        _session.NearbyAvatarsUpdated -= OnNearbyAvatarsUpdated;
        _session.NameResolved -= OnNameResolved;
        _session.DisplayNameResolved -= OnNameResolved;
        _session.BriefProfileUpdated -= OnBriefProfileUpdated;
    }

    private void OnNearbyAvatarsUpdated(object? sender, NearbyAvatarsEvent e) => _pendingNearby = e;

    private void OnNameResolved(object? sender, NameResolvedEvent e) => System.Threading.Volatile.Write(ref _dataChanged, 1);

    private void OnBriefProfileUpdated(object? sender, AvatarBriefProfile profile) =>
        System.Threading.Volatile.Write(ref _dataChanged, 1);

    public void Toggle()
    {
        Visible = !Visible;
        if (!Visible) return;
        ApplyFirstOpenDefaults();
        MoveToFront();
    }

    private void Zoom(float factor) =>
        _visibleRangeMeters = Math.Clamp(_visibleRangeMeters * factor, MinVisibleRangeMeters, MaxVisibleRangeMeters);

    /// <summary>The canvas has already checked the click is inside the region it draws, which is
    /// always the current one -- the radar shows nothing else yet, so there is no neighbour to
    /// resolve and no region that might not exist.</summary>
    private void HandleTeleportClick(System.Numerics.Vector2 local)
    {
        if (_session == null || _world == null) return;
        ulong regionHandle = _session.CurrentRegionHandle;
        if (regionHandle == 0) return;

        _world.Terrains.TryGetValue(regionHandle, out var terrain);
        OnTeleportRequested?.Invoke(regionHandle, RadarTeleportTarget.Resolve(terrain, local, _ownZ));
    }

    /// <summary>A click on a dot selects that avatar here and in the table, and scrolls the row into
    /// view -- the two halves of "a dot selects its row, a row highlights its dot".</summary>
    private void OnDotClicked(Guid agentId)
    {
        _selectedAgentId = agentId;
        _table.SelectRow(agentId, scrollTo: true);
    }

    public override void _Process(double delta)
    {
        if (_world == null || _session == null) return;

        // The canvas is hidden with the window, with the HUD, and when the window is minimized, so
        // this one check covers all three: no map, no rows and no profile requests for a radar
        // nobody can see. Who is nearby, and since when, is still noted.
        if (!_canvas.IsVisibleInTree())
        {
            TrackWhileHidden(delta);
            return;
        }

        var pending = System.Threading.Interlocked.Exchange(ref _pendingNearby, null);
        if (pending != null) _lastNearby = pending;

        ulong regionHandle = _session.CurrentRegionHandle;
        if (regionHandle == 0)
        {
            _regionLabel.Text = "";
            _canvas.Clear();
            if (_roster.Count > 0)
            {
                _roster.Clear();
                RefreshTable();
            }
            _focusAgentId = null;
            _visibleRangeMeters = DefaultVisibleRangeMeters;
            return;
        }

        _regionLabel.Text = _session.CurrentRegionName;

        int width = RegionTerrain.DefaultRegionSize, height = RegionTerrain.DefaultRegionSize;
        if (_world.Terrains.TryGetValue(regionHandle, out var terrain))
        {
            width = terrain.Width;
            height = terrain.Height;
        }

        BuildRoster(regionHandle, out var ownPos, out var heading);
        _ownPosition = ownPos;
        if (ownPos is { } own) _ownZ = own.Z;

        // A focused avatar centres the radar in their place -- but if they've since left range
        // (dropped out of the roster entirely, e.g. they teleported away or moved out of both
        // draw distance and CoarseLocationUpdate for this region), there's no position left to
        // centre on, so silently fall back to centring on the local avatar instead of freezing
        // the view on a stale point.
        System.Numerics.Vector3? focusPos = null;
        if (_focusAgentId is { } focusId)
        {
            foreach (var entry in _roster)
            {
                if (entry.AgentId != focusId) continue;
                focusPos = entry.Position;
                break;
            }
            if (focusPos == null) _focusAgentId = null;
        }
        var center = focusPos ?? ownPos;

        // A refresh is due on the timer, or sooner when a name or a profile has just arrived.
        _tableTimer -= (float)delta;
        if (System.Threading.Interlocked.Exchange(ref _dataChanged, 0) != 0) _tableTimer = 0f;
        if (_tableTimer <= 0f) RefreshTable();

        BuildTiles(regionHandle, center);
        BuildDots();
        _canvas.Update(width, height, center, ownPos, heading, _visibleRangeMeters, _dots, _selectedAgentId, _tiles);
    }

    /// <summary>The map tiles to draw: the current region always, and any loaded neighbour that
    /// could reach the canvas. Asking <see cref="MapTileTextures.Get"/> is what starts a load, so a
    /// neighbour far outside the view is never fetched.</summary>
    private void BuildTiles(ulong currentHandle, System.Numerics.Vector3? center)
    {
        _tiles.Clear();
        if (_tileTextures == null || _world == null) return;

        double currentX = RegionHandle.OriginX(currentHandle);
        double currentY = RegionHandle.OriginY(currentHandle);
        foreach (var (handle, terrain) in _world.Terrains)
        {
            bool isCurrent = handle == currentHandle;
            // Double arithmetic: a neighbour west or south of us has the smaller origin.
            var origin = new System.Numerics.Vector2(
                (float)(RegionHandle.OriginX(handle) - currentX),
                (float)(RegionHandle.OriginY(handle) - currentY));
            if (!isCurrent && !ReachesView(origin, terrain.Width, terrain.Height, center)) continue;
            _tiles.Add(new RadarCanvas.Tile(_tileTextures.Get(handle), origin, terrain.Width, terrain.Height, isCurrent));
        }
    }

    /// <summary>Generous on purpose: the canvas can be wider than tall, so a square of one full
    /// visible range around the focus on each side is always enough to cover it.</summary>
    private bool ReachesView(System.Numerics.Vector2 origin, float width, float height, System.Numerics.Vector3? center)
    {
        if (center is not { } c) return false;
        float reach = _visibleRangeMeters;
        return origin.X < c.X + reach && origin.X + width > c.X - reach
            && origin.Y < c.Y + reach && origin.Y + height > c.Y - reach;
    }

    private void BuildDots()
    {
        _dots.Clear();
        foreach (var entry in _roster)
        {
            _relations.TryGetValue(entry.AgentId, out var relation); // missing = Other, the default
            _dots.Add(new RadarCanvas.Dot(entry.AgentId, entry.Position, relation));
        }
    }

    /// <summary>Merges World's exact-but-draw-distance-limited avatar entities with
    /// CoarseLocationUpdate's region-wide-but-coarse snapshot, keyed by agent id -- see the class
    /// doc comment. Populates <see cref="_roster"/> (everyone but the local agent) and returns the
    /// local agent's own position/heading separately, since it's drawn differently (with a heading
    /// arrow, never in the table).</summary>
    private void BuildRoster(ulong regionHandle, out System.Numerics.Vector3? ownPos, out float heading)
    {
        _roster.Clear();
        _byAgent.Clear();
        ownPos = null;
        heading = 0f;

        if (_lastNearby != null && _lastNearby.RegionHandle == regionHandle)
        {
            foreach (var a in _lastNearby.Avatars)
                _byAgent[a.AgentId] = new RosterEntry(a.AgentId, a.Position, "", null, false, false);
        }

        Guid ownAgentId = Guid.Empty;
        foreach (var entity in _world!.Query<AvatarComponent>())
        {
            if (entity.RegionHandle != regionHandle) continue;
            var avatar = entity.GetComponent<AvatarComponent>();
            var t = entity.GetComponent<TransformComponent>();
            if (avatar == null || t == null) continue;

            // World's live data wins over the coarse one.
            _byAgent[avatar.AgentId] = new RosterEntry(
                avatar.AgentId, t.Position, AvatarDisplayName(avatar), t.Rotation, true, avatar.SittingOnLocalId != 0);
            if (avatar.IsLocalAgent)
            {
                ownPos = t.Position;
                heading = HeadingOf(t.Rotation);
                ownAgentId = avatar.AgentId;
            }
        }
        if (ownAgentId != Guid.Empty) _byAgent.Remove(ownAgentId);

        foreach (var entry in _byAgent.Values)
            _roster.Add(entry.Name.Length == 0 ? entry with { Name = ResolveName(entry.AgentId) } : entry);
    }

    private static string AvatarDisplayName(AvatarComponent avatar)
    {
        if (!string.IsNullOrWhiteSpace(avatar.DisplayName)) return avatar.DisplayName;
        var full = $"{avatar.FirstName} {avatar.LastName}".Trim();
        return string.IsNullOrWhiteSpace(full) ? avatar.AgentId.ToString() : full;
    }

    /// <summary>A CoarseLocationUpdate-only avatar (outside draw distance, no World entity yet)
    /// has no name attached to the packet at all -- fall back to the shared name cache, kicking
    /// off a resolve if it's not there yet (picked up by the NameResolved handler above).</summary>
    private string ResolveName(Guid agentId)
    {
        if (_session == null) return agentId.ToString();
        if (_session.TryGetCachedName(agentId, out var name) && !string.IsNullOrWhiteSpace(name)) return name;
        _session.RequestAvatarName(agentId);
        return agentId.ToString();
    }

    /// <summary>SL's forward vector is +X at zero rotation; heading is measured counter-clockwise
    /// from East in the region's XY (north-up) plane -- the same convention the radar's own arrow
    /// drawing uses, so no extra sign-flip is needed at the call site.</summary>
    private static float HeadingOf(System.Numerics.Quaternion rotation)
    {
        var fwd = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, rotation);
        return MathF.Atan2(fwd.Y, fwd.X);
    }

    // ---- The table -------------------------------------------------------------------------

    /// <summary>Turns the roster into rows and puts them in the table. Called twice a second, when a
    /// name or profile arrives, and at once when the filter, the sort or the columns change.</summary>
    private void RefreshTable()
    {
        _tableTimer = TableRefreshSeconds;
        var now = DateTime.UtcNow;

        var rows = new List<RadarRow>(_roster.Count);
        if (_session != null)
        {
            // The friend list is a snapshot of LibreMetaverse state, so it is read here with the
            // table and not once per frame; the mute check is a short scan of the mute list.
            _friendIds.Clear();
            foreach (var friend in _session.GetFriends()) _friendIds.Add(friend.Id);

            UpdatePresence(now);
            _relations.Clear();
            _rowById.Clear();
            foreach (var entry in _roster)
            {
                // Cheap to ask every refresh: GridSession answers from its cache, and sends only for
                // an avatar it has not heard about (or has not heard from in ten minutes), through a
                // throttled queue. Asked here rather than when the avatar arrives because an avatar
                // that came in while the window was closed has not been asked for yet. The answer
                // arrives on its own thread and shows up through BriefProfileUpdated.
                _session.RequestBriefProfile(entry.AgentId);

                var relation = _session.IsAvatarMuted(entry.AgentId) ? RadarRelation.Muted
                    : _friendIds.Contains(entry.AgentId) ? RadarRelation.Friend
                    : RadarRelation.Other;
                _relations[entry.AgentId] = relation;

                var row = BuildRow(entry, relation, now);
                rows.Add(row);
                _rowById[entry.AgentId] = row;
            }
        }

        ApplyRows(rows, now);
    }

    private RadarRow BuildRow(RosterEntry entry, RadarRelation relation, DateTime now)
    {
        // A coarse-only avatar whose height is pinned has no usable Z: its distance is measured on
        // the ground plane only, which is a true lower bound -- the range cell then says ">".
        bool heightKnown = entry.InWorld || entry.Position.Z < UnknownCoarseHeight;
        float distance = 0f;
        if (_ownPosition is { } own)
        {
            distance = heightKnown
                ? System.Numerics.Vector3.Distance(own, entry.Position)
                : System.Numerics.Vector2.Distance(
                    new System.Numerics.Vector2(own.X, own.Y),
                    new System.Numerics.Vector2(entry.Position.X, entry.Position.Y));
        }

        AvatarBriefProfile? profile = null;
        if (_session != null && _session.TryGetBriefProfile(entry.AgentId, out var known)) profile = known;

        return new RadarRow(
            entry.AgentId,
            entry.Name,
            entry.Position,
            distance,
            heightKnown,
            relation,
            InSameRegion: true,  // the roster is the current region only until neighbours are added (BUG-UI-11)
            entry.IsSitting,
            IsTyping: false,     // no source for typing yet; the column is not offered
            VoiceLevelSource?.Invoke(entry.AgentId),
            _firstSeen.TryGetValue(entry.AgentId, out var first) ? now - first : TimeSpan.Zero,
            profile,
            WithinDrawDistance: entry.InWorld);
    }

    /// <summary>Filters, sorts and shows the rows. Split from <see cref="RefreshTable"/> because this
    /// half needs no session, only the rows.</summary>
    private void ApplyRows(List<RadarRow> all, DateTime utcNow)
    {
        var shown = RadarTable.Sort(
            RadarTable.Filter(all, _filterEdit.Text), _settings.SortColumn, _settings.SortAscending, utcNow);
        _table.Update(shown, all, utcNow, RenderConfig.DrawDistance, SayRangeMetres, ShoutRangeMetres);
        // The selection is the window's: keep the table's highlight on it, including after a column
        // change rebuilt the table or the avatar came back into the list.
        _table.SelectRow(_selectedAgentId, scrollTo: false);
    }

    /// <summary>Notes who entered and who left, and nothing else. Seen means "since the avatar entered
    /// the list", and the list exists whether or not the window is open: Firestorm's radar keeps
    /// running behind a closed floater, and an avatar who has been beside you for twenty minutes
    /// should say so the moment the window opens, not "0:00:00". Once a second is enough for a
    /// clock shown in whole seconds; no rows, map or requests are built.</summary>
    private void TrackWhileHidden(double delta)
    {
        _hiddenTimer -= (float)delta;
        if (_hiddenTimer > 0f) return;
        _hiddenTimer = HiddenTrackSeconds;

        var pending = System.Threading.Interlocked.Exchange(ref _pendingNearby, null);
        if (pending != null) _lastNearby = pending;

        ulong regionHandle = _session!.CurrentRegionHandle;
        if (regionHandle == 0) return;

        BuildRoster(regionHandle, out _, out _);
        UpdatePresence(DateTime.UtcNow);
    }

    /// <summary>Starts the Seen clock for an avatar that just entered the roster and stops it for
    /// one that left -- so coming back starts again at zero, as in Firestorm.</summary>
    private void UpdatePresence(DateTime now)
    {
        _present.Clear();
        foreach (var entry in _roster)
        {
            _present.Add(entry.AgentId);
            if (!_firstSeen.ContainsKey(entry.AgentId)) _firstSeen[entry.AgentId] = now;
        }

        _departed.Clear();
        foreach (var id in _firstSeen.Keys)
        {
            if (!_present.Contains(id)) _departed.Add(id);
        }
        foreach (var id in _departed) _firstSeen.Remove(id);
    }

    private void ClearTracking()
    {
        _firstSeen.Clear();
        _relations.Clear();
        _rowById.Clear();
        _tableTimer = 0f;
    }

    private void OnRowActivated(Guid agentId)
    {
        // Double-click LMB: jumps the RADAR to this avatar (pan + zoom in) AND turns the real 3D
        // camera to look at them -- two independent "jump there" actions the tester asked for
        // (the second one explicitly clarified as the actual in-world camera, not the radar's own
        // zoom). Double-clicking the ALREADY-radar-focused row un-focuses the radar (back to
        // centring on the local avatar) without re-firing the camera turn -- there's no sensible
        // "undo" for a one-shot camera look, unlike the radar's persistent centring.
        if (_focusAgentId == agentId)
        {
            _focusAgentId = null;
            _visibleRangeMeters = DefaultVisibleRangeMeters;
            return;
        }

        _focusAgentId = agentId;
        _visibleRangeMeters = FocusVisibleRangeMeters;
        if (_session == null) return;

        ulong regionHandle = _session.CurrentRegionHandle;
        foreach (var entry in _roster)
        {
            if (entry.AgentId != agentId) continue;
            var godotPos = RenderConfig.ToGodot(regionHandle, entry.Position);
            Vector3? godotForward = null;
            if (entry.Rotation is { } rot)
            {
                // SL forward is +X at zero rotation (same convention HeadingOf uses);
                // RenderConfig.ToGodot's documented axis map (SL(X,Y,Z) -> Godot(X,Z,-Y)) applies
                // to a direction exactly like a position -- no origin subtraction needed since
                // it's a pure axis permutation.
                var fwdSl = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, rot);
                godotForward = new Vector3(fwdSl.X, fwdSl.Z, -fwdSl.Y);
            }
            OnFocusAvatarRequested?.Invoke(godotPos, godotForward);
            break;
        }
    }

    /// <summary>Right-click on a row: the SAME shared avatar context menu (Profile/IM/Offer
    /// Teleport/Mute) the in-world right-click gesture shows -- Boot.cs owns that menu, so this
    /// just reports where and who.</summary>
    private void OnRowContextMenu(Guid agentId, Vector2 screenPosition)
    {
        if (_rowById.TryGetValue(agentId, out var row))
            OnAvatarContextMenuRequested?.Invoke(screenPosition, agentId, row.Name);
    }

    // ---- Column titles, sorting and menus --------------------------------------------------

    private void OnTitleClicked(RadarColumn column, MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            FillColumnMenu(_columnMenu);
            _columnMenu.Position = (Vector2I)GetGlobalMousePosition();
            _columnMenu.Popup();
            return;
        }

        if (button != MouseButton.Left || !RadarColumns.Info(column).Sortable) return;

        // Clicking the sorted column flips it; any other starts ascending.
        if (_settings.SortColumn == column) _settings.SortAscending = !_settings.SortAscending;
        else
        {
            _settings.SortColumn = column;
            _settings.SortAscending = true;
        }
        SortChanged();
    }

    private void SortChanged()
    {
        _table.SetSort(_settings.SortColumn, _settings.SortAscending);
        SavePreferences();
        RefreshTable();
    }

    private void ColumnsChanged()
    {
        // The only change that rebuilds the table: the set of columns is the Tree's shape.
        _table.Configure(_settings.VisibleColumns);
        _table.SetSort(_settings.SortColumn, _settings.SortAscending);
        SavePreferences();
        RefreshTable();
    }

    private void SavePreferences()
    {
        var offsets = _split.SplitOffsets;
        RadarPreferences.Save(_settings, offsets.Length > 0 ? offsets[0] : 0);
    }

    private void OnColumnMenuId(long id)
    {
        if (id == ResetColumnsId) _settings.ResetColumns();
        else if (id >= 0 && id < RadarColumns.All.Count)
        {
            var column = (RadarColumn)id;
            _settings.SetVisible(column, !_settings.IsVisible(column));
        }
        else return;

        // Both menus (the gear's and the title's) show the same state, and neither closes.
        UpdateColumnMenuChecks(_columnMenu);
        UpdateColumnMenuChecks(_gearColumnsMenu);
        ColumnsChanged();
    }

    private void OnSortMenuId(long id)
    {
        if (id == SortAscendingId) _settings.SortAscending = true;
        else if (id == SortDescendingId) _settings.SortAscending = false;
        else if (id >= 0 && id < RadarColumns.All.Count) _settings.SortColumn = (RadarColumn)id;
        else return;

        SortChanged();
    }

    /// <summary>(Re)builds the gear's popup: a Columns submenu. Rebuilt every time it opens so the
    /// text follows the language and the checks follow the settings.</summary>
    private void RebuildGearMenu()
    {
        var popup = _gearButton.GetPopup();
        popup.Clear();
        FillColumnMenu(_gearColumnsMenu);
        popup.AddSubmenuNodeItem(L10n.Tr("ui.radar.columns"), _gearColumnsMenu);
    }

    private void FillColumnMenu(PopupMenu menu)
    {
        menu.Clear();
        foreach (var info in RadarColumns.All)
        {
            if (!info.Offered) continue;
            menu.AddCheckItem(L10n.Tr(info.TitleKey), (int)info.Column);
            int index = menu.GetItemIndex((int)info.Column);
            // The name can't be hidden: a table without it is unreadable.
            if (info.AlwaysVisible) menu.SetItemDisabled(index, true);
            menu.SetItemTooltip(index, L10n.Tr(info.TooltipKey));
        }
        menu.AddSeparator();
        menu.AddItem(L10n.Tr("ui.radar.reset_columns"), ResetColumnsId);
        UpdateColumnMenuChecks(menu);
    }

    private void UpdateColumnMenuChecks(PopupMenu menu)
    {
        foreach (var info in RadarColumns.All)
        {
            if (!info.Offered) continue;
            int index = menu.GetItemIndex((int)info.Column);
            if (index >= 0) menu.SetItemChecked(index, _settings.IsVisible(info.Column));
        }
    }

    private void RebuildSortMenu()
    {
        var popup = _sortButton.GetPopup();
        popup.Clear();
        foreach (var info in RadarColumns.All)
        {
            if (!info.Sortable || !info.Offered) continue;
            popup.AddRadioCheckItem(L10n.Tr(info.TitleKey), (int)info.Column);
            popup.SetItemChecked(popup.GetItemIndex((int)info.Column), info.Column == _settings.SortColumn);
        }
        popup.AddSeparator();
        popup.AddRadioCheckItem(L10n.Tr("ui.radar.ascending"), SortAscendingId);
        popup.SetItemChecked(popup.GetItemIndex(SortAscendingId), _settings.SortAscending);
        popup.AddRadioCheckItem(L10n.Tr("ui.radar.descending"), SortDescendingId);
        popup.SetItemChecked(popup.GetItemIndex(SortDescendingId), !_settings.SortAscending);
    }
}
