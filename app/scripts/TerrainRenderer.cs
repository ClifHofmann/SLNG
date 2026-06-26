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
    private readonly StandardMaterial3D _terrainMaterial = new() { AlbedoColor = new Color(0.2f, 0.6f, 0.2f) };

    private World? _world;

    public void Initialize(World world)
    {
        _world = world;
        _world.TerrainUpdated += OnTerrainUpdated;
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

            // Position root node based on global coordinates
            uint regionX = (uint)(regionHandle >> 32);
            uint regionY = (uint)(regionHandle & 0xFFFFFFFF);
            
            // Godot X = SL X, Godot -Z = SL Y
            regionNode.Root.Position = new Vector3(regionX, 0, -regionY);
        }

        var heights = regionTerrain.GetHeights();
        int width = regionTerrain.Width;
        int height = regionTerrain.Height;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetMaterial(_terrainMaterial);

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
        
        var arrayMesh = st.Commit();
        regionNode.MeshInstance.Mesh = arrayMesh;

        // Create CollisionShape
        var shape = new HeightMapShape3D();
        shape.MapWidth = width;
        shape.MapDepth = height;
        shape.MapData = heights; 
        
        regionNode.CollisionShape.Shape = shape;
        regionNode.CollisionShape.Position = new Vector3((width - 1) / 2.0f, 0, -(height - 1) / 2.0f);
    }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.TerrainUpdated -= OnTerrainUpdated;
        }
    }
}
