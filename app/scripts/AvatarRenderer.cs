using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Assets;
using System.Collections.Generic;
using System.Linq;
using System;

namespace SLNG.App;

public partial class AvatarRenderer : Node3D
{
    private class AvatarVisual
    {
        public Node3D Root { get; }
        public Skeleton3D? Skeleton { get; set; }
        public Dictionary<string, MeshInstance3D> Parts { get; } = new();
        // Base (un-morphed) body-part data, keyed by part name. Kept so the body meshes can be
        // re-morphed and rebuilt whenever the avatar's shape (VisualParams) changes.
        public Dictionary<string, SLNG.Assets.AvatarBodyPartMesh> BodyPartData { get; } = new();
        public Dictionary<int, Guid> LoadedTextures { get; } = new();
        public AvatarAnimationPlayer AnimPlayer { get; } = new();
        public List<Guid>? LoadedAnimationIds { get; set; }
        public byte[]? LastAppliedVisualParams { get; set; }
        // Joint-position overrides harvested from worn rigged meshes (viewer:
        // LLVOAvatar::addAttachmentOverridesForObject). Keyed by bone name; value is the
        // overridden LOCAL joint position in SL space (relative to the parent joint).
        // ApplyShape re-applies these after rebuilding rests so they survive shape updates.
        public Dictionary<string, System.Numerics.Vector3> JointPosOverrides { get; } = new();
        // Bakes-on-Mesh bookkeeping (viewer: LLVOAvatar::updateMeshVisibility +
        // LLViewerObject::getBakedTextureForMagicId). Which bake channels (AvatarTextureIndex)
        // any worn mesh consumes — used to hide the matching system body parts — and the worn
        // meshes whose face materials must be re-resolved when a new server bake arrives.
        public HashSet<int> AttachmentBakeChannels { get; } = new();
        public List<(MeshInstance3D Mi, int[] FaceIndices, FaceTexture[]? Faces, FaceTexture DefaultFace)> BomAttachments { get; } = new();

        public AvatarVisual()
        {
            Root = new Node3D();
        }

        public void QueueFree() => Root.QueueFree();
    }

    private World? _world;
    private AssetService? _assetService;
    private GpuCache? _gpuCache;
    private AvatarSkeleton? _avatarSkeleton;
    private readonly Dictionary<Guid, AvatarVisual> _visuals = new();
    // attachment entity ID → BoneAttachment3D node parented to the avatar skeleton
    private readonly Dictionary<Guid, BoneAttachment3D> _attachmentNodes = new();
    // attachment entity ID → Rigged mesh node parented to the avatar skeleton
    private readonly Dictionary<Guid, MeshInstance3D> _riggedAttachments = new();
    // attachment entity ID → (mesh id, per-face textures, default face) of the currently
    // loaded/loading attachment mesh. Guards against re-triggering the async mesh load on every
    // redundant PrimitiveComponent update (LibreMetaverse's ObjectUpdate fires several times per
    // object during initial rez — the same entity was observed re-requesting the same mesh 4x
    // before the first load even finished). Without this, each overlapping load adds its OWN
    // mesh instance to the skeleton; only the last is tracked for cleanup, so earlier ones leak
    // as permanent, stacked duplicates (z-fighting, doubled alpha-scissor blending, wasted GPU
    // cost). Must also compare Faces/DefaultFace, not just MeshId: a worn mesh (e.g. a mesh
    // head's applier/relay HUD) commonly re-sends the SAME mesh id with UPDATED per-face
    // textures moments after the initial attach (placeholder/UV-template skin -> the user's
    // actual applied skin) — comparing only MeshId made that legitimate refresh look identical
    // to the noisy rez-time duplicates this guard exists to swallow, permanently freezing the
    // mesh on its placeholder texture.
    private readonly Dictionary<Guid, (Guid MeshId, FaceTexture[]? Faces, FaceTexture DefaultFace)> _attachmentMeshIds = new();

    // Bump this string with every fix and check it's actually printed at the top of the log
    // before trusting anything else in it — this session got burned repeatedly by stale/
    // incrementally-rebuilt assemblies silently running old code despite a fresh-looking
    // DLL timestamp. If this line is missing or shows an old tag, the client is NOT running
    // the code you think it is; close it fully (not just the window) and re-run
    // tools/run-client.ps1 before drawing any conclusion from the rest of the log.
    private const string BuildMarker = "2026-07-03-session7d-viewer-exact-weight-decoder";

    public void Initialize(World world, AssetService assetService, GpuCache gpuCache)
    {
        GD.Print($"[AvatarRenderer] BUILD MARKER: {BuildMarker}");
        _world = world;
        _assetService = assetService;
        _gpuCache = gpuCache;

        // Load the SL Bento skeleton definition. Read via Godot's FileAccess so it works
        // both from source and from an exported .pck — System.IO + GlobalizePath cannot
        // read resources packed into the export, which silently fell back to a capsule.
        const string skeletonResPath = "res://assets/avatar/avatar_skeleton.xml";
        try
        {
            using var file = FileAccess.Open(skeletonResPath, FileAccess.ModeFlags.Read);
            if (file == null)
            {
                throw new Exception($"cannot open {skeletonResPath}: {FileAccess.GetOpenError()}");
            }

            _avatarSkeleton = AvatarSkeleton.LoadFromXml(file.GetAsText());
            GD.Print($"[AvatarRenderer] Loaded Bento skeleton: {_avatarSkeleton.Bones.Count} entries");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[AvatarRenderer] Failed to load skeleton: {ex.Message}. Falling back to capsule.");
            _avatarSkeleton = null;
        }

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
        if (e.Component is AttachmentComponent || e.Component is PrimitiveComponent)
            CallDeferred(nameof(UpdateAttachment), e.Entity.Id.ToString());
        
        if (e.Component is AvatarComponent || e.Component is TransformComponent)
            CallDeferred(nameof(UpdateVisual), e.Entity.Id.ToString());
    }

