using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// MVP2-3 Phase 1: a toggleable, north-up radar of the CURRENT region -- region bounds, own
/// position/heading, and dots for nearby avatars.
///
/// Deliberately NOT an <see cref="SLNGWindow"/>, same reasoning as <see cref="StatsOverlay"/>: this
/// is a passive corner readout, not a floater someone drags or resizes, and swallowing clicks in
/// the corner would fight the camera controls for no benefit.
///
/// Phase 1 maps the WHOLE current region onto the square canvas rather than panning/zooming
/// around the avatar -- for a standard 256 m region that already shows everything a radar needs
/// (the avatar's own dot moves freely inside the square), and it keeps "region bounds" literally
/// true: the canvas edge IS the region edge. A varregion is simply the same square stretched to a
/// non-square aspect and centred; genuine pan/zoom is <see cref="WorldMapWindow"/>'s job (Phase 2),
/// not this radar's.
///
/// Nearby-avatar dots come from two independent sources that this overlay merges by agent id:
/// <see cref="GridSession.NearbyAvatarsUpdated"/> (LibreMetaverse's <c>CoarseLocationUpdate</c> --
/// every avatar the SIMULATOR knows about, region-wide, including ones outside draw distance) and
/// the <see cref="ECS.World"/>'s own <see cref="AvatarComponent"/> entities (avatars actually
/// streamed to us, with live per-frame positions). CoarseLocationUpdate is coarse (updated a few
/// times a second, byte-precision Z) and World is exact but draw-distance-limited, so merging
/// covers the truthful union: everyone the sim reports, positioned as precisely as we know how.
/// </summary>
public partial class MinimapOverlay : PanelContainer
{
    private const float CanvasSize = 176f;

    private World? _world;
    private GridSession? _session;
    private RadarCanvas _canvas = null!;
    private Label _regionLabel = null!;

    // Parked off the network thread by the NearbyAvatarsUpdated handler, applied on the main
    // thread in _Process. Plain reference assignment (not Callable.From(...).CallDeferred()) --
    // see Boot.cs's _pendingRegionEnvironment for why a custom Callable's deferred dispatch is
    // unsafe from a background thread in Godot .NET.
    private volatile NearbyAvatarsEvent? _pendingNearby;
    private NearbyAvatarsEvent? _lastNearby;

    public override void _Ready()
    {
        Visible = false; // toggled from the ButtonBar, like every other panel
        MouseFilter = MouseFilterEnum.Ignore;

        // Top-right, mirroring StatsOverlay's top-left placement so the two never collide.
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetRight = -12;
        OffsetLeft = -(12 + CanvasSize + 16);
        OffsetTop = 52;
        OffsetBottom = 52 + CanvasSize + 40;

        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.06f, 0.09f, 0.82f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderColor = new Color(0.15f, 0.6f, 0.9f, 0.35f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        });

