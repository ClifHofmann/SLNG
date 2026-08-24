using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class TerrainRenderer : Node3D
{
    private class RegionTerrainNode
    {
        public Node3D Root { get; }
        public MeshInstance3D MeshInstance { get; }
        public StaticBody3D StaticBody { get; }
        public CollisionShape3D CollisionShape { get; }

        // Detail texture ids currently pinned in the GpuCache for this region (see
        // TerrainRenderer.SetTerrainTextureRefs for why terrain textures need this at all).
        public List<Guid> UsedTextureIds = new();

        public RegionTerrainNode()
        {
            Root = new Node3D();
            MeshInstance = new MeshInstance3D();
            // On its own layer (PhysicsLayers.Terrain), not the shared Objects layer -- see that
            // constant's doc comment for why: a hover raycast that includes terrain pays a console
            // warning on every unstreamed patch it crosses for a hit it can't do anything with.
            StaticBody = new StaticBody3D { Name = "TerrainPhysics", CollisionLayer = PhysicsLayers.Terrain };
            StaticBody.SetMeta("EntityId", "TERRAIN");
            StaticBody.SetMeta("LocalId", "TERRAIN");
            CollisionShape = new CollisionShape3D { Name = "TerrainCollision" };

            Root.AddChild(MeshInstance);
            Root.AddChild(StaticBody);
            StaticBody.AddChild(CollisionShape);
        }

        public void QueueFree() => Root.QueueFree();
    }

    private readonly Dictionary<ulong, RegionTerrainNode> _regions = new();

    // Regions whose terrain changed since the last frame. Many 16x16 patches arrive per
    // region during load; we coalesce them into at most one rebuild per region per frame.
    private readonly HashSet<ulong> _dirtyRegions = new();
    private ShaderMaterial _terrainMaterial = new();
    private ShaderMaterial _waterMaterial = new();

    /// <summary>The shared water material every region's water plane uses as its
    /// <c>MaterialOverride</c>. Exposed for <c>EnvironmentDriver</c> (FEAT-ENV-01 Phase D) to
    /// drive the water's colour from the region's actual EEP/Windlight settings instead of the
    /// shader's hardcoded default.</summary>
    public ShaderMaterial WaterMaterial => _waterMaterial;

    private World? _world;
    private SLNG.Assets.AssetService? _assetService;
    private GpuCache? _gpuCache;

    /// <summary>
    /// 256x1 RGBAF lookup feeding the terrain shader's Perlin noise: rg = normalized 2D gradient,
    /// b = permutation entry. Built once from <see cref="SlPerlinNoise"/> so the CPU reference
    /// implementation and the shader cannot drift apart — the tables come out of srand(42) plus
    /// the Microsoft CRT's rand(), and a table that does not match the viewer's paints the same
    /// region with a visibly different pattern.
    /// </summary>
    private static ImageTexture? _noiseLut;

    private static ImageTexture GetNoiseLut()
    {
        if (_noiseLut != null) return _noiseLut;

        var perm = SlPerlinNoise.Permutation;
        var grad = SlPerlinNoise.Gradients2D;

        var image = Image.CreateEmpty(SlPerlinNoise.B, 1, false, Image.Format.Rgbaf);
        for (int i = 0; i < SlPerlinNoise.B; i++)
        {
            image.SetPixel(i, 0, new Color(grad[i * 2], grad[i * 2 + 1], perm[i], 0f));
        }

        _noiseLut = ImageTexture.CreateFromImage(image);
        return _noiseLut;
    }

    /// <summary>
    /// The viewer's own terrain blend ramp (IMG_ALPHA_GRAD_2D / alpha_gradient_2d.j2c), shipped
    /// with us under CC BY-SA 3.0 — see THIRD-PARTY-NOTICES.md.
    ///
    /// Its second axis is the point of it: holding the ramp at a single curve gives every place at
    /// a given composition value the same blend, where the viewer varies it with position. That
    /// variation is what makes its texture boundaries crisp, and approximating it with a fitted
    /// curve was tried and measurably made things worse, so we sample the real table.
    /// </summary>
    private static Texture2D? _alphaRamp;

    private static Texture2D GetAlphaRamp() =>
        _alphaRamp ??= ResourceLoader.Load<Texture2D>("res://textures/sl_alpha_gradient_2d.png");

    /// <summary>Non-negative modulo — C#'s % keeps the sign of the dividend, which would phase
    /// the noise and detail UVs wrongly for a region west or south of the global origin.</summary>
    private static double Mod(double a, double b) => (a % b + b) % b;

    /// <summary>The composition summaries already logged, keyed by region AND by the settings
    /// they described, so a rebuild per arriving patch does not spam while a genuine settings
    /// CHANGE still reports.
    ///
    /// It used to be keyed on the region alone, and that quietly defeated the diagnostic's whole
    /// purpose. Estate terrain settings are exactly what you change while A/B-ing against another
    /// viewer -- swap in probe textures, narrow the elevation range -- and after the first log line
    /// per region, none of it was ever reported again. A session's log then showed the DEFAULT
    /// Terrain Dirt/Grass/Mountain/Rock ids and start=10/range=60 while the screen was drawing
    /// probe checkerboards, and the stale line read exactly like a current measurement.</summary>
    private readonly HashSet<string> _compositionLogged = new();

    /// <summary>Regions already reported as drawing without terrain settings.</summary>
    private readonly HashSet<ulong> _settingsMissingLogged = new();

    /// <summary>
    /// FEAT-RENDER-02 diagnostic: reports what the terrain shader is actually being asked to do.
    ///
    /// The shader's inputs cannot be read back from the GPU, so a mismatch against the real viewer
    /// is otherwise impossible to attribute — a wrong `height_range` off the wire and a wrong noise
    /// port look identical on screen. This recomputes the composition on the CPU through
    /// <see cref="SlTerrainComposition"/> (the same maths the shader runs) and prints the
    /// per-detail-slot area split, which IS directly comparable against a screenshot.
    ///
    /// Deliberately GD.Print and not Logger.Info: Logger sits at Warning unless Diagnostics is
    /// switched on (Diagnostics.cs:39), so an Info line here is invisible in a normal run — which
    /// is exactly the run where this measurement is wanted. Four lines once per region, matching
    /// how [ENV] and [Boot] already report, so it does not reopen the quiet-console decision.
    /// </summary>
    /// <summary>
    /// A cheap fingerprint of the heightmap, so a terrain import re-logs the diagnostic.
    ///
    /// Subsampled on a 16-cell stride rather than hashed whole: this runs on every patch rebuild,
    /// and a full pass over a 256x256 (or 1536x1536) map per patch would be real work for a
    /// diagnostic. A stride still moves for any change that alters the terrain visibly, which is
    /// the only kind worth re-reporting -- and patches arrive incrementally, so a partially
    /// streamed map produces its own intermediate fingerprints and settles once complete.
    /// </summary>
    private static long HeightmapFingerprint(RegionTerrain terrain)
    {
        var heights = terrain.GetHeights();
        long hash = 17;
        for (int i = 0; i < heights.Length; i += 16)
        {
            // Quantised to centimetres before hashing: raw float bits would make the fingerprint
            // move on noise far below anything the composition can see.
            hash = hash * 31 + (long)MathF.Round(heights[i] * 100f);
        }
        return hash;
    }

    private void LogCompositionDiagnostics(ulong regionHandle, RegionTerrain terrain)
    {
        // Terrain patches arrive independently of (and often before) the RegionHandshake that
        // carries the composition settings, so the first rebuilds run with all-zero start/range.
        // Logging those would pin a meaningless snapshot and never log the real one.
        bool settingsArrived = false;
        foreach (float r in terrain.TerrainHeightRanges)
        {
            if (r > 0f) { settingsArrived = true; break; }
        }
        if (!settingsArrived)
        {
            // Worth saying out loud rather than returning quietly. A region drawn without
            // settings has no meaningful composition at all, and a second such region rendering
            // beside the real one is otherwise invisible in the logs -- which is exactly how a
            // whole neighbouring region painted in the top detail texture went unnoticed.
            if (_settingsMissingLogged.Add(regionHandle))
            {
                GD.Print($"[TerrainComposition] region {regionHandle} is being drawn with NO terrain " +
                         "settings (height_range all zero) -- falling back to the lowest detail band");
            }
            return;
        }

        // Keyed on everything the composition actually depends on. Detail ids are in there
        // because swapping the textures changes nothing numeric, yet it is a common mid-session
        // change and the one that makes every earlier line misleading. The HEIGHTMAP is in there
        // for the same reason and was missed on the first pass: importing a terrain changes no
        // setting at all, so a freshly uploaded known-answer map produced no new line and the
        // block from region entry -- describing the terrain that had just been replaced -- stayed
        // the only one in the log.
        string signature = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{regionHandle}|{string.Join(',', terrain.TerrainStartHeights)}|" +
            $"{string.Join(',', terrain.TerrainHeightRanges)}|{terrain.WaterHeight}|" +
            $"{terrain.TerrainDetail0},{terrain.TerrainDetail1},{terrain.TerrainDetail2},{terrain.TerrainDetail3}|" +
            $"{HeightmapFingerprint(terrain)}");
        if (!_compositionLogged.Add(signature)) return;

        double originX = (uint)(regionHandle >> 32);
        double originY = (uint)(regionHandle & 0xFFFFFFFF);

        var starts = terrain.TerrainStartHeights;
        var ranges = terrain.TerrainHeightRanges;

        var slotArea = new float[SlTerrainComposition.AssetCount];
        var slotAreaDry = new float[SlTerrainComposition.AssetCount];
        var weights = new float[SlTerrainComposition.AssetCount];
        var dryValues = new List<float>();
        float minH = float.MaxValue, maxH = float.MinValue, sumH = 0f;
        int samples = 0;

        // Every 4 m is plenty for an area split and keeps this off the frame budget.
        for (int y = 0; y < terrain.Height; y += 4)
        {
            for (int x = 0; x < terrain.Width; x += 4)
            {
                if (!terrain.TryGetKnownHeight(x, y, out float h)) continue;

                float east = terrain.Width > 1 ? (float)x / terrain.Width : 0f;
                float north = terrain.Height > 1 ? (float)y / terrain.Height : 0f;
                float start = SlTerrainComposition.BilinearCorners(starts, east, north);
                float range = SlTerrainComposition.BilinearCorners(ranges, east, north);

                float value = SlTerrainComposition.Value(
                    (float)(originX + x), (float)(originY + y), h, start, range);

                SlTerrainComposition.Weights(value, weights);
                for (int i = 0; i < weights.Length; i++) slotArea[i] += weights[i];

                // The visible part. Most of a coastal region is seabed far below the waterline,
                // and averaging it in swamps the split for the land you can actually see: the
                // first version of this report read "detail0 = 91%" for an island that renders
                // almost entirely as detail1.
                if (h >= terrain.WaterHeight)
                {
                    for (int i = 0; i < weights.Length; i++) slotAreaDry[i] += weights[i];
                    dryValues.Add(value);
                }

                minH = MathF.Min(minH, h);
                maxH = MathF.Max(maxH, h);
                sumH += h;
                samples++;
            }
        }

        if (samples == 0) return;

        GD.Print($"[TerrainComposition] region {regionHandle} '{terrain.Width}x{terrain.Height}' " +
                    $"start=[{starts[0]:F1},{starts[1]:F1},{starts[2]:F1},{starts[3]:F1}] " +
                    $"range=[{ranges[0]:F1},{ranges[1]:F1},{ranges[2]:F1},{ranges[3]:F1}] " +
                    $"water={terrain.WaterHeight:F1}");
        GD.Print($"[TerrainComposition] height min={minH:F1} mean={sumH / samples:F1} max={maxH:F1} " +
                    $"over {samples} samples");
        GD.Print($"[TerrainComposition] area split (whole region, mostly seabed)  " +
                    $"detail0={slotArea[0] / samples:P1} detail1={slotArea[1] / samples:P1} " +
                    $"detail2={slotArea[2] / samples:P1} detail3={slotArea[3] / samples:P1}");

        if (dryValues.Count > 0)
        {
            int dry = dryValues.Count;
            dryValues.Sort();
            GD.Print($"[TerrainComposition] area split ABOVE WATER ({dry} samples)  " +
                     $"detail0={slotAreaDry[0] / dry:P1} detail1={slotAreaDry[1] / dry:P1} " +
                     $"detail2={slotAreaDry[2] / dry:P1} detail3={slotAreaDry[3] / dry:P1}");
            GD.Print($"[TerrainComposition] composition value above water  " +
                     $"min={dryValues[0]:F2} p10={dryValues[dry / 10]:F2} " +
                     $"median={dryValues[dry / 2]:F2} p90={dryValues[dry * 9 / 10]:F2} " +
                     $"max={dryValues[dry - 1]:F2}");
        }
        GD.Print($"[TerrainComposition] detail ids  0={terrain.TerrainDetail0} 1={terrain.TerrainDetail1} " +
                    $"2={terrain.TerrainDetail2} 3={terrain.TerrainDetail3}");

        LogCompositionMap(terrain, originX, originY, starts, ranges);
    }

    /// <summary>
    /// Prints the composition as a coarse map, north at the top, so it can be laid over a
    /// screenshot directly.
    ///
    /// Summary statistics have repeatedly failed to settle this task: the same mean can come from
    /// evenly-spread mixing or from concentrated patches, and those look nothing alike. A map shows
    /// *where* each band falls, which is the thing actually in dispute against the real viewer.
    ///
    /// Each cell prints the dominant band as a digit when that band clearly wins (weight >= 70%),
    /// and as a letter a-d for the same band when the cell is a blend, so mixing zones are
    /// distinguishable from solid ones at a glance. '~' is below the waterline, '.' has no height
    /// data yet.
    ///
    /// The band comes from the CPU stand-in ramp, not the real gradient the shader samples, so
    /// treat it as a map of the composition VALUE rather than a pixel-accurate preview. That is
    /// the part in dispute; the ramp only shapes how sharply the bands meet.
    /// </summary>
    private static void LogCompositionMap(RegionTerrain terrain, double originX, double originY,
                                          float[] starts, float[] ranges)
    {
        const int Cells = 32;
        int stepX = Math.Max(1, terrain.Width / Cells);
        int stepY = Math.Max(1, terrain.Height / Cells);
        var weights = new float[SlTerrainComposition.AssetCount];

        GD.Print($"[TerrainComposition] map, north at top, {stepX}x{stepY} m per cell " +
                 "(digit = dominant band, lower case = mixed, ~ = under water, . = no data)");

        for (int y = terrain.Height - 1; y >= 0; y -= stepY)
        {
            var row = new System.Text.StringBuilder(Cells);
            for (int x = 0; x < terrain.Width; x += stepX)
            {
                if (!terrain.TryGetKnownHeight(x, y, out float h)) { row.Append('.'); continue; }
                if (h < terrain.WaterHeight) { row.Append('~'); continue; }

                float east = (float)x / terrain.Width;
                float north = (float)y / terrain.Height;
                float value = SlTerrainComposition.Value(
                    (float)(originX + x), (float)(originY + y), h,
                    SlTerrainComposition.BilinearCorners(starts, east, north),
                    SlTerrainComposition.BilinearCorners(ranges, east, north));

                SlTerrainComposition.Weights(value, weights);

                int best = 0;
                for (int i = 1; i < weights.Length; i++)
                {
                    if (weights[i] > weights[best]) best = i;
                }

                row.Append(weights[best] >= 0.7f ? (char)('0' + best) : (char)('a' + best));
            }
            GD.Print("[TerrainComposition] | " + row);
        }
    }

    /// <summary>A region's global SW corner expressed in one noise octave's lattice space and
    /// reduced modulo the 256-entry lattice, which noise2 is periodic in.</summary>
    private static Vector2 NoiseOrigin(double originX, double originY, double scale) =>
        new((float)Mod(originX * scale, 256.0), (float)Mod(originY * scale, 256.0));

    public void Initialize(World world, SLNG.Assets.AssetService? assetService, GpuCache? gpuCache)
    {
        _world = world;
        _assetService = assetService;
        _gpuCache = gpuCache;
        _world.TerrainUpdated += OnTerrainUpdated;
        _world.TerrainSettingsUpdated += OnTerrainSettingsUpdated;

        var terrainShader = ResourceLoader.Load<Shader>("res://materials/terrain.gdshader");
        _terrainMaterial.Shader = terrainShader;

        var waterShader = ResourceLoader.Load<Shader>("res://materials/water.gdshader");
        _waterMaterial.Shader = waterShader;
    }

    private void OnTerrainSettingsUpdated(object? sender, ulong regionHandle)
    {
        // Settings arrived. Trigger a rebuild and also start fetching textures.
        _dirtyRegions.Add(regionHandle);
        _ = FetchTerrainTexturesAsync(regionHandle);
    }

    private async System.Threading.Tasks.Task FetchTerrainTexturesAsync(ulong regionHandle)
    {
        if (_world == null || _assetService == null || _gpuCache == null) return;
        if (!_world.Terrains.TryGetValue(regionHandle, out var terrain)) return;
        if (!_regions.TryGetValue(regionHandle, out var node)) return;

        // Register interest in these ids *before* awaiting the fetch, mirroring
        // ObjectRenderer.BuildFaceMaterialAsync's AddRef-before-Put ordering (see GpuCache's
        // _pendingRefDelta doc comment) -- the terrain detail ids are already known synchronously
        // from RegionTerrain, same as ObjectRenderer already knows a face's texture id before its
        // fire-and-forget decode completes. This pins each entry the instant Put() adds it to the
        // cache. Without it, terrain detail textures sat at a permanent RefCount of 0 (unlike every
        // other GPU resource in the renderer) and were the first thing EvictIfNeeded() reclaimed
        // under a content-dense region's texture pressure -- the sibling gap to the object-texture
        // race fixed in 4d1358d, just never plugged for terrain specifically.
        SetTerrainTextureRefs(node, new List<Guid>(4)
        {
            terrain.TerrainDetail0, terrain.TerrainDetail1, terrain.TerrainDetail2, terrain.TerrainDetail3
        });

        // Fetch textures concurrently
        var t0 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail0);
        var t1 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail1);
        var t2 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail2);
        var t3 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail3);

        await System.Threading.Tasks.Task.WhenAll(t0, t1, t2, t3);

        CallDeferred(MethodName.ApplyTerrainTextures, regionHandle, t0.Result!, t1.Result!, t2.Result!, t3.Result!);
    }

    /// <summary>Diffs and applies the GpuCache ref-counts for a region's detail textures -- same
    /// add/release-the-delta pattern as ObjectRenderer.SetTexturesForVisual, kept separate because
    /// terrain tracks refs on RegionTerrainNode rather than a VisualState.</summary>
    private void SetTerrainTextureRefs(RegionTerrainNode node, List<Guid> newTextureIds)
    {
        if (_gpuCache == null) return;

        newTextureIds.RemoveAll(id => id == Guid.Empty);

        foreach (var old in node.UsedTextureIds)
        {
            if (!newTextureIds.Contains(old)) _gpuCache.ReleaseRef(old);
        }

        foreach (var newTex in newTextureIds)
        {
            if (!node.UsedTextureIds.Contains(newTex)) _gpuCache.AddRef(newTex);
        }

        node.UsedTextureIds = newTextureIds;
    }

    // FEAT-PERF-02: thin wrapper delegating to GpuCache.GetOrUploadTextureAsync (single-flight
    // fetch/decode/Image/upload shared across every renderer, not just terrain).
    //
    // FEAT-RENDER-02: generateMipmaps is now true. It had been false only because that was the
    // pre-existing behaviour, justified as "tiled many times across a large mesh" -- which is the
    // argument FOR mipmaps, not against. The terrain shader declares filter_linear_mipmap, and
    // Godot silently degrades that to plain linear when the texture has no mip chain. The real
    // terrain textures are 128x128 repeating every 12 m, so looking down at a region from any
    // height minifies them several times over; without mips that samples one arbitrary texel per
    // pixel and the ground shimmers and reads muddier than the texture's actual average colour.
    private System.Threading.Tasks.Task<ImageTexture?> GetOrCreateGpuTextureAsync(Guid textureId)
    {
        if (textureId == Guid.Empty || _gpuCache == null || _assetService == null)
            return System.Threading.Tasks.Task.FromResult<ImageTexture?>(null);

        return _gpuCache.GetOrUploadTextureAsync(textureId, _assetService, generateMipmaps: true);
    }

    private void ApplyTerrainTextures(ulong regionHandle, ImageTexture? tex0, ImageTexture? tex1, ImageTexture? tex2, ImageTexture? tex3)
    {
        if (!_regions.TryGetValue(regionHandle, out var node)) return;
        var mat = node.MeshInstance.MaterialOverride as ShaderMaterial;
        if (mat == null) return;

        if (tex0 != null) mat.SetShaderParameter("detail0", tex0);
        if (tex1 != null) mat.SetShaderParameter("detail1", tex1);
        if (tex2 != null) mat.SetShaderParameter("detail2", tex2);
        if (tex3 != null) mat.SetShaderParameter("detail3", tex3);
    }

    private void OnTerrainUpdated(object? sender, ulong regionHandle)
    {
        // World events are applied on the main thread (WorldSimulation.Pump), so this is
        // safe. Just mark the region dirty; the rebuild is coalesced in _Process.
        _dirtyRegions.Add(regionHandle);
    }

    private double _rebuildAccum;

    /// <summary>Baseline coalescing window. Patches stream in over many frames, so a rebuild per
    /// patch would be absurd; this is the floor, not the actual interval -- see _nextRebuildDelay.</summary>
    private const double MinRebuildInterval = 0.75;

    /// <summary>Hard ceiling on the adaptive backoff, so a pathological region cannot stop updating
    /// its terrain altogether.</summary>
    private const double MaxRebuildInterval = 10.0;

    /// <summary>
    /// How long to wait before the next rebuild, grown from how long the LAST one actually took.
    ///
    /// Without this the rebuild rate is a death spiral, and the watchdog logged it: a rebuild that
    /// costs 2 s against a fixed 0.75 s interval means the next one is already due the moment the
    /// last finishes, so the main thread does nothing else for as long as patches keep arriving --
    /// phase=terrain stalls of 2.1 to 7.1 s while the work queue backed up to 11,849 items behind
    /// them. Scaling the wait to the observed cost makes the terrain self-limiting: cheap rebuilds
    /// stay responsive at the 0.75 s floor, expensive ones automatically get out of the way.
    /// </summary>
    private double _nextRebuildDelay = MinRebuildInterval;

    public override void _Process(double delta)
    {
        using var _phase = MainThreadPhase.Enter("terrain");

        if (_dirtyRegions.Count == 0) return;

        _rebuildAccum += delta;
        if (_rebuildAccum < _nextRebuildDelay) return;
        _rebuildAccum = 0;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        foreach (var regionHandle in _dirtyRegions)
        {
            RebuildTerrain(regionHandle);
        }
        _dirtyRegions.Clear();

        double tookSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                             / (double)System.Diagnostics.Stopwatch.Frequency;

        // Spend at most ~20% of wall-clock time rebuilding terrain: wait four times as long as the
        // rebuild itself took, never less than the floor or more than the ceiling.
        _nextRebuildDelay = Math.Clamp(tookSeconds * 4.0, MinRebuildInterval, MaxRebuildInterval);

        if (tookSeconds > 0.1)
        {
            Logger.Info($"[TerrainRebuild] {_regions.Count} region(s) in {tookSeconds * 1000:0} ms " +
                        $"— next rebuild in {_nextRebuildDelay:0.00}s");
        }
    }

    private void RebuildTerrain(ulong regionHandle)
    {
        if (_world == null) return;

        if (!_world.Terrains.TryGetValue(regionHandle, out var regionTerrain))
        {
            // Region was removed
            if (_regions.TryGetValue(regionHandle, out var node))
            {
                if (_gpuCache != null)
                    foreach (var texId in node.UsedTextureIds) _gpuCache.ReleaseRef(texId);
                node.QueueFree();
                _regions.Remove(regionHandle);
            }
            return;
        }

        if (!_regions.TryGetValue(regionHandle, out var regionNode))
        {
            regionNode = new RegionTerrainNode();
            AddChild(regionNode.Root);
            _regions[regionHandle] = regionNode;

            // Position root node relative to the floating origin (see RenderConfig) so terrain
            // lines up with objects/avatars without overflowing float precision on OSGrid.
            regionNode.Root.Position = RenderConfig.ToGodot(regionHandle, System.Numerics.Vector3.Zero);
        }

        var heights = regionTerrain.GetHeights();
        int width = regionTerrain.Width;
        int height = regionTerrain.Height;

        // Built as INDEXED arrays handed to Godot in one call, rather than per-vertex through
        // SurfaceTool. The watchdog caught this method holding the main thread for up to 12.2 s
        // (phase=terrain), and the old shape explains why: 6 unshared vertices per quad meant
        // ~390,000 individual AddVertex interop calls for a 256x256 region -- and up to 6.3 million
        // for a 1024x1024 varregion -- repeated every 0.75 s for as long as patches keep streaming
        // in. Sharing vertices between adjacent quads also cuts the vertex count by about six.
        //
        // Normals are accumulated per vertex here instead of via GenerateNormals(). The face normal
        // formula deliberately mirrors Godot's own (Plane's three-point constructor, which
        // SurfaceTool uses): normal = (a - c) x (a - b). Getting this wrong would invert terrain
        // lighting, and the winding below is likewise kept exactly as it was -- the collision shape
        // is built from the same triangles, and a flipped winding makes a ConcavePolygonShape3D
        // that the avatar's ground ray passes straight through.
        // Timed with a timestamp pair rather than a Measure() scope: the vertex and index lists built
        // here are used well past the end of this section, and wrapping them in a lambda purely to
        // time them would mean hoisting every one of them out by hand.
        long geometryStart = System.Diagnostics.Stopwatch.GetTimestamp();

        var vertexOf = new int[width * height];
        System.Array.Fill(vertexOf, -1);

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();

        int VertexFor(int x, int z)
        {
            int cell = z * width + x;
            int existing = vertexOf[cell];
            if (existing >= 0) return existing;

            verts.Add(new Vector3(x, heights[cell], -z));
            normals.Add(Vector3.Zero);
            vertexOf[cell] = verts.Count - 1;
            return vertexOf[cell];
        }

        void AddTriangle(int a, int b, int c)
        {
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);

            var n = (verts[a] - verts[c]).Cross(verts[a] - verts[b]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }

        for (int z = 0; z < height - 1; z++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                // Skip any quad touching a cell whose patch hasn't streamed in yet (see
                // RegionTerrain.TryGetKnownHeight) rather than building it from the array's 0.0f
                // default -- this mesh's collider is what AvatarController's ground-clamp raycasts
                // against, so baking a flat walkable surface at height 0 for not-yet-loaded terrain
                // let the avatar land ON that false floor, usually well below the real ~20-25 m
                // terrain. Leaving a hole means the raycast genuinely misses, hasGround stays false,
                // and the avatar holds position until the real patch arrives and a later rebuild
                // fills it in.
                if (!regionTerrain.TryGetKnownHeight(x, z, out _) ||
                    !regionTerrain.TryGetKnownHeight(x + 1, z, out _) ||
                    !regionTerrain.TryGetKnownHeight(x, z + 1, out _) ||
                    !regionTerrain.TryGetKnownHeight(x + 1, z + 1, out _))
                {
                    continue;
                }

                int v0 = VertexFor(x, z);
                int v1 = VertexFor(x + 1, z);
                int v2 = VertexFor(x, z + 1);
                int v3 = VertexFor(x + 1, z + 1);

                AddTriangle(v0, v2, v1);
                AddTriangle(v1, v2, v3);
            }
        }

        if (indices.Count == 0)
        {
            // Nothing known yet. Clear rather than leave stale geometry, and leave no collider, so
            // the ground ray misses and AvatarController holds position instead of landing on a
            // surface that isn't there.
            regionNode.MeshInstance.Mesh = null;
            regionNode.CollisionShape.Shape = null;
            return;
        }

        MainThreadWorkQueue.RecordExternal("terrain.geometry",
            (System.Diagnostics.Stopwatch.GetTimestamp() - geometryStart) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency);

        var vertArray = verts.ToArray();
        var normalArray = new Vector3[normals.Count];
        for (int i = 0; i < normals.Count; i++)
        {
            // A vertex only referenced by degenerate triangles accumulates a zero normal, which
            // Normalized() would turn into NaN and the shader into a black hole in the terrain.
            var n = normals[i];
            normalArray[i] = n.LengthSquared() > 0f ? n.Normalized() : Vector3.Up;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertArray;
        arrays[(int)Mesh.ArrayType.Normal] = normalArray;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        MainThreadWorkQueue.Measure("terrain.mesh", () =>
        {
            var built = new ArrayMesh();
            built.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            regionNode.MeshInstance.Mesh = built;
        });

        // Collision from the same CPU triangles rather than CreateTrimeshShape(), which reads every
        // face back out of the rendering server first -- the identical GPU-readback cost already
        // removed from the object path, and far worse here because it is one enormous mesh.
        // The measurement settled it: as a ConcavePolygonShape3D this was 18.50 s across four
        // rebuilds -- 4,626 ms each, worst case 8,037 ms -- against 219 ms for building the geometry
        // and 80 ms for uploading the mesh. Building a BVH over ~130,000 triangles inside the
        // physics server was, on its own, the entire terrain freeze.
        //
        // A triangle soup is simply the wrong structure for terrain. Terrain IS a height field, and
        // HeightMapShape3D is the shape built for one: the physics server indexes it arithmetically
        // from a grid position, so there is no acceleration structure to build at all and the cost
        // collapses to copying floats.
        //
        // Index mapping: HeightMapShape3D centres its grid on the shape's own origin, so cell (i, j)
        // sits at shape-local (i - (W-1)/2, h, j - (D-1)/2). The visual mesh puts grid cell (x, z) at
        // (x, h, -z). Offsetting the CollisionShape3D by ((W-1)/2, 0, -(D-1)/2) makes i = x, and the
        // negated Z axis makes the rows run backwards: j = (D-1) - z.
        MainThreadWorkQueue.Measure("terrain.collision", () =>
        {
            var map = new float[width * height];
            int holes = 0;

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int dst = (height - 1 - z) * width + x;
                    if (regionTerrain.TryGetKnownHeight(x, z, out float h))
                    {
                        map[dst] = h;
                    }
                    else
                    {
                        // NaN marks a hole, matching what the mesh does by omitting the quad. Both
                        // exist for the same reason: a cell whose patch has not arrived must make the
                        // ground ray MISS, so AvatarController holds position, rather than reporting
                        // a walkable floor at the array's 0.0f default well below the real terrain.
                        map[dst] = float.NaN;
                        holes++;
                    }
                }
            }

            var shape = new HeightMapShape3D { MapWidth = width, MapDepth = height };
            shape.MapData = map; // after the dimensions -- setting those resizes the data array
            regionNode.CollisionShape.Shape = shape;
            regionNode.CollisionShape.Position = new Vector3((width - 1) / 2f, 0f, -(height - 1) / 2f);

            if (holes > 0)
            {
                Logger.Info($"[TerrainCollision] {width}x{height} height field, {holes} cell(s) still " +
                            $"unloaded and left as holes");
            }
        });

        // Use a per-region material instance
        ShaderMaterial mat;
        if (regionNode.MeshInstance.MaterialOverride is ShaderMaterial existingMat)
        {
            mat = existingMat;
        }
        else
        {
            mat = (ShaderMaterial)_terrainMaterial.Duplicate();
            regionNode.MeshInstance.MaterialOverride = mat;
        }

        // Pass height parameters to shader
        mat.SetShaderParameter("start_height", new Vector4(
            regionTerrain.TerrainStartHeights[0], regionTerrain.TerrainStartHeights[1],
            regionTerrain.TerrainStartHeights[2], regionTerrain.TerrainStartHeights[3]));

        mat.SetShaderParameter("height_range", new Vector4(
            regionTerrain.TerrainHeightRanges[0], regionTerrain.TerrainHeightRanges[1],
            regionTerrain.TerrainHeightRanges[2], regionTerrain.TerrainHeightRanges[3]));

        mat.SetShaderParameter("region_size", (float)regionTerrain.Width);

        // Terrain composition parity (see sl_terrain_composition.gdshaderinc). The noise is
        // sampled in SL GLOBAL coordinates so the pattern runs continuously across region
        // borders; we hand the shader each octave's lattice origin rather than the raw global
        // position, because global coordinates run to the hundreds of thousands where float32
        // has lost the centimetres the noise needs.
        double originX = (uint)(regionHandle >> 32);
        double originY = (uint)(regionHandle & 0xFFFFFFFF);

        const double xyScaleInv = 1.0 / 4.9215;
        mat.SetShaderParameter("sl_noise_origin_low", NoiseOrigin(originX, originY, xyScaleInv * 0.2222222222));
        mat.SetShaderParameter("sl_noise_origin_mid", NoiseOrigin(originX, originY, xyScaleInv));
        mat.SetShaderParameter("sl_noise_origin_high", NoiseOrigin(originX, originY, xyScaleInv * 2.0));

        // The viewer's offset_x/offset_y: the detail UV is continuous in global space, phased by
        // the region origin reduced modulo one texture repeat (LLDrawPoolTerrain).
        float detailScale = SlTerrainComposition.DetailScaleMetres;
        mat.SetShaderParameter("sl_detail_scale_m", detailScale);
        mat.SetShaderParameter("sl_detail_origin_m", new Vector2(
            (float)Mod(originX, detailScale), (float)Mod(originY, detailScale)));

        mat.SetShaderParameter("sl_noise_origin_ramp",
            NoiseOrigin(originX, originY, SlTerrainComposition.RampNoiseScale));

        mat.SetShaderParameter("sl_noise_lut", GetNoiseLut());
        mat.SetShaderParameter("sl_alpha_ramp", GetAlphaRamp());

        LogCompositionDiagnostics(regionHandle, regionTerrain);

        // Build water plane
        MainThreadWorkQueue.Measure("terrain.water", () => BuildWaterPlane(regionNode, regionTerrain));

        // Fetch/apply textures in case RebuildTerrain runs after settings arrived,
        // or if settings arrived very quickly and were missed before the mesh existed.
        _ = FetchTerrainTexturesAsync(regionHandle);
    }

    private void BuildWaterPlane(RegionTerrainNode node, RegionTerrain regionTerrain)
    {
        // Try to find existing water instance
        MeshInstance3D? waterInstance = null;
        foreach (var child in node.Root.GetChildren())
        {
            if (child.Name == "WaterPlane")
            {
                waterInstance = child as MeshInstance3D;
                break;
            }
        }

        PlaneMesh planeMesh;
        if (waterInstance == null)
        {
            waterInstance = new MeshInstance3D
            {
                Name = "WaterPlane"
            };
            node.Root.AddChild(waterInstance);

            // 7 subdivisions = 8 segments = one quad per 32 m on a 256 m region, which is
            // exactly LLVOWater's own tessellation (llvowater.cpp:137-142, 8x8 quads with
            // transparent water on). It matters because water.gdshader generates the wave UVs per
            // vertex including a non-linear sweep term, so the mesh density is part of the wave
            // pattern rather than a free quality knob.
            planeMesh = new PlaneMesh
            {
                SubdivideWidth = 7,
                SubdivideDepth = 7
            };
            waterInstance.Mesh = planeMesh;
            waterInstance.MaterialOverride = _waterMaterial;
        }
        else
        {
            planeMesh = (PlaneMesh)waterInstance.Mesh;
        }

        planeMesh.Size = new Vector2(regionTerrain.Width, regionTerrain.Height);

        // SL origin for the region is 0,0 but PlaneMesh is centered
        // Also Godot Z is -Y in SL
        waterInstance.Position = new Vector3(regionTerrain.Width / 2f, regionTerrain.WaterHeight, -regionTerrain.Height / 2f);
    }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.TerrainUpdated -= OnTerrainUpdated;
        }
        if (_gpuCache != null)
        {
            foreach (var node in _regions.Values)
                foreach (var texId in node.UsedTextureIds) _gpuCache.ReleaseRef(texId);
        }
        _regions.Clear();
        _terrainMaterial?.Dispose();
        _waterMaterial?.Dispose();
    }
}
