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
        // The sculpt type byte the currently-loaded sculpt geometry was built with. Tracked
        // alongside LoadedMeshId because an edit can change the stitching mode or the
        // Invert/Mirror flags without touching the sculpt map's UUID; gating the reload on the
        // id alone left the object rendering its previous (e.g. unmirrored) geometry forever.
        public byte LoadedSculptType;
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
    private readonly Dictionary<(PrimShape Shape, MeshDetailLevel Lod), Guid> _primShapeKeys = new();

    // Same idea for sculpts: one GpuCache key per (sculpt map, sculpt type) pair. See KeyForSculpt.
    private readonly Dictionary<(Guid SculptId, byte SculptType), Guid> _sculptKeys = new();

    // Per shared-mesh key: the SL face number of each surface, so any instance can apply that
    // face's texture via SetSurfaceOverrideMaterial.
    private readonly Dictionary<Guid, int[]> _meshFaceIndices = new();

    // glTF metallicRoughness maps that have been reported as "now actually sampled" (see the
    // ORM branch in BuildFaceMaterialAsync). Main-thread only.
    private readonly HashSet<Guid> _ormMapsSeen = new();

    // Objects already reported by the [SculptSite] scan. Main-thread only.
    private readonly HashSet<Guid> _sculptSitesLogged = new();

    // Objects already reported by the [RotSite] scan. Main-thread only.
    private readonly HashSet<Guid> _rotationSitesLogged = new();

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
    private const string BuildMarker = "2026-08-02-texgen-diagnostic";

    public void Initialize(World world, SLNG.Assets.AssetService assetService, GpuCache gpuCache)
    {
        GD.Print($"[ObjectRenderer] BUILD MARKER: {BuildMarker}");
        // Pull the shader family in (and trigger its compile) here on the main thread, rather
        // than letting the first worker-thread material build do it mid-frame.
        PrimShaderFamily.Preload();
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
            else if (state.MeshInstance.Visible && !state.ResourcesReleased
                     && state.UsedTextureIds.Count > 0 && _gpuCache != null && _assetService != null)
            {
                // Re-offer this object's current on-screen size to the GpuCache. A texture first
                // uploaded while the object was small/distant was downsampled and, before this,
                // stayed that way for the session however close the camera later got -- so
                // walking up to something left it permanently soft. GpuCache decides whether that
                // actually warrants a sharper re-upload (it ignores anything already at full
                // resolution, and requires a full discard level of headroom), so this is a cheap
                // no-op for the overwhelming majority of objects. Runs on the existing 4 Hz cull
                // tick rather than per frame, which is ample for approach speed.
                var (screenPixelArea, priority) = ComputeTextureLod(state.MeshInstance);
                if (screenPixelArea > 0f)
                {
                    foreach (var texId in state.UsedTextureIds)
                    {
                        _ = _gpuCache.GetOrUploadTextureAsync(
                            texId, _assetService, generateMipmaps: true,
                            screenPixelArea: screenPixelArea, priority: priority);
                    }
                }
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

    /// <summary>Prints the per-face texture placement of a selected object, in the same terms
    /// the SL build floater shows (repeats, offsets, rotation in degrees).
    ///
    /// Exists because "the texture looks wrong on that pillar" is not a debuggable statement:
    /// it cannot distinguish a rotation that never arrives from one applied the wrong way, from
    /// a mirror (negative repeat), from a plain offset. Select the object here, open the same
    /// object's Texture tab in Firestorm, and the two number sets either agree or they don't —
    /// which turns a visual impression into a decidable comparison. Firestorm shows rotation in
    /// DEGREES, so it is printed both ways.
    ///
    /// Note the Firestorm half of that comparison is unavailable for no-modify content, which is
    /// most of what a real grid contains. These numbers are still authoritative on their own
    /// though: the sim sends the full TextureEntry regardless of permissions, so this IS what SL
    /// told us to draw — the open question is only whether we draw it the viewer's way.</summary>
    public static void LogFaceTextureParams(Entity entity)
    {
        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim == null) return;

        if (prim.IsSculpt)
        {
            // The sculpt type byte decides horizontal mirroring, and SLNG and the viewer disagree
            // about what to do with it. The viewer computes
            //     reverse_horizontal = invert XOR mirror        (llvolume.cpp:3049, :6821)
            // and when set it BOTH reads the sculpt map's columns backwards
            // (sculptGenerateMapVertices, reversed_t = sizeT-t-1) AND flips the texture
            // coordinate (createSide, ss = 1-ss). PrimMesher does neither: on `mirror` it only
            // negates the X component of the sculpted position and flips triangle winding. So a
            // sculpt carrying either flag is expected to come out horizontally mirrored here —
            // which is exactly the reported symptom (plaster patch on the wrong side).
            byte st = prim.SculptType;
            bool invert = (st & 64) != 0, mirror = (st & 128) != 0;
            Logger.Info($"[FaceParams]   SCULPT type=0x{st:X2} stitching={st & 0x07} " +
                        $"invert={invert} mirror={mirror} reverse_horizontal={invert ^ mirror} " +
                        $"map={prim.SculptId.ToString()[..8]}");
        }

        Logger.Info($"[FaceParams] object {entity.LocalId} mesh={prim.IsMesh} sculpt={prim.IsSculpt} " +
                    $"default: repeat=({prim.RepeatU:0.###},{prim.RepeatV:0.###}) " +
                    $"offset=({prim.OffsetU:0.###},{prim.OffsetV:0.###}) " +
                    $"rot={prim.Rotation:0.####} rad = {Mathf.RadToDeg(prim.Rotation):0.##}° " +
                    $"texgen={DescribeTexGen(prim.TexGen)}");

        if (prim.Faces == null) { Logger.Info("[FaceParams]   (no per-face data — all faces use the default above)"); return; }

        for (int i = 0; i < prim.Faces.Length; i++)
        {
            var f = prim.Faces[i];
            Logger.Info($"[FaceParams]   face {i}: repeat=({f.RepeatU:0.###},{f.RepeatV:0.###}) " +
                        $"offset=({f.OffsetU:0.###},{f.OffsetV:0.###}) " +
                        $"rot={f.Rotation:0.####} rad = {Mathf.RadToDeg(f.Rotation):0.##}° " +
                        $"texgen={DescribeTexGen(f.TexGen)} " +
                        $"tex={f.TextureId.ToString()[..8]}");
        }
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
                    if (state.LoadedMeshId != prim.SculptId || state.LoadedSculptType != prim.SculptType)
                    {
                        state.LoadedMeshId = prim.SculptId;
                        state.LoadedSculptType = prim.SculptType;
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
                    var lod = PickPrimDetailLevel(entity, prim.Scale);
                    _ = LoadAndApplyPrimMeshAsync(state, prim.Shape, prim.ProfileCurve, lod);
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

            var sx = float.IsNaN(prim.Scale.X) ? 1f : Mathf.Clamp(prim.Scale.X, 0.001f, 1000f);
            var sy = float.IsNaN(prim.Scale.Y) ? 1f : Mathf.Clamp(prim.Scale.Y, 0.001f, 1000f);
            var sz = float.IsNaN(prim.Scale.Z) ? 1f : Mathf.Clamp(prim.Scale.Z, 0.001f, 1000f);
            state.MeshInstance.Scale = new Godot.Vector3(sx, sz, sy);

            // Planar UVs are derived from vertex position in metres, so a resize changes them.
            // The mesh and the materials both survive a resize untouched (only the node scale
            // moves), so without this a planar face keeps the tiling of its previous size.
            UpdatePrimScaleUniform(state, prim.Scale);

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
                AssignSharedMesh(state, KeyForSculpt(sculptId, sculptType), mesh, flipV: true);
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

    /// <summary>
    /// Picks a <see cref="MeshDetailLevel"/> from the object's apparent (on-screen) size --
    /// scale divided by distance to the local agent, approximating a real SL viewer's own
    /// distance/size-based LOD. Needed because <see cref="SLNG.Assets.AssetService.GetPrimMeshAsync"/>
    /// otherwise always defaults to Medium (12 sides for a curved profile) regardless of how
    /// large or close the object is: a heavily-scaled sphere/torus/ring cut rendered at Medium is
    /// visibly faceted -- flat, angular, "origami" -- compared to Firestorm's much smoother
    /// curve. Flat-profile prims (box/prism) are unaffected either way: LibreMetaverse's mesher
    /// only varies side count for curved profiles (Circle/HalfCircle/EqualTriangle), so this is
    /// safe to compute unconditionally.
    /// </summary>
    private MeshDetailLevel PickPrimDetailLevel(Entity entity, System.Numerics.Vector3 scale)
    {
        if (_world == null || !RenderConfig.TryGetLocalAgentGodotPos(_world, out var agentPos))
            return MeshDetailLevel.Medium;

        var transform = entity.GetComponent<TransformComponent>();
        if (transform == null) return MeshDetailLevel.Medium;

        var objectPos = RenderConfig.ToGodot(entity.RegionHandle, transform.Position);
        float distance = objectPos.DistanceTo(agentPos);

        float maxScale = Mathf.Max(scale.X, Mathf.Max(scale.Y, scale.Z));
        float apparentSize = maxScale / Mathf.Max(distance, 0.1f);

        if (apparentSize > 0.3f) return MeshDetailLevel.Highest;
        if (apparentSize > 0.1f) return MeshDetailLevel.High;
        if (apparentSize > 0.03f) return MeshDetailLevel.Medium;
        return MeshDetailLevel.Low;
    }

    private async System.Threading.Tasks.Task LoadAndApplyPrimMeshAsync(VisualState state, PrimShape shape, byte profileCurve, MeshDetailLevel lod)
    {
        if (_assetService == null) return;

        var mesh = await _assetService.GetPrimMeshAsync(shape, lod);

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
                AssignSharedMesh(state, KeyForShape(shape, lod), mesh, flipV: true);
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

        // FEAT-PERF-02 Phase 2: one detail/priority decision per object (not per face) -- computed
        // once here from the mesh's already-applied Position/Scale (see UpdateVisual) and the
        // camera, before this object's faces potentially fan out into several concurrent texture
        // requests below. Must stay ahead of the first await -- see ComputeTextureLod's note on
        // main-thread-only access.
        var (screenPixelArea, priority) = ComputeTextureLod(state.MeshInstance);

        // FEAT-RENDER-01 Phase 2 needs an object that actually HAS a texture rotation to be
        // testable at all. The one picked by eye turned out to have rot=0 on every face, so the
        // feature could not possibly have changed anything there. This reports where the real
        // rotation cases are, so the acceptance check can be aimed at one instead of guessed at.
        // Once per object, and still on the main thread (before the first await) because it reads
        // the node's transform.
        // Companion scan for the still-unexplained mirrored-texture sculpt. Everything analytic
        // came out "equivalent to the viewer", so what is needed next is one observation that does
        // not depend on that analysis being right: does a sculpt WITHOUT the invert flag look
        // mirrored too? If yes the fault is in our base sculpt UV generation and affects every
        // sculptie; if no, it is the invert path after all. Logging both groups with positions so
        // one of each can be compared side by side against the real viewer.
        if (prim.IsSculpt && _sculptSitesLogged.Add(state.EntityId))
        {
            var se = _world.GetEntity(state.EntityId);
            var sp2 = se?.GetComponent<TransformComponent>()?.Position;
            bool inv = (prim.SculptType & 64) != 0, mir = (prim.SculptType & 128) != 0;
            Logger.Info($"[SculptSite] object {se?.LocalId} at " +
                        $"{(sp2 is { } q ? $"<{q.X:0.#}, {q.Y:0.#}, {q.Z:0.#}>" : "?")} " +
                        $"stitching={prim.SculptType & 0x07} invert={inv} mirror={mir}");
        }

        if (prim.Faces != null && _rotationSitesLogged.Add(state.EntityId))
        {
            float maxRot = 0f;
            foreach (var f in prim.Faces) maxRot = Mathf.Max(maxRot, Mathf.Abs(f.Rotation));
            if (maxRot > 0.01f)
            {
                // SL region-local position, NOT the Godot global one: the latter carries the
                // region's world offset, so it reads as e.g. (523, 41, -879) and matches nothing
                // you can type into a viewer's location bar or compare against Firestorm.
                var ent = _world.GetEntity(state.EntityId);
                var slPos = ent?.GetComponent<TransformComponent>()?.Position;
                string where = slPos is { } sp ? $"<{sp.X:0.#}, {sp.Y:0.#}, {sp.Z:0.#}>" : "?";
                Logger.Info($"[RotSite] object {ent?.LocalId} at {where} rotation up to " +
                            $"{Mathf.RadToDeg(maxRot):0.#}° — usable as a Phase 2 test target");
            }
        }

        var defaultFace = new FaceTexture(prim.TextureId, prim.RenderMaterialId, prim.ColorTint, prim.RepeatU, prim.RepeatV, prim.OffsetU, prim.OffsetV, prim.Rotation, prim.TexGen);

        // Fallback solid / mesh without per-surface face info: one material for the whole node.
        if (!_meshFaceIndices.TryGetValue(state.LoadedMeshKey, out var faceIndices) || faceIndices.Length == 0)
        {
            var (mat, used) = await BuildFaceMaterialAsync(defaultFace, prim.Scale, screenPixelArea, priority, prim.IsSculpt);
            ApplyOnMainThread(state, () => state.MeshInstance.MaterialOverride = mat, used);
            return;
        }

        var faceTasks = new List<System.Threading.Tasks.Task<(int Surface, ShaderMaterial Material, List<Guid> Used)>>();

        for (int surface = 0; surface < faceIndices.Length; surface++)
        {
            int faceIdx = faceIndices[surface];
            FaceTexture ft = (prim.Faces != null && faceIdx >= 0 && faceIdx < prim.Faces.Length)
                ? prim.Faces[faceIdx] : defaultFace;

            int surf = surface; // capture
            faceTasks.Add(BuildFaceMaterialAsync(ft, prim.Scale, screenPixelArea, priority, prim.IsSculpt).ContinueWith(t =>
            {
                return (surf, t.Result.Material, t.Result.Used);
            }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously));
        }

        var results = await System.Threading.Tasks.Task.WhenAll(faceTasks);
        var allUsed = new List<Guid>();

        foreach (var result in results)
        {
            allUsed.AddRange(result.Used);
            int surf = result.Surface;
            var material = result.Material;

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
    private async System.Threading.Tasks.Task<(ShaderMaterial Material, List<Guid> Used)> BuildFaceMaterialAsync(FaceTexture ft, System.Numerics.Vector3 primScale, float screenPixelArea, float priority, bool isSculpted = false)
    {
        var used = new List<Guid>();
        var colorTint = new Godot.Color(ft.Color.X, ft.Color.Y, ft.Color.Z, ft.Color.W);

        // Scale the object's on-screen pixel area down to what ONE TILE of this face's texture
        // actually covers, before it decides a discard level. Ported from LLFace::
        // getTextureVirtualSize (llface.cpp:2245-2264):
        //     tdim       = mTexExtents[1] - mTexExtents[0]     // the face's UV span
        //     texel_area = |tdim * 0.5|^2 * PI
        //     face_area  = mPixelArea / clamp(texel_area, 1/64, 128)
        // A face's UV span IS its repeat count, so RepeatU/RepeatV stand in for tdim here. The
        // effect is the intuitive one: a texture tiled 4x across a face has each tile covering a
        // quarter of it, so one tile needs proportionally fewer texels and can take a higher
        // discard; a face showing only part of a texture (repeat < 1) is effectively zoomed in and
        // needs a LOWER discard to stay sharp. Without this, per-face texture scaling was ignored
        // entirely and every face of an object shared the object's raw pixel area.
        //
        // Sculpts are deliberately exempt when texel_area > 1, exactly as the viewer notes
        // ("sculpts can break assumptions about texel area") -- a sculpt's UV layout is generated
        // from its map, not authored per face, so its repeats are not a reliable tiling signal.
        float texelArea = new Godot.Vector2(ft.RepeatU * 0.5f, ft.RepeatV * 0.5f).LengthSquared() * Mathf.Pi;
        if (texelArea <= 0f) texelArea = 1f; // probably animated -- viewer uses the same default
        if (!(isSculpted && texelArea > 1f))
        {
            screenPixelArea /= Mathf.Clamp(texelArea, 0.015625f, 128f);
        }

        // SL face rotation, FEAT-RENDER-01 Phase 2: now rendered at its real angle. Until
        // 2026-08-01 only 0 and ±π were representable, because a StandardMaterial3D UV transform
        // is scale+offset with no rotation term; every other angle was rendered UNROTATED. That
        // was not a corner case -- 49 faces at exactly pi/2 in a single view of OSGrid's Dangazi
        // Forest, where it visibly mis-placed textures (a pillar's light plaster patch sat
        // top-RIGHT instead of Firestorm's top-LEFT). The old ±π case negated both repeats, which
        // is the same point-mirror a real π rotation produces, so those faces are unaffected by
        // this change; angles between 0.75π and π, previously snapped to π, now render correctly.
        //
        // The angle goes to the shader unmodified even though the meshes are flipV. Deriving it:
        // the viewer rotates in bottom-origin space with [c, s; -s, c] (llface.cpp:734-756), and
        // our centred V is p_v = -p_t. Substituting p_t = -p_v and mapping back through
        // p_v' = -t' flips BOTH sine signs, giving [c, -s; s, c] -- which is exactly what
        // slng_transform_uv already applies. The two sign inversions cancel, so no negation here.
        //
        // AvatarRenderer still carries the old ±π approximation; it moves to this family in
        // Phase 3.
        float repeatU = ft.RepeatU, repeatV = ft.RepeatV;

        // FEAT-RENDER-01 Phase 1: a ShaderMaterial from the prim shader family instead of a
        // StandardMaterial3D. Two properties that used to be set here per material are now baked
        // into the shaders themselves, and MUST NOT be assumed lost:
        //
        //  * Anisotropic filtering -- now the `filter_linear_mipmap_anisotropic` hint on every
        //    texture uniform (prim_common.gdshaderinc). It matches the real viewer's
        //    TFO_ANISOTROPIC path (llrender.cpp:530-540, GL_TEXTURE_MAX_ANISOTROPY at the driver
        //    maximum). Plain isotropic mip selection takes the LARGEST UV derivative for a pixel,
        //    so wherever UVs are strongly stretched in one direction it drops to a coarse mip
        //    across the whole region. A sphere-stitched sculpt is the worst case -- U compresses
        //    to a point at the pole -- and it showed exactly that: a cushion's fine leather grain
        //    smeared into a soft radial swirl while Firestorm kept it crisp (live-compared
        //    2026-08-01, texture confirmed uploading at full 512x512, so never a resolution
        //    problem).
        //  * Back-face culling -- now `cull_back` in each variant's render_mode, matching the
        //    viewer's global default (llrender.cpp:863, `glCullFace(GL_BACK)`). The viewer lifts
        //    it ONLY for particles (lldrawpoolalpha.cpp:643) and for a GLTF material that
        //    declares mDoubleSided (lldrawpool.cpp:839, :856) -- never blanket for prim faces,
        //    and notably not for alpha-blended ones either. With culling off, a closed prim draws
        //    its own interior surfaces over its near ones and solid objects read as glassy shells
        //    (live-verified 2026-08-01 on a sculpted seat).
        //
        // Both were expensive to find; if a future variant is added, it inherits neither
        // automatically.
        var material = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
        material.SetShaderParameter(PrimShaderFamily.AlbedoColor, colorTint);
        material.SetShaderParameter(PrimShaderFamily.UvScale, new Godot.Vector2(repeatU, repeatV));
        material.SetShaderParameter(PrimShaderFamily.UvRotation, ft.Rotation);

        // TexGen. Collapsed from the raw wire value to a 0/1 flag, so the two SL modes we do not
        // implement (spherical=4, cylindrical=6) fall back to the mesh's own UVs instead of
        // selecting a shader branch that does not exist. prim_scale is in SL axes, unswizzled:
        // slng_planar_uv reconstructs the SL-space position from the Godot vertex and then
        // multiplies component-wise, so it needs SL's own (X, Y, Z), not the node's (X, Z, Y).
        material.SetShaderParameter(PrimShaderFamily.UvTexGen, ft.IsPlanar ? 1 : 0);
        material.SetShaderParameter(PrimShaderFamily.PrimScale,
            new Godot.Vector3(primScale.X, primScale.Y, primScale.Z));
        // Centered like SL (u' = (u-0.5)*repeat + 0.5 + off) — the shader scales UVs from the
        // corner, so without the 0.5-0.5*repeat correction any repeat != 1 shifts the texture
        // off-center. Folded into the offset so the shader stays a plain multiply-add.
        //
        // NOTE THE MINUS ON V. The viewer transforms in SL's bottom-origin space
        // (llface.cpp:734-756, xform): t_tex = (t-0.5)*magT + 0.5 + offT. Every world mesh here
        // is built with flipV:true (see AssignSharedMesh), so our vertex V is v = 1-t and the
        // texture's rows are likewise top-origin, giving v_tex = 1 - t_tex. Substituting:
        //     v_tex = 1 - [ (t-0.5)*magT + 0.5 + offT ]
        //           = (v-0.5)*magT + 0.5 - offT
        // so the flip negates the OFFSET while leaving the scale term alone. U is unflipped and
        // keeps its plus — and that asymmetry is the confirmation: a single flip predicts exactly
        // one of the two axes changing sign, which is what the algebra produces. This line read
        // "+ ft.OffsetV" until 2026-08-01, i.e. any face with a V offset had its texture shifted
        // the wrong way by twice the offset.
        material.SetShaderParameter(PrimShaderFamily.UvOffset, new Godot.Vector2(
            0.5f - 0.5f * repeatU + ft.OffsetU,
            0.5f - 0.5f * repeatV - ft.OffsetV));

        // Translucent per-face tint: pick the blending variant. This is the direct equivalent of
        // the old `material.Transparency = Alpha` — see PrimShaderFamily for why transparency is
        // a shader swap rather than a property.
        bool tintIsTranslucent = colorTint.A < 0.99f;
        if (tintIsTranslucent)
        {
            material.Shader = PrimShaderFamily.Blend;
        }

        if (ft.MaterialId != Guid.Empty && _assetService != null)
        {
            var pbr = await _assetService.GetMaterialAsync(ft.MaterialId);
            if (pbr != null)
            {
                var baseColor = new Godot.Color(pbr.BaseColorFactor.X, pbr.BaseColorFactor.Y, pbr.BaseColorFactor.Z, pbr.BaseColorFactor.W) * colorTint;
                material.SetShaderParameter(PrimShaderFamily.AlbedoColor, baseColor);
                material.SetShaderParameter(PrimShaderFamily.MetallicFactor, pbr.MetallicFactor);
                material.SetShaderParameter(PrimShaderFamily.RoughnessFactor, pbr.RoughnessFactor);
                material.SetShaderParameter(PrimShaderFamily.EmissionEnabled, pbr.EmissiveFactor != System.Numerics.Vector3.Zero);
                material.SetShaderParameter(PrimShaderFamily.EmissionColor,
                    new Godot.Color(pbr.EmissiveFactor.X, pbr.EmissiveFactor.Y, pbr.EmissiveFactor.Z));

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
                    material.Shader = PrimShaderFamily.Blend;
                }
                else if (pbr.AlphaMode == SLNG.Assets.PbrAlphaMode.Mask)
                {
                    material.Shader = PrimShaderFamily.Scissor;
                    material.SetShaderParameter(PrimShaderFamily.AlphaScissorThreshold, pbr.AlphaCutoff);
                }

                var tasks = new List<System.Threading.Tasks.Task>();
                if (pbr.BaseColorTextureId != Guid.Empty)
                {
                    used.Add(pbr.BaseColorTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.BaseColorTextureId, screenPixelArea, priority).ContinueWith(t =>
                        Godot.Callable.From(() =>
                        {
                            if (IsInstanceValid(t.Result))
                            {
                                material.SetShaderParameter(PrimShaderFamily.AlbedoTexture, t.Result);
                                material.SetShaderParameter(PrimShaderFamily.HasAlbedoTexture, true);
                            }
                            else GD.PrintErr($"[FaceTex] object PBR baseColor {pbr.BaseColorTextureId} pixelArea={screenPixelArea:F0} fetch/decode returned null");
                        }).CallDeferred()));
                }
                if (pbr.NormalTextureId != Guid.Empty)
                {
                    used.Add(pbr.NormalTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.NormalTextureId, screenPixelArea, priority).ContinueWith(t =>
                        Godot.Callable.From(() =>
                        {
                            if (IsInstanceValid(t.Result))
                            {
                                material.SetShaderParameter(PrimShaderFamily.NormalTexture, t.Result);
                                material.SetShaderParameter(PrimShaderFamily.HasNormalTexture, true);
                            }
                            else GD.PrintErr($"[FaceTex] object PBR normal {pbr.NormalTextureId} pixelArea={screenPixelArea:F0} fetch/decode returned null");
                        }).CallDeferred()));
                }
                if (pbr.MetallicRoughnessTextureId != Guid.Empty)
                {
                    used.Add(pbr.MetallicRoughnessTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.MetallicRoughnessTextureId, screenPixelArea, priority).ContinueWith(t =>
                        Godot.Callable.From(() =>
                        {
                            if (IsInstanceValid(t.Result))
                            {
                                // DELIBERATE Phase-1 behaviour change, and the only one: this used
                                // to set BaseMaterial3D.OrmTexture, which a StandardMaterial3D
                                // NEVER SAMPLES -- Godot only reads texture_orm when the material
                                // is an ORMMaterial3D, so glTF metallicRoughness maps were silently
                                // ignored on every world prim. The shader family honours it, so
                                // such faces now get their authored roughness/metallic. Logged
                                // because it makes the Phase-1 "visually identical" comparison
                                // ambiguous otherwise: if the scene looks different, this line
                                // tells you whether an ORM face was even involved.
                                material.SetShaderParameter(PrimShaderFamily.OrmTexture, t.Result);
                                material.SetShaderParameter(PrimShaderFamily.HasOrmTexture, true);
                                // Info, not Debug: Logger's default level is Info, so a Debug line
                                // here would never print and this note would be worthless exactly
                                // when it's needed. Once per texture id (this callback is
                                // main-thread via CallDeferred, so the set needs no lock) to keep
                                // a heavily-reused ORM map from spamming.
                                if (_ormMapsSeen.Add(pbr.MetallicRoughnessTextureId))
                                    Logger.Info($"[FaceTex] ORM map now sampled (StandardMaterial3D ignored it): {pbr.MetallicRoughnessTextureId}");
                            }
                            else GD.PrintErr($"[FaceTex] object PBR metallicRoughness {pbr.MetallicRoughnessTextureId} pixelArea={screenPixelArea:F0} fetch/decode returned null");
                        }).CallDeferred()));
                }
                if (pbr.EmissiveTextureId != Guid.Empty)
                {
                    used.Add(pbr.EmissiveTextureId);
                    tasks.Add(GetOrCreateGpuTextureAsync(pbr.EmissiveTextureId, screenPixelArea, priority).ContinueWith(t =>
                        Godot.Callable.From(() =>
                        {
                            if (IsInstanceValid(t.Result))
                            {
                                material.SetShaderParameter(PrimShaderFamily.EmissionTexture, t.Result);
                                material.SetShaderParameter(PrimShaderFamily.HasEmissionTexture, true);
                            }
                            else GD.PrintErr($"[FaceTex] object PBR emissive {pbr.EmissiveTextureId} pixelArea={screenPixelArea:F0} fetch/decode returned null");
                        }).CallDeferred()));
                }
                // No await Task.WhenAll(tasks) here! Let the textures populate asynchronously so the mesh renders immediately.
            }
        }
        else if (ft.TextureId != Guid.Empty)
        {
            used.Add(ft.TextureId);
            _ = GetOrCreateGpuTextureAsync(ft.TextureId, screenPixelArea, priority).ContinueWith(t =>
            {
                var tex = t.Result;
                if (tex != null)
                {
                    Godot.Callable.From(() =>
                    {
                        // The texture can legitimately be evicted+disposed by the GpuCache between
                        // this continuation being scheduled and actually running on the main thread
                        // -- e.g. the object this material belongs to went out of draw distance and
                        // released its ref in the meantime, and nothing else was still holding one.
                        // Checked here (inside the deferred callback, not before scheduling it) so
                        // there's no gap left for a same-frame eviction to invalidate the check.
                        if (!IsInstanceValid(tex)) return;
                        material.SetShaderParameter(PrimShaderFamily.AlbedoTexture, tex);
                        material.SetShaderParameter(PrimShaderFamily.HasAlbedoTexture, true);
                        ApplyAlphaCutout(material, tex, tintIsTranslucent);
                    }).CallDeferred();
                }
                else
                {
                    // FEAT-PERF-02: this branch previously failed completely silently -- unlike
                    // AvatarRenderer's identical situation (see its "[FaceTex] ... fetch/decode
                    // returned null" log), a world-object face that never got its texture just
                    // rendered flat AlbedoColor forever with zero diagnostic trail. One line per
                    // failed id (not per attempt) so this doesn't itself become log spam.
                    GD.PrintErr($"[FaceTex] object texture {ft.TextureId} pixelArea={screenPixelArea:F0} fetch/decode returned null — face renders untextured (see [TextureFetch] for the HTTP reason)");
                }
            });
        }

        return (material, used);
    }

    /// <summary>Picks the right transparency mode from the texture's actual alpha:
    /// binary alpha (foliage/fences) → alpha-scissor cutout; graded alpha (glass, soft edges)
    /// → alpha blend; fully opaque → left fully opaque, single-sided. Only genuinely alpha
    /// surfaces render double-sided.
    ///
    /// A <c>DetectAlpha() == None</c> verdict is treated as authoritative "this texture is
    /// opaque" and returns early, leaving Transparency and CullMode at their opaque defaults.
    /// An earlier version deliberately fell THROUGH to the AlphaScissor branch on None, on the
    /// theory that DetectAlpha() false negatives were the bigger risk and a cutout test on a
    /// fully-opaque texture is a harmless no-op. Both halves of that were wrong (live-verified
    /// 2026-08-01, [FaceMatDiag] logging): every ordinary opaque world prim reported
    /// tintA=1.000/detectAlpha=None and still came out AlphaScissor + CullMode.Disabled, so
    /// EVERY object in the world rendered double-sided in a discard pass — a sculpted seat
    /// showed its own interior surfaces through its front, reading as a glassy, see-through
    /// shell instead of the solid cushion the real viewer draws. The alpha=0-placeholder case
    /// the fallback was protecting against is a DECODE-side problem (a texture whose alpha
    /// plane never arrived has no alpha channel to detect, so it is correctly None here);
    /// AssetService.DecodeTexture now flags those as degraded and retries instead — that is
    /// the right layer for it, and it does not cost every opaque prim its backface culling.
    ///
    /// Deliberately stays on AlphaScissor (not AlphaHash) for the Bit case: this runs per-face
    /// on potentially thousands of world prims, so it keeps the cheaper cutout mode. Avatar
    /// content (bakes, worn mesh attachments) is bounded per-avatar and uses AlphaHash instead —
    /// see AvatarRenderer for that reasoning.
    ///
    /// <paramref name="tintIsTranslucent"/> is passed in rather than read back off the material:
    /// since FEAT-RENDER-01 the transparency mode IS the assigned shader, and asking "which
    /// variant is this" would be an indirect re-derivation of a fact the caller already knows.
    /// It is exactly the old <c>material.Transparency == Alpha</c> test — in this (non-PBR)
    /// path the per-face color tint is the only thing that can have selected the blend variant
    /// before now.</summary>
    private static void ApplyAlphaCutout(ShaderMaterial material, ImageTexture tex, bool tintIsTranslucent)
    {
        var img = tex.GetImage();
        if (img == null) return;

        var alphaMode = img.DetectAlpha();

        // Fully opaque texture: leave the material exactly as-is (opaque, back-face culled).
        // A translucent per-face color tint is a SEPARATE SL signal that BuildFaceMaterialAsync
        // has already applied (the blend variant) and must survive, so only the genuinely-opaque
        // case returns here.
        if (alphaMode == Image.AlphaMode.None && !tintIsTranslucent)
        {
            return;
        }

        // If the primitive is already explicitly translucent via color tint, keep true Alpha blending.
        // Otherwise, pick the right variant based on the texture's alpha content.
        if (!tintIsTranslucent)
        {
            if (alphaMode == Image.AlphaMode.Blend)
            {
                // Smooth translucent edges (hair, glass, clouds)
                material.Shader = PrimShaderFamily.Blend;
            }
            else
            {
                // Binary alpha (fences, foliage). The alpha-to-coverage that used to be set
                // here is baked into prim_scissor.gdshader's render_mode -- and it does real
                // work: project.godot runs 4x MSAA (msaa_3d=2), despite an older comment here
                // claiming 3D MSAA was off.
                material.Shader = PrimShaderFamily.Scissor;
                material.SetShaderParameter(PrimShaderFamily.AlphaScissorThreshold, 0.5f);
            }
        }
        // CullMode is deliberately NOT touched here -- see BuildFaceMaterialAsync's CullMode
        // comment. The real viewer back-face culls alpha-blended and alpha-masked prim faces
        // exactly like opaque ones (lldrawpoolalpha.cpp only lifts culling for particles and
        // explicitly-double-sided GLTF materials), so an alpha face is not a reason to render
        // an object's interior surfaces.
    }

    // FEAT-PERF-02: thin wrapper -- the real fetch/decode/Image/mipmap/upload work (and its
    // single-flight dedup across every renderer, not just this one) lives in
    // GpuCache.GetOrUploadTextureAsync.
    private System.Threading.Tasks.Task<ImageTexture?> GetOrCreateGpuTextureAsync(Guid textureId, float screenPixelArea = 0f, float priority = 0f)
    {
        if (_gpuCache == null || _assetService == null)
            return System.Threading.Tasks.Task.FromResult<ImageTexture?>(null);

        return _gpuCache.GetOrUploadTextureAsync(textureId, _assetService, generateMipmaps: true, screenPixelArea: screenPixelArea, priority: priority);
    }

    /// <summary>
    /// FEAT-PERF-02 Phase 2: decides both how much texture detail this object needs (discard
    /// level) and how urgently it should be fetched (queue priority), from one shared measure of
    /// on-screen prominence.
    ///
    /// <para>Measured from the CAMERA, not the avatar: the two diverge whenever the user zooms or
    /// orbits away from their own body, and it's what the camera is pointed at that needs to
    /// resolve first. (This is why the local-agent position that an earlier revision used was
    /// wrong -- zooming across the region kept prioritizing scenery around the avatar.) Falls back
    /// to the agent position only when no active camera exists yet, e.g. mid-login before
    /// AvatarController.MakeCurrent().</para>
    ///
    /// <para>Approximates the real SL-viewer discard formula (protocol-re verified: real discard =
    /// floor(log4(textureTexelCount / onScreenPixelArea)), see the Phase 2.1 write-up in
    /// docs/specs/FEAT-PERF-02-texture-loading-speed.md) using only what's cheap here. The real
    /// formula needs the camera's FOV/viewport pixel scale and the texture's true resolution
    /// (unknown before it has ever been fetched); this uses <c>apparentSize = radius / distance</c>
    /// -- the small-angle approximation of the same angular-size quantity -- against thresholds
    /// picked so each step is roughly a halving of apparent linear size, matching the real
    /// formula's log4-of-AREA (= log2-of-LINEAR-SIZE) shape. Same curve shape, not the same curve.</para>
    ///
    /// <para>Objects behind the camera keep a real (if reduced) priority rather than being pushed
    /// to the back of the queue: turning around is instant and common, and a hard cutoff would
    /// mean everything behind you starts from scratch the moment you do.</para>
    ///
    /// <para>Caller must be on the main thread -- reads the viewport/camera and the mesh's
    /// scene-graph state. Both call sites in ApplyFaceMaterialsAsync reach this before their
    /// first await, from main-thread-deferred callers.</para>
    /// </summary>
    private (float ScreenPixelArea, float Priority) ComputeTextureLod(MeshInstance3D meshInstance)
    {
        Vector3 viewPoint;
        Vector3 viewDirection = Vector3.Zero;

        var camera = GetViewport()?.GetCamera3D();
        if (camera != null && IsInstanceValid(camera))
        {
            viewPoint = camera.GlobalPosition;
            // -Z is forward for a Godot Camera3D.
            viewDirection = -camera.GlobalTransform.Basis.Z.Normalized();
        }
        else if (_world != null && RenderConfig.TryGetLocalAgentGodotPos(_world, out var agentPos))
        {
            viewPoint = agentPos;
        }
        else
        {
            return (0f, 0f); // Nothing to measure against yet -- stay conservative (full detail).
        }

        var toObject = meshInstance.Position - viewPoint;
        float distance = toObject.Length();

        float radius = 1f;
        if (meshInstance.Mesh != null)
        {
            var localAabb = meshInstance.Mesh.GetAabb();
            var worldSize = localAabb.Size * meshInstance.Scale;
            radius = worldSize.Length() * 0.5f;
        }

        float apparentSize = radius / Mathf.Max(distance, 0.1f);

        // Screen-space area this object covers, in PIXELS -- a direct port of the real viewer's
        // LLFace::calcPixelArea (llface.cpp:2382-2398):
        //     dist      = max(|center - camera| - |halfExtents|, 0.001)   // to the NEAR surface
        //     app_angle = atan(|halfExtents| / dist)
        //     radius    = app_angle * sCurPixelAngle
        //     mPixelArea = radius^2 * PI
        // with sCurPixelAngle = windowHeightRaw / camera vertical FOV in radians
        // (lldrawable.cpp:90). This is the quantity the viewer feeds into its discard decision,
        // and unlike a bare size/distance ratio it accounts for viewport resolution and FOV --
        // the same object at the same distance genuinely needs more texels on a taller viewport.
        float pixelsPerRadian = 1024f; // conservative fallback if no camera/viewport is available
        var vp = GetViewport();
        if (camera != null && IsInstanceValid(camera) && vp != null)
        {
            float viewportHeight = vp.GetVisibleRect().Size.Y;
            // Camera3D.Fov is the VERTICAL fov in degrees under the default KeepHeight aspect mode.
            float fovRadians = Mathf.DegToRad(camera.Fov);
            if (viewportHeight > 0f && fovRadians > 0.0001f)
                pixelsPerRadian = viewportHeight / fovRadians;
        }

        float nearDistance = Mathf.Max(distance - radius, 0.001f);
        float appAngle = Mathf.Atan(radius / nearDistance);
        float radiusPixels = appAngle * pixelsPerRadian;
        float screenPixelArea = radiusPixels * radiusPixels * Mathf.Pi;

        // FEAT-PERF-02: the downsample this drives is a LOCAL post-decode shrink only, not the
        // network fetch -- see GpuCache.FetchAndUploadTextureAsync. Network-side discard
        // truncation via HTTP Range was tried and disabled: Magick.NET does not tolerate a
        // deliberately-Range-truncated J2C stream, falling back to CoreJ2K en masse and, at
        // aggressive discard levels, frequently failing outright rather than degrading
        // gracefully (one session: 6752 of 6786 total log lines were CoreJ2K "Codestream
        // truncated" warnings; reported live as slow loading + ~80% of textures missing vs.
        // ~100% in Firestorm on the same region). Always fetching+decoding the full asset
        // sidesteps that bug entirely -- the shrink only trades network bandwidth (still full,
        // unresolved -- see the FEAT-PERF-02 spec) for a real VRAM reduction on distant/small
        // objects, which is where a user-reported "GPU memory really filling up" needs the win.
        //
        // This method deliberately does NOT decide the discard level itself anymore. It used to,
        // from hardcoded thresholds on `radius / distance` -- which ignored both the viewport
        // resolution and, more importantly, the TEXTURE'S OWN RESOLUTION, so a 64x64 and a 1024x1024
        // texture on the same prim were shrunk by the same number of halvings. Those thresholds also
        // demanded radius/distance >= 0.5 for full detail, i.e. full resolution only within roughly
        // two radii of the object: an ordinary ~1m prim went to half resolution past ~1.6m away and
        // quarter resolution past ~3.5m, which reads as "everything in the world is blurry"
        // (user-reported, 2026-08-01). The real viewer instead compares texels to on-screen pixels
        // (LLViewerLODTexture::processTextureStats, llviewertexture.cpp:
        //     discard = floor( log(mTexelsPerImage / mMaxVirtualSize) / log(4) )
        // ), which needs the decoded texture's dimensions -- known only inside GpuCache. So the
        // screen-space pixel area travels down there and the discard is computed at that point.
        //
        // Priority stays on the old apparent-size scale (roughly 0..1, comparable across objects
        // and unchanged in meaning for AssetService's fetch ordering), halved for anything behind
        // the camera plane.
        float priority = apparentSize;
        if (viewDirection != Vector3.Zero && distance > 0.001f
            && viewDirection.Dot(toObject / distance) < 0f)
        {
            priority *= 0.5f;
        }

        return (screenPixelArea, priority);
    }

    /// <summary>Returns a stable GpuCache key for a (shape, LOD) pair -- one id per unique
    /// shape+detail-level combination. MUST include lod: <see cref="AssetService.GetPrimMeshAsync"/>
    /// generates different geometry (vertex/side count) for the same shape at different
    /// MeshDetailLevel, and this cache is otherwise the ONLY thing standing between "the correct
    /// mesh for this LOD" and "whatever mesh some other differently-scaled/positioned prim with
    /// the identical PrimShape happened to cache first" -- shape-only keying (the pre-LOD-support
    /// version of this method) would silently reuse a stale, wrong-LOD ArrayMesh for every prim
    /// sharing that shape after the first one loads, since AssignSharedMesh trusts the cache over
    /// whatever MeshData it was just asked to assign.</summary>
    /// <summary>Returns a stable GpuCache key for a (sculpt map, sculpt type) pair.
    /// <para>MUST include the type byte. It carries the stitching mode (sphere/torus/plane/
    /// cylinder) plus the Invert (0x40) and Mirror (0x80) flags, and every one of those changes
    /// the generated geometry — Mirror negates X, Invert reverses the triangle winding, and the
    /// stitching mode decides whether the vertex grid wraps, pinches at the poles, or stays an
    /// open sheet. Reusing one sculpt map with different flags inside a single linkset is normal
    /// SL content authoring (a tree's left and right branch are one map, one of them mirrored).</para>
    /// <para>Keying this cache by the bare sculpt-map UUID — as it did — meant the FIRST prim to
    /// finish uploading won, and every later prim sharing that map silently rendered the first
    /// one's geometry no matter what its own flags said: AssignSharedMesh returns the cached
    /// ArrayMesh and never looks at the MeshData it was just handed. That is what made the
    /// Dangazi Forest tree read as three loose parts instead of one tree (its mirrored branch was
    /// drawn unmirrored, pointing the wrong way), and it mis-shapes any scenery that reuses a
    /// sculpt map with different flags. Exactly the failure mode <see cref="KeyForShape"/> already
    /// documents for prim LOD.</para></summary>
    private Guid KeyForSculpt(Guid sculptId, byte sculptType)
    {
        var key2 = (sculptId, sculptType);
        if (!_sculptKeys.TryGetValue(key2, out var key))
        {
            key = Guid.NewGuid();
            _sculptKeys[key2] = key;
        }
        return key;
    }

    private Guid KeyForShape(PrimShape shape, MeshDetailLevel lod)
    {
        var key2 = (shape, lod);
        if (!_primShapeKeys.TryGetValue(key2, out var key))
        {
            key = Guid.NewGuid();
            _primShapeKeys[key2] = key;
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
    /// <summary>Human-readable texgen for the diagnostic log, from the RAW wire value.
    /// Spelled out because the previous version tested <c>== 1</c> against a field that holds
    /// SL's enum, where planar is 2 -- so it reported "default" for every planar face there has
    /// ever been, and the missing implementation looked like it was never being hit.</summary>
    private static string DescribeTexGen(byte texGen) => texGen switch
    {
        FaceTexture.TexGenDefault => "default",
        FaceTexture.TexGenPlanar => "PLANAR",
        4 => "spherical (not implemented — falls back to default)",
        6 => "cylindrical (not implemented — falls back to default)",
        _ => $"unknown ({texGen})"
    };

    /// <summary>Pushes a new prim size into every material already on this visual. Only the
    /// planar projection reads it; for a default-texgen face the uniform is inert.</summary>
    private static void UpdatePrimScaleUniform(VisualState state, System.Numerics.Vector3 scale)
    {
        if (!IsInstanceValid(state.MeshInstance)) return;
        var v = new Godot.Vector3(scale.X, scale.Y, scale.Z);

        if (state.MeshInstance.MaterialOverride is ShaderMaterial mo)
            mo.SetShaderParameter(PrimShaderFamily.PrimScale, v);

        int surfaces = state.MeshInstance.Mesh?.GetSurfaceCount() ?? 0;
        for (int i = 0; i < surfaces; i++)
        {
            if (state.MeshInstance.GetSurfaceOverrideMaterial(i) is ShaderMaterial sm)
                sm.SetShaderParameter(PrimShaderFamily.PrimScale, v);
        }
    }

    private static ArrayMesh BuildArrayMesh(MeshData mesh, bool flipV, out int[] faceIndices)
    {
        var arrayMesh = new ArrayMesh();
        var indices = new List<int>(mesh.Submeshes.Count);

        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front.
            // Reverse each triangle's winding by swapping its last two indices.
            // We supply the vertices once, then supply the reversed indices.
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

            // st.GenerateTangents(); // Disabled to prevent Vulkan driver NaN explosion on degenerate triangles
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
