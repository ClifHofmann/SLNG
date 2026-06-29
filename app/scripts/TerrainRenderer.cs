using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using System;
using System.Collections.Generic;

namespace SLNG.App;

public partial class TerrainRenderer : Node3D
{
    private class RegionTerrainNode
    {
        public Node3D Root { get; }
        public MeshInstance3D MeshInstance { get; }
        public StaticBody3D StaticBody { get; }
        public CollisionShape3D CollisionShape { get; }

        public RegionTerrainNode()
        {
            Root = new Node3D();
            MeshInstance = new MeshInstance3D();
            StaticBody = new StaticBody3D();
            CollisionShape = new CollisionShape3D();

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

        // Fetch textures concurrently
        var t0 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail0);
        var t1 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail1);
        var t2 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail2);
        var t3 = GetOrCreateGpuTextureAsync(terrain.TerrainDetail3);

        await System.Threading.Tasks.Task.WhenAll(t0, t1, t2, t3);

        CallDeferred(MethodName.ApplyTerrainTextures, regionHandle, t0.Result, t1.Result, t2.Result, t3.Result);
    }

    private async System.Threading.Tasks.Task<ImageTexture?> GetOrCreateGpuTextureAsync(Guid textureId)
    {
        if (textureId == Guid.Empty) return null;

        if (_gpuCache != null)
        {
            var cached = _gpuCache.Get(textureId) as ImageTexture;
            if (cached != null) return cached;
        }

        if (_assetService == null) return null;

        var textureData = await _assetService.GetTextureAsync(textureId);
        if (textureData == null) return null;

        var tcs = new System.Threading.Tasks.TaskCompletionSource<ImageTexture?>();
        
        Godot.Callable.From(() => {
            var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
            var tex = ImageTexture.CreateFromImage(image);
            
            if (tex != null && _gpuCache != null)
            {
                long size = textureData.Width * textureData.Height * 4;
                _gpuCache.Put(textureId, tex, size);
            }
            tcs.SetResult(tex);
        }).CallDeferred();

        return await tcs.Task;
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

    public override void _Process(double delta)
    {
        if (_dirtyRegions.Count == 0) return;

        foreach (var regionHandle in _dirtyRegions)
        {
            RebuildTerrain(regionHandle);
        }
        _dirtyRegions.Clear();
    }

    private void RebuildTerrain(ulong regionHandle)
    {
        if (_world == null) return;

        if (!_world.Terrains.TryGetValue(regionHandle, out var regionTerrain))
        {
            // Region was removed
            if (_regions.TryGetValue(regionHandle, out var node))
            {
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

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // Generate vertices
        for (int z = 0; z < height - 1; z++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                int i0 = z * width + x;
                int i1 = z * width + (x + 1);
                int i2 = (z + 1) * width + x;
                int i3 = (z + 1) * width + (x + 1);

                Vector3 v0 = new Vector3(x, heights[i0], -z);
                Vector3 v1 = new Vector3(x + 1, heights[i1], -z);
                Vector3 v2 = new Vector3(x, heights[i2], -(z + 1));
                Vector3 v3 = new Vector3(x + 1, heights[i3], -(z + 1));

                // Triangle 1
                st.AddVertex(v0);
                st.AddVertex(v2);
                st.AddVertex(v1);

                // Triangle 2
                st.AddVertex(v1);
                st.AddVertex(v2);
                st.AddVertex(v3);
            }
        }

        st.GenerateNormals();
        var mesh = st.Commit();

        regionNode.MeshInstance.Mesh = mesh;
        regionNode.CollisionShape.Shape = mesh.CreateTrimeshShape();
        
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

        // Build water plane
        BuildWaterPlane(regionNode, regionTerrain);
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

        if (waterInstance == null)
        {
            waterInstance = new MeshInstance3D
            {
                Name = "WaterPlane"
            };
            node.Root.AddChild(waterInstance);

            var planeMesh = new PlaneMesh
            {
                Size = new Vector2(regionTerrain.Width, regionTerrain.Height),
                SubdivideWidth = 10,
                SubdivideDepth = 10
            };
            waterInstance.Mesh = planeMesh;
            waterInstance.MaterialOverride = _waterMaterial;
        }

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
    }
}
