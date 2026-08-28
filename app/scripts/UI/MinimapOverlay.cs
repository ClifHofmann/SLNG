using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// MVP2-3 Phase 1: a zoomable, north-up radar of the CURRENT region -- own position/heading, dots
/// for nearby avatars, and a roster list. Clicking a name in the list highlights that avatar's dot
/// on the radar. NOT the same tool as <see cref="WorldMapWindow"/> -- see that class's doc comment
/// for the 2026-08-28 clarification that these are two separate windows (grid-wide sim search vs.
/// per-region avatar radar), not one feature with two entry points.
///
/// An <see cref="SLNGWindow"/> (draggable/resizable, per the UI Standard), unlike its first
/// revision as a passive StatsOverlay-style corner panel -- a zoomable radar plus a clickable
/// roster is real interactive content, not a passive readout, so it earns the same window chrome
/// every other panel gets.
///
/// Nearby-avatar dots come from two independent sources merged by agent id:
/// <see cref="GridSession.NearbyAvatarsUpdated"/> (LibreMetaverse's <c>CoarseLocationUpdate</c> --
/// every avatar the SIMULATOR knows about, region-wide, including ones outside draw distance) and
/// the <see cref="ECS.World"/>'s own <see cref="AvatarComponent"/> entities (avatars actually
/// streamed to us, with live per-frame positions AND names). CoarseLocationUpdate-only avatars
/// (outside draw distance) have no name yet -- resolved via <see cref="GridSession.RequestAvatarName"/>,
/// same lazy-resolve pattern <see cref="FriendsPanel"/> uses.
/// </summary>
public partial class MinimapOverlay : SLNGWindow
{
    private const float DefaultVisibleRangeMeters = 64f;
    private const float MinVisibleRangeMeters = 16f;
    private const float MaxVisibleRangeMeters = 512f;

    private World? _world;
    private GridSession? _session;

    private Label _regionLabel = null!;
    private RadarCanvas _canvas = null!;
    private VBoxContainer _avatarList = null!;

    // Parked off the network thread by NearbyAvatarsUpdated, applied on the main thread in
    // _Process -- see Boot.cs's _pendingRegionEnvironment for why a plain reference assignment
    // (not Callable.From(...).CallDeferred()) is the safe pattern here.
    private volatile NearbyAvatarsEvent? _pendingNearby;
    private NearbyAvatarsEvent? _lastNearby;

    private float _visibleRangeMeters = DefaultVisibleRangeMeters;
    private Guid? _selectedAgentId;

    // Rebuilt every frame in _Process; feeds both the radar draw and the list.
    private readonly List<(Guid AgentId, System.Numerics.Vector3 Position, string Name)> _roster = new();

    // Membership+selection signature the roster list was last actually rebuilt for -- see
    // RefreshListIfChanged.
    private string _lastListSignature = "";

