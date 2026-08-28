using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// MVP2-3 Phase 2/3: the full grid map. Pan + zoom over region tiles, a search box that resolves
/// a region name (optionally with trailing "x y [z]" coordinates) to a location, click-to-inspect
/// and double-click-to-teleport.
///
/// Threading: every LibreMetaverse-backed lookup here (<see cref="GridSession.ResolveRegionByNameAsync"/>,
/// <see cref="GridSession.ResolveRegionByHandleAsync"/>, <see cref="GridSession.TeleportToAsync"/>,
/// and the fire-and-forget tile texture fetch) resumes its <c>await</c> continuation on an
/// arbitrary thread-pool thread -- Godot's main thread carries no <c>SynchronizationContext</c>,
/// so "started from a button click" does NOT mean "still on the main thread after the first
/// <c>await</c>" (see <see cref="SLNGWindow"/>'s LoadTextureIntoAsync doc comment for the same
/// gotcha). Every such continuation below parks its result in a field/queue and calls
/// <see cref="Node.CallDeferred(StringName, Godot.Variant[])"/> to resume on the main thread before
/// touching a Control or a Godot object, per AGENTS.md's buffer-and-drain rule.
/// </summary>
public partial class WorldMapWindow : SLNGWindow
{
    private const float MinPixelsPerMeter = 0.08f;
    private const float MaxPixelsPerMeter = 4f;
    private const float DefaultPixelsPerMeter = 0.4f;
    private const float RegionMeters = 256f;

    private GridSession? _session;
    private GpuCache? _gpuCache;
    private SLNG.Assets.AssetService? _assetService;
    private World? _world;

    private LineEdit _searchEdit = null!;
    private Button _goButton = null!;
    private Button _recenterButton = null!;
    private MapCanvas _canvas = null!;
    private Label _infoLabel = null!;
    private Button _teleportButton = null!;

    private readonly Dictionary<ulong, MapRegionInfo> _regions = new();
    private readonly Dictionary<ulong, Texture2D> _tileTextures = new();
    private readonly HashSet<ulong> _tileRequested = new();

    // Populated off-thread by GridSession.RegionDiscovered (map-block streaming) and by the
    // click-to-inspect resolve; drained on the main thread. Order doesn't matter -- it only ever
    // feeds a dictionary keyed by region handle.
    private readonly ConcurrentQueue<MapRegionInfo> _pendingRegions = new();

    // Same story for tile textures: enqueued by a background fetch continuation, applied on the
    // main thread. The Texture2D reference itself is never touched off-thread, only stored.
    private readonly ConcurrentQueue<(ulong Handle, Texture2D Tex)> _pendingTiles = new();

    private sealed class PendingSearch
    {
        public MapRegionInfo? Info;
        public float[] Coords = Array.Empty<float>();
        public string QueryText = "";
    }
    private volatile PendingSearch? _pendingSearch;
    private volatile string? _pendingTeleportMessage;

    private double _centerGlobalX;
    private double _centerGlobalY;
    private float _pixelsPerMeter = DefaultPixelsPerMeter;
    private bool _centered;
    private (int MinGX, int MinGY, int MaxGX, int MaxGY)? _requestedRect;

    private ulong? _selectedRegionHandle;
    private System.Numerics.Vector3 _selectedLocal;

    private bool _dragging;
    private bool _dragMoved;
    private Vector2 _dragLastScreen;

    public override void _Ready()
    {
        base._Ready();

        PersistId = "world_map";
        Title = L10n.Tr("ui.worldmap.title");
        CustomMinimumSize = new Vector2(480, 380);
        Size = new Vector2(640, 520);
        Position = new Vector2(200, 100);
        Visible = false;

        var vbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 6);
        ContentContainer.AddChild(vbox);

        var searchRow = new HBoxContainer();
        searchRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(searchRow);

