using Godot;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using System.Collections.Generic;
using System;

namespace SLNG.App;

public partial class ObjectRenderer : Node3D
{
    private World? _world;
    private SLNG.Assets.AssetService? _assetService;
    private readonly Dictionary<Guid, MeshInstance3D> _visuals = new();

    // Box = 0 (Square), Cylinder = 1 (Circle), Sphere = 5 (HalfCircle) in LibreMetaverse typically
    // Actually, ProfileCurve maps roughly: Circle = 0, Square = 1, IsoTriangle = 2, EqualTriangle = 3, RightTriangle = 4, HalfCircle = 5
    // Let's do a basic mapping
    private Mesh _boxMesh = new BoxMesh();
    private Mesh _sphereMesh = new SphereMesh();
    private Mesh _cylinderMesh = new CylinderMesh();

    public void Initialize(World world, SLNG.Assets.AssetService assetService)
    {
        _world = world;
        _assetService = assetService;

        _world.EntityAdded += OnEntityAdded;
        _world.EntityRemoved += OnEntityRemoved;
        _world.ComponentUpdated += OnComponentUpdated;
    }

    private void OnEntityAdded(object? sender, EntityEventArgs e)
    {
        CallDeferred(nameof(CreateVisual), e.Entity.Id.ToString());
    }

    private void OnEntityRemoved(object? sender, EntityEventArgs e)
    {
        CallDeferred(nameof(RemoveVisual), e.Entity.Id.ToString());
    }

    private void OnComponentUpdated(object? sender, ComponentEventArgs e)
    {
        CallDeferred(nameof(UpdateVisual), e.Entity.Id.ToString());
    }

    private void CreateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.ContainsKey(entityId)) return;

        var meshInstance = new MeshInstance3D();
        AddChild(meshInstance);
        _visuals[entityId] = meshInstance;

        UpdateVisual(entityIdStr);
    }

    private void RemoveVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.TryGetValue(entityId, out var meshInstance))
        {
            meshInstance.QueueFree();
            _visuals.Remove(entityId);
        }
    }

    private void UpdateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_world == null) return;
        if (!_visuals.TryGetValue(entityId, out var meshInstance)) return;

        var entity = _world.GetEntity(entityId);
        if (entity == null) return;

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim != null)
        {
            if (prim.IsMesh && _assetService != null && prim.MeshId != Guid.Empty)
            {
                // We use an async fire-and-forget to load the mesh
                // Godot's CallDeferred inside the async method will ensure we update the mesh thread-safely
                _ = LoadAndApplyMeshAsync(meshInstance, prim.MeshId);
            }
            else
            {
                // Fallback primitives
                if (prim.ProfileCurve == 0) // Circle -> Cylinder
                {
                    meshInstance.Mesh = new CylinderMesh();
                }
                else if (prim.ProfileCurve == 5) // HalfCircle -> Sphere
                {
                    meshInstance.Mesh = new SphereMesh();
                }
                else // Square -> Box
                {
                    meshInstance.Mesh = new BoxMesh();
                }
            }

            // Apply Scale
            // SL uses Z up, Godot uses Y up, so we swap Y and Z in the scale
            meshInstance.Scale = new Godot.Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y);
        }

        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            // Extract global region offset
            uint regionX = (uint)(entity.RegionHandle >> 32);
            uint regionY = (uint)(entity.RegionHandle & 0xFFFFFFFF);

            // Apply Position (SL Z-up -> Godot Y-up)
            // Godot X = SL RegionX + SL X
            // Godot Y = SL Z
            // Godot Z = -(SL RegionY + SL Y)
            meshInstance.Position = new Godot.Vector3(
                regionX + transform.Position.X, 
                transform.Position.Z, 
                -(regionY + transform.Position.Y));

            // Apply Rotation (SL Z-up -> Godot Y-up mapping)
            // This is a naive conversion; exact SL-to-Godot quaternion conversion might require careful axis flipping
            var slQuat = new Godot.Quaternion(transform.Rotation.X, transform.Rotation.Z, -transform.Rotation.Y, transform.Rotation.W);
            meshInstance.Quaternion = slQuat;
        }
    }

    private async System.Threading.Tasks.Task LoadAndApplyMeshAsync(MeshInstance3D meshInstance, Guid meshId)
    {
        if (_assetService == null) return;
        
        var facetedMesh = await _assetService.GetMeshAsync(meshId);
        if (facetedMesh == null || facetedMesh.Faces == null || facetedMesh.Faces.Count == 0) return;

        // Create the ArrayMesh on the main thread
        Godot.Callable.From(() => ApplyFacetedMesh(meshInstance, facetedMesh)).CallDeferred();
    }

    private void ApplyFacetedMesh(MeshInstance3D meshInstance, LibreMetaverse.Rendering.FacetedMesh facetedMesh)
    {
        if (meshInstance == null || !IsInstanceValid(meshInstance)) return;

        var arrayMesh = new ArrayMesh();
        var st = new SurfaceTool();

        int surfaceIndex = 0;
        foreach (var face in facetedMesh.Faces)
        {
            if (face.Vertices == null || face.Indices == null || face.Indices.Count == 0) continue;

            st.Begin(Mesh.PrimitiveType.Triangles);

            for (int i = 0; i < face.Indices.Count; i++)
            {
                var vertex = face.Vertices[face.Indices[i]];
                
                // Decode positions & normals
                // Position: SL Z-up -> Godot Y-up
                st.SetNormal(new Godot.Vector3(vertex.Normal.X, vertex.Normal.Z, -vertex.Normal.Y));
                st.SetUV(new Godot.Vector2(vertex.TexCoord.X, vertex.TexCoord.Y));
                st.AddVertex(new Godot.Vector3(vertex.Position.X, vertex.Position.Z, -vertex.Position.Y));
            }

            st.GenerateTangents(); // Useful for PBR later
            st.Commit(arrayMesh);
            surfaceIndex++;
        }

        meshInstance.Mesh = arrayMesh;
    }

    public override void _ExitTree()
    {
        if (_world != null)
        {
            _world.EntityAdded -= OnEntityAdded;
            _world.EntityRemoved -= OnEntityRemoved;
            _world.ComponentUpdated -= OnComponentUpdated;
        }
    }
}