    private void CreateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.ContainsKey(entityId)) return;
        if (_world == null) return;

        var entity = _world.GetEntity(entityId);
        if (entity == null || entity.GetComponent<AvatarComponent>() == null) return;

        var avatar = entity.GetComponent<AvatarComponent>()!;
        var visual = new AvatarVisual();

        // Add the visual root to the tree first so all sub-nodes inherit the active scene tree lifecycle
        AddChild(visual.Root);

        // Neutral skin tone as placeholder — replaced by baked textures once they arrive.
        var color = new Color(0.76f, 0.60f, 0.46f);

        if (_avatarSkeleton != null)
        {
            var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
            skeleton.Name = "Skeleton3D";
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;

            // The LibreMetaverse NuGet package deploys .llm character files to the
            // assembly output directory (linden/character/*.llm). Use the assembly
            // location rather than AppContext.BaseDirectory — in the Godot editor the
            // latter resolves to the editor executable directory, not the build output.
            var asmDir = System.IO.Path.GetDirectoryName(
                typeof(AvatarBodyMeshService).Assembly.Location) ?? AppContext.BaseDirectory;
            var charDir = System.IO.Path.Combine(asmDir, "linden", "character");
            GD.Print($"[AvatarRenderer] character dir: {charDir}");
            var bodyData = AvatarBodyMeshService.Load(charDir);

            if (bodyData != null)
            {
                foreach (var part in bodyData.Parts)
                {
                    // Base mesh here (no weights yet); morphs are applied by the UpdateVisual call
                    // at the end of CreateVisual once VisualParams are present (RebuildBodyMorphs).
                    var mi = BuildSkinnedMeshInstance(part, skeleton, color, weights: null);
                    if (mi == null) continue;
                    mi.Name = part.Name + "_Mesh";
                    skeleton.AddChild(mi);
                    // Explicitly point the mesh at the skeleton. The default NodePath("..")
                    // is not reliably populated for programmatically-created MeshInstance3D,
                    // and without it Godot renders the static vertex buffer (the T-pose) and
                    // never applies bone skinning.
                    mi.Skeleton = mi.GetPathTo(skeleton);
                    visual.Parts[part.Name] = mi;
                    visual.BodyPartData[part.Name] = part;
                }
            }
            else
            {
                // Fallback: procedural box-man when character files are absent.
                BuildProceduralBoxMan(skeleton, visual, color);
            }

            skeleton.ResetBonePoses();

            // Bind animation player to the skeleton
            visual.AnimPlayer.SetSkeleton(skeleton);
        }
        else
        {
            // Fallback to capsule
            var capsule = new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.45f, Height = 1.9f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color }
            };
            visual.Root.AddChild(capsule);
            visual.Parts["root"] = capsule;
        }

        _visuals[entityId] = visual;

        UpdateVisual(entityIdStr);
    }

    private void RemoveVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.TryGetValue(entityId, out var visual))
        {
            visual.QueueFree();
            _visuals.Remove(entityId);
        }
        if (_attachmentNodes.TryGetValue(entityId, out var attachNode))
        {
            attachNode.QueueFree();
            _attachmentNodes.Remove(entityId);
        }
        if (_riggedAttachments.TryGetValue(entityId, out var riggedMesh))
        {
            riggedMesh.QueueFree();
            _riggedAttachments.Remove(entityId);
        }
        _attachmentMeshIds.Remove(entityId);
    }

    private void UpdateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_world == null) return;
        if (!_visuals.TryGetValue(entityId, out var visual))
        {
            // Might have just gained the AvatarComponent
            CreateVisual(entityIdStr);
            return;
        }

        var entity = _world.GetEntity(entityId);
        if (entity == null || entity.GetComponent<AvatarComponent>() == null) return;

        var avatar = entity.GetComponent<AvatarComponent>()!;

        // 1. Transform root position/rotation
        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            // Position the avatar root node (floating-origin relative; see RenderConfig)
            visual.Root.Position = RenderConfig.ToGodot(entity.RegionHandle, transform.Position);

            var slQuat = new Godot.Quaternion(
                transform.Rotation.X, transform.Rotation.Z,
                -transform.Rotation.Y, transform.Rotation.W);
            visual.Root.Quaternion = slQuat;
        }

        // 2. Apply Shape Morphs (Skeletal Distortions) — only when params actually changed.
        // ResetBonePoses() wipes all animation poses, so calling it every frame (via
        // frequent AvatarAnimationEvents) would keep the skeleton stuck in T-pose.
        if (visual.Skeleton != null && _avatarSkeleton != null && avatar.VisualParams != null)
        {
            bool needsApply = visual.LastAppliedVisualParams == null ||
                              !avatar.VisualParams.SequenceEqual(visual.LastAppliedVisualParams);
            
            if (needsApply)
            {
                visual.LastAppliedVisualParams = avatar.VisualParams;
                // Same "linden/character" directory the skeleton/body meshes load from (see
                // CreateVisual) — needed here too so ComputeDistortions can read avatar_lad.xml's
                // per-param sex tags (LibreMetaverse's generated VisualParam struct drops that
                // attribute entirely; see AvatarShapeService).
                var charDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(AvatarBodyMeshService).Assembly.Location) ?? AppContext.BaseDirectory,
                    "linden", "character");
                // One effective-weight map drives BOTH the skeletal distortions (bone scale/pos)
                // and the vertex morphs (body silhouette) — so they can never disagree on a slider.
                var weights = AvatarShapeService.ComputeEffectiveWeights(avatar.VisualParams, charDir);

                var distortions = AvatarShapeService.ComputeDistortions(avatar.VisualParams, charDir);
                ApplyShape(visual.Skeleton, _avatarSkeleton, distortions, visual.JointPosOverrides);
                visual.Skeleton.ResetBonePoses();

                // Deform the system body into this avatar's real proportions (male/muscle/breast/…
                // sliders are vertex morphs, not bone scales — see AvatarMorphService).
                RebuildBodyMorphs(visual, weights);
            }
        }

        // 3. Texture streaming / Bakes-on-Mesh
        if (avatar.BakedTextures != null && _assetService != null)
        {
            bool anyBakeChanged = false;
            foreach (var kv in avatar.BakedTextures)
            {
                int bakeIndex = kv.Key;
                var textureId = kv.Value;
                if (textureId == Guid.Empty) continue;
                if (!visual.LoadedTextures.TryGetValue(bakeIndex, out var currentId) || currentId != textureId)
                {
                    visual.LoadedTextures[bakeIndex] = textureId;
                    anyBakeChanged = true;
                    _ = LoadAndApplyTextureAsync(visual, bakeIndex, textureId);
                }
            }

            // Bakes-on-Mesh faces resolve to these bake ids at material-build time; a worn mesh
            // that loaded BEFORE its bake arrived rendered untextured, so re-resolve its face
            // materials now (viewer analog: setTETexture -> updateMeshTextures propagating new
            // bakes to attachment faces).
            if (anyBakeChanged && visual.BomAttachments.Count > 0)
            {
                foreach (var e in visual.BomAttachments)
                    if (IsInstanceValid(e.Mi))
                        _ = ApplyFaceMaterialsAsync(e.Mi, e.FaceIndices, e.Faces, e.DefaultFace, visual);
            }
        }

        // 4. Animation playback — detect changes in ActiveAnimations
        if (avatar.ActiveAnimations != null && _assetService != null && visual.Skeleton != null)
        {
            bool animsChanged = false;
            if (visual.LoadedAnimationIds == null || visual.LoadedAnimationIds.Count != avatar.ActiveAnimations.Count)
            {
                animsChanged = true;
            }
            else
            {
                for (int i = 0; i < avatar.ActiveAnimations.Count; i++)
                {
                    if (avatar.ActiveAnimations[i] != visual.LoadedAnimationIds[i])
                    {
                        animsChanged = true;
                        break;
                    }
                }
            }

            if (animsChanged)
            {
                visual.LoadedAnimationIds = new List<Guid>(avatar.ActiveAnimations);
                var animIds = new List<Guid>(avatar.ActiveAnimations);
                _ = LoadAndStartAnimationsAsync(visual, animIds);
            }
        }
    }

    private void ApplyShape(Skeleton3D skeleton, AvatarSkeleton avatarSkeleton,
        Dictionary<string, (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Position)> distortions,
        Dictionary<string, System.Numerics.Vector3>? posOverrides = null)
    {
        for (int idx = 0; idx < skeleton.GetBoneCount(); idx++)
        {
            string name = skeleton.GetBoneName(idx);
            var bone = avatarSkeleton.GetBone(name);
            if (bone == null) continue;

            var slPos = bone.Position;
            var slScale = bone.Scale;

            if (distortions.TryGetValue(name, out var dist))
            {
                slScale += dist.Scale;
                slPos += dist.Position;
            }

            // Joint-position override from a worn rigged mesh wins over base + shape
            // distortion (viewer: LLJoint::updatePos — active override replaces the local
            // position outright; rotation and scale are untouched).
            if (posOverrides != null && posOverrides.TryGetValue(name, out var ov))
                slPos = ov;

            var godotPos = new Godot.Vector3(slPos.X, slPos.Z, -slPos.Y);

            // Same SL→Godot rotation-order conversion as SkeletonBuilder.Build — see
            // SlEulerDegToGodotBasis's doc comment for the full derivation. Must stay in sync;
            // this rebuilds EVERY bone's rest on each shape update, so any divergence between
            // the two would silently undo one or the other.
            var basis = SkeletonBuilder.SlEulerDegToGodotBasis(bone.Rotation);
            basis = basis.Scaled(new Godot.Vector3(slScale.X, slScale.Z, slScale.Y));

            var rest = new Transform3D(basis, godotPos);
            skeleton.SetBoneRest(idx, rest);
        }
    }

    private async System.Threading.Tasks.Task LoadAndApplyTextureAsync(AvatarVisual visual, int bakeIndex, Guid textureId)
    {
        if (_assetService == null) return;

        // Try GPU Cache first
        ImageTexture? godotTexture = null;
        if (_gpuCache != null)
        {
            godotTexture = _gpuCache.Get(textureId) as ImageTexture;
        }

        if (godotTexture == null)
        {
            GD.Print($"[AvatarRenderer] Fetching bake {bakeIndex} (ID: {textureId}) from AssetService...");
            var textureData = await _assetService.GetTextureAsync(textureId);
            if (textureData == null)
            {
                GD.Print($"[AvatarRenderer] FAILED to fetch/decode bake {bakeIndex} (ID: {textureId})!");
                return;
            }
            GD.Print($"[AvatarRenderer] Successfully fetched bake {bakeIndex} (ID: {textureId}), creating Godot image...");

            var tcs = new System.Threading.Tasks.TaskCompletionSource<ImageTexture?>();
            
            Godot.Callable.From(() => {
                var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
                if (image == null)
                {
                    GD.Print($"[AvatarRenderer] Image.CreateFromData FAILED for bake {bakeIndex} (ID: {textureId})!");
                    tcs.SetResult(null);
                    return;
                }
                var tex = ImageTexture.CreateFromImage(image);
                
                if (tex != null && _gpuCache != null)
                {
                    long size = textureData.Width * textureData.Height * 4;
                    _gpuCache.Put(textureId, tex, size);
                }
                tcs.SetResult(tex);
            }).CallDeferred();

            godotTexture = await tcs.Task;
        }

        if (godotTexture == null)
        {
            GD.Print($"[AvatarRenderer] Final godotTexture was null for bake {bakeIndex} (ID: {textureId})!");
            return;
        }

        // Map SL bake indices (AvatarTextureIndex) to which mesh parts they cover.
        // 8=HeadBaked, 9=UpperBaked, 10=LowerBaked, 11=EyesBaked, 19=SkirtBaked, 20=HairBaked —
        // per LibreMetaverse's AvatarTextureIndex enum (12/13 are LowerSocks/UpperJacket wearable
        // textures, NOT bakes; mapping them here previously sent SkirtBaked/HairBaked into the void).
        // Part names match AvatarBodyPartMesh.Name (after stripping "avatar_" prefix in AvatarBodyMeshService).
        var partsForBake = bakeIndex switch
        {
            8  => new[] { "head", "eyelashes" },
            9  => new[] { "upper_body" },
            10 => new[] { "lower_body" },
            11 => new[] { "eye" },
            20 => new[] { "hair" },
            _ => Array.Empty<string>()   // 19 (SkirtBaked): no skirt part is loaded
        };

        Godot.Callable.From(() => {
            if (visual.Root == null || !IsInstanceValid(visual.Root)) return;

            var targets = partsForBake != null
                ? partsForBake.Select(n => visual.Parts.TryGetValue(n, out var m) ? m : null)
                              .Where(m => m != null)
                              .Cast<MeshInstance3D>()
                : visual.Parts.Values.Cast<MeshInstance3D>();

            var targetNames = string.Join(", ", targets.Select(m => m.Name));
            GD.Print($"[AvatarRenderer] Applying bake {bakeIndex} (ID: {textureId}) to meshes: {targetNames}");

            foreach (var meshInstance in targets)
            {
                if (!IsInstanceValid(meshInstance)) continue;
                var mat = meshInstance.MaterialOverride as StandardMaterial3D;
                if (mat == null)
                {
                    mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                    meshInstance.MaterialOverride = mat;
                }
                mat.AlbedoTexture = godotTexture;
                mat.AlbedoColor = Godot.Colors.White; // Reset placeholder tint!
                
                // SL avatars use baked alpha to hide the system body when wearing mesh bodies.
                // Always enable AlphaScissor because DetectAlpha can be unreliable or slow,
                // and transparent pixels in SL textures are often black RGB.
                mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                mat.AlphaScissorThreshold = 0.5f;
            }
        }).CallDeferred();
    }

    private void UpdateAttachment(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_world == null) return;

        var entity = _world.GetEntity(entityId);
        if (entity == null) return;

        var attachment = entity.GetComponent<AttachmentComponent>();
        if (attachment == null) return;

        var prim = entity.GetComponent<PrimitiveComponent>();
        var transform = entity.GetComponent<TransformComponent>();

        // The attachment-point bone only matters for STATIC attachments. A rigged mesh
        // (mesh body, mesh clothing) carries its own skin weights and ignores the point — so
        // never drop a mesh attachment just because its point is a HUD/unmapped slot.
        var boneName = AttachmentPointMap.GetBoneName(attachment.AttachmentPoint);
        bool isMeshAttachment = prim is { IsMesh: true } && prim.MeshId != Guid.Empty;
        if (boneName == null && !isMeshAttachment) return;
        boneName ??= "mPelvis";

        GD.Print($"[Attachment] pt {attachment.AttachmentPoint} bone {boneName} " +
                 $"mesh {(isMeshAttachment ? prim!.MeshId.ToString() : "no")} entity {entityId:N}");

        // Avatar must already be rendered.
        if (!_visuals.TryGetValue(attachment.AvatarEntityId, out var avatarVisual)) return;
        if (avatarVisual.Skeleton == null) return;

        var defaultFace = isMeshAttachment
            ? new FaceTexture(prim!.TextureId, prim.RenderMaterialId, prim.ColorTint, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f)
            : default;

        // Skip a redundant reload: LibreMetaverse's ObjectUpdate can fire several times for the
        // same object during initial rez (observed 4x for one entity before the first async
        // mesh load even finished). Without this guard each overlapping call tears down +
        // re-kicks the async load; the "clear previous" step below runs synchronously before
        // any of those loads resolve, so it never sees the others' results — every completed
        // load adds its own mesh instance to the skeleton and only the LAST is tracked for
        // cleanup, leaking the rest as permanent stacked duplicates (z-fighting, doubled
        // alpha-scissor blending, wasted GPU cost). Must compare Faces/DefaultFace too (see
        // _attachmentMeshIds doc) — same mesh id, changed face textures, is a real update
        // (an applier HUD swapping the placeholder skin for the real one), not a dupe.
        if (isMeshAttachment && _attachmentMeshIds.TryGetValue(entityId, out var loaded)
            && loaded.MeshId == prim!.MeshId
            && loaded.DefaultFace == defaultFace
            && (loaded.Faces == prim.Faces || (loaded.Faces != null && prim.Faces != null && loaded.Faces.SequenceEqual(prim.Faces))))
            return;

        // Re-use existing node or create a new BoneAttachment3D on the avatar skeleton.
        if (!_attachmentNodes.TryGetValue(entityId, out var boneAttach))
        {
            boneAttach = new BoneAttachment3D { Name = $"WornItem_{entityId:N}" };
            int boneIdx = avatarVisual.Skeleton.FindBone(boneName);
            if (boneIdx >= 0) boneAttach.BoneIdx = boneIdx;
            boneAttach.BoneName = boneName;
            avatarVisual.Skeleton.AddChild(boneAttach);
            _attachmentNodes[entityId] = boneAttach;
        }

        // Clear previous static attachment visuals
        foreach (var child in boneAttach.GetChildren())
        {
            child.QueueFree();
        }
        
        // Clear previous rigged attachment visuals
        if (_riggedAttachments.TryGetValue(entityId, out var oldRigged))
        {
            oldRigged.QueueFree();
            _riggedAttachments.Remove(entityId);
        }

        // Kick off mesh/texture load for mesh attachments.
        if (prim != null)
        {
            if (prim.IsMesh && prim.MeshId != Guid.Empty)
            {
                // Set BEFORE kicking off the async load (not after it completes) so a second
                // redundant UpdateAttachment arriving while this load is still in flight sees
                // it immediately and hits the early-return guard above instead of starting an
                // overlapping duplicate load.
                _attachmentMeshIds[entityId] = (prim.MeshId, prim.Faces, defaultFace);
                GD.Print($"[Attachment] REQUESTING MESH {prim.MeshId} for entity {entityId}");
                _ = LoadAndApplyAttachmentMeshAsync(boneAttach, avatarVisual, prim.MeshId,
                    prim.Faces, defaultFace,
                    new System.Numerics.Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z),
                    transform != null ? transform.Position : System.Numerics.Vector3.Zero,
                    transform != null ? transform.Rotation : System.Numerics.Quaternion.Identity,
                    entityId);
            }
            else
            {
                // Not a mesh (or reverted to a plain prim) — drop any stale mesh-id tracking so
                // a later switch back to a mesh isn't blocked by a stale match.
                _attachmentMeshIds.Remove(entityId);
                // Prim attachment: show a scaled box placeholder.
                var color = new Color(prim.ColorTint.X, prim.ColorTint.Y, prim.ColorTint.Z, prim.ColorTint.W);
                var box = new MeshInstance3D
                {
                    Name = "AttachBox",
                    Mesh = new BoxMesh { Size = new Godot.Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y) },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
                    Position = transform != null ? new Godot.Vector3(transform.Position.X, transform.Position.Z, -transform.Position.Y) : Godot.Vector3.Zero,
                    Quaternion = transform != null ? new Godot.Quaternion(transform.Rotation.X, transform.Rotation.Z, -transform.Rotation.Y, transform.Rotation.W) : Godot.Quaternion.Identity
                };
                boneAttach.AddChild(box);
            }
        }
    }

    private async System.Threading.Tasks.Task LoadAndApplyAttachmentMeshAsync(
        BoneAttachment3D boneAttach, AvatarVisual avatarVisual, Guid meshId,
        FaceTexture[]? faces, FaceTexture defaultFace, System.Numerics.Vector3 slScale,
        System.Numerics.Vector3 slPos, System.Numerics.Quaternion slRot, Guid entityId)
    {
        if (_assetService == null) return;
        var skeleton = avatarVisual.Skeleton;

        var meshData = await _assetService.GetMeshAsync(meshId).ConfigureAwait(false);
        if (meshData == null)
        {
            GD.PrintErr($"[Attachment] mesh {meshId} failed to fetch/decode — skipped");
            return;
        }
        GD.Print($"[Attachment] mesh {meshId}: {meshData.Submeshes.Count} submeshes, rigged={meshData.Skin != null}");

        // Rigged / fitted mesh (worn mesh bodies and clothing) carries skin data: skin it to
        // the avatar skeleton so it deforms and animates with the body, instead of bolting it
        // statically to one bone (which collapses it into a blob).
        if (meshData.Skin != null && skeleton != null)
        {
            Godot.Callable.From(() =>
            {
                if (!IsInstanceValid(skeleton)) return;
                // The mesh may be rigged to shifted joint positions (mesh bodies/heads).
                // Apply its joint-position overrides to the skeleton BEFORE binding, like the
                // viewer does, so invBind·jointWorld cancels at the intended pose.
                ApplyJointPositionOverrides(avatarVisual, skeleton, meshData.Skin, meshId);
                var mi = BuildRiggedMeshInstance(meshData, skeleton, meshId, out var faceIndices);
                if (mi == null) return;
                mi.Name = "RiggedMesh";
                
                // Set a generous CustomAabb to prevent Godot from culling the mesh if the bind pose is far away
                mi.CustomAabb = new Aabb(new Godot.Vector3(-4, -4, -4), new Godot.Vector3(8, 8, 8));
                
                skeleton.AddChild(mi);
                _riggedAttachments[entityId] = mi;

                // Skin is already assigned on the instance; the skeleton path must be set after
                // the node is in the tree so Godot can resolve and drive the skinning.
                mi.Skeleton = mi.GetPathTo(skeleton);
                RegisterBomAndUpdateVisibility(avatarVisual, mi, faceIndices, faces, defaultFace);
                _ = ApplyFaceMaterialsAsync(mi, faceIndices, faces, defaultFace, avatarVisual);
            }).CallDeferred();
            return;
        }

        Godot.Callable.From(() =>
        {
            if (!IsInstanceValid(boneAttach)) return;

            var arrayMesh = new ArrayMesh();
            var faceIndices = new List<int>();
            foreach (var sub in meshData.Submeshes)
            {
                if (sub.Indices.Length == 0) continue;
                var st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                foreach (int idx in sub.Indices)
                {
                    var p = sub.Positions[idx];
                    var n = sub.Normals[idx];
                    var uv = sub.UVs[idx];
                    st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
                    // SL→Godot V-flip, same as the body parts and rigged meshes.
                    st.SetUV(new Godot.Vector2(uv.X, 1.0f - uv.Y));
                    st.AddVertex(new Godot.Vector3(p.X * slScale.X, p.Z * slScale.Z, -p.Y * slScale.Y));
                }
                st.GenerateTangents();
                st.Commit(arrayMesh);
                faceIndices.Add(sub.FaceIndex);
            }

            var mi = new MeshInstance3D { Name = "AttachMesh", Mesh = arrayMesh };
            mi.Position = new Godot.Vector3(slPos.X, slPos.Z, -slPos.Y);
            mi.Quaternion = new Godot.Quaternion(slRot.X, slRot.Z, -slRot.Y, slRot.W);
            boneAttach.AddChild(mi);
            RegisterBomAndUpdateVisibility(avatarVisual, mi, faceIndices.ToArray(), faces, defaultFace);
            _ = ApplyFaceMaterialsAsync(mi, faceIndices.ToArray(), faces, defaultFace, avatarVisual);
        }).CallDeferred();
    }

    /// <summary>Applies one material per mesh surface, picking each surface's SL face texture
    /// (texture id + colour tint) from <paramref name="faces"/> by its face index. Worn items
    /// texture each face independently — many parts are flat colour tints with no texture.
    /// <paramref name="avatarVisual"/> (when the mesh is worn on an avatar) resolves
    /// Bakes-on-Mesh faces to that avatar's server-baked textures.</summary>
    private async System.Threading.Tasks.Task ApplyFaceMaterialsAsync(
        MeshInstance3D mi, int[] faceIndices, FaceTexture[]? faces, FaceTexture defaultFace,
        AvatarVisual? avatarVisual = null)
    {
        if (mi.Mesh is not ArrayMesh am) return;
        int surfaceCount = am.GetSurfaceCount();

        for (int surf = 0; surf < surfaceCount; surf++)
        {
            int faceIndex = surf < faceIndices.Length ? faceIndices[surf] : 0;
            FaceTexture ft = (faces != null && faceIndex >= 0 && faceIndex < faces.Length)
                ? faces[faceIndex] : defaultFace;

            var material = await BuildFaceMaterialAsync(ft, avatarVisual).ConfigureAwait(false);
            int s = surf;
            Godot.Callable.From(() =>
            {
                if (IsInstanceValid(mi) && s < ((ArrayMesh)mi.Mesh).GetSurfaceCount())
                    mi.SetSurfaceOverrideMaterial(s, material);
            }).CallDeferred();
        }
    }

    /// <summary>Builds a material for one SL face: optional albedo texture modulated by the
    /// face colour tint, with alpha-cutout when the texture has alpha. Texture decode runs off
    /// the main thread; only the GPU upload is marshalled back.</summary>
    private async System.Threading.Tasks.Task<StandardMaterial3D> BuildFaceMaterialAsync(
        FaceTexture ft, AvatarVisual? avatarVisual = null)
    {
        var tint = ft.Color == default
            ? new Color(1, 1, 1, 1)
            : new Color(ft.Color.X, ft.Color.Y, ft.Color.Z, ft.Color.W);

        var material = new StandardMaterial3D
        {
            AlbedoColor = tint,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        };

        // Bakes-on-Mesh: a face carrying one of the IMG_USE_BAKED_* magic ids wants the AVATAR's
        // server-baked texture for that channel, not the magic id itself (which is just a red UV
        // placeholder asset). Viewer: LLViewerObject::getBakedTextureForMagicId. If the bake
        // hasn't arrived yet, leave the face untextured — the bake-arrival hook in UpdateVisual
        // re-runs ApplyFaceMaterialsAsync for registered BoM meshes.
        Guid texId = ft.TextureId;
        if (avatarVisual != null && SLNG.Assets.BakedTextureIds.TryGetBakeIndex(texId, out int bakeIdx))
            texId = avatarVisual.LoadedTextures.TryGetValue(bakeIdx, out var bakeTexId) ? bakeTexId : Guid.Empty;

        if (texId == Guid.Empty || _assetService == null)
            return material;

        var tcs = new System.Threading.Tasks.TaskCompletionSource<ImageTexture?>();
        ImageTexture? cached = _gpuCache?.Get(texId) as ImageTexture;
        if (cached != null) { material.AlbedoTexture = cached; return material; }

        var textureData = await _assetService.GetTextureAsync(texId).ConfigureAwait(false);
        if (textureData == null) return material;

        Godot.Callable.From(() =>
        {
            var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
            image.GenerateMipmaps();
            var tex = ImageTexture.CreateFromImage(image);
            if (tex != null)
            {
                _gpuCache?.Put(texId, tex, (long)textureData.Width * textureData.Height * 4);
                if (image.DetectAlpha() != Image.AlphaMode.None)
                {
                    material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                    material.AlphaScissorThreshold = 0.5f;
                }
            }
            tcs.SetResult(tex);
        }).CallDeferred();

        var built = await tcs.Task.ConfigureAwait(false);
        if (built != null) material.AlbedoTexture = built;
        return material;
    }

    /// <summary>Registers a worn mesh's Bakes-on-Mesh usage on its avatar and hides the system
    /// body parts whose bake channel the mesh consumes — the viewer's
    /// LLVOAvatar::updateMeshVisibility. Main thread only (mutates node visibility).</summary>
    private void RegisterBomAndUpdateVisibility(
        AvatarVisual avatarVisual, MeshInstance3D mi, int[] faceIndices, FaceTexture[]? faces, FaceTexture defaultFace)
    {
        bool usesBom = false;
        void Scan(FaceTexture f)
        {
            if (SLNG.Assets.BakedTextureIds.TryGetBakeIndex(f.TextureId, out int b))
            {
                avatarVisual.AttachmentBakeChannels.Add(b);
                usesBom = true;
            }
        }
        if (faces != null) foreach (var f in faces) Scan(f);
        Scan(defaultFace);

        if (usesBom)
            avatarVisual.BomAttachments.Add((mi, faceIndices, faces, defaultFace));

        // Hide base parts per consumed channel (head bake also covers the eyelashes part, exactly
        // like MESH_ID_EYELASH in the viewer's updateMeshVisibility).
        var ch = avatarVisual.AttachmentBakeChannels;
        void SetPartVisible(string part, bool visible)
        {
            if (avatarVisual.Parts.TryGetValue(part, out var pmi) && IsInstanceValid(pmi))
                pmi.Visible = visible;
        }
        SetPartVisible("head", !ch.Contains(8));
        SetPartVisible("eyelashes", !ch.Contains(8));
        SetPartVisible("upper_body", !ch.Contains(9));
        SetPartVisible("lower_body", !ch.Contains(10));
        SetPartVisible("eye", !ch.Contains(11));
        SetPartVisible("hair", !ch.Contains(20));
    }

    /// <summary>
    /// Builds a skinned <see cref="MeshInstance3D"/> for a rigged/fitted mesh, bound to the
    /// avatar <paramref name="skeleton"/>. Returns null if no joints resolve. The instance must
    /// be added as a DIRECT child of the skeleton; its <see cref="Skin"/> is stashed in a node
    /// meta ("skin") so the caller can assign it after AddChild. Main thread only.
    /// </summary>
    /// <summary>
    /// Applies the joint-position overrides a rigged mesh carries (its "alternate inverse
    /// bind matrices") to the avatar skeleton, mirroring
    /// <c>LLVOAvatar::addAttachmentOverridesForObject</c>: the TRANSLATION of alt[j] is the
    /// overridden LOCAL position of joint j relative to its parent, in SL space. Rotation and
    /// scale of the alt matrix are ignored (the viewer ignores them too). Without this, a mesh
    /// body/head rigged to shifted joints renders as an unrecognisable tangle because
    /// invBind·jointWorld no longer cancels. Main thread only.
    /// </summary>
    private void ApplyJointPositionOverrides(AvatarVisual visual, Skeleton3D skeleton, SLNG.Assets.MeshSkin skinData, Guid meshId)
    {
        var alt = skinData.AltInverseBindMatrices;
        int jointCount = skinData.JointNames.Length;
        // Viewer rule: overrides only count when EVERY joint has one (bindCnt == jointCnt).
        if (alt == null || alt.Length != jointCount) return;

        int applied = 0;
        float maxDelta = 0f;
        for (int j = 0; j < jointCount; j++)
        {
            var m = alt[j];
            if (System.Math.Abs(m.M44 - 1f) > 0.01f) continue; // uninitialised slot in the asset

            string boneName = skinData.JointNames[j];
            if (_avatarSkeleton != null) boneName = _avatarSkeleton.ResolveBoneName(boneName);
            int bone = skeleton.FindBone(boneName);
            if (bone < 0) continue;

            var slPos = new System.Numerics.Vector3(m.M41, m.M42, m.M43);

            // Viewer threshold: skip overrides within 0.1 mm of the default local position.
            var basePos = _avatarSkeleton?.GetBone(boneName)?.Position ?? System.Numerics.Vector3.Zero;
            float delta = (slPos - basePos).Length();
            if (delta <= 0.0001f) continue;

            visual.JointPosOverrides[boneName] = slPos;
            var rest = skeleton.GetBoneRest(bone);
            rest.Origin = new Godot.Vector3(slPos.X, slPos.Z, -slPos.Y);
            skeleton.SetBoneRest(bone, rest);
            applied++;
            if (delta > maxDelta) maxDelta = delta;
        }

        if (applied > 0)
        {
            skeleton.ResetBonePoses();
            GD.Print($"[JointOverride] mesh {meshId}: {applied}/{jointCount} joint positions overridden (max shift {maxDelta:0.###} m)");
        }
        if (System.Math.Abs(skinData.PelvisOffset) > 0.0001f)
            GD.Print($"[JointOverride] mesh {meshId}: pelvis offset {skinData.PelvisOffset:0.###} m (not yet applied)");
    }

    private MeshInstance3D? BuildRiggedMeshInstance(MeshData meshData, Skeleton3D skeleton, Guid meshId, out int[] faceIndices)
    {
        faceIndices = System.Array.Empty<int>();
        var skinData = meshData.Skin!;
        int jointCount = skinData.JointNames.Length;

        // Bind each joint using the mesh's OWN inverse-bind matrix (model→bone), converted
        // from SL to Godot space. This is the correct, general rig: the garment was authored
        // against its own bind pose, which need not equal our skeleton's rest. Binding to the
        // skeleton rest instead (as the body parts do) only works for meshes whose bind pose
        // matches exactly — these OpenSim meshes don't, and exploded into petals.
        var skin = new Skin();
        var slotForJoint = new int[jointCount];

        var bindShape = skinData.BindShapeMatrix;

        // Normals need the inverse-transpose of the bind-shape's linear part, not the raw
        // matrix — verified against the viewer source (llface.cpp, getGeometryVolume):
        // positions are pre-multiplied by BindShapeMatrix directly, but normals/tangents by
        // `transpose(inverse(BindShapeMatrix))`. Using the raw matrix (as System.Numerics'
        // Vector3.TransformNormal does by default) only matches for uniform scale; several of
        // our real assets have highly non-uniform bind-shape scale (observed up to ~4:1 on one
        // axis vs another), which would skew normals — and therefore lighting — noticeably off
        // true. Computed once per mesh, not per vertex, since bindShape doesn't vary by vertex.
        var bindShapeNormalMatrix = bindShape;
        if (System.Numerics.Matrix4x4.Invert(bindShape, out var bindShapeInv))
            bindShapeNormalMatrix = System.Numerics.Matrix4x4.Transpose(bindShapeInv);

        // Resolve every joint's bone index up front.
        var jointBone = new int[jointCount];
        for (int j = 0; j < jointCount; j++)
        {
            string boneName = skinData.JointNames[j];
            if (_avatarSkeleton != null)
                boneName = _avatarSkeleton.ResolveBoneName(boneName);
            jointBone[j] = skeleton.FindBone(boneName);
        }

        // A rigged mesh FOLLOWS avatar-shape distortion — this is a deliberate SL feature (Bento
        // mesh heads/bodies respond to shape sliders like Head Size). Verified against the real
        // viewer: LLSkinningUtil::initSkinningMatrixPalette (indra/newview/llskinningutil.cpp)
        // computes the skinning matrix for EVERY joint as `mat[j] = invBind[j] * joint->
        // getWorldMatrix4a()`, where getWorldMatrix4a() carries the joint's CURRENT scale/position
        // including skeletal-distortion (shape). There is NO step anywhere in the skinning path
        // that removes shape from the joint matrix. Godot's own skinning does the equivalent —
        // skinMatrix = boneGlobalPose(shaped) * bindPose — so binding with the asset's own invBind
        // (below) and letting Godot compose the shaped pose already matches the viewer exactly.
        //
        // A previous "jointCorrection" pass here pre-multiplied liveGlobal⁻¹*neutralGlobal into
        // each bind to CANCEL shape distortion for attachments. That was the opposite of the
        // viewer and, once driven shape params (Head Size etc.) finally reached the head bones,
        // made worn mesh heads NOT shrink with shape while the system head DID — so the system
        // head poked out from under the mesh head. It was originally added to paper over the
        // (since-fixed) height-distortion bug that produced bogus huge face-bone offsets; with that
        // gone, the correct behavior is simply to let the mesh follow shape, as the viewer does.

        for (int j = 0; j < jointCount; j++)
        {
            int bone = jointBone[j];
            if (bone < 0) { slotForJoint[j] = -1; continue; }

            var ibm = skinData.InverseBindMatrices[j];
            // Bind_godot = C⁻¹ · InverseBind_SL · C   (verified change-of-basis). bind-shape is
            // applied to the vertices below, NOT folded in here — matching the SL viewer
            // (LLSkinningUtil: palette = invBind·jointWorld; bind-shape pre-applied to verts).
            // AffineInverse (not Inverse): Inverse() assumes an orthonormal basis and only
            // transposes it, which is wrong once non-uniform scale is baked in (bone.Scale from
            // the XML, or shape distortion) — AffineInverse does a real matrix invert.
            Transform3D bind = ibm == System.Numerics.Matrix4x4.Identity
                ? ComputeGlobalRestTransform(skeleton, bone).AffineInverse()
                : RowMatrixToTransform(SlToGodotInv * ibm * SlToGodot);

            slotForJoint[j] = skin.GetBindCount();
            skin.AddBind(bone, bind);
        }
        if (skin.GetBindCount() == 0) return null;

        var arrayMesh = new ArrayMesh();
        var faceList = new List<int>();

        // Track the bind-pose extent (positions AFTER the bind-shape matrix) for the sanity
        // guard below. The RAW vertex AABB is meaningless for that: "giant rig" uploads store
        // vertices in a huge position domain (±50 m) that the tiny bind-shape scale cancels.
        var bpMin = new System.Numerics.Vector3(float.MaxValue);
        var bpMax = new System.Numerics.Vector3(float.MinValue);

        // Diagnostic: count vertices whose weights don't resolve to any bound joint (all 4
        // influences reference a joint outside this mesh's own joint list, or the referenced
        // joint failed to resolve in OUR skeleton). Those fall back to "pin to skin slot 0" —
        // an ARBITRARY joint (whichever happened to be first in this mesh's own joint list),
        // potentially anatomically distant from the vertex's real position. A mesh that's a mix
        // of correctly-weighted and orphaned vertices stretches between the two — a classic
        // "candy-wrapper" skinning artifact that looks exactly like an elongated snout/spike.
        int totalVerts = 0, orphanedVerts = 0;

        // Diagnostic: which bone this mesh is mostly weighted to, and how much of its total
        // vertex weight lands there. Points straight at a shape/scale bug on a specific bone
        // (e.g. an unexpectedly huge mHead scale) without having to guess from bind-pose extent
        // alone, which is meaningless pre-skinning (see the no-rejection comment below).
        var slotWeightSum = new float[skin.GetBindCount()];

        foreach (var sub in meshData.Submeshes)
        {
            if (sub.Indices.Length == 0 || sub.Weights == null) continue;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            foreach (int idx in sub.Indices)
            {
                // Mesh-local → bind pose (SL coords) via the bind-shape matrix, then SL→Godot.
                var pSL = System.Numerics.Vector3.Transform(sub.Positions[idx], bindShape);
                var nSL = System.Numerics.Vector3.TransformNormal(sub.Normals[idx], bindShapeNormalMatrix);
                if (nSL.LengthSquared() > 1e-8f) nSL = System.Numerics.Vector3.Normalize(nSL);
                var uv = sub.UVs[idx];
                var w = sub.Weights[idx];

                bpMin = System.Numerics.Vector3.Min(bpMin, pSL);
                bpMax = System.Numerics.Vector3.Max(bpMax, pSL);

                var bones = new int[4];
                var wts = new float[4];
                int c = 0; float sum = 0f;
                AddInfluence(w.Joint0, w.Weight0, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                AddInfluence(w.Joint1, w.Weight1, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                AddInfluence(w.Joint2, w.Weight2, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                AddInfluence(w.Joint3, w.Weight3, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                totalVerts++;
                if (sum > 1e-5f) { for (int k = 0; k < 4; k++) wts[k] /= sum; }
                else { bones[0] = 0; wts[0] = 1f; orphanedVerts++; } // orphaned vertex — pin to first bound bone
                for (int k = 0; k < 4; k++) if (wts[k] > 0f) slotWeightSum[bones[k]] += wts[k];

                st.SetBones(bones);
                st.SetWeights(wts);
                st.SetNormal(new Godot.Vector3(nSL.X, nSL.Z, -nSL.Y));
                // Same SL→Godot V-flip as the system body parts (see BuildPartResources):
                // SL UVs are authored bottom-left origin; Godot samples top-left.
                st.SetUV(new Godot.Vector2(uv.X, 1.0f - uv.Y));
                st.AddVertex(new Godot.Vector3(pSL.X, pSL.Z, -pSL.Y));
            }

            st.GenerateTangents();
            st.Commit(arrayMesh);
            faceList.Add(sub.FaceIndex);
        }

        if (arrayMesh.GetSurfaceCount() == 0) return null;

        // Diagnostic only — deliberately NO size/distance rejection here. The static bind-pose
        // AABB says nothing about where a skinned mesh RENDERS: uploads may park the bind pose
        // hundreds of metres away with a huge bind-shape scale that the inverse-bind matrices'
        // 3×3 (tiny scale + axis permutation) cancels exactly during skinning. Verified
        // numerically against the viewer chain (v·BSM)·Σw(invBind·jointWorld): such a mesh
        // lands correctly on the body — an earlier guard here hid a perfectly valid face
        // attachment. Culling is handled by the generous CustomAabb on the instance instead.
        var bpSize = bpMax - bpMin;
        var bpCenter = (bpMin + bpMax) * 0.5f;
        int resolved = 0;
        for (int j = 0; j < jointCount; j++) if (slotForJoint[j] >= 0) resolved++;
        int topSlot = 0;
        for (int s = 1; s < slotWeightSum.Length; s++) if (slotWeightSum[s] > slotWeightSum[topSlot]) topSlot = s;
        string topBoneName = slotWeightSum.Length > 0 ? skeleton.GetBoneName(skin.GetBindBone(topSlot)) : "?";
        float topShare = totalVerts > 0 && slotWeightSum.Length > 0 ? slotWeightSum[topSlot] / totalVerts : 0f;
        GD.Print($"[RiggedMesh] mesh {meshId} joints {resolved}/{jointCount} resolved, binds {skin.GetBindCount()}, " +
                 $"bind-pose size ({bpSize.X:0.##}, {bpSize.Y:0.##}, {bpSize.Z:0.##}) at ({bpCenter.X:0.#}, {bpCenter.Y:0.#}, {bpCenter.Z:0.#}), " +
                 $"dominant joint \"{topBoneName}\" ({topShare:P0}), orphaned verts {orphanedVerts}/{totalVerts}" +
                 (orphanedVerts > 0 ? $" [PINNED TO SKIN SLOT 0 = bone \"{skeleton.GetBoneName(skin.GetBindBone(0))}\"]" : ""));

        faceIndices = faceList.ToArray();
        return new MeshInstance3D
        {
            Mesh = arrayMesh,
            Skin = skin,
            Skeleton = new NodePath("../.."),
            CustomAabb = new Aabb(new Godot.Vector3(-4, -4, -4), new Godot.Vector3(8, 8, 8))
        };
    }

    private static void AddInfluence(int joint, float weight, int[] slotForJoint, int jointCount,
        int[] bones, float[] wts, ref int count, ref float sum)
    {
        if (weight <= 0f || joint < 0 || joint >= jointCount || count >= 4) return;
        int slot = slotForJoint[joint];
        if (slot < 0) return;
        bones[count] = slot;
        wts[count] = weight;
        sum += weight;
        count++;
    }

    // -------------------------------------------------------------------------
    // Skinned-mesh helpers
    // -------------------------------------------------------------------------

    // The base body geometry and its skin binds are identical for every avatar: the same
    // .llm files and the same skeleton rest pose. Build the (mesh, skin) once per body part
    // and share the resources across all avatars. Without this, every avatar that appears on
    // a busy region re-parses the meshes and rebuilds the skin on the main thread — which is
    // what froze the client on OSGrid. Accessed only from the main thread (CallDeferred).
    // The Skin (bone binds) is shape-independent — it depends only on the neutral skeleton rest,
    // which is identical for every avatar — so cache it (plus its boneName→slot map) per part name
    // and share it. The MESH geometry, by contrast, bakes in the avatar's vertex morphs (male,
    // muscle, breast, … sliders) and so is rebuilt per avatar / on every shape change.
    private static readonly Dictionary<string, (Skin Skin, Dictionary<string, int> Slots)> _partSkinCache = new();

    /// <summary>
    /// Returns a <see cref="MeshInstance3D"/> for one SL body-part mesh, with correct per-vertex
    /// bone indices and weights, and the avatar's vertex morphs applied when
    /// <paramref name="weights"/> is supplied (null = base mesh, e.g. before appearance arrives).
    /// The instance must be added as a DIRECT child of the <see cref="Skeleton3D"/> for Godot's
    /// built-in skinning to take effect.
    /// </summary>
    private MeshInstance3D? BuildSkinnedMeshInstance(
        AvatarBodyPartMesh part, Skeleton3D skeleton, Color baseColor, IReadOnlyDictionary<int, float>? weights)
    {
        if (part.Indices.Length == 0) return null;

        var (skin, slots) = GetPartSkin(part, skeleton);

        // Vertex morphs (LLPolyMorphTarget) deform the base mesh into this avatar's real shape.
        // With no weights yet, render the neutral base mesh.
        var (positions, normals) = weights != null
            ? AvatarMorphService.Apply(part, weights)
            : (part.Positions, part.Normals);

        return new MeshInstance3D
        {
            Mesh             = BuildPartMesh(part, positions, normals, slots),
            Skin             = skin,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = baseColor,
                CullMode    = BaseMaterial3D.CullModeEnum.Disabled
            }
        };
    }

    /// <summary>Builds (once, then cached) the shared skin resource + boneName→slot map for one
    /// body part. Shape-independent: safe to share across avatars and shape changes.</summary>
    private static (Skin Skin, Dictionary<string, int> Slots) GetPartSkin(AvatarBodyPartMesh part, Skeleton3D skeleton)
    {
        if (_partSkinCache.TryGetValue(part.Name, out var cached)) return cached;

        var skin = new Skin();
        var slots = new Dictionary<string, int>(); // boneName → slot index in Skin
        for (int vi = 0; vi < part.Positions.Length; vi++)
        {
            AddSkinSlot(part.Bone1Names[vi], skin, skeleton, slots);
            AddSkinSlot(part.Bone2Names[vi], skin, skeleton, slots);
        }

        var result = (skin, slots);
        _partSkinCache[part.Name] = result;
        return result;
    }

    /// <summary>Builds the (per-avatar) ArrayMesh for one body part from already-morphed
    /// <paramref name="positions"/>/<paramref name="normals"/> (SL space, indexed by the part's
    /// base vertex ids).</summary>
    private static ArrayMesh BuildPartMesh(
        AvatarBodyPartMesh part, System.Numerics.Vector3[] positions, System.Numerics.Vector3[] normals,
        Dictionary<string, int> skinSlots)
    {
        // Non-indexed surface: expand each face into 3 unique vertex entries so
        // GenerateTangents() works correctly and the approach mirrors the existing
        // attachment-mesh builder.
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int fi = 0; fi < part.Indices.Length; fi++)
        {
            int vi = part.Indices[fi];
            var p  = positions[vi];
            var n  = normals[vi];
            var uv = part.UVs[vi];

            // Resolve skin slot indices
            int s1 = 0, s2 = 0;
            if (part.Bone1Names[vi] != null && skinSlots.TryGetValue(part.Bone1Names[vi]!, out int ss1)) s1 = ss1;
            if (part.Bone2Names[vi] != null && skinSlots.TryGetValue(part.Bone2Names[vi]!, out int ss2)) s2 = ss2;

            float w1 = part.Bone1Weights[vi];
            float w2 = part.Bone2Weights[vi];

            // Normalize the two SL weights so they sum to 1 — Godot expects normalized
            // skin weights and a zero-sum vertex would not deform at all.
            float wsum = w1 + w2;
            if (wsum > 0.0001f) { w1 /= wsum; w2 /= wsum; }
            else { w1 = 1f; w2 = 0f; }

            // SL is Z-up; Godot is Y-up: SL(X,Y,Z) → Godot(X,Z,−Y)
            st.SetBones(new int[]   { s1,  s2,  0,   0   });
            st.SetWeights(new float[]{ w1,  w2,  0f,  0f  });
            st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
            // SL/OpenGL texture origin is bottom-left (V grows up); Godot/Vulkan is top-left
            // (V grows down) and Magick decodes row 0 = top. Flip V so the baked skin lands
            // on the correct body parts instead of mirrored (front texture on the back, etc).
            st.SetUV(new Godot.Vector2(uv.X, 1.0f - uv.Y));
            st.AddVertex(new Godot.Vector3(p.X, p.Z, -p.Y));
        }

        st.GenerateTangents();
        return st.Commit();
    }

    /// <summary>Re-applies the avatar's vertex morphs to every body part and swaps in the rebuilt
    /// mesh. Called when the avatar's shape (VisualParams) changes. Main-thread (mutates scene
    /// nodes); the per-part morph+rebuild is a few thousand vertices, cheap enough inline for now
    /// — TODO(M4-6): move the morph+SurfaceTool build to a worker and CallDeferred only the swap.</summary>
    private void RebuildBodyMorphs(AvatarVisual visual, IReadOnlyDictionary<int, float> weights)
    {
        foreach (var (name, part) in visual.BodyPartData)
        {
            if (part.Morphs.Count == 0) continue;                       // nothing to morph
            if (!visual.Parts.TryGetValue(name, out var mi) || !IsInstanceValid(mi)) continue;
            if (!_partSkinCache.TryGetValue(name, out var sk)) continue;

            var (positions, normals) = AvatarMorphService.Apply(part, weights);
            mi.Mesh = BuildPartMesh(part, positions, normals, sk.Slots);

            // TEMPORARY DIAGNOSTIC: rank this part's morphs by actual contribution to THIS avatar
            // (|weight| × max per-vertex delta magnitude) so an over-deforming or wrongly-applied
            // morph (e.g. a clothing displacement firing on a nude body) is visible by name.
            if (name is "upper_body" or "lower_body")
            {
                var ranked = new List<(string Name, int ParamId, float Weight, float MaxDelta)>();
                foreach (var m in part.Morphs)
                {
                    if (!weights.TryGetValue(m.ParamId, out float w) || w == 0f) continue;
                    float maxD = 0f;
                    for (int k = 0; k < m.PositionDeltas.Length; k++)
                    {
                        float d = m.PositionDeltas[k].Length();
                        if (d > maxD) maxD = d;
                    }
                    ranked.Add((m.Name, m.ParamId, w, maxD));
                }
                ranked.Sort((a, b) => (System.Math.Abs(b.Weight) * b.MaxDelta).CompareTo(System.Math.Abs(a.Weight) * a.MaxDelta));
                GD.Print($"[DBG-MORPH] {name}: {ranked.Count} active morphs");
                foreach (var r in ranked.GetRange(0, System.Math.Min(12, ranked.Count)))
                    GD.Print($"[DBG-MORPH]   {r.Name} (id={r.ParamId}) weight={r.Weight:0.###} maxDelta={r.MaxDelta:0.####}m contrib={System.Math.Abs(r.Weight) * r.MaxDelta:0.####}");
            }

            // TEMPORARY DIAGNOSTIC: dump the SKELETAL proportion params' effective weights (arm/leg
            // length are bone scales, not morphs). "Arm Length" (693) defaults to 0.6 = long arms;
            // if we're stuck at the default instead of the avatar's transmitted value, arms render
            // too long. Printed once (on upper_body pass) so it isn't repeated per part.
            if (name == "upper_body")
            {
                (int Id, string N)[] proportionParams =
                {
                    (33, "Height"), (36, "Shoulders"), (37, "Hip Width"), (38, "Torso Length"),
                    (842, "Hip Length"), (692, "Leg Length"), (693, "Arm Length"), (756, "Neck Length")
                };
                foreach (var (id, pn) in proportionParams)
                {
                    string wStr = weights.TryGetValue(id, out float pw) ? pw.ToString("0.###") : "ABSENT";
                    string def = LibreMetaverse.VisualParams.Params.TryGetValue(id, out var vp) ? vp.DefaultValue.ToString("0.###") : "?";
                    GD.Print($"[DBG-PROP] {pn} (id={id}) effWeight={wStr} default={def}");
                }
            }
        }
    }

    /// <summary>
    /// Adds a named bind to <paramref name="skin"/> for <paramref name="boneName"/> if not
    /// already present. The bind transform is the INVERSE of the bone's global rest transform
    /// so that the avatar mesh appears unchanged when the skeleton is in T-pose.
    /// </summary>
    private static void AddSkinSlot(
        string? boneName, Skin skin, Skeleton3D skeleton, Dictionary<string, int> skinSlots)
    {
        if (boneName == null || skinSlots.ContainsKey(boneName)) return;

        int boneIdx = skeleton.FindBone(boneName);
        if (boneIdx < 0) return;

        var globalRest = ComputeGlobalRestTransform(skeleton, boneIdx);
        // Bind by explicit skeleton bone index rather than by name. Named binds rely on
        // a name-resolution pass that has proven unreliable here; AddBind ties the skin
        // slot directly to the bone that drives it. The slot index is the bind's position.
        int slot = skin.GetBindCount();
        skin.AddBind(boneIdx, globalRest.Inverse());
        skinSlots[boneName] = slot;
    }

    // SL→Godot basis change (row-vector convention, v·C): SL(x,y,z) → Godot(x, z, −y).
    // Used to convert a mesh's inverse-bind matrices into the skeleton's coordinate space.
    private static readonly System.Numerics.Matrix4x4 SlToGodot =
        new(1, 0, 0, 0,  0, 0, -1, 0,  0, 1, 0, 0,  0, 0, 0, 1);
    private static readonly System.Numerics.Matrix4x4 SlToGodotInv = Invert(SlToGodot);

    private static System.Numerics.Matrix4x4 Invert(System.Numerics.Matrix4x4 m)
    {
        System.Numerics.Matrix4x4.Invert(m, out var inv);
        return inv;
    }

    /// <summary>Converts a row-vector <see cref="System.Numerics.Matrix4x4"/> (v·M) into a
    /// Godot <see cref="Transform3D"/> (column-vector, M·v) — the 3×3 is transposed and the
    /// translation comes from the matrix's fourth row.</summary>
    private static Transform3D RowMatrixToTransform(System.Numerics.Matrix4x4 m)
    {
        var basis = new Basis(
            new Godot.Vector3(m.M11, m.M12, m.M13),
            new Godot.Vector3(m.M21, m.M22, m.M23),
            new Godot.Vector3(m.M31, m.M32, m.M33));
        return new Transform3D(basis, new Godot.Vector3(m.M41, m.M42, m.M43));
    }

    /// <summary>
    /// Computes the global rest transform of a bone by multiplying local rest transforms
    /// up the parent chain — equivalent to <c>GetBoneGlobalRest</c> but explicit.
    /// </summary>
    private static Transform3D ComputeGlobalRestTransform(Skeleton3D skeleton, int boneIdx)
    {
        int parent = skeleton.GetBoneParent(boneIdx);
        var local  = skeleton.GetBoneRest(boneIdx);
        if (parent < 0) return local;
        return ComputeGlobalRestTransform(skeleton, parent) * local;
    }

    /// <summary>Procedural box/sphere man — used when the .llm character files are absent.</summary>
    private static void BuildProceduralBoxMan(Skeleton3D skeleton, AvatarVisual visual, Color color)
    {
        foreach (var part in ProceduralAvatarMesh.BodyParts)
        {
            var attachment = new BoneAttachment3D { Name = "Attach_" + part.BoneName };
            skeleton.AddChild(attachment);

            int boneIdx = skeleton.FindBone(part.BoneName);
            if (boneIdx != -1) attachment.BoneIdx = boneIdx;
            attachment.BoneName = part.BoneName;

            Mesh mesh = part.IsSphere
                ? new SphereMesh { Radius = part.Size.X * 0.5f, Height = part.Size.Y }
                : new BoxMesh    { Size   = part.Size };

            var mi = new MeshInstance3D
            {
                Name             = part.BoneName + "_Mesh",
                Mesh             = mesh,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                Position         = part.Offset
            };

            attachment.AddChild(mi);
            visual.Parts[part.BoneName] = mi;
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

    private double _cullAccum = 0;

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Recompute draw-distance visibility a few times a second (not every frame — the
        // agent lookup scans all entities). Animation still advances every frame, but only
        // for avatars currently inside the draw distance.
        _cullAccum += delta;
        bool doCull = _cullAccum >= 0.25;
        Godot.Vector3 agentPos = Godot.Vector3.Zero;
        bool haveAgent = false;
        if (doCull)
        {
            _cullAccum = 0;
            haveAgent = _world != null && RenderConfig.TryGetLocalAgentGodotPos(_world, out agentPos);
        }

        float maxSq = RenderConfig.DrawDistance * RenderConfig.DrawDistance;
        foreach (var visual in _visuals.Values)
        {
            if (doCull && haveAgent)
            {
                bool visible = visual.Root.Position.DistanceSquaredTo(agentPos) <= maxSq;
                if (visual.Root.Visible != visible) visual.Root.Visible = visible;
            }

            if (visual.Root.Visible && visual.AnimPlayer.IsPlaying)
            {
                visual.AnimPlayer.Advance(dt);
            }
        }
    }

    private async System.Threading.Tasks.Task LoadAndStartAnimationsAsync(AvatarVisual visual, List<Guid> animIds)
    {
        if (_assetService == null) return;

        GD.Print($"[AvatarRenderer] Loading {animIds.Count} animation(s): {string.Join(", ", animIds)}");

        var loaded = new List<(Guid id, AnimationData data)>();
        foreach (var animId in animIds)
        {
            try
            {
                var data = await _assetService.GetAnimationAsync(animId);
                if (data != null)
                {
                    var jointNames = string.Join(", ", System.Linq.Enumerable.Select(data.Joints, j => j.JointName));
                    GD.Print($"[AvatarRenderer] Animation {animId}: {data.Joints.Length} joints ({jointNames}), {data.Length:F2}s");
                    loaded.Add((animId, data));
                }
                else
                {
                    GD.PrintErr($"[AvatarRenderer] Animation {animId}: fetch returned null (not in grid assets?)");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[AvatarRenderer] Failed to fetch animation {animId}: {ex.Message}");
            }
        }

        GD.Print($"[AvatarRenderer] Starting {loaded.Count}/{animIds.Count} animation(s)");

        // Apply on main thread via CallDeferred
        Godot.Callable.From(() => {
            if (visual.Root == null || !IsInstanceValid(visual.Root)) return;
            visual.AnimPlayer.SetActiveAnimations(loaded);
        }).CallDeferred();
    }
}
