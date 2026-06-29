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
        public Dictionary<int, Guid> LoadedTextures { get; } = new();
        public AvatarAnimationPlayer AnimPlayer { get; } = new();
        public List<Guid>? LoadedAnimationIds { get; set; }
        public byte[]? LastAppliedVisualParams { get; set; }

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

    public void Initialize(World world, AssetService assetService, GpuCache gpuCache)
    {
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
        if (e.Component is AttachmentComponent)
            CallDeferred(nameof(UpdateAttachment), e.Entity.Id.ToString());
        else
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
                    var mi = BuildSkinnedMeshInstance(part, skeleton, color);
                    if (mi == null) continue;
                    mi.Name = part.Name + "_Mesh";
                    skeleton.AddChild(mi);
                    // Explicitly point the mesh at the skeleton. The default NodePath("..")
                    // is not reliably populated for programmatically-created MeshInstance3D,
                    // and without it Godot renders the static vertex buffer (the T-pose) and
                    // never applies bone skinning.
                    mi.Skeleton = mi.GetPathTo(skeleton);
                    visual.Parts[part.Name] = mi;
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
                var distortions = AvatarShapeService.ComputeDistortions(avatar.VisualParams);
                ApplyShape(visual.Skeleton, _avatarSkeleton, distortions);
                visual.Skeleton.ResetBonePoses();
            }
        }

        // 3. Texture streaming / Bakes-on-Mesh
        if (avatar.BakedTextures != null && _assetService != null)
        {
            foreach (var kv in avatar.BakedTextures)
            {
                int bakeIndex = kv.Key;
                var textureId = kv.Value;
                if (textureId == Guid.Empty) continue;
                if (!visual.LoadedTextures.TryGetValue(bakeIndex, out var currentId) || currentId != textureId)
                {
                    visual.LoadedTextures[bakeIndex] = textureId;
                    _ = LoadAndApplyTextureAsync(visual, bakeIndex, textureId);
                }
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

    private void ApplyShape(Skeleton3D skeleton, AvatarSkeleton avatarSkeleton, Dictionary<string, (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Position)> distortions)
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

            var godotPos = new Godot.Vector3(slPos.X, slPos.Z, -slPos.Y);
            
            var slRot = bone.Rotation;
            var godotRotDeg = new Godot.Vector3(slRot.X, slRot.Z, -slRot.Y);
            var godotRotRad = new Godot.Vector3(
                Mathf.DegToRad(godotRotDeg.X),
                Mathf.DegToRad(godotRotDeg.Y),
                Mathf.DegToRad(godotRotDeg.Z)
            );
            var basis = Basis.FromEuler(godotRotRad);
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
            var textureData = await _assetService.GetTextureAsync(textureId);
            if (textureData == null) return;

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

            godotTexture = await tcs.Task;
        }

        if (godotTexture == null) return;

        // Map SL bake indices (AvatarTextureIndex) to which mesh parts they cover.
        // 8=BakedHead, 9=BakedUpperBody, 10=BakedLowerBody, 11=BakedEyes, 12=BakedSkirt, 13=BakedHair
        // Part names match AvatarBodyPartMesh.Name (after stripping "avatar_" prefix in AvatarBodyMeshService).
        var partsForBake = bakeIndex switch
        {
            8  => new[] { "head", "eyelashes" },
            9  => new[] { "upper_body" },
            10 => new[] { "lower_body" },
            11 => new[] { "eye" },
            12 => new[] { "lower_body" },
            13 => new[] { "hair" },
            _  => (string[]?)null
        };

        Godot.Callable.From(() => {
            if (visual.Root == null || !IsInstanceValid(visual.Root)) return;

            var targets = partsForBake != null
                ? partsForBake.Select(n => visual.Parts.TryGetValue(n, out var m) ? m : null)
                              .Where(m => m != null)
                              .Cast<MeshInstance3D>()
                : visual.Parts.Values.Cast<MeshInstance3D>();

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

        // HUD and unmapped points have no world bone.
        var boneName = AttachmentPointMap.GetBoneName(attachment.AttachmentPoint);
        if (boneName == null) return;

        // Avatar must already be rendered.
        if (!_visuals.TryGetValue(attachment.AvatarEntityId, out var avatarVisual)) return;
        if (avatarVisual.Skeleton == null) return;

        // Re-use existing node or create a new BoneAttachment3D on the avatar skeleton.
        if (!_attachmentNodes.TryGetValue(entityId, out var boneAttach))
        {
            boneAttach = new BoneAttachment3D { Name = $"WornItem_{entityId:N}" };
            int boneIdx = avatarVisual.Skeleton.FindBone(boneName);
            if (boneIdx >= 0) boneAttach.BoneIdx = boneIdx;
            boneAttach.BoneName = boneName;
            avatarVisual.Skeleton.AddChild(boneAttach);
            _attachmentNodes[entityId] = boneAttach;

            // Kick off mesh/texture load for mesh attachments.
            var prim = entity.GetComponent<PrimitiveComponent>();
            if (prim != null)
            {
                if (prim.IsMesh && prim.MeshId != Guid.Empty)
                {
                    _ = LoadAndApplyAttachmentMeshAsync(boneAttach, avatarVisual.Skeleton, prim.MeshId, prim.TextureId,
                        new System.Numerics.Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z));
                }
                else
                {
                    // Prim attachment: show a scaled box placeholder.
                    var color = new Color(prim.ColorTint.X, prim.ColorTint.Y, prim.ColorTint.Z, prim.ColorTint.W);
                    var box = new MeshInstance3D
                    {
                        Name = "AttachBox",
                        Mesh = new BoxMesh { Size = new Godot.Vector3(prim.Scale.X, prim.Scale.Z, prim.Scale.Y) },
                        MaterialOverride = new StandardMaterial3D { AlbedoColor = color }
                    };
                    boneAttach.AddChild(box);
                }
            }
        }
    }

    private async System.Threading.Tasks.Task LoadAndApplyAttachmentMeshAsync(
        BoneAttachment3D boneAttach, Skeleton3D? skeleton, Guid meshId, Guid textureId, System.Numerics.Vector3 slScale)
    {
        if (_assetService == null) return;

        var meshData = await _assetService.GetMeshAsync(meshId).ConfigureAwait(false);
        if (meshData == null) return;

        // Rigged / fitted mesh (worn mesh bodies and clothing) carries skin data: skin it to
        // the avatar skeleton so it deforms and animates with the body, instead of bolting it
        // statically to one bone (which collapses it into a blob).
        if (meshData.Skin != null && skeleton != null)
        {
            Godot.Callable.From(() =>
            {
                if (!IsInstanceValid(skeleton)) return;
                var mi = BuildRiggedMeshInstance(meshData, skeleton);
                if (mi == null) return;
                mi.Name = "RiggedMesh";
                skeleton.AddChild(mi);
                mi.Skin = mi.GetMeta("skin").As<Skin>();
                mi.Skeleton = mi.GetPathTo(skeleton);
                if (textureId != Guid.Empty)
                    _ = LoadAndApplyAttachmentTextureAsync(mi, textureId);
            }).CallDeferred();
            return;
        }

        Godot.Callable.From(() =>
        {
            if (!IsInstanceValid(boneAttach)) return;

            var arrayMesh = new ArrayMesh();
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
                    st.SetUV(new Godot.Vector2(uv.X, uv.Y));
                    st.AddVertex(new Godot.Vector3(p.X * slScale.X, p.Z * slScale.Z, -p.Y * slScale.Y));
                }
                st.GenerateTangents();
                st.Commit(arrayMesh);
            }

            var mi = new MeshInstance3D { Name = "AttachMesh", Mesh = arrayMesh };
            boneAttach.AddChild(mi);

            if (textureId != Guid.Empty)
                _ = LoadAndApplyAttachmentTextureAsync(mi, textureId);
        }).CallDeferred();
    }

    /// <summary>
    /// Builds a skinned <see cref="MeshInstance3D"/> for a rigged/fitted mesh, bound to the
    /// avatar <paramref name="skeleton"/>. Returns null if no joints resolve. The instance must
    /// be added as a DIRECT child of the skeleton; its <see cref="Skin"/> is stashed in a node
    /// meta ("skin") so the caller can assign it after AddChild. Main thread only.
    /// </summary>
    private MeshInstance3D? BuildRiggedMeshInstance(MeshData meshData, Skeleton3D skeleton)
    {
        var skinData = meshData.Skin!;
        int jointCount = skinData.JointNames.Length;

        // Bind every resolvable joint to its bone's global-rest inverse — same convention as
        // the system body parts (robust against the asset's own bind pose). One bind per bone.
        var skin = new Skin();
        var slotForBone = new Dictionary<int, int>();
        var slotForJoint = new int[jointCount];
        for (int j = 0; j < jointCount; j++)
        {
            int bone = skeleton.FindBone(skinData.JointNames[j]);
            if (bone < 0) { slotForJoint[j] = -1; continue; }
            if (!slotForBone.TryGetValue(bone, out int slot))
            {
                slot = skin.GetBindCount();
                skin.AddBind(bone, ComputeGlobalRestTransform(skeleton, bone).Inverse());
                slotForBone[bone] = slot;
            }
            slotForJoint[j] = slot;
        }
        if (skin.GetBindCount() == 0) return null;

        var bindShape = skinData.BindShapeMatrix;
        var arrayMesh = new ArrayMesh();

        foreach (var sub in meshData.Submeshes)
        {
            if (sub.Indices.Length == 0 || sub.Weights == null) continue;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);

            foreach (int idx in sub.Indices)
            {
                // Mesh-local → bind pose (SL coords) via the bind-shape matrix, then SL→Godot.
                var pSL = System.Numerics.Vector3.Transform(sub.Positions[idx], bindShape);
                var nSL = System.Numerics.Vector3.TransformNormal(sub.Normals[idx], bindShape);
                if (nSL.LengthSquared() > 1e-8f) nSL = System.Numerics.Vector3.Normalize(nSL);
                var uv = sub.UVs[idx];
                var w = sub.Weights[idx];

                // Resolve up to four influences, dropping joints our skeleton lacks; renormalize.
                var bones = new int[4];
                var wts = new float[4];
                int c = 0; float sum = 0f;
                AddInfluence(w.Joint0, w.Weight0, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                AddInfluence(w.Joint1, w.Weight1, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                AddInfluence(w.Joint2, w.Weight2, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                AddInfluence(w.Joint3, w.Weight3, slotForJoint, jointCount, bones, wts, ref c, ref sum);
                if (sum > 1e-5f) { for (int k = 0; k < 4; k++) wts[k] /= sum; }
                else { bones[0] = 0; wts[0] = 1f; } // orphaned vertex — pin to first bound bone

                st.SetBones(bones);
                st.SetWeights(wts);
                st.SetNormal(new Godot.Vector3(nSL.X, nSL.Z, -nSL.Y));
                st.SetUV(new Godot.Vector2(uv.X, uv.Y));
                st.AddVertex(new Godot.Vector3(pSL.X, pSL.Z, -pSL.Y));
            }

            st.GenerateTangents();
            st.Commit(arrayMesh);
        }

        if (arrayMesh.GetSurfaceCount() == 0) return null;

        var mi = new MeshInstance3D
        {
            Mesh = arrayMesh,
            MaterialOverride = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled }
        };
        mi.SetMeta("skin", skin);
        return mi;
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

    private async System.Threading.Tasks.Task LoadAndApplyAttachmentTextureAsync(MeshInstance3D mi, Guid textureId)
    {
        if (_assetService == null) return;

        var textureData = await _assetService.GetTextureAsync(textureId).ConfigureAwait(false);
        if (textureData == null) return;

        Godot.Callable.From(() =>
        {
            if (!IsInstanceValid(mi)) return;
            var image = Image.CreateFromData(textureData.Width, textureData.Height, false, Image.Format.Rgba8, textureData.Rgba);
            var tex = ImageTexture.CreateFromImage(image);
            if (tex == null) return;

            if (_gpuCache != null)
                _gpuCache.Put(textureId, tex, (long)textureData.Width * textureData.Height * 4);

            var mat = new StandardMaterial3D { AlbedoTexture = tex };
            mi.MaterialOverride = mat;
        }).CallDeferred();
    }

    // -------------------------------------------------------------------------
    // Skinned-mesh helpers
    // -------------------------------------------------------------------------

    // The base body geometry and its skin binds are identical for every avatar: the same
    // .llm files and the same skeleton rest pose. Build the (mesh, skin) once per body part
    // and share the resources across all avatars. Without this, every avatar that appears on
    // a busy region re-parses the meshes and rebuilds the skin on the main thread — which is
    // what froze the client on OSGrid. Accessed only from the main thread (CallDeferred).
    private static readonly Dictionary<string, (ArrayMesh Mesh, Skin Skin)> _builtPartCache = new();

    /// <summary>
    /// Returns a <see cref="MeshInstance3D"/> for one SL body-part mesh, with correct
    /// per-vertex bone indices and weights. The geometry and skin are cached and shared;
    /// only the per-instance material is fresh. The instance must be added as a DIRECT child
    /// of the <see cref="Skeleton3D"/> for Godot's built-in skinning to take effect.
    /// </summary>
    private MeshInstance3D? BuildSkinnedMeshInstance(
        AvatarBodyPartMesh part, Skeleton3D skeleton, Color baseColor)
    {
        if (part.Indices.Length == 0) return null;

        if (!_builtPartCache.TryGetValue(part.Name, out var built))
        {
            built = BuildPartResources(part, skeleton);
            _builtPartCache[part.Name] = built;
        }

        return new MeshInstance3D
        {
            Mesh             = built.Mesh,
            Skin             = built.Skin,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = baseColor,
                CullMode    = BaseMaterial3D.CullModeEnum.Disabled
            }
        };
    }

    /// <summary>Builds the shared (mesh, skin) pair for one body part. Called once per part.</summary>
    private static (ArrayMesh Mesh, Skin Skin) BuildPartResources(AvatarBodyPartMesh part, Skeleton3D skeleton)
    {
        // Build a Skin resource: one bind per unique bone referenced in this part.
        var skin = new Skin();
        var skinSlots = new Dictionary<string, int>(); // boneName → slot index in Skin

        for (int vi = 0; vi < part.Positions.Length; vi++)
        {
            AddSkinSlot(part.Bone1Names[vi], skin, skeleton, skinSlots);
            AddSkinSlot(part.Bone2Names[vi], skin, skeleton, skinSlots);
        }

        // Non-indexed surface: expand each face into 3 unique vertex entries so
        // GenerateTangents() works correctly and the approach mirrors the existing
        // attachment-mesh builder.
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int fi = 0; fi < part.Indices.Length; fi++)
        {
            int vi = part.Indices[fi];
            var p  = part.Positions[vi];
            var n  = part.Normals[vi];
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
            st.SetUV(new Godot.Vector2(uv.X, uv.Y));
            st.AddVertex(new Godot.Vector3(p.X, p.Z, -p.Y));
        }

        st.GenerateTangents();
        return (st.Commit(), skin);
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
                    GD.Print($"[AvatarRenderer] Animation {animId}: {data.Joints.Length} joints, {data.Length:F2}s");
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
