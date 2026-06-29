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
        public Guid EntityId;
        public MeshInstance3D MeshInstance = null!;
        public StaticBody3D StaticBody = null!;
        public CollisionShape3D CollisionShape = null!;
        public List<Guid> UsedTextureIds = new();

        // What we've already loaded, so position/scale updates don't rebuild the mesh or
        // re-create the material every frame. Guid.Empty means "not yet loaded".
        public Guid LoadedMeshId;
        public Guid LoadedTextureId = NotLoaded;
        public Guid LoadedMaterialId = NotLoaded;
        public PrimShape? LoadedPrimShape;

        // GpuCache key of the mesh this object currently references (Guid.Empty = none).
        public Guid LoadedMeshKey;

        // True while this object is beyond draw distance and we've dropped its mesh/texture
        // refs to free GPU memory. It reloads when it comes back into range.
        public bool ResourcesReleased;

        // Sentinel distinct from Guid.Empty (which is a valid "no texture" value) so the
        // first update always applies.
        public static readonly Guid NotLoaded = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
    }

    private readonly Dictionary<Guid, VisualState> _visuals = new();

    // Meshes are shared and budgeted through the GpuCache (LRU + refcount), keyed by mesh
    // asset id or by a stable id assigned per unique prim shape. Identical objects share one
    // upload; out-of-range objects release their ref so the cache can reclaim the VRAM.
    private readonly Dictionary<PrimShape, Guid> _primShapeKeys = new();

    // Per shared-mesh key: the SL face number of each surface, so any instance can apply that
    // face's texture via SetSurfaceOverrideMaterial.
    private readonly Dictionary<Guid, int[]> _meshFaceIndices = new();

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
        // Draw-distance management, throttled to ~4 Hz. Beyond the radius an object is hidden;
        // beyond the radius + hysteresis its GPU resources (mesh + texture refs) are released
        // so VRAM stays bounded to the nearby working set — without this, every object ever
        // seen keeps its texture pinned and memory grows without bound. Re-enters reload when
        // it comes back into range.
        _cullAccum += delta;
        if (_cullAccum < 0.25) return;
        _cullAccum = 0;

        if (_world == null) return;
        if (!RenderConfig.TryGetLocalAgentGodotPos(_world, out var agentPos)) return;

        float draw = RenderConfig.DrawDistance;
        float showSq = draw * draw;
        float hideSq = (draw * 1.15f) * (draw * 1.15f);  // hide a bit past the edge (visibility hysteresis)
        float releaseSq = (draw * 1.25f) * (draw * 1.25f); // only free GPU memory well beyond the edge

        foreach (var (id, state) in _visuals)
        {
            if (!IsInstanceValid(state.MeshInstance)) continue;

            float dSq = state.MeshInstance.Position.DistanceSquaredTo(agentPos);

            // Visibility with hysteresis: show within draw distance, hide only past 1.15x, so
            // objects sitting near the edge don't flicker on/off every tick while moving.
            if (dSq <= showSq && !state.MeshInstance.Visible) state.MeshInstance.Visible = true;
            else if (dSq > hideSq && state.MeshInstance.Visible) state.MeshInstance.Visible = false;

            if (dSq <= showSq && state.ResourcesReleased)
            {
                state.ResourcesReleased = false;
                UpdateVisual(id.ToString()); // reload mesh + material now that it's near again
            }
            else if (dSq > releaseSq && !state.ResourcesReleased)
            {
                ReleaseResources(state); // far enough that we reclaim its VRAM
            }
        }
    }

    /// <summary>Drops an out-of-range object's GPU resources so VRAM can be reclaimed. The
    /// load state is reset so <see cref="UpdateVisual"/> rebuilds it when it returns.</summary>
    private void ReleaseResources(VisualState state)
    {
        if (state.ResourcesReleased) return;

        state.MeshInstance.Mesh = null;
        state.MeshInstance.MaterialOverride = null;
        ReleaseMeshRef(state);

        if (_gpuCache != null)
            foreach (var texId in state.UsedTextureIds) _gpuCache.ReleaseRef(texId);
        state.UsedTextureIds = new List<Guid>();

        state.LoadedMeshId = Guid.Empty;
        state.LoadedPrimShape = null;
        state.LoadedTextureId = VisualState.NotLoaded;
        state.LoadedMaterialId = VisualState.NotLoaded;
        state.ResourcesReleased = true;
    }

    private void CreateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_world == null) return;
        var entity = _world.GetEntity(entityId);
        if (entity == null) return;
        if (_visuals.ContainsKey(entityId)) return;

        var state = new VisualState
        {
            EntityId = entity.Id,
            MeshInstance = new MeshInstance3D { Name = "Obj_" + entity.Id.ToString("N"), Visible = false },
            StaticBody = new StaticBody3D { Name = "StaticBody" },
            CollisionShape = new CollisionShape3D { Name = "Collision" },
            ResourcesReleased = true
        };
        state.StaticBody.SetMeta("EntityId", entity.Id.ToString());
        state.StaticBody.SetMeta("LocalId", entity.LocalId.ToString());
        state.StaticBody.AddChild(state.CollisionShape);
        state.MeshInstance.AddChild(state.StaticBody);

        _visuals[entity.Id] = state;
        AddChild(state.MeshInstance);

        UpdateVisual(entityIdStr);
    }

    private void RemoveVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.TryGetValue(entityId, out var state))
        {
            ReleaseMeshRef(state);
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
            // Skip all asset loading while the object is released (out of draw distance). The
            // cull pass clears ResourcesReleased and re-calls UpdateVisual when it returns; only
            // position/scale are kept current here so the distance check stays accurate.
            if (!state.ResourcesReleased)
            {
            // Only (re)load the mesh when it actually changes — UpdateVisual fires on every
            // ObjectUpdate (i.e. every position change), and rebuilding the mesh each time is
            // what stalls the main thread on a busy region.
            if (prim.IsMesh && _assetService != null && prim.MeshId != Guid.Empty)
            {
                if (state.LoadedMeshId != prim.MeshId)
                {
                    state.LoadedMeshId = prim.MeshId;
                    state.LoadedPrimShape = null;
                    _ = LoadAndApplyMeshAsync(state, prim.MeshId);
                }
            }
            else if (prim.IsSculpt && _assetService != null && prim.SculptId != Guid.Empty)
            {
                // Sculpted prim: geometry comes from the sculpt-map texture, not the profile/path.
                if (state.LoadedMeshId != prim.SculptId)
                {
                    state.LoadedMeshId = prim.SculptId;
                    state.LoadedPrimShape = null;
                    _ = LoadAndApplySculptMeshAsync(state, prim.SculptId, prim.SculptType, prim.ProfileCurve);
                }
            }
            else if (_assetService != null && !prim.IsSculpt && state.LoadedPrimShape != prim.Shape)
            {
                // Procedural prim: generate its real geometry (profile/path/cut/hollow/twist)
                // off-thread instead of a box placeholder. Re-requested only when the shape
                // changes. Falls back to a primitive solid if meshing fails.
                state.LoadedPrimShape = prim.Shape;
                state.LoadedMeshId = Guid.Empty;
                _ = LoadAndApplyPrimMeshAsync(state, prim.Shape, prim.ProfileCurve);
            }

            // Re-apply materials when the default texture/material changes (a proxy for "the
            // object's appearance changed"). The mesh-assignment callback also re-applies once
            // surfaces exist; here covers texture-only changes on an already-loaded mesh.
            if (_assetService != null
                && (prim.TextureId != state.LoadedTextureId || prim.RenderMaterialId != state.LoadedMaterialId))
            {
                state.LoadedTextureId = prim.TextureId;
                state.LoadedMaterialId = prim.RenderMaterialId;
                if (state.LoadedMeshKey != Guid.Empty)
                    _ = ApplyFaceMaterialsAsync(state);
            }
            }

            state.MeshInstance.Scale = new Godot.Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y);
        }

        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            state.MeshInstance.Position = RenderConfig.ToGodot(entity.RegionHandle, transform.Position);

            var slQuat = new Godot.Quaternion(transform.Rotation.X, transform.Rotation.Z, -transform.Rotation.Y, transform.Rotation.W);
            state.MeshInstance.Quaternion = slQuat;
        }
    }

    private async System.Threading.Tasks.Task LoadAndApplyMeshAsync(VisualState state, Guid meshId)
    {
        if (_assetService == null) return;

        var mesh = await _assetService.GetMeshAsync(meshId);
        if (mesh == null || mesh.Submeshes.Count == 0) return;

        Godot.Callable.From(() =>
        {
            if (!IsInstanceValid(state.MeshInstance)) return;
            if (state.LoadedMeshId != meshId) return; // shape/asset changed while loading
            AssignSharedMesh(state, meshId, mesh);
        }).CallDeferred();
    }

    private async System.Threading.Tasks.Task LoadAndApplySculptMeshAsync(VisualState state, Guid sculptId, byte sculptType, byte profileCurve)
    {
        if (_assetService == null) return;

        var mesh = await _assetService.GetSculptMeshAsync(sculptId, sculptType);

        Godot.Callable.From(() =>
        {
            if (!IsInstanceValid(state.MeshInstance)) return;
            if (state.LoadedMeshId != sculptId) return; // changed while meshing

            if (mesh != null && mesh.Submeshes.Count > 0)
            {
                AssignSharedMesh(state, sculptId, mesh);
            }
            else
            {
                // Sculpt map not ready / undecodable — show a placeholder solid for now.
                ReleaseMeshRef(state);
                if (profileCurve == 0)
                {
                    state.MeshInstance.Mesh = _cylinderMesh;
                    state.CollisionShape.Shape = new Godot.CylinderShape3D { Height = 1.0f, Radius = 0.5f };
                }
                else if (profileCurve == 5)
                {
                    state.MeshInstance.Mesh = _sphereMesh;
                    state.CollisionShape.Shape = new Godot.SphereShape3D { Radius = 0.5f };
                }
                else
                {
                    state.MeshInstance.Mesh = _boxMesh;
                    state.CollisionShape.Shape = new Godot.BoxShape3D { Size = new Godot.Vector3(1, 1, 1) };
                }
            }
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
                AssignSharedMesh(state, KeyForShape(shape), mesh);
            }
            else
            {
                // Meshing failed (e.g. sculpt or odd shape) — fall back to a primitive solid.
                ReleaseMeshRef(state);
                if (profileCurve == 0)
                {
                    state.MeshInstance.Mesh = _cylinderMesh;
                    state.CollisionShape.Shape = new Godot.CylinderShape3D { Height = 1.0f, Radius = 0.5f };
                }
                else if (profileCurve == 5)
                {
                    state.MeshInstance.Mesh = _sphereMesh;
                    state.CollisionShape.Shape = new Godot.SphereShape3D { Radius = 0.5f };
                }
                else
                {
                    state.MeshInstance.Mesh = _boxMesh;
                    state.CollisionShape.Shape = new Godot.BoxShape3D { Size = new Godot.Vector3(1, 1, 1) };
                }
            }
        }).CallDeferred();
    }

    /// <summary>Builds and applies a material per mesh surface from the prim's per-face textures
    /// (falling back to the object's default texture for faces without their own).</summary>
    private async System.Threading.Tasks.Task ApplyFaceMaterialsAsync(VisualState state)
    {
        if (_assetService == null || _world == null) return;
        var prim = _world.GetEntity(state.EntityId)?.GetComponent<PrimitiveComponent>();
        if (prim == null) return;

        var defaultFace = new FaceTexture(prim.TextureId, prim.RenderMaterialId, prim.ColorTint, prim.RepeatU, prim.RepeatV, prim.OffsetU, prim.OffsetV, prim.Rotation);

        // Fallback solid / mesh without per-surface face info: one material for the whole node.
        if (!_meshFaceIndices.TryGetValue(state.LoadedMeshKey, out var faceIndices) || faceIndices.Length == 0)
        {
            var (mat, used) = await BuildFaceMaterialAsync(defaultFace);
            ApplyOnMainThread(state, () => state.MeshInstance.MaterialOverride = mat, used);
            return;
        }

        var allUsed = new List<Guid>();
        for (int surface = 0; surface < faceIndices.Length; surface++)
        {
            int faceIdx = faceIndices[surface];
            FaceTexture ft = (prim.Faces != null && faceIdx >= 0 && faceIdx < prim.Faces.Length)
                ? prim.Faces[faceIdx] : defaultFace;

            var (material, used) = await BuildFaceMaterialAsync(ft);
            allUsed.AddRange(used);

            int surf = surface; // capture
            Godot.Callable.From(() =>
            {
                if (!IsInstanceValid(state.MeshInstance) || state.MeshInstance.Mesh == null) return;
                if (surf >= state.MeshInstance.Mesh.GetSurfaceCount()) return;
                state.MeshInstance.MaterialOverride = null; // per-surface overrides take effect
                state.MeshInstance.SetSurfaceOverrideMaterial(surf, material);
            }).CallDeferred();
        }

        ApplyOnMainThread(state, null, allUsed.Distinct().ToList());
    }

    /// <summary>Marshals texture ref-count bookkeeping (and an optional action) to the main thread,
    /// releasing refs if the node was freed mid-load.</summary>
    private void ApplyOnMainThread(VisualState state, Action? action, List<Guid> usedTextures)
    {
        Godot.Callable.From(() =>
        {
            if (IsInstanceValid(state.MeshInstance))
            {
                action?.Invoke();
                SetTexturesForVisual(state, usedTextures);
            }
            else if (_gpuCache != null)
            {
                foreach (var id in usedTextures) _gpuCache.ReleaseRef(id);
            }
        }).CallDeferred();
    }

    /// <summary>Builds one face's material (classic texture or PBR) and returns the texture ids
    /// it references. Texture/material application is marshalled to the main thread.</summary>
    private async System.Threading.Tasks.Task<(StandardMaterial3D Material, List<Guid> Used)> BuildFaceMaterialAsync(FaceTexture ft)
    {
        var used = new List<Guid>();
        var colorTint = new Godot.Color(ft.Color.X, ft.Color.Y, ft.Color.Z, ft.Color.W);
        var material = new StandardMaterial3D
        {
            AlbedoColor = colorTint,
            // Linear + mipmaps: SL textures look smooth, not blocky/pixelated, and don't shimmer with distance.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
            // SL texture repeats scale from the center (0.5, 0.5), not top-left.
            // u_godot = u * RepeatU + OffsetU_godot
            // u_sl = (u - 0.5) * RepeatU + 0.5 + OffsetU_sl = u * RepeatU + (0.5 - 0.5 * RepeatU + OffsetU_sl)
            Uv1Scale = new Godot.Vector3(ft.RepeatU, ft.RepeatV, 1.0f),
            Uv1Offset = new Godot.Vector3(
                0.5f - 0.5f * ft.RepeatU + ft.OffsetU,
                0.5f - 0.5f * ft.RepeatV + ft.OffsetV,
                0.0f)
        };

        if (ft.MaterialId != Guid.Empty && _assetService != null)
        {
            var pbr = await _assetService.GetMaterialAsync(ft.MaterialId);
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
                    used.Add(pbr.BaseColorTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.BaseColorTextureId).ContinueWith(t =>
                        Godot.Callable.From(() => material.AlbedoTexture = t.Result).CallDeferred()));
                }
                if (pbr.NormalTextureId != Guid.Empty)
                {
                    used.Add(pbr.NormalTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.NormalTextureId).ContinueWith(t =>
                        Godot.Callable.From(() => { material.NormalEnabled = true; material.NormalTexture = t.Result; }).CallDeferred()));
                }
                if (pbr.MetallicRoughnessTextureId != Guid.Empty)
                {
                    used.Add(pbr.MetallicRoughnessTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.MetallicRoughnessTextureId).ContinueWith(t =>
                        Godot.Callable.From(() => material.OrmTexture = t.Result).CallDeferred()));
                }
                if (pbr.EmissiveTextureId != Guid.Empty)
                {
                    used.Add(pbr.EmissiveTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.EmissiveTextureId).ContinueWith(t =>
                        Godot.Callable.From(() => material.EmissionTexture = t.Result).CallDeferred()));
                }
                await System.Threading.Tasks.Task.WhenAll(tasks);
            }
        }
        else if (ft.TextureId != Guid.Empty)
        {
            used.Add(ft.TextureId);
            var tex = await GetOrCreateGpuTextureAsync(ft.TextureId);
            if (tex != null)
            {
                Godot.Callable.From(() =>
                {
                    material.AlbedoTexture = tex;
                    ApplyAlphaCutout(material, tex);
                }).CallDeferred();
            }
        }

        return (material, used);
    }

    /// <summary>Picks the right transparency mode from the texture's actual alpha:
    /// binary alpha (foliage/fences) → alpha-scissor cutout; graded alpha (glass, soft edges)
    /// → alpha blend; fully opaque → left unchanged. Alpha surfaces render double-sided.</summary>
    private static void ApplyAlphaCutout(StandardMaterial3D material, ImageTexture tex)
    {
        var img = tex.GetImage();
        if (img == null) return;

        if (img.DetectAlpha() == Image.AlphaMode.None) return; // fully opaque — leave default

        // Use alpha-scissor (cutout) for any texture with alpha. Foliage uses soft-edged alpha
        // masks that read as "Blend", but they're meant to be cut to a leaf shape — true
        // alpha-blend turns them into big translucent cards. Cutout is the right SL default.
        material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
        material.AlphaScissorThreshold = 0.5f;
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
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
            image.GenerateMipmaps(); // so LinearWithMipmaps actually filters — no shimmer/aliasing at distance
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

    /// <summary>Returns a stable GpuCache key for a prim shape (one id per unique shape).</summary>
    private Guid KeyForShape(PrimShape shape)
    {
        if (!_primShapeKeys.TryGetValue(shape, out var key))
        {
            key = Guid.NewGuid();
            _primShapeKeys[shape] = key;
        }
        return key;
    }

    /// <summary>Assigns a shared, refcounted mesh to the object's node, building+caching it on
    /// first use. Releases the previous mesh ref so the GpuCache can reclaim it.</summary>
    private void AssignSharedMesh(VisualState state, Guid key, MeshData data)
    {
        if (state.LoadedMeshKey == key && state.MeshInstance.Mesh != null) return;

        ReleaseMeshRef(state);

        ArrayMesh? mesh = _gpuCache?.Get(key) as ArrayMesh;
        if (mesh != null && _meshFaceIndices.ContainsKey(key))
        {
            _gpuCache!.AddRef(key);
        }
        else
        {
            mesh = BuildArrayMesh(data, out var faceIndices);
            _meshFaceIndices[key] = faceIndices;
            _gpuCache?.Put(key, mesh, EstimateMeshSize(data), initialRefCount: 1);
        }

        state.MeshInstance.Mesh = mesh;
        state.LoadedMeshKey = key;
        
        if (mesh != null)
        {
            state.CollisionShape.Shape = mesh.CreateTrimeshShape();
        }
        else
        {
            state.CollisionShape.Shape = null;
        }

        // Geometry surfaces now exist — (re)apply per-face materials.
        _ = ApplyFaceMaterialsAsync(state);
    }

    /// <summary>Drops this object's current shared-mesh reference (if any).</summary>
    private void ReleaseMeshRef(VisualState state)
    {
        if (state.LoadedMeshKey != Guid.Empty)
        {
            _gpuCache?.ReleaseRef(state.LoadedMeshKey);
            state.LoadedMeshKey = Guid.Empty;
        }
    }

    private static long EstimateMeshSize(MeshData mesh)
    {
        long total = 0;
        foreach (var sub in mesh.Submeshes)
            total += (long)sub.Positions.Length * 32 + (long)sub.Indices.Length * 4;
        return total > 0 ? total : 1;
    }

    /// <summary>Builds a Godot <see cref="ArrayMesh"/> from neutral mesh data (one surface per
    /// submesh) and returns the SL face number of each surface (parallel to surface order).</summary>
    private static ArrayMesh BuildArrayMesh(MeshData mesh, out int[] faceIndices)
    {
        var arrayMesh = new ArrayMesh();
        var indices = new List<int>(mesh.Submeshes.Count);

        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            for (int i = sub.Indices.Length - 1; i >= 0; i--)
            {
                int index = sub.Indices[i];
                var p = sub.Positions[index];
                var n = sub.Normals[index];
                var uv = sub.UVs[index];

                st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
                st.SetUV(new Godot.Vector2(uv.X, uv.Y));
                st.AddVertex(new Godot.Vector3(p.X, p.Z, -p.Y));
            }

            st.GenerateTangents();
            st.Commit(arrayMesh);
            indices.Add(sub.FaceIndex);
        }

        faceIndices = indices.ToArray();
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
