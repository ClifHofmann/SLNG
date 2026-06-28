using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Assets;
using System.Collections.Generic;
using System;
using System.Linq;

namespace SLNG.App;

public partial class ObjectRenderer : Node3D
{
    private World? _world;
    private SLNG.Assets.AssetService? _assetService;
    private GpuCache? _gpuCache;

    private class VisualState
    {
        public MeshInstance3D MeshInstance = null!;
        public List<Guid> UsedTextureIds = new();

        // What we've already loaded, so position/scale updates don't rebuild the mesh or
        // re-create the material every frame. Guid.Empty means "not yet loaded".
        public Guid LoadedMeshId;
        public Guid LoadedTextureId = NotLoaded;
        public Guid LoadedMaterialId = NotLoaded;
        public PrimShape? LoadedPrimShape;

        // Sentinel distinct from Guid.Empty (which is a valid "no texture" value) so the
        // first update always applies.
        public static readonly Guid NotLoaded = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
    }

    private readonly Dictionary<Guid, VisualState> _visuals = new();

    // Share one GPU mesh resource across every object with the same prim shape / mesh asset.
    // Without this, each of the (often thousands of) prims uploads its own ArrayMesh and the
    // GPU runs out of memory (VkResult -2). Identical shapes/meshes now cost one upload.
    private readonly Dictionary<PrimShape, ArrayMesh> _primMeshCache = new();
    private readonly Dictionary<Guid, ArrayMesh> _assetMeshCache = new();

    private Mesh _boxMesh = new BoxMesh();
    private Mesh _sphereMesh = new SphereMesh();
    private Mesh _cylinderMesh = new CylinderMesh();

    public void Initialize(World world, SLNG.Assets.AssetService assetService, GpuCache gpuCache)
    {
        _world = world;
        _assetService = assetService;
        _gpuCache = gpuCache;

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

    private double _cullAccum = 0;

    public override void _Process(double delta)
    {
        // Draw-distance culling: hide objects beyond the configured radius from the local
        // agent. Throttled to ~4 Hz; objects don't move often and the agent lookup scans
        // all entities.
        _cullAccum += delta;
        if (_cullAccum < 0.25) return;
        _cullAccum = 0;

        if (_world == null) return;
        if (!RenderConfig.TryGetLocalAgentGodotPos(_world, out var agentPos)) return;

        float maxSq = RenderConfig.DrawDistance * RenderConfig.DrawDistance;
        foreach (var state in _visuals.Values)
        {
            if (!IsInstanceValid(state.MeshInstance)) continue;
            bool visible = state.MeshInstance.Position.DistanceSquaredTo(agentPos) <= maxSq;
            if (state.MeshInstance.Visible != visible) state.MeshInstance.Visible = visible;
        }
    }

    private void CreateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.ContainsKey(entityId)) return;

        var meshInstance = new MeshInstance3D();
        AddChild(meshInstance);
        _visuals[entityId] = new VisualState { MeshInstance = meshInstance };

