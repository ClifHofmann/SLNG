using Godot;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Assets;
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
                // Fallback primitives — reuse shared mesh resources, don't allocate per update.
                meshInstance.Mesh = prim.ProfileCurve switch
                {
                    0 => _cylinderMesh, // Circle -> Cylinder
                    5 => _sphereMesh,   // HalfCircle -> Sphere
                    _ => _boxMesh,      // Square -> Box
                };
            }

            if (_assetService != null && (prim.TextureId != Guid.Empty || prim.RenderMaterialId != Guid.Empty))
            {
                _ = LoadAndApplyMaterialAsync(meshInstance, prim.TextureId, prim.RenderMaterialId, new Godot.Color(prim.ColorTint.X, prim.ColorTint.Y, prim.ColorTint.Z, prim.ColorTint.W));
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

        var mesh = await _assetService.GetMeshAsync(meshId);
        if (mesh == null || mesh.Submeshes.Count == 0) return;

        // Build the Godot mesh on the main thread.
        Godot.Callable.From(() => ApplyMeshData(meshInstance, mesh)).CallDeferred();
    }

    private async System.Threading.Tasks.Task LoadAndApplyMaterialAsync(MeshInstance3D meshInstance, Guid textureId, Guid renderMaterialId, Godot.Color colorTint)
    {
        if (_assetService == null) return;

        StandardMaterial3D material = new StandardMaterial3D
        {
            AlbedoColor = colorTint,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest
        };

        if (renderMaterialId != Guid.Empty)
        {
            // GLTF PBR Material
            var pbr = await _assetService.GetMaterialAsync(renderMaterialId);
            if (pbr != null)
            {
                material.AlbedoColor = new Godot.Color(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z, pbr.BaseColorFactor.W) * colorTint;
                material.Metallic = pbr.MetallicFactor;
                material.Roughness = pbr.RoughnessFactor;
                material.EmissionEnabled = pbr.EmissiveFactor != System.Numerics.Vector3.Zero;
                material.Emission = new Godot.Color(pbr.EmissiveFactor.X, pbr.EmissiveFactor.Y, pbr.EmissiveFactor.Z);

                // Fetch underlying maps concurrently
                var tasks = new List<System.Threading.Tasks.Task>();

                if (pbr.BaseColorTextureId != Guid.Empty)
                    tasks.Add(_assetService.GetTextureAsync(pbr.BaseColorTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => material.AlbedoTexture = CreateGodotTexture(t.Result)).CallDeferred()));

                if (pbr.NormalTextureId != Guid.Empty)
                    tasks.Add(_assetService.GetTextureAsync(pbr.NormalTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => {
                            material.NormalEnabled = true;
                            material.NormalTexture = CreateGodotTexture(t.Result);
                        }).CallDeferred()));

                if (pbr.MetallicRoughnessTextureId != Guid.Empty)
                    tasks.Add(_assetService.GetTextureAsync(pbr.MetallicRoughnessTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => material.OrmTexture = CreateGodotTexture(t.Result)).CallDeferred()));

                if (pbr.EmissiveTextureId != Guid.Empty)
                    tasks.Add(_assetService.GetTextureAsync(pbr.EmissiveTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => material.EmissionTexture = CreateGodotTexture(t.Result)).CallDeferred()));

                await System.Threading.Tasks.Task.WhenAll(tasks);
            }
        }
        else if (textureId != Guid.Empty)
        {
            // Classic Material (Fallback)
            var texture = await _assetService.GetTextureAsync(textureId);
            if (texture != null)
            {
                Godot.Callable.From(() => material.AlbedoTexture = CreateGodotTexture(texture)).CallDeferred();
            }
        }

        Godot.Callable.From(() => {
            if (IsInstanceValid(meshInstance))
            {
                meshInstance.MaterialOverride = material;
            }
        }).CallDeferred();
    }

    private static ImageTexture? CreateGodotTexture(TextureData? textureData)
    {
        if (textureData == null) return null;
        var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
        return ImageTexture.CreateFromImage(image);
    }

    private void ApplyMeshData(MeshInstance3D meshInstance, MeshData mesh)
    {
        if (meshInstance == null || !IsInstanceValid(meshInstance)) return;

        var arrayMesh = new ArrayMesh();

        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            foreach (int index in sub.Indices)
            {
                var p = sub.Positions[index];
                var n = sub.Normals[index];
                var uv = sub.UVs[index];

                // SL Z-up -> Godot Y-up
                st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
                st.SetUV(new Godot.Vector2(uv.X, uv.Y));
                st.AddVertex(new Godot.Vector3(p.X, p.Z, -p.Y));
            }

            st.GenerateTangents(); // useful for PBR later
            st.Commit(arrayMesh);
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