        _searchEdit = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = L10n.Tr("ui.worldmap.search_placeholder"),
        };
        _searchEdit.TextSubmitted += _ => OnSearch();
        searchRow.AddChild(_searchEdit);

        _goButton = new Button { Text = L10n.Tr("ui.worldmap.go"), FocusMode = FocusModeEnum.None };
        _goButton.Pressed += OnSearch;
        searchRow.AddChild(_goButton);

        _recenterButton = new Button { Text = L10n.Tr("ui.worldmap.recenter"), FocusMode = FocusModeEnum.None };
        _recenterButton.Pressed += () => { _centered = false; CenterOnAvatarIfNeeded(); };
        searchRow.AddChild(_recenterButton);

        _canvas = new MapCanvas
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop,
            ClipContents = true,
        };
        _canvas.OnInput = HandleCanvasInput;
        vbox.AddChild(_canvas);

        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(bottomRow);

        _infoLabel = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Text = L10n.Tr("ui.worldmap.hint"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _infoLabel.AddThemeFontSizeOverride("font_size", 12);
        bottomRow.AddChild(_infoLabel);

        _teleportButton = new Button
        {
            Text = L10n.Tr("ui.worldmap.teleport"),
            FocusMode = FocusModeEnum.None,
            Disabled = true,
        };
        _teleportButton.Pressed += OnTeleportPressed;
        bottomRow.AddChild(_teleportButton);
    }

    /// <summary>Boot hands over the session/asset plumbing once, after all three exist -- and
    /// again on every relogin, since a new <see cref="GridSession"/> can mean a different grid
    /// entirely. Everything keyed off the OLD session (cached tiles/regions, pan position) is
    /// stale at that point and is dropped rather than carried over.</summary>
    public void Initialize(GridSession session, GpuCache gpuCache, SLNG.Assets.AssetService assetService, World world)
    {
        _session = session;
        _gpuCache = gpuCache;
        _assetService = assetService;
        _world = world;
        _session.RegionDiscovered += (s, info) => { _pendingRegions.Enqueue(info); CallDeferred(MethodName.DrainPendingRegions); };

        _regions.Clear();
        _tileTextures.Clear();
        _tileRequested.Clear();
        _requestedRect = null;
        _selectedRegionHandle = null;
        _pendingClickResolve = null;
        _centered = false;
        _teleportButton.Disabled = true;
        _infoLabel.Text = L10n.Tr("ui.worldmap.hint");
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            MoveToFront();
            CenterOnAvatarIfNeeded();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        CenterOnAvatarIfNeeded();
        RedrawCanvas();
    }

    // ---- Search --------------------------------------------------------------------------

    private async void OnSearch()
    {
        if (_session == null) return;
        string text = _searchEdit.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        var (regionName, coords) = ParseSearchText(text);
        if (string.IsNullOrWhiteSpace(regionName))
        {
            _infoLabel.Text = L10n.Tr("ui.worldmap.enter_region_name");
            return;
        }

        _infoLabel.Text = L10n.Tr("ui.worldmap.searching");
        var info = await _session.ResolveRegionByNameAsync(regionName).ConfigureAwait(false);

        _pendingSearch = new PendingSearch { Info = info, Coords = coords, QueryText = regionName };
        CallDeferred(MethodName.ApplySearchResult);
    }

    private void ApplySearchResult()
    {
        var pending = System.Threading.Interlocked.Exchange(ref _pendingSearch, null);
        if (pending == null) return;

        if (pending.Info == null)
        {
            _infoLabel.Text = L10n.TrFormat("ui.worldmap.region_not_found", pending.QueryText);
            return;
        }

        var info = pending.Info;
        _regions[info.RegionHandle] = info;
        RequestTile(info);
        CenterOn(info.GlobalX + RegionMeters / 2, info.GlobalY + RegionMeters / 2);

        var local = new System.Numerics.Vector3(
            pending.Coords.Length >= 1 ? pending.Coords[0] : RegionMeters / 2,
            pending.Coords.Length >= 2 ? pending.Coords[1] : RegionMeters / 2,
            pending.Coords.Length >= 3 ? pending.Coords[2] : 0f);
        _selectedRegionHandle = info.RegionHandle;
        _selectedLocal = local;
        _teleportButton.Disabled = false;
        _infoLabel.Text = $"{info.Name}  ({local.X:0}, {local.Y:0})";

        RequestVisibleTilesIfNeeded();
        RedrawCanvas();
    }

    /// <summary>Splits trailing numeric tokens ("x y" or "x y z") off the end of the query, so
    /// "Region Name 128 128 25" resolves the name and pre-selects that local position, while a
    /// bare "Region Name" just centres on it (matching an SLURL's own <c>region/x/y/z</c> shape,
    /// loosely -- this accepts space-separated instead of requiring the URL form).</summary>
    private static (string Name, float[] Coords) ParseSearchText(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int numericStart = parts.Length;
        while (numericStart > 0 && float.TryParse(parts[numericStart - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            numericStart--;
        // A bare number (no region name at all) isn't a valid query -- require at least one
        // non-numeric token, and cap at 3 trailing numbers (x, y, z).
        if (numericStart == 0 || parts.Length - numericStart > 3) return (text, Array.Empty<float>());

        string name = string.Join(' ', parts, 0, numericStart);
        var coords = parts.Skip(numericStart).Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return (name, coords);
    }

    // ---- Teleport --------------------------------------------------------------------------

    private async void OnTeleportPressed()
    {
        if (_session == null || _selectedRegionHandle == null) return;
        _teleportButton.Disabled = true;
        _infoLabel.Text = L10n.Tr("ui.worldmap.teleporting");
        GD.Print($"[WorldMap] Requesting teleport to region handle {_selectedRegionHandle.Value}, local {_selectedLocal}");

        var result = await _session.TeleportToAsync(_selectedRegionHandle.Value, _selectedLocal).ConfigureAwait(false);
        GD.Print($"[WorldMap] Teleport result: success={result.Success} message=\"{result.Message}\"");
        _pendingTeleportMessage = result.Success ? "" : $"{L10n.Tr("ui.worldmap.teleport_failed")}: {result.Message}";
        CallDeferred(MethodName.ApplyTeleportResult);
    }

    private void ApplyTeleportResult()
    {
        _teleportButton.Disabled = _selectedRegionHandle == null;
        var msg = System.Threading.Interlocked.Exchange(ref _pendingTeleportMessage, null);
        if (!string.IsNullOrEmpty(msg)) _infoLabel.Text = msg;
    }

    // ---- Click / drag / zoom --------------------------------------------------------------

    private void HandleCanvasInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _dragging = true;
                    _dragMoved = false;
                    _dragLastScreen = mb.Position;
                    if (mb.DoubleClick) HandlePointAction(mb.Position, teleport: true);
                }
                else
                {
                    bool wasDrag = _dragMoved;
                    _dragging = false;
                    if (!wasDrag && !mb.DoubleClick) HandlePointAction(mb.Position, teleport: false);
                }
            }
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) Zoom(1.25f, mb.Position);
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) Zoom(1f / 1.25f, mb.Position);
        }
        else if (@event is InputEventMouseMotion mm && _dragging)
        {
            var delta = mm.Position - _dragLastScreen;
            _dragLastScreen = mm.Position;
            if (delta.Length() > 0.01f) _dragMoved = true;
            _centerGlobalX -= delta.X / _pixelsPerMeter;
            _centerGlobalY += delta.Y / _pixelsPerMeter;
            RequestVisibleTilesIfNeeded();
            RedrawCanvas();
        }
    }

    private void Zoom(float factor, Vector2 aroundScreen)
    {
        var (beforeX, beforeY) = ScreenToGlobal(aroundScreen);
        _pixelsPerMeter = Math.Clamp(_pixelsPerMeter * factor, MinPixelsPerMeter, MaxPixelsPerMeter);
        var (afterX, afterY) = ScreenToGlobal(aroundScreen);
        _centerGlobalX += beforeX - afterX;
        _centerGlobalY += beforeY - afterY;
        RequestVisibleTilesIfNeeded();
        RedrawCanvas();
    }

    /// <summary>Never fires a teleport at a region handle that hasn't been CONFIRMED to exist.
    /// A raw handle-based <c>TeleportLocationRequest</c> to a grid square with no region on it
    /// isn't rejected -- it just gets no reply at all, so <see cref="GridSession.TeleportToAsync"/>
    /// sits until <c>AgentManager.TeleportTimeout</c> (40s) before reporting "timed out", with
    /// nothing visible in between. That is exactly what a click landing on empty grid space (very
    /// easy at the old fixed-zoomed-out default -- see <see cref="CenterOnAvatarIfNeeded"/>) looked
    /// like: "teleport doesn't do anything." The real viewer has the same gate (LLAgent::
    /// doTeleportViaLocation only takes the direct path once <c>LLWorldMap::simInfoFromHandle</c>
    /// has resolved the target; verified against slviewer source, not guessed) -- resolve first,
    /// teleport only once/if that confirms a region is actually there.</summary>
    private void HandlePointAction(Vector2 screenPos, bool teleport)
    {
        if (_session == null) return;
        var (gx, gy) = ScreenToGlobal(screenPos);
        if (gx < 0 || gy < 0)
        {
            _infoLabel.Text = L10n.Tr("ui.worldmap.no_region_here");
            return;
        }

        uint originX = (uint)(Math.Floor(gx / RegionMeters) * RegionMeters);
        uint originY = (uint)(Math.Floor(gy / RegionMeters) * RegionMeters);
        ulong regionHandle = ((ulong)originX << 32) | originY;
        var local = new System.Numerics.Vector3((float)(gx - originX), (float)(gy - originY), 0f);

        _selectedRegionHandle = regionHandle;
        _selectedLocal = local;

        if (_regions.TryGetValue(regionHandle, out var known))
        {
            _teleportButton.Disabled = false;
            _infoLabel.Text = $"{known.Name}  ({local.X:0}, {local.Y:0})";
            RedrawCanvas();
            if (teleport) OnTeleportPressed();
        }
        else
        {
            // Not resolved yet -- most clicks land on a tile RequestVisibleTilesIfNeeded already
            // streamed in, so this is the uncommon case (a click just outside the loaded area, or
            // genuinely empty grid space). ApplyResolvedRegion teleports automatically once/if the
            // resolve confirms a region here, but only if the selection hasn't moved on meanwhile.
            _teleportButton.Disabled = true;
            _infoLabel.Text = L10n.Tr("ui.worldmap.resolving");
            RedrawCanvas();
            _ = ResolveRegionInBackgroundAsync(regionHandle, teleport);
        }
    }

    private sealed class PendingResolve
    {
        public ulong RegionHandle;
        public MapRegionInfo? Info;
        public bool TeleportAfter;
    }
    private volatile PendingResolve? _pendingClickResolve;

    private async Task ResolveRegionInBackgroundAsync(ulong regionHandle, bool teleportAfter)
    {
        if (_session == null) return;
        var info = await _session.ResolveRegionByHandleAsync(regionHandle).ConfigureAwait(false);
        _pendingClickResolve = new PendingResolve { RegionHandle = regionHandle, Info = info, TeleportAfter = teleportAfter };
        CallDeferred(MethodName.ApplyResolvedRegion);
    }

    private void ApplyResolvedRegion()
    {
        var pending = System.Threading.Interlocked.Exchange(ref _pendingClickResolve, null);
        // The user clicked somewhere else while this was in flight -- its answer no longer
        // applies to the current selection.
        if (pending == null || _selectedRegionHandle != pending.RegionHandle) return;

        if (pending.Info != null)
        {
            _regions[pending.Info.RegionHandle] = pending.Info;
            RequestTile(pending.Info);
            _teleportButton.Disabled = false;
            _infoLabel.Text = $"{pending.Info.Name}  ({_selectedLocal.X:0}, {_selectedLocal.Y:0})";
            RedrawCanvas();
            if (pending.TeleportAfter) OnTeleportPressed();
        }
        else
        {
            _infoLabel.Text = L10n.Tr("ui.worldmap.no_region_here");
            _teleportButton.Disabled = true;
            _selectedRegionHandle = null;
        }
    }

    private void DrainPendingRegions()
    {
        bool any = false;
        while (_pendingRegions.TryDequeue(out var info))
        {
            _regions[info.RegionHandle] = info;
            RequestTile(info);
            if (_selectedRegionHandle == info.RegionHandle)
                _infoLabel.Text = $"{info.Name}  ({_selectedLocal.X:0}, {_selectedLocal.Y:0})";
            any = true;
        }
        if (any) RedrawCanvas();
    }

    // ---- Tiles -------------------------------------------------------------------------------

    private void RequestTile(MapRegionInfo info)
    {
        if (info.MapImageId == Guid.Empty) return;
        if (!_tileRequested.Add(info.RegionHandle)) return;
        _ = LoadTileAsync(info.RegionHandle, info.MapImageId);
    }

    private async Task LoadTileAsync(ulong regionHandle, Guid textureId)
    {
        if (_gpuCache == null || _assetService == null) return;
        var tex = await _gpuCache.GetOrUploadTextureAsync(textureId, _assetService, generateMipmaps: false).ConfigureAwait(false);
        if (tex == null) return;
        _pendingTiles.Enqueue((regionHandle, tex));
        CallDeferred(MethodName.DrainPendingTiles);
    }

    private void DrainPendingTiles()
    {
        bool any = false;
        while (_pendingTiles.TryDequeue(out var t))
        {
            _tileTextures[t.Handle] = t.Tex;
            any = true;
        }
        if (any) RedrawCanvas();
    }

    private void RequestVisibleTilesIfNeeded()
    {
        if (_session == null) return;
        var size = _canvas.Size;
        if (size.X <= 0 || size.Y <= 0) return;

        double halfW = size.X / 2.0 / _pixelsPerMeter;
        double halfH = size.Y / 2.0 / _pixelsPerMeter;
        int minGX = (int)Math.Floor((_centerGlobalX - halfW) / RegionMeters) - 1;
        int maxGX = (int)Math.Floor((_centerGlobalX + halfW) / RegionMeters) + 1;
        int minGY = (int)Math.Floor((_centerGlobalY - halfH) / RegionMeters) - 1;
        int maxGY = (int)Math.Floor((_centerGlobalY + halfH) / RegionMeters) + 1;

        if (_requestedRect is { } r && minGX >= r.MinGX && maxGX <= r.MaxGX && minGY >= r.MinGY && maxGY <= r.MaxGY)
            return; // already covered

        // Pad so a small pan/zoom step doesn't immediately re-trigger another fetch.
        var padded = (MinGX: minGX - 2, MinGY: minGY - 2, MaxGX: maxGX + 2, MaxGY: maxGY + 2);
        _requestedRect = padded;
        _session.RequestMapBlocks(padded.MinGX, padded.MinGY, padded.MaxGX, padded.MaxGY);
    }

    // ---- Coordinate transforms + drawing ---------------------------------------------------

    private void CenterOn(double gx, double gy)
    {
        _centerGlobalX = gx;
        _centerGlobalY = gy;
    }

    /// <summary>Also RE-FITS the zoom, not just the pan position -- see the class-level "Requesting
    /// teleport" doc comment on <see cref="HandlePointAction"/>. <see cref="DefaultPixelsPerMeter"/>
    /// alone rendered a 256 m region as a ~100 px square lost in a mostly-EMPTY canvas on a typical
    /// window size, so most of the visible area was actually non-existent neighbour grid squares --
    /// exactly what made a stray click land on nothing. Fitting the home region to ~70% of the
    /// shorter canvas side makes "click your own region" the easy, obvious target by default.</summary>
    private void CenterOnAvatarIfNeeded()
    {
        if (_centered || _session == null) return;
        ulong handle = _session.CurrentRegionHandle;
        if (handle == 0) return;
        var (ox, oy) = RegionOrigin(handle);
        CenterOn(ox + RegionMeters / 2, oy + RegionMeters / 2);

        var size = _canvas.Size;
        if (size.X > 0 && size.Y > 0)
            _pixelsPerMeter = Math.Clamp(Math.Min(size.X, size.Y) * 0.7f / RegionMeters, MinPixelsPerMeter, MaxPixelsPerMeter);

        _centered = true;
        RequestVisibleTilesIfNeeded();
    }

    private static (double X, double Y) RegionOrigin(ulong regionHandle) =>
        ((uint)(regionHandle >> 32), (uint)(regionHandle & 0xFFFFFFFF));

    private (double GlobalX, double GlobalY) ScreenToGlobal(Vector2 screen)
    {
        var size = _canvas.Size;
        double gx = _centerGlobalX + (screen.X - size.X / 2.0) / _pixelsPerMeter;
        double gy = _centerGlobalY - (screen.Y - size.Y / 2.0) / _pixelsPerMeter;
        return (gx, gy);
    }

    private Vector2 GlobalToScreen(double gx, double gy)
    {
        var size = _canvas.Size;
        return new Vector2(
            (float)(size.X / 2.0 + (gx - _centerGlobalX) * _pixelsPerMeter),
            (float)(size.Y / 2.0 - (gy - _centerGlobalY) * _pixelsPerMeter));
    }

    private void RedrawCanvas()
    {
        var size = _canvas.Size;
        var viewport = new Rect2(Vector2.Zero, size);
        var tiles = new List<(Rect2 Rect, Texture2D? Tex)>();
        foreach (var info in _regions.Values)
        {
            var topLeft = GlobalToScreen(info.GlobalX, info.GlobalY + RegionMeters);
            var rect = new Rect2(topLeft, new Vector2(RegionMeters * _pixelsPerMeter, RegionMeters * _pixelsPerMeter));
            if (!rect.Intersects(viewport)) continue;
            _tileTextures.TryGetValue(info.RegionHandle, out var tex);
            tiles.Add((rect, tex));
        }
        _canvas.Tiles = tiles;
        _canvas.OwnMarker = ComputeOwnMarker();
        _canvas.SelectedMarker = ComputeSelectedMarker();
        _canvas.QueueRedraw();
    }

    /// <summary>Deliberately own-avatar-only, unlike <see cref="MinimapOverlay"/>. The world map
    /// and the minimap are two different tools (per the spec and 2026-08-28 clarification): the
    /// minimap is the per-region avatar radar; the world map is a grid-wide sim search/teleport
    /// tool, and showing OTHER avatars there would need CoarseLocationUpdate data for regions this
    /// client isn't even connected to, which does not exist -- exactly the real SL/Firestorm World
    /// Map's own behaviour (it shows your own position, not a radar of strangers).</summary>
    private Vector2? ComputeOwnMarker()
    {
        if (_world == null || _session == null) return null;
        ulong regionHandle = _session.CurrentRegionHandle;
        if (regionHandle == 0) return null;

        foreach (var entity in _world.Query<AvatarComponent>())
        {
            if (entity.RegionHandle != regionHandle) continue;
            var avatar = entity.GetComponent<AvatarComponent>();
            var t = entity.GetComponent<TransformComponent>();
            if (avatar is not { IsLocalAgent: true } || t == null) continue;
            var (ox, oy) = RegionOrigin(regionHandle);
            return GlobalToScreen(ox + t.Position.X, oy + t.Position.Y);
        }
        return null;
    }

    private Vector2? ComputeSelectedMarker()
    {
        if (_selectedRegionHandle is not { } handle) return null;
        var (ox, oy) = RegionOrigin(handle);
        return GlobalToScreen(ox + _selectedLocal.X, oy + _selectedLocal.Y);
    }

    /// <summary>The actual drawing surface, split out like <see cref="StatsOverlay"/>'s FrameGraph
    /// and <see cref="MinimapOverlay"/>'s RadarCanvas -- the parent window precomputes what to draw
    /// (in world-independent screen coordinates) and this just paints it plus forwards raw input
    /// back for the window to interpret (it owns the pan/zoom/selection state, not this canvas).</summary>
    private sealed partial class MapCanvas : Control
    {
        public Action<InputEvent>? OnInput;
        public List<(Rect2 Rect, Texture2D? Tex)> Tiles = new();
        public Vector2? OwnMarker;
        public Vector2? SelectedMarker;

        public override void _GuiInput(InputEvent @event) => OnInput?.Invoke(@event);

        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.03f, 0.03f, 0.05f, 1f));

            foreach (var (rect, tex) in Tiles)
            {
                if (tex != null) DrawTextureRect(tex, rect, false);
                else DrawRect(rect, new Color(0.12f, 0.14f, 0.18f, 1f));
                DrawRect(rect, new Color(1f, 1f, 1f, 0.08f), false, 1f);
            }

            if (SelectedMarker is { } sel)
            {
                var col = new Color(1f, 0.85f, 0.25f, 0.95f);
                DrawLine(sel - new Vector2(8, 0), sel + new Vector2(8, 0), col, 2f);
                DrawLine(sel - new Vector2(0, 8), sel + new Vector2(0, 8), col, 2f);
            }

            if (OwnMarker is { } own)
            {
                DrawCircle(own, 5f, new Color(0.3f, 0.75f, 1f, 1f));
                DrawArc(own, 5f, 0, Mathf.Tau, 16, new Color(0, 0, 0, 0.6f), 1.5f);
            }
        }
    }
}
