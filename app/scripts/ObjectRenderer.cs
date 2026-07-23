using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class ObjectRenderer : Node3D
{
    // Physics layers: 1 = terrain/objects (AvatarController's ground-detection ray masks to just
    // this), 2 = avatar (AvatarRenderer). Phantom objects move to their own layer instead of
    // disabling their CollisionShape3D outright -- Disabled would also block the object-selection
    // raycast (ObjectSelectionController), which queries all layers by default, so a phantom
    // object would become unclickable/un-editable, not just un-standable-on.
    private const uint PhantomLayer = 1u << 2;

    private World? _world;
    private SLNG.Assets.AssetService? _assetService;
    private GpuCache? _gpuCache;

    private class VisualState
    {
        public Guid EntityId;
        public MeshInstance3D MeshInstance = null!;
        public StaticBody3D StaticBody = null!;
        public CollisionShape3D CollisionShape = null!;
        public OmniLight3D? LightNode;
        public List<Guid> UsedTextureIds = new();

        // What we've already loaded, so position/scale updates don't rebuild the mesh or
        // re-create the material every frame. Guid.Empty means "not yet loaded".
        public Guid LoadedMeshId;
        public Guid LoadedTextureId = NotLoaded;
        public Guid LoadedMaterialId = NotLoaded;
        // NaN so the very first UpdateVisual always counts as "changed" (a real ColorTint can
        // never equal this). Needed alongside TextureId/RenderMaterialId below: a face-color/
        // alpha-only edit (e.g. the build floater's Transparency slider) changes neither texture
        // nor material id, so without tracking this too the re-apply gate never fires and the
        // object keeps rendering its stale (e.g. opaque) color forever — see ApplyFaceMaterialsAsync.
        public System.Numerics.Vector4 LoadedColorTint =
            new(float.NaN, float.NaN, float.NaN, float.NaN);
        public FaceTexture[]? LoadedFaces;
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

    private StandardMaterial3D _highlightMaterial = new StandardMaterial3D
    {
        AlbedoColor = new Color(1.0f, 0.8f, 0.0f, 0.3f), // Yellowish tint
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        NoDepthTest = true, // See through walls slightly
    };

    // Bump alongside every fix so a fresh log line proves this exact build is running (see
    // AvatarRenderer.BuildMarker's doc comment — same stale-assembly hazard applies here).
    private const string BuildMarker = "2026-07-22-face-color-reapply-gate-fixed";

    public void Initialize(World world, SLNG.Assets.AssetService assetService, GpuCache gpuCache)
    {
        GD.Print($"[ObjectRenderer] BUILD MARKER: {BuildMarker}");
        _world = world;
        _assetService = assetService;
        _gpuCache = gpuCache;

        _world.EntityAdded += OnEntityAdded;
        _world.EntityRemoved += OnEntityRemoved;
        _world.ComponentUpdated += OnComponentUpdated;
        _world.EntitySelected += OnEntitySelected;
        _world.EntityDeselected += OnEntityDeselected;
    }

    private void OnEntitySelected(object? sender, EntityEventArgs e)
    {
        CallDeferred(nameof(HighlightVisual), e.Entity.Id.ToString(), true);
    }

    private void OnEntityDeselected(object? sender, EntityEventArgs e)
    {
        CallDeferred(nameof(HighlightVisual), e.Entity.Id.ToString(), false);
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
        if (e.Component is PrimitiveComponent || e.Component is TransformComponent)
        {
            CallDeferred(nameof(UpdateVisual), e.Entity.Id.ToString());
        }
        else if (e.Component is AttachmentComponent)
        {
            // If an object becomes an attachment, remove its standalone visual.
            CallDeferred(nameof(RemoveVisual), e.Entity.Id.ToString());
        }
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
        state.LoadedColorTint = new System.Numerics.Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        state.LoadedFaces = null;
        state.ResourcesReleased = true;
    }

    private void CreateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_world == null) return;
        var entity = _world.GetEntity(entityId);
        if (entity == null) return;
        if (_visuals.ContainsKey(entityId)) return;

        // Do not render attachments as standalone objects.
        if (entity.GetComponent<AttachmentComponent>() != null) return;

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

    private void HighlightVisual(string idStr, bool isSelected)
    {
        if (_world == null) return;
        if (!Guid.TryParse(idStr, out var id)) return;

        var entity = _world.GetEntity(id);
        if (entity == null) return;

        // Edit Linked Parts ON (FEAT-UI-06): highlight only the specific part that was actually
        // selected -- grouping by root here would glow the WHOLE linkset regardless of which
        // part got selected, making it look like per-part selection silently does nothing.
        if (SelectionSettings.EditLinkedParts)
        {
            if (_visuals.TryGetValue(id, out var soloState) && soloState.MeshInstance != null)
            {
                soloState.MeshInstance.MaterialOverlay = isSelected ? _highlightMaterial : null;
            }
            return;
        }

        var transform = entity.GetComponent<TransformComponent>();
        if (transform == null) return;

        uint rootLocalId = transform.ParentLocalId != 0 ? transform.ParentLocalId : entity.LocalId;

        foreach (var kvp in _visuals)
        {
            var visEntity = _world.GetEntity(kvp.Key);
            if (visEntity == null) continue;

            var visTransform = visEntity.GetComponent<TransformComponent>();
            if (visTransform == null) continue;

            uint visRootLocalId = visTransform.ParentLocalId != 0 ? visTransform.ParentLocalId : visEntity.LocalId;
            if (visRootLocalId == rootLocalId && kvp.Value.MeshInstance != null)
            {
                kvp.Value.MeshInstance.MaterialOverlay = isSelected ? _highlightMaterial : null;
            }
        }
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
            // Do not render attachments as standalone objects. They are handled by AvatarRenderer.
            if (entity.GetComponent<AttachmentComponent>() != null) return;

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

                // Re-apply materials when the default texture/material/color changes (a proxy for
                // "the object's appearance changed"). The mesh-assignment callback also re-applies
                // once surfaces exist; here covers appearance-only changes on an already-loaded
                // mesh. Must also watch ColorTint/Faces, not just TextureId/RenderMaterialId: a
                // face-color or per-face-alpha edit (e.g. Object > Features > Transparency in the
                // build floater) touches neither id, so a gate that only checked ids never noticed
                // and the object kept rendering its original (often opaque) alpha forever — see
                // BuildFaceMaterialAsync's colorTint.A < 0.99f Transparency branch, which was
                // structurally correct but never re-ran after the initial load.
                if (_assetService != null
                    && (prim.TextureId != state.LoadedTextureId
                        || prim.RenderMaterialId != state.LoadedMaterialId
                        || prim.ColorTint != state.LoadedColorTint
                        || !FacesEqual(state.LoadedFaces, prim.Faces)))
                {
                    state.LoadedTextureId = prim.TextureId;
                    state.LoadedMaterialId = prim.RenderMaterialId;
                    state.LoadedColorTint = prim.ColorTint;
                    state.LoadedFaces = prim.Faces;
                    if (state.LoadedMeshKey != Guid.Empty)
                        _ = ApplyFaceMaterialsAsync(state);
                }
            }

            state.MeshInstance.Scale = new Godot.Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y);

            // Phantom means "no collision" in SL: move off the terrain/objects layer so
            // AvatarController's ground ray (masked to layer 1) passes through, while staying
            // selectable/editable (the object-selection raycast queries all layers).
            state.StaticBody.CollisionLayer = prim.IsPhantom ? PhantomLayer : 1u;

            if (prim.LightEnabled)
            {
                if (state.LightNode == null)
                {
                    state.LightNode = new OmniLight3D { Name = "Light" };
                    state.MeshInstance.AddChild(state.LightNode);
                }
                state.LightNode.LightColor = new Godot.Color(prim.LightColor.X, prim.LightColor.Y, prim.LightColor.Z);
                // SL's Intensity has no direct Godot equivalent unit -- scaled up so a default
                // (Intensity 1) reads as a visible light rather than a near-invisible dim glow.
                state.LightNode.LightEnergy = prim.LightIntensity * 2.0f;
                state.LightNode.OmniRange = prim.LightRadius;
                // OmniAttenuation of 0 is a degenerate/invalid falloff in Godot; SL's own default
                // Falloff is 1.0, well inside Godot's valid range, but a user-set 0 shouldn't zero
                // the light out entirely.
                state.LightNode.OmniAttenuation = Mathf.Max(0.1f, prim.LightFalloff);
            }
            else if (state.LightNode != null)
            {
                state.LightNode.QueueFree();
                state.LightNode = null;
            }
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
            AssignSharedMesh(state, meshId, mesh, flipV: true);
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
                // LMV's SCULPT meshing path emits raw (bottom-left) UVs — unlike its prim path,
                // which pre-flips; scenery sculpts (rocks etc.) rarely make the difference
                // visible, so this leans on the SL-convention default rather than hard proof.
                AssignSharedMesh(state, sculptId, mesh, flipV: true);
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
                // flipV:true — verified against the real viewer (llvolume.cpp
                // LLVolumeFace::createSide): SL sets a box side face's V directly from
                // path_data[t].mTexT with NO flip, but LibreMetaverse's MeshFoundry applies an
                // extra 1-V (GenerateFacetedMesh) that leaves prim faces vertically inverted —
                // invisible on tiled/symmetric textures, but upside-down on anything oriented
                // (a HUD's logo/text). This is the SAME flip mesh assets already use; prims were
                // wrongly exempted on the assumption MeshFoundry's internal flip cancelled out.
                AssignSharedMesh(state, KeyForShape(shape), mesh, flipV: true);
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

        // SL face rotation: only 0 and ±π are representable in a StandardMaterial3D UV transform
        // (no rotation, just scale/offset) — π is a point-mirror, i.e. negated repeats around the
        // face center. Other angles logged, rendered unrotated. Same handling as
        // AvatarRenderer.BuildFaceMaterialAsync — keep in sync.
        float effRepeatU = ft.RepeatU, effRepeatV = ft.RepeatV;
        float wrappedRot = Mathf.Wrap(ft.Rotation, -Mathf.Pi, Mathf.Pi);
        if (Mathf.Abs(wrappedRot) > Mathf.Pi * 0.75f)
        {
            effRepeatU = -effRepeatU;
            effRepeatV = -effRepeatV;
        }
        else if (Mathf.Abs(wrappedRot) > 0.05f)
        {
            Logger.Debug($"[FaceTex] unsupported face rotation {ft.Rotation:0.##} rad (tex {ft.TextureId.ToString()[..8]}) — rendered unrotated");
        }

        var material = new StandardMaterial3D
        {
            AlbedoColor = colorTint,
            // Linear + mipmaps: SL textures look smooth, not blocky/pixelated, and don't shimmer with distance.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Uv1Scale = new Godot.Vector3(effRepeatU, effRepeatV, 1.0f),
            // Centered like SL (u' = (u-0.5)*repeat + 0.5 + off) — Godot scales UVs from the
            // corner, so without the 0.5-0.5*repeat correction any repeat != 1 shifts the
            // texture off-center.
            Uv1Offset = new Godot.Vector3(
                0.5f - 0.5f * effRepeatU + ft.OffsetU,
                0.5f - 0.5f * effRepeatV + ft.OffsetV,
                0.0f)
        };

        if (colorTint.A < 0.99f)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }

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

                // glTF's own alphaMode is authoritative here — a real, creator-declared signal,
                // never a pixel-content guess (see AvatarRenderer.BuildFaceMaterialAsync's
                // identical use of this field, added alongside PbrMaterialData.AlphaMode this
                // session — this branch previously never set Transparency at all for PBR-
                // materialed faces, so a BLEND/MASK-authored glTF material rendered fully opaque
                // regardless of its own alpha content). PbrAlphaMode.Opaque intentionally leaves
                // Transparency untouched: colorTint may have already forced Alpha above for a
                // translucent per-face tint, which is a separate SL signal from the material's
                // own declared transparency and must not be downgraded back to opaque.
                if (pbr.AlphaMode == SLNG.Assets.PbrAlphaMode.Blend)
                {
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                }
                else if (pbr.AlphaMode == SLNG.Assets.PbrAlphaMode.Mask)
                {
                    material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                    material.AlphaScissorThreshold = pbr.AlphaCutoff;
                    material.AlphaAntialiasingMode = BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage;
                }

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
                // No await Task.WhenAll(tasks) here! Let the textures populate asynchronously so the mesh renders immediately.
            }
        }
        else if (ft.TextureId != Guid.Empty)
        {
            used.Add(ft.TextureId);
            _ = GetOrCreateGpuTextureAsync(ft.TextureId).ContinueWith(t =>
            {
                var tex = t.Result;
                if (tex != null)
                {
                    Godot.Callable.From(() =>
                    {
                        material.AlbedoTexture = tex;
                        ApplyAlphaCutout(material, tex);
                    }).CallDeferred();
                }
            });
        }

        return (material, used);
    }

    /// <summary>Picks the right transparency mode from the texture's actual alpha:
    /// binary alpha (foliage/fences) → alpha-scissor cutout; graded alpha (glass, soft edges)
    /// → alpha blend; fully opaque → left unchanged. Alpha surfaces render double-sided.
    ///
    /// Does NOT gate on DetectAlpha() == None to skip entirely — that heuristic is unreliable
    /// (a fully alpha=0 placeholder texture, correct data, was reported opaque and rendered
    /// solid instead of cut; see AvatarRenderer.ApplyAlphaCutout and the
    /// godot-material-transparency-gotchas memory note for the avatar-side instance of this
    /// exact bug). A "None" verdict here now falls through to the AlphaScissor branch below
    /// instead of returning early, so a false negative degrades to a cheap no-op cutout test
    /// rather than silently staying opaque. DetectAlpha is still used, lower-stakes, only to
    /// choose BETWEEN Blend and Scissor once we know we're applying something.
    ///
    /// Deliberately stays on AlphaScissor (not AlphaHash) for the Bit case: this runs per-face
    /// on potentially thousands of world prims, so it keeps the cheaper cutout mode. Avatar
    /// content (bakes, worn mesh attachments) is bounded per-avatar and uses AlphaHash instead —
    /// see AvatarRenderer for that reasoning.</summary>
    private static void ApplyAlphaCutout(StandardMaterial3D material, ImageTexture tex)
    {
        var img = tex.GetImage();
        if (img == null) return;

        var alphaMode = img.DetectAlpha();

        // If the primitive is already explicitly translucent via color tint, keep true Alpha blending.
        // Otherwise, pick the right mode based on the texture's alpha content.
        if (material.Transparency != BaseMaterial3D.TransparencyEnum.Alpha)
        {
            if (alphaMode == Image.AlphaMode.Blend)
            {
                // Smooth translucent edges (hair, glass, clouds)
                material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            }
            else
            {
                // Binary alpha (fences, foliage) — and the safe fallback for a "None" verdict
                // that might be a DetectAlpha() false negative.
                material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                material.AlphaScissorThreshold = 0.5f;
                // Free once MSAA 3D is enabled project-wide (currently off); harmless no-op until then.
                material.AlphaAntialiasingMode = BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage;
            }
        }
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

        Godot.Callable.From(() =>
        {
            if (_gpuCache != null)
            {
                var cached = _gpuCache.Get(textureId) as ImageTexture;
                if (cached != null)
                {
                    tcs.SetResult(cached);
                    return;
                }
            }

            var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
            image.GenerateMipmaps(); // so LinearWithMipmaps actually filters — no shimmer/aliasing at distance
            var tex = ImageTexture.CreateFromImage(image);

            if (tex != null && _gpuCache != null)
            {
                long size = textureData.Width * textureData.Height * 4;
                _gpuCache.Put(textureId, tex, size);
            }
            tcs.SetResult(tex);
            image.Dispose();
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
    /// first use. Releases the previous mesh ref so the GpuCache can reclaim it.
    /// <paramref name="flipV"/>: true for geometry whose UVs are in SL's bottom-left-origin
    /// convention and must be flipped for Godot's top-left sampling. Currently true for every
    /// caller — LLMesh assets, sculpt meshing, AND MeshFoundry prim output all need it (see the
    /// llvolume.cpp verification at the prim call site) — but it stays a parameter rather than a
    /// constant since sculpt meshing's flip is inferred from SL convention, not proven the same
    /// way.</summary>
    private void AssignSharedMesh(VisualState state, Guid key, MeshData data, bool flipV)
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
            mesh = BuildArrayMesh(data, flipV, out var faceIndices);
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

    /// <summary>Structural equality for the per-face texture/color array — used to detect a
    /// face-color/alpha/texture edit that changed neither the object's default TextureId nor
    /// RenderMaterialId (see the ColorTint/Faces re-apply gate in UpdateVisual). FaceTexture is a
    /// record struct, so SequenceEqual already compares every field value-wise; this just adds the
    /// null/length shortcuts SequenceEqual doesn't give you for free on two nullable arrays.</summary>
    private static bool FacesEqual(FaceTexture[]? a, FaceTexture[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
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
    private static ArrayMesh BuildArrayMesh(MeshData mesh, bool flipV, out int[] faceIndices)
    {
        var arrayMesh = new ArrayMesh();
        var indices = new List<int>(mesh.Submeshes.Count);

        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front. Left as-is,
            // every triangle here rasterizes as a backface — masked by CullMode.Disabled (needed
            // just to make anything render at all), but Godot's double-sided handling flips the
            // normal for perceived backfaces, inverting diffuse lighting on every SL-sourced mesh
            // in the scene while leaving shadows (depth-only, no normals) unaffected — exactly the
            // "shadow one way, shading the other way" bug reported and confirmed this session via
            // a T-pose + a debug shader + a gizmo pointing at the actual light direction. Fix:
            // reverse each triangle's own winding by swapping its last two indices, so each group
            // of 3 becomes (i0, i2, i1) instead of (i0, i1, i2) — every other per-vertex step is
            // unchanged, only the ORDER the 3 vertices of each triangle are submitted in.
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var p = sub.Positions[i];
                var n = sub.Normals[i];
                var uv = sub.UVs[i];

                st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
                st.SetUV(new Godot.Vector2(uv.X, flipV ? 1.0f - uv.Y : uv.Y));
                st.AddVertex(new Godot.Vector3(p.X, p.Z, -p.Y));
            }

            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                st.AddIndex(sub.Indices[t]);
                st.AddIndex(sub.Indices[t + 2]);
                st.AddIndex(sub.Indices[t + 1]);
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
        _visuals.Clear();
    }
}