    public override void _Ready()
    {
        base._Ready();

        PersistId = "minimap";
        Title = L10n.Tr("ui.minimap.title");
        CustomMinimumSize = new Vector2(360, 260);
        Size = new Vector2(420, 300);
        Position = new Vector2(900, 60);
        Visible = false;

        var hbox = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        hbox.AddThemeConstantOverride("separation", 10);
        ContentContainer.AddChild(hbox);

        var left = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        left.AddThemeConstantOverride("separation", 4);
        hbox.AddChild(left);

        _regionLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _regionLabel.AddThemeFontSizeOverride("font_size", 11);
        _regionLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 0.95f));
        left.AddChild(_regionLabel);

        _canvas = new RadarCanvas
        {
            CustomMinimumSize = new Vector2(200, 200),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop,
        };
        _canvas.OnZoom = Zoom;
        left.AddChild(_canvas);

        var zoomHint = new Label
        {
            Text = L10n.Tr("ui.minimap.zoom_hint"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        zoomHint.AddThemeFontSizeOverride("font_size", 9);
        zoomHint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f));
        left.AddChild(zoomHint);

        var right = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(140, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        right.AddThemeConstantOverride("separation", 4);
        hbox.AddChild(right);

        var listHeader = new Label { Text = L10n.Tr("ui.minimap.nearby") };
        listHeader.AddThemeFontSizeOverride("font_size", 12);
        right.AddChild(listHeader);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        right.AddChild(scroll);

        _avatarList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _avatarList.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_avatarList);
    }

    /// <summary>Boot hands over the world/session once after both exist. Both are read-only from
    /// here on -- the overlay never mutates world state.</summary>
    public void Initialize(World world, GridSession session)
    {
        _world = world;
        _session = session;
        _session.NearbyAvatarsUpdated += (s, e) => _pendingNearby = e;
        // A name resolved after the roster was already built (RequestAvatarName is fire-and-
        // forget) -- refresh the list text once it lands rather than waiting for the next
        // position update to happen to rebuild it.
        _session.NameResolved += (s, e) => CallDeferred(MethodName.RefreshList);
        _session.DisplayNameResolved += (s, e) => CallDeferred(MethodName.RefreshList);
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible) MoveToFront();
    }

    private void Zoom(float factor) =>
        _visibleRangeMeters = Math.Clamp(_visibleRangeMeters * factor, MinVisibleRangeMeters, MaxVisibleRangeMeters);

    public override void _Process(double delta)
    {
        if (!Visible || _world == null || _session == null) return;

        var pending = System.Threading.Interlocked.Exchange(ref _pendingNearby, null);
        if (pending != null) _lastNearby = pending;

        ulong regionHandle = _session.CurrentRegionHandle;
        if (regionHandle == 0)
        {
            _regionLabel.Text = "";
            _canvas.Clear();
            if (_roster.Count > 0) { _roster.Clear(); RefreshListIfChanged(); }
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
        _canvas.Update(width, height, ownPos, heading, _visibleRangeMeters, _roster, _selectedAgentId);
        RefreshListIfChanged();
    }

    /// <summary>Only tears down and rebuilds the roster's Button rows when who's-in-the-list (or
    /// the selection) actually changed -- NOT every frame, even though <see cref="BuildRoster"/>
    /// itself runs every frame (positions move every frame; names/membership don't). A previous
    /// revision called <see cref="RefreshList"/> unconditionally here, which froze every row's
    /// button under a fresh instance ~60 times a second: Godot's <c>BaseButton</c> only fires
    /// <c>Pressed</c> if the SAME node instance is still alive for both the press and the release,
    /// so a click landing between two rebuilds silently never registered -- the reported "left
    /// click doesn't do anything" bug.</summary>
    private void RefreshListIfChanged()
    {
        var signature = string.Join('|', _roster.Select(r => $"{r.AgentId}:{r.Name}"));
        signature += "#" + _selectedAgentId;
        if (signature == _lastListSignature) return;
        _lastListSignature = signature;
        RefreshList();
    }

    /// <summary>Merges World's exact-but-draw-distance-limited avatar entities with
    /// CoarseLocationUpdate's region-wide-but-coarse snapshot, keyed by agent id -- see the class
    /// doc comment. Populates <see cref="_roster"/> (everyone but the local agent) and returns the
    /// local agent's own position/heading separately, since it's drawn differently (always centred,
    /// with a heading arrow, never in the list).</summary>
    private void BuildRoster(ulong regionHandle, out System.Numerics.Vector3? ownPos, out float heading)
    {
        _roster.Clear();
        ownPos = null;
        heading = 0f;

        var byAgent = new Dictionary<Guid, System.Numerics.Vector3>();
        if (_lastNearby != null && _lastNearby.RegionHandle == regionHandle)
        {
            foreach (var a in _lastNearby.Avatars) byAgent[a.AgentId] = a.Position;
        }

        var names = new Dictionary<Guid, string>();
        Guid ownAgentId = Guid.Empty;
        foreach (var entity in _world!.Query<AvatarComponent>())
        {
            if (entity.RegionHandle != regionHandle) continue;
            var avatar = entity.GetComponent<AvatarComponent>();
            var t = entity.GetComponent<TransformComponent>();
            if (avatar == null || t == null) continue;

            byAgent[avatar.AgentId] = t.Position; // World's live position wins over the coarse one
            names[avatar.AgentId] = AvatarDisplayName(avatar);
            if (avatar.IsLocalAgent)
            {
                ownPos = t.Position;
                heading = HeadingOf(t.Rotation);
                ownAgentId = avatar.AgentId;
            }
        }
        if (ownAgentId != Guid.Empty) byAgent.Remove(ownAgentId);

        foreach (var (agentId, pos) in byAgent)
        {
            if (!names.TryGetValue(agentId, out var name)) name = ResolveName(agentId);
            _roster.Add((agentId, pos, name));
        }
        _roster.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
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

    private void RefreshList()
    {
        foreach (Node child in _avatarList.GetChildren())
        {
            _avatarList.RemoveChild(child);
            child.QueueFree();
        }

        if (_roster.Count == 0)
        {
            var empty = new Label
            {
                Text = L10n.Tr("ui.minimap.none_nearby"),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f));
            _avatarList.AddChild(empty);
            return;
        }

        foreach (var entry in _roster)
            _avatarList.AddChild(BuildAvatarRow(entry.AgentId, entry.Name));
    }

    /// <summary>Same row idiom as <see cref="FriendsPanel"/>'s BuildRow: a flat Button for the
    /// clickable name, a PanelContainer stylebox for the selected-row highlight.</summary>
    private Control BuildAvatarRow(Guid agentId, string name)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = agentId == _selectedAgentId ? new Color(0.3f, 0.6f, 0.9f, 0.25f) : new Color(0, 0, 0, 0),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        });

        var nameBtn = new Button
        {
            Text = name,
            Flat = true,
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        nameBtn.AddThemeFontSizeOverride("font_size", 12);
        nameBtn.Pressed += () => { _selectedAgentId = agentId; RefreshList(); };
        row.AddChild(nameBtn);
        return row;
    }

    /// <summary>SL's forward vector is +X at zero rotation; heading is measured counter-clockwise
    /// from East in the region's XY (north-up) plane -- the same convention the radar's own arrow
    /// drawing uses, so no extra sign-flip is needed at the call site.</summary>
    private static float HeadingOf(System.Numerics.Quaternion rotation)
    {
        var fwd = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, rotation);
        return MathF.Atan2(fwd.Y, fwd.X);
    }

    /// <summary>The actual radar drawing surface, split out like <see cref="StatsOverlay"/>'s
    /// FrameGraph -- <see cref="Update"/> just stores the latest snapshot and requests a repaint;
    /// all drawing happens in <see cref="_Draw"/> on the main thread. Avatar-centred (own position
    /// always at the canvas centre) and zoomable, unlike the whole-region-fixed first revision --
    /// zoom is mouse wheel over the canvas, reported back via <see cref="OnZoom"/> since the
    /// window (not the canvas) owns <see cref="_visibleRangeMeters"/>.</summary>
    private sealed partial class RadarCanvas : Control
    {
        public Action<float>? OnZoom;

        private int _regionWidth = RegionTerrain.DefaultRegionSize;
        private int _regionHeight = RegionTerrain.DefaultRegionSize;
        private System.Numerics.Vector3? _ownPos;
        private float _heading;
        private float _visibleRangeMeters = DefaultVisibleRangeMeters;
        private readonly List<(Guid AgentId, System.Numerics.Vector3 Position, string Name)> _roster = new();
        private Guid? _selectedAgentId;
        private bool _hasData;

        public void Update(int regionWidth, int regionHeight, System.Numerics.Vector3? ownPos, float heading,
            float visibleRangeMeters, List<(Guid AgentId, System.Numerics.Vector3 Position, string Name)> roster,
            Guid? selectedAgentId)
        {
            _regionWidth = Math.Max(1, regionWidth);
            _regionHeight = Math.Max(1, regionHeight);
            _ownPos = ownPos;
            _heading = heading;
            _visibleRangeMeters = Math.Max(1f, visibleRangeMeters);
            _roster.Clear();
            _roster.AddRange(roster);
            _selectedAgentId = selectedAgentId;
            _hasData = true;
            QueueRedraw();
        }

        public void Clear()
        {
            _hasData = false;
            QueueRedraw();
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { Pressed: true } mb)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp) OnZoom?.Invoke(0.8f);
                else if (mb.ButtonIndex == MouseButton.WheelDown) OnZoom?.Invoke(1.25f);
            }
        }

        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.05f, 0.03f, 0.9f));
            DrawRect(new Rect2(Vector2.Zero, size), new Color(0.3f, 0.9f, 0.5f, 0.4f), false, 1.5f);
            if (!_hasData || _ownPos is not { } own) return;

            float scale = Math.Min(size.X, size.Y) / _visibleRangeMeters;
            var center = size / 2f;

            Vector2 ToCanvas(System.Numerics.Vector3 p) => center + new Vector2(
                (p.X - own.X) * scale,
                -(p.Y - own.Y) * scale); // region Y is north; canvas Y grows downward

            // Region boundary, relative to own position -- only ever partly visible unless
            // zoomed out past the region size, same as a real minimap's edge-of-region behaviour.
            var c0 = ToCanvas(new System.Numerics.Vector3(0, 0, 0));
            var c1 = ToCanvas(new System.Numerics.Vector3(_regionWidth, _regionHeight, 0));
            DrawRect(new Rect2(
                new Vector2(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y)),
                new Vector2(Math.Abs(c1.X - c0.X), Math.Abs(c1.Y - c0.Y))),
                new Color(0.3f, 0.9f, 0.5f, 0.25f), false, 1f);

            foreach (var (agentId, pos, _) in _roster)
            {
                var p = ToCanvas(pos);
                if (_selectedAgentId.HasValue && agentId == _selectedAgentId.Value)
                {
                    DrawCircle(p, 7f, new Color(1f, 0.3f, 0.3f, 0.3f));
                    DrawArc(p, 7f, 0, Mathf.Tau, 20, new Color(1f, 0.3f, 0.3f, 1f), 2f);
                }
                DrawCircle(p, 3f, new Color(1f, 0.85f, 0.3f, 0.9f));
            }

            DrawCircle(center, 4f, new Color(0.3f, 0.75f, 1f, 1f));
            var dir = new Vector2(MathF.Cos(_heading), -MathF.Sin(_heading));
            var tip = center + dir * 10f;
            var left = center + dir.Rotated(Mathf.DegToRad(140)) * 6f;
            var right = center + dir.Rotated(Mathf.DegToRad(-140)) * 6f;
            DrawPolygon(new[] { tip, left, right }, new[] { new Color(0.3f, 0.75f, 1f, 1f) });
        }
    }
}