        var vbox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 4);
        AddChild(vbox);

        _regionLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _regionLabel.AddThemeFontSizeOverride("font_size", 11);
        _regionLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 0.95f));
        vbox.AddChild(_regionLabel);

        _canvas = new RadarCanvas
        {
            CustomMinimumSize = new Vector2(CanvasSize, CanvasSize),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddChild(_canvas);
    }

    /// <summary>Boot hands over the world/session once after both exist. Both are read-only from
    /// here on -- the overlay never mutates world state.</summary>
    public void Initialize(World world, GridSession session)
    {
        _world = world;
        _session = session;
        _session.NearbyAvatarsUpdated += (s, e) => _pendingNearby = e;
    }

    public void Toggle() => Visible = !Visible;

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
            return;
        }

        _regionLabel.Text = _session.CurrentRegionName;

        int width = RegionTerrain.DefaultRegionSize, height = RegionTerrain.DefaultRegionSize;
        if (_world.Terrains.TryGetValue(regionHandle, out var terrain))
        {
            width = terrain.Width;
            height = terrain.Height;
        }

        // Merge World's exact-but-draw-distance-limited avatar entities with CoarseLocationUpdate's
        // region-wide-but-coarse snapshot, keyed by agent id -- see the class doc comment.
        var byAgent = new Dictionary<Guid, System.Numerics.Vector3>();
        if (_lastNearby != null && _lastNearby.RegionHandle == regionHandle)
        {
            foreach (var a in _lastNearby.Avatars) byAgent[a.AgentId] = a.Position;
        }

        System.Numerics.Vector3? ownPos = null;
        float heading = 0f;
        Guid ownAgentId = Guid.Empty;
        foreach (var entity in _world.Query<AvatarComponent>())
        {
            if (entity.RegionHandle != regionHandle) continue;
            var avatar = entity.GetComponent<AvatarComponent>();
            var t = entity.GetComponent<TransformComponent>();
            if (avatar == null || t == null) continue;

            byAgent[avatar.AgentId] = t.Position; // World's live position wins over the coarse one
            if (avatar.IsLocalAgent)
            {
                ownPos = t.Position;
                heading = HeadingOf(t.Rotation);
                ownAgentId = avatar.AgentId;
            }
        }

        if (ownAgentId != Guid.Empty) byAgent.Remove(ownAgentId);

        _canvas.Update(width, height, ownPos, heading, byAgent.Values);
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
    /// all drawing happens in <see cref="_Draw"/> on the main thread.</summary>
    private sealed partial class RadarCanvas : Control
    {
        private int _regionWidth = RegionTerrain.DefaultRegionSize;
        private int _regionHeight = RegionTerrain.DefaultRegionSize;
        private System.Numerics.Vector3? _ownPos;
        private float _heading;
        private readonly List<System.Numerics.Vector3> _others = new();
        private bool _hasData;

        public void Update(int regionWidth, int regionHeight, System.Numerics.Vector3? ownPos, float heading,
            IEnumerable<System.Numerics.Vector3> others)
        {
            _regionWidth = Math.Max(1, regionWidth);
            _regionHeight = Math.Max(1, regionHeight);
            _ownPos = ownPos;
            _heading = heading;
            _others.Clear();
            _others.AddRange(others);
            _hasData = true;
            QueueRedraw();
        }

        public void Clear()
        {
            _hasData = false;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.05f, 0.03f, 0.9f));
            DrawRect(new Rect2(Vector2.Zero, size), new Color(0.3f, 0.9f, 0.5f, 0.4f), false, 1.5f);
            if (!_hasData) return;

            // Region-local (x=east, y=north) -> canvas pixels (x right, y down): flip Y so north
            // is up, and fit the region's aspect inside the square without distortion.
            float scale = Math.Min(size.X / _regionWidth, size.Y / _regionHeight);
            Vector2 origin = new(
                (size.X - _regionWidth * scale) / 2f,
                (size.Y - _regionHeight * scale) / 2f);

            Vector2 ToCanvas(System.Numerics.Vector3 p) => new(
                origin.X + p.X * scale,
                size.Y - (origin.Y + p.Y * scale));

            foreach (var pos in _others)
            {
                DrawCircle(ToCanvas(pos), 3f, new Color(1f, 0.85f, 0.3f, 0.9f));
            }

            if (_ownPos.HasValue)
            {
                var center = ToCanvas(_ownPos.Value);
                DrawCircle(center, 4f, new Color(0.3f, 0.75f, 1f, 1f));

                // Heading arrow: canvas Y is flipped relative to region Y, so negate the sine term.
                var dir = new Vector2(MathF.Cos(_heading), -MathF.Sin(_heading));
                var tip = center + dir * 10f;
                var left = center + dir.Rotated(Mathf.DegToRad(140)) * 6f;
                var right = center + dir.Rotated(Mathf.DegToRad(-140)) * 6f;
                DrawPolygon(new[] { tip, left, right }, new[] { new Color(0.3f, 0.75f, 1f, 1f) });
            }
        }
    }
}
