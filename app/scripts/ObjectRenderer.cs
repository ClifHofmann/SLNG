using Godot;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using System.Collections.Generic;

namespace SLNG.App;

public partial class ObjectRenderer : Node3D
{
    private World? _world;
    private readonly Dictionary<uint, MeshInstance3D> _visuals = new();

    // Box = 0 (Square), Cylinder = 1 (Circle), Sphere = 5 (HalfCircle) in LibreMetaverse typically
    // Actually, ProfileCurve maps roughly: Circle = 0, Square = 1, IsoTriangle = 2, EqualTriangle = 3, RightTriangle = 4, HalfCircle = 5
    // Let's do a basic mapping
    private Mesh _boxMesh = new BoxMesh();
    private Mesh _sphereMesh = new SphereMesh();
    private Mesh _cylinderMesh = new CylinderMesh();

    public void Initialize(World world)
    {
        _world = world;
        _world.EntityAdded += OnEntityAdded;
        _world.EntityRemoved += OnEntityRemoved;
        _world.ComponentUpdated += OnComponentUpdated;
    }

    private void OnEntityAdded(object? sender, EntityEventArgs e)
    {
        CallDeferred(nameof(CreateVisual), e.Entity.LocalId);
    }

    private void OnEntityRemoved(object? sender, EntityEventArgs e)
    {
        CallDeferred(nameof(RemoveVisual), e.Entity.LocalId);
    }

    private void OnComponentUpdated(object? sender, ComponentEventArgs e)
    {
        CallDeferred(nameof(UpdateVisual), e.Entity.LocalId);
    }

    private void CreateVisual(uint entityId)
    {
        if (_visuals.ContainsKey(entityId)) return;

        var meshInstance = new MeshInstance3D();
        AddChild(meshInstance);
        _visuals[entityId] = meshInstance;

        UpdateVisual(entityId);
    }

    private void RemoveVisual(uint entityId)
    {
        if (_visuals.TryGetValue(entityId, out var meshInstance))
        {
            meshInstance.QueueFree();
            _visuals.Remove(entityId);
        }
    }

    private void UpdateVisual(uint entityId)
    {
        if (_world == null) return;
        if (!_visuals.TryGetValue(entityId, out var meshInstance)) return;

        var entity = _world.GetOrCreateEntity(entityId);

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim != null)
        {
            // Map ProfileCurve to a Mesh type
            // 0 = Circle (Cylinder/Tube), 1 = Square (Box), 5 = HalfCircle (Sphere)
            meshInstance.Mesh = prim.ProfileCurve switch
            {
                1 => _boxMesh,
                5 => _sphereMesh,
                0 => _cylinderMesh,
                _ => _boxMesh // Default fallback
            };

            // Apply Scale
            // SL uses Z up, Godot uses Y up, so we swap Y and Z in the scale
            meshInstance.Scale = new Godot.Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y);
        }

        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            // Apply Position (SL Z-up -> Godot Y-up)
            meshInstance.Position = new Godot.Vector3(transform.Position.X, transform.Position.Z, -transform.Position.Y);

            // Apply Rotation (SL Z-up -> Godot Y-up mapping)
            // This is a naive conversion; exact SL-to-Godot quaternion conversion might require careful axis flipping
            var slQuat = new Godot.Quaternion(transform.Rotation.X, transform.Rotation.Z, -transform.Rotation.Y, transform.Rotation.W);
            meshInstance.Quaternion = slQuat;
        }
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
