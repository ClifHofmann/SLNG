using Godot;
using SLNG.Core;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Assets;
using System.Collections.Generic;
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

        bool isLocal = avatar.IsLocalAgent;
        var color = isLocal
            ? new Color(0.3f, 0.5f, 1.0f)   // Blue for local agent
            : new Color(0.2f, 0.8f, 0.3f);   // Green for others

        if (_avatarSkeleton != null)
        {
            // Build the Skeleton3D
            var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
            skeleton.Name = "Skeleton3D";
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;

            // Instantiate separate Box/Sphere meshes parented via BoneAttachment3D
            foreach (var part in ProceduralAvatarMesh.BodyParts)
            {
                var attachment = new BoneAttachment3D();
                attachment.Name = "Attach_" + part.BoneName;
                
                // Add attachment to skeleton FIRST while it's in the tree
                skeleton.AddChild(attachment);

                // Set bone index and name so it binds correctly
                int boneIdx = skeleton.FindBone(part.BoneName);
                if (boneIdx != -1)
                {
                    attachment.BoneIdx = boneIdx;
                }
                attachment.BoneName = part.BoneName;

                Mesh mesh;
                if (part.IsSphere)
                {
                    mesh = new SphereMesh { Radius = part.Size.X * 0.5f, Height = part.Size.Y };
                }
                else
                {
                    mesh = new BoxMesh { Size = part.Size };
                }

                var mat = new StandardMaterial3D
                {
                    AlbedoColor = color,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled
                };

                var meshInstance = new MeshInstance3D
                {
                    Name = part.BoneName + "_Mesh",
                    Mesh = mesh,
                    MaterialOverride = mat,
                    Position = part.Offset
                };

                attachment.AddChild(meshInstance);
                visual.Parts[part.BoneName] = meshInstance;
            }
            skeleton.ResetBonePoses();
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
            uint regionX = (uint)(entity.RegionHandle >> 32);
            uint regionY = (uint)(entity.RegionHandle & 0xFFFFFFFF);

            // Position the avatar root node
            visual.Root.Position = new Godot.Vector3(
                regionX + transform.Position.X,
                transform.Position.Z,
                -(regionY + transform.Position.Y));

            var slQuat = new Godot.Quaternion(
                transform.Rotation.X, transform.Rotation.Z,
                -transform.Rotation.Y, transform.Rotation.W);
            visual.Root.Quaternion = slQuat;
        }

        // 2. Apply Shape Morphs (Skeletal Distortions)
        if (visual.Skeleton != null && _avatarSkeleton != null && avatar.VisualParams != null)
        {
            var distortions = AvatarShapeService.ComputeDistortions(avatar.VisualParams);
            ApplyShape(visual.Skeleton, _avatarSkeleton, distortions);
            visual.Skeleton.ResetBonePoses();
        }

        // 3. Texture streaming / Bakes-on-Mesh
        if (avatar.BakedTextures != null && _assetService != null)
        {
            foreach (var part in ProceduralAvatarMesh.BodyParts)
            {
                if (avatar.BakedTextures.TryGetValue(part.BakeIndex, out var textureId))
                {
                    if (textureId != Guid.Empty)
                    {
                        if (!visual.LoadedTextures.TryGetValue(part.BakeIndex, out var currentLoadedId) || currentLoadedId != textureId)
                        {
                            visual.LoadedTextures[part.BakeIndex] = textureId;
                            _ = LoadAndApplyTextureAsync(visual, part.BakeIndex, textureId);
                        }
                    }
                }
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

        // Apply texture to all body parts matching this BakeIndex on the main thread
        Godot.Callable.From(() => {
            if (visual.Root == null || !IsInstanceValid(visual.Root)) return;

            foreach (var part in ProceduralAvatarMesh.BodyParts)
            {
                if (part.BakeIndex == bakeIndex)
                {
                    if (visual.Parts.TryGetValue(part.BoneName, out var meshInstance) && IsInstanceValid(meshInstance))
                    {
                        var mat = meshInstance.MaterialOverride as StandardMaterial3D;
                        if (mat == null)
                        {
                            mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                            meshInstance.MaterialOverride = mat;
                        }
                        mat.AlbedoTexture = godotTexture;
                    }
                }
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
                    _ = LoadAndApplyAttachmentMeshAsync(boneAttach, prim.MeshId, prim.TextureId,
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
        BoneAttachment3D boneAttach, Guid meshId, Guid textureId, System.Numerics.Vector3 slScale)
    {
        if (_assetService == null) return;

        var meshData = await _assetService.GetMeshAsync(meshId).ConfigureAwait(false);
        if (meshData == null) return;

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
