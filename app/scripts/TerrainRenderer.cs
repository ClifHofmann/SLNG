using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using System;

namespace SLNG.App;

public partial class TerrainRenderer : Node3D
{
    private MeshInstance3D _meshInstance = null!;
    private StaticBody3D _staticBody = null!;
    private CollisionShape3D _collisionShape = null!;
    
    private World? _world;

    public override void _Ready()
    {
        _meshInstance = new MeshInstance3D();
        AddChild(_meshInstance);

        _staticBody = new StaticBody3D();
        AddChild(_staticBody);

        _collisionShape = new CollisionShape3D();
        _staticBody.AddChild(_collisionShape);
    }

    public void Initialize(World world)
    {
        _world = world;
        _world.TerrainUpdated += OnTerrainUpdated;
    }

    private void OnTerrainUpdated(object? sender, EventArgs e)
    {
        // Must run on main thread to interact with Godot Nodes
        CallDeferred(nameof(RebuildTerrain));
    }

    private void RebuildTerrain()
    {
        if (_world == null) return;

        var heights = _world.Terrain.GetHeights();
        int width = _world.Terrain.Width;
        int height = _world.Terrain.Height;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // Optional: Simple material
        var mat = new StandardMaterial3D();
        mat.AlbedoColor = new Color(0.2f, 0.6f, 0.2f); // Greenish
        st.SetMaterial(mat);

        // Generate vertices
        for (int z = 0; z < height - 1; z++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                // In Second Life, coordinate system is usually Z-up. Godot is Y-up.
                // We'll map OpenSim X to Godot X, and OpenSim Y to Godot -Z
                // Height (Z in SL) -> Godot Y
                
                int i0 = z * width + x;
                int i1 = z * width + (x + 1);
                int i2 = (z + 1) * width + x;
                int i3 = (z + 1) * width + (x + 1);

                Vector3 v0 = new Vector3(x, heights[i0], -z);
                Vector3 v1 = new Vector3(x + 1, heights[i1], -z);
                Vector3 v2 = new Vector3(x, heights[i2], -(z + 1));
                Vector3 v3 = new Vector3(x + 1, heights[i3], -(z + 1));

                // Triangle 1 (v0, v2, v1)
                st.SetNormal(CalculateNormal(v0, v2, v1));
                st.AddVertex(v0);
                st.SetNormal(CalculateNormal(v2, v1, v0));
                st.AddVertex(v2);
                st.SetNormal(CalculateNormal(v1, v0, v2));
                st.AddVertex(v1);

                // Triangle 2 (v1, v2, v3)
                st.SetNormal(CalculateNormal(v1, v3, v2));
                st.AddVertex(v1);
                st.SetNormal(CalculateNormal(v2, v1, v3));
                st.AddVertex(v2);
                st.SetNormal(CalculateNormal(v3, v2, v1));
                st.AddVertex(v3);
            }
        }

        st.GenerateNormals(); // Redundant if we provide good normals, but good for safety
        
        var arrayMesh = st.Commit();
        _meshInstance.Mesh = arrayMesh;

        // Create CollisionShape
        var shape = new HeightMapShape3D();
        shape.MapWidth = width;
        shape.MapDepth = height;
        // HeightMapShape3D takes a 1D float array of size MapWidth * MapDepth
        // In Godot, Y is height. The data is row-major.
        shape.MapData = heights; 
        
        _collisionShape.Shape = shape;
        
        // HeightMapShape3D is centered by default in Godot, 
        // we might need to offset it to match the mesh, which starts at (0, 0, 0).
        _collisionShape.Position = new Vector3((width - 1) / 2.0f, 0, -(height - 1) / 2.0f);
    }

    private Vector3 CalculateNormal(Vector3 p1, Vector3 p2, Vector3 p3)
    {
        var u = p2 - p1;
        var v = p3 - p1;
        return u.Cross(v).Normalized();
    }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.TerrainUpdated -= OnTerrainUpdated;
        }
    }
}
