using Godot;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using System.Collections.Generic;
using System;

namespace SLNG.App;

public partial class AvatarRenderer : Node3D
{
    private World? _world;
    private readonly Dictionary<Guid, MeshInstance3D> _visuals = new();
    
    // A standard capsule mesh as placeholder for the avatar
    private Mesh _avatarMesh = new CapsuleMesh() 
    { 
        Radius = 0.45f, 
        Height = 1.9f 
    };

    public void Initialize(World world)
    {
        _world = world;

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
        if (_world == null) return;

        var entity = _world.GetEntity(entityId);
        if (entity == null || entity.GetComponent<AvatarComponent>() == null) return;

        var meshInstance = new MeshInstance3D
        {
            Mesh = _avatarMesh
        };
        
        var material = new StandardMaterial3D();
        var avatar = entity.GetComponent<AvatarComponent>();
        if (avatar != null && avatar.IsLocalAgent)
        {
            // Give the local agent a distinct color (e.g. Blue)
            material.AlbedoColor = new Godot.Color(0.2f, 0.4f, 1.0f);
        }
        else
        {
            // Other avatars (e.g. Green)
            material.AlbedoColor = new Godot.Color(0.2f, 0.8f, 0.2f);
        }
        meshInstance.MaterialOverride = material;

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
        if (!_visuals.TryGetValue(entityId, out var meshInstance))
        {
            // Might have just gained the AvatarComponent
            CreateVisual(entityIdStr);
            return;
        }

        var entity = _world.GetEntity(entityId);
        if (entity == null || entity.GetComponent<AvatarComponent>() == null) return;

        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            uint regionX = (uint)(entity.RegionHandle >> 32);
            uint regionY = (uint)(entity.RegionHandle & 0xFFFFFFFF);

            // Capsule origin is the center. SL avatars stand on the ground, so we might need an offset,
            // but for a placeholder, centering it at the given position is fine.
            meshInstance.Position = new Godot.Vector3(
                regionX + transform.Position.X, 
                transform.Position.Z + 0.95f, // offset by half height so feet are roughly on ground
                -(regionY + transform.Position.Y));

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