        UpdateVisual(entityIdStr);
    }

    private void RemoveVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.TryGetValue(entityId, out var state))
        {
            state.MeshInstance.QueueFree();
            if (_gpuCache != null)
            {
                foreach (var texId in state.UsedTextureIds)
                {
                    _gpuCache.ReleaseRef(texId);
                }
            }
            _visuals.Remove(entityId);
        }
    }

    private void SetTexturesForVisual(VisualState state, List<Guid> newTextureIds)
    {
        if (_gpuCache == null) return;
        
        foreach (var old in state.UsedTextureIds)
        {
            if (!newTextureIds.Contains(old)) _gpuCache.ReleaseRef(old);
        }
        
        foreach (var newTex in newTextureIds)
        {
            if (!state.UsedTextureIds.Contains(newTex)) _gpuCache.AddRef(newTex);
        }

        state.UsedTextureIds = newTextureIds;
    }

    private void UpdateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_world == null) return;
        if (!_visuals.TryGetValue(entityId, out var state)) return;

        var entity = _world.GetEntity(entityId);
        if (entity == null) return;

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim != null)
        {
            // Only (re)load the mesh when it actually changes — UpdateVisual fires on every
            // ObjectUpdate (i.e. every position change), and rebuilding the mesh each time is
            // what stalls the main thread on a busy region.
            if (prim.IsMesh && _assetService != null && prim.MeshId != Guid.Empty)
            {
                if (state.LoadedMeshId != prim.MeshId)
                {
                    state.LoadedMeshId = prim.MeshId;
                    _ = LoadAndApplyMeshAsync(state.MeshInstance, prim.MeshId);
                }
            }
            else if (_assetService != null && state.LoadedPrimShape != prim.Shape)
            {
                // Procedural prim: generate its real geometry (profile/path/cut/hollow/twist)
                // off-thread instead of a box placeholder. Re-requested only when the shape
                // changes. Falls back to a primitive solid if meshing fails.
                state.LoadedPrimShape = prim.Shape;
                state.LoadedMeshId = Guid.Empty;
                _ = LoadAndApplyPrimMeshAsync(state, prim.Shape, prim.ProfileCurve);
            }

            // Likewise, only rebuild the material when the texture/material id changes.
            if (_assetService != null
                && (prim.TextureId != state.LoadedTextureId || prim.RenderMaterialId != state.LoadedMaterialId))
            {
                state.LoadedTextureId = prim.TextureId;
                state.LoadedMaterialId = prim.RenderMaterialId;
                if (prim.TextureId != Guid.Empty || prim.RenderMaterialId != Guid.Empty)
                {
                    _ = LoadAndApplyMaterialAsync(state, prim.TextureId, prim.RenderMaterialId, new Godot.Color(prim.ColorTint.X, prim.ColorTint.Y, prim.ColorTint.Z, prim.ColorTint.W));
                }
            }

            state.MeshInstance.Scale = new Godot.Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y);
        }

        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            uint regionX = (uint)(entity.RegionHandle >> 32);
            uint regionY = (uint)(entity.RegionHandle & 0xFFFFFFFF);

            state.MeshInstance.Position = new Godot.Vector3(
                regionX + transform.Position.X, 
                transform.Position.Z, 
                -(regionY + transform.Position.Y));

            var slQuat = new Godot.Quaternion(transform.Rotation.X, transform.Rotation.Z, -transform.Rotation.Y, transform.Rotation.W);
            state.MeshInstance.Quaternion = slQuat;
        }
    }

    private async System.Threading.Tasks.Task LoadAndApplyMeshAsync(MeshInstance3D meshInstance, Guid meshId)
    {
        if (_assetService == null) return;

        var mesh = await _assetService.GetMeshAsync(meshId);
        if (mesh == null || mesh.Submeshes.Count == 0) return;

        Godot.Callable.From(() =>
        {
            if (!IsInstanceValid(meshInstance)) return;
            if (!_assetMeshCache.TryGetValue(meshId, out var arrayMesh))
            {
                arrayMesh = BuildArrayMesh(mesh);
                _assetMeshCache[meshId] = arrayMesh;
            }
            meshInstance.Mesh = arrayMesh;
        }).CallDeferred();
    }

    private async System.Threading.Tasks.Task LoadAndApplyPrimMeshAsync(VisualState state, PrimShape shape, byte profileCurve)
    {
        if (_assetService == null) return;

        var mesh = await _assetService.GetPrimMeshAsync(shape);

        Godot.Callable.From(() =>
        {
            if (!IsInstanceValid(state.MeshInstance)) return;
            // Drop stale results: the shape may have changed again while we were meshing.
            if (state.LoadedPrimShape != shape) return;

            if (mesh != null && mesh.Submeshes.Count > 0)
            {
                if (!_primMeshCache.TryGetValue(shape, out var arrayMesh))
                {
                    arrayMesh = BuildArrayMesh(mesh);
                    _primMeshCache[shape] = arrayMesh;
                }
                state.MeshInstance.Mesh = arrayMesh;
            }
            else
            {
                // Meshing failed (e.g. sculpt or odd shape) — fall back to a primitive solid.
                state.MeshInstance.Mesh = profileCurve switch
                {
                    0 => _cylinderMesh,
                    5 => _sphereMesh,
                    _ => _boxMesh,
                };
            }
        }).CallDeferred();
    }

    private async System.Threading.Tasks.Task LoadAndApplyMaterialAsync(VisualState state, Guid textureId, Guid renderMaterialId, Godot.Color colorTint)
    {
        if (_assetService == null) return;

        StandardMaterial3D material = new StandardMaterial3D
        {
            AlbedoColor = colorTint,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest
        };

        var usedTextures = new List<Guid>();

        if (renderMaterialId != Guid.Empty)
        {
            var pbr = await _assetService.GetMaterialAsync(renderMaterialId);
            if (pbr != null)
            {
                material.AlbedoColor = new Godot.Color(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z, pbr.BaseColorFactor.W) * colorTint;
                material.Metallic = pbr.MetallicFactor;
                material.Roughness = pbr.RoughnessFactor;
                material.EmissionEnabled = pbr.EmissiveFactor != System.Numerics.Vector3.Zero;
                material.Emission = new Godot.Color(pbr.EmissiveFactor.X, pbr.EmissiveFactor.Y, pbr.EmissiveFactor.Z);

                var tasks = new List<System.Threading.Tasks.Task>();

                if (pbr.BaseColorTextureId != Guid.Empty)
                {
                    usedTextures.Add(pbr.BaseColorTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.BaseColorTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => material.AlbedoTexture = t.Result).CallDeferred()));
                }

                if (pbr.NormalTextureId != Guid.Empty)
                {
                    usedTextures.Add(pbr.NormalTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.NormalTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => {
                            material.NormalEnabled = true;
                            material.NormalTexture = t.Result;
                        }).CallDeferred()));
                }

                if (pbr.MetallicRoughnessTextureId != Guid.Empty)
                {
                    usedTextures.Add(pbr.MetallicRoughnessTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.MetallicRoughnessTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => material.OrmTexture = t.Result).CallDeferred()));
                }

                if (pbr.EmissiveTextureId != Guid.Empty)
                {
                    usedTextures.Add(pbr.EmissiveTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.EmissiveTextureId).ContinueWith(t => 
                        Godot.Callable.From(() => material.EmissionTexture = t.Result).CallDeferred()));
                }

                await System.Threading.Tasks.Task.WhenAll(tasks);
            }
        }
        else if (textureId != Guid.Empty)
        {
            usedTextures.Add(textureId);
            var tex = await GetOrCreateGpuTextureAsync(textureId);
            if (tex != null)
            {
                Godot.Callable.From(() => material.AlbedoTexture = tex).CallDeferred();
            }
        }

        Godot.Callable.From(() => {
            if (IsInstanceValid(state.MeshInstance))
            {
                SetTexturesForVisual(state, usedTextures);
                state.MeshInstance.MaterialOverride = material;
            }
            else
            {
                // If it was destroyed while we were fetching, immediately release the refs we just intended to add
                if (_gpuCache != null)
                {
                    foreach (var id in usedTextures) _gpuCache.ReleaseRef(id);
                }
            }
        }).CallDeferred();
    }

    private async System.Threading.Tasks.Task<ImageTexture?> GetOrCreateGpuTextureAsync(Guid textureId)
    {
        if (_gpuCache != null)
        {
            var cached = _gpuCache.Get(textureId) as ImageTexture;
            if (cached != null) return cached;
        }

        if (_assetService == null) return null;

        var textureData = await _assetService.GetTextureAsync(textureId);
        if (textureData == null) return null;

        // Create on main thread, but we can do it via CallDeferred and TaskCompletionSource
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

    /// <summary>Builds a Godot <see cref="ArrayMesh"/> from neutral mesh data. The result is
    /// cached and shared across all instances using the same shape/asset.</summary>
    private static ArrayMesh BuildArrayMesh(MeshData mesh)
    {
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

                st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
                st.SetUV(new Godot.Vector2(uv.X, uv.Y));
                st.AddVertex(new Godot.Vector3(p.X, p.Z, -p.Y));
            }

            st.GenerateTangents();
            st.Commit(arrayMesh);
        }

        return arrayMesh;
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
