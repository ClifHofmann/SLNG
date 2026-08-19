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
            StaticBody = new StaticBody3D { Name = "TerrainPhysics" };
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

    private World? _world;
    private SLNG.Assets.AssetService? _assetService;
    private GpuCache? _gpuCache;

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
    // fetch/decode/Image/upload shared across every renderer, not just terrain). generateMipmaps
    // stays false here, matching this method's pre-existing behavior -- unlike ObjectRenderer/
    // AvatarRenderer, terrain detail textures were never mipmapped (tiled many times across a
    // large mesh via the terrain shader), so this preserves that rather than silently changing it.
    private System.Threading.Tasks.Task<ImageTexture?> GetOrCreateGpuTextureAsync(Guid textureId)
    {
        if (textureId == Guid.Empty || _gpuCache == null || _assetService == null)
            return System.Threading.Tasks.Task.FromResult<ImageTexture?>(null);

        return _gpuCache.GetOrUploadTextureAsync(textureId, _assetService, generateMipmaps: false);
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

            planeMesh = new PlaneMesh
            {
                SubdivideWidth = 10,
                SubdivideDepth = 10
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
