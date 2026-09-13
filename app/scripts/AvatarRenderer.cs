using Godot;
using SLNG.Core;
using SLNG.Core.Avatars;
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
        // BUG-AVATAR-02 follow-up: the id of the avatar this visual belongs to. Part of the SL
        // bake-texture CDN URL path (LLVOAvatar::getImageURL uses getID() -- the DISPLAYED avatar,
        // not the viewer), so every BoM/system-bake fetch has to carry it or other people's mesh
        // bodies 403 and render untextured. Set from AvatarComponent.AgentId in CreateVisual/UpdateVisual.
        public Guid AgentId { get; set; }
        // True for the local agent's own visual. Only used to decide what is worth printing
        // ungated: a busy sim puts a dozen other people's mesh bodies through the same code, and
        // their numbers are noise when the question is "why does MY avatar render short".
        public bool IsSelf { get; set; }
        // Bookkeeping for the stationary-gated [AvatarHeight] re-report (self only).
        public float LastRootY { get; set; } = float.NaN;
        public float LastLoggedRootY { get; set; } = float.NaN;
        public double LastHeightLogTime { get; set; }
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
        // Bones whose SCALE a worn rigged mesh has pinned to the skeleton's default, because its
        // skin section sets lock_scale_if_joint_position (viewer: LLVOAvatar::
        // addAttachmentOverridesForObject -> LLJoint::addAttachmentScaleOverride, which
        // LLPolySkeletalDistortion::apply's setScale(..., apply_attachment_overrides: true) then
        // loses to). ApplyShape skips the shape's scale distortion on these — see
        // SlJointComposer.IsScaleLocked for the full source trail (BUG-AVATAR-07). Populated
        // alongside JointPosOverrides and, like it, not reverted per-mesh on detach: a re-login or
        // the next full appearance rebuild re-derives both from whatever is actually worn.
        public HashSet<string> JointScaleLocks { get; } = new();
        // Per-mesh pelvis Z fixups harvested from worn rigged meshes' skin data (viewer:
        // LLAvatarAppearance::addPelvisFixup / LLVector3OverrideMap, indra/llappearance/
        // llavatarappearance.cpp + indra/llcharacter/lljoint.{h,cpp}). Keyed by the contributing
        // mesh id so it can be removed again when that mesh is un-worn — mirrors the viewer's
        // LLVOAvatar::removeAttachmentOverridesForObject, which calls removePelvisFixup(mesh_id)
        // on detach. NOT applied to the mPelvis joint's local position: the viewer adds the
        // active fixup's Z to the AVATAR ROOT's world render position every frame
        // (LLVOAvatar::getRenderPosition, "pos[VZ] += fixup") — see UpdateVisual's root-position
        // step, which mirrors that. When more than one worn mesh carries a nonzero offset
        // simultaneously, only ONE wins (LLVector3OverrideMap::findActiveOverride picks the
        // entry whose mesh_id key compares greatest — an arbitrary but deterministic tie-break,
        // not a sum/max/most-recent rule); TryGetActivePelvisFixup below reproduces that same
        // "one deterministic winner by key" contract.
        public Dictionary<Guid, float> PelvisFixups { get; } = new();
        // Bakes-on-Mesh bookkeeping (viewer: LLVOAvatar::updateMeshVisibility +
        // LLViewerObject::getBakedTextureForMagicId). Which bake channels (AvatarTextureIndex)
        // any worn mesh consumes — used to hide the matching system body parts — and the worn
        // meshes whose face materials must be re-resolved when a new server bake arrives.
        public HashSet<int> AttachmentBakeChannels { get; } = new();
        public List<(MeshInstance3D Mi, int[] FaceIndices, FaceTexture[]? Faces, FaceTexture DefaultFace)> BomAttachments { get; } = new();
        // Per-bone OWN scale (base + shape distortion), keyed by bone name — deliberately NOT
        // multiplied by any ancestor's scale. Verified against LLXformMatrix::update()/
        // LLMatrix4::initAll (indra/llmath/xform.cpp, m4math.cpp): a real SL joint's world matrix
        // uses ONLY its own local scale; Godot's Skeleton3D compounds scale down the hierarchy by
        // default, which is wrong for SL avatars (measured: a rigged sleeve landed 1.2-2.7 m off
        // its own joint and rendered 4-6x too large from this alone). ApplyShape keeps every
        // bone's Rest as a PURE rotation (no scale) so Godot's native pose composition only
        // inherits position/rotation, matching SL; this map supplies the scale each bone's own
        // skinning bind needs to inject back in afterward (see SlJointComposer).
        public Dictionary<string, System.Numerics.Vector3> BoneOwnScale { get; } = new();
        // Last shape distortions passed to RecomputeFootOffset (see that method), cached so a
        // later joint-position-override change (e.g. a fitted mesh attaching after shape already
        // applied) can recompute FootOffsetY without re-deriving distortions from VisualParams.
        public Dictionary<string, (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Position)> LastDistortions { get; } = new();
        // mFootLeft's actual current Y position relative to Root (== relative to the Skeleton3D
        // node, which sits at Root's local origin with zero further offset — see CreateVisual),
        // measured DIRECTLY from the live Skeleton3D via GetBoneGlobalPose. UpdateVisual subtracts
        // this from the network-derived root position every frame so the ACTUAL foot bone — not
        // Root itself — lands at the avatar's network Z (which AvatarController's ground-clamp
        // treats as "feet at ground", matching the real protocol convention).
        //
        // REPLACES an earlier RootOffsetZ field that ported LLVOAvatar::updateRootPositionAndRotation's
        // formula verbatim ("root_pos.mdV[VZ] -= (0.5f * mBodySize.mV[VZ]) - mPelvisToFoot", i.e.
        // SlJointComposer.ComputeBodySize's PelvisToFoot/BodySizeZ). That formula is provably NOT
        // the bug fix it looked like: with AvatarController's ground-clamp computing
        // clampTargetZ = groundHeight - correction and AvatarRenderer computing
        // Root.Y = clampTargetZ + correction, the correction term algebraically cancels for ANY
        // value — Root.Y always equals groundHeight regardless of whether the formula's output is
        // right, wrong, or zero. Root.Y matching groundHeight in every round of live testing was a
        // tautology of that cancellation, not evidence the fix worked (confirmed with live
        // [RootApply]/[GroundClamp] logging, 2026-07-22, rounds 2-4 of this investigation — see
        // git history on fix/avatar-pelvis-offset). The actual bug was never in the arithmetic;
        // it's that NOTHING was measuring where the foot bone really sits after THIS avatar's real
        // shape distortions + worn-mesh joint overrides move it. Measured on a real avatar this
        // session: mFootLeft sits ~+0.006 m from Root for an undistorted default rest pose
        // (negligible — matches SlJointComposerTests' default-skeleton reference), but ~-0.19 m for
        // this avatar's actual shape + worn coat/boots — a gap the formula-based approach, which
        // never reads a live bone pose, could not see. The real viewer uses the formula because
        // LLAvatarAppearance::updateRootPositionAndRotation doesn't have cheap access to a composed
        // bone pose at that point in its pipeline; SLNG does (Skeleton3D.GetBoneGlobalPose), so this
        // uses that directly instead of porting the indirect formula.
        public float FootOffsetY { get; set; }
        public float BodySizeZ { get; set; } = 1.90f;
        // SlJointComposer.ComputeBodySize's PelvisToFoot for THIS avatar's real shape — the exact
        // quantity LLVOAvatar::updateRootPositionAndRotation uses (verified against
        // linden_llvoavatar.cpp, 2026-07-22 round 6): root_pos starts at getRenderPosition() ==
        // getPositionAgent(), i.e. the RAW NETWORK POSITION, which real SL/LSL semantics define as
        // the avatar's PELVIS (llGetPos()/OBJECT_POS on an avatar UUID famously returns pelvis, not
        // a capsule center) — confirmed the same correction runs for isSelf() and remote avatars
        // alike, only the extra gAgent.setPositionAgent() call differs. Our LOCAL avatar's own
        // transform.Position.Z is NOT this real semantic at all: it's an artificial "capsule-center"
        // convention AvatarController's ground-clamp invented (groundHeight + halfBodyZ) purely for
        // our own rendering convenience, which happens to be empirically validated (real user
        // confirmation against Firestorm) for how THIS field's FootOffsetY sibling and BodySizeZ
        // combine in UpdateVisual's root-position formula below. A REMOTE avatar's transform.Position.Z
        // has no such massaging — it's the genuine wire value — so UpdateVisual converts it into
        // our own capsule-center convention using THIS field before running that same formula,
        // rather than rewriting the already-validated local path. See claude-handover-height.md,
        // round 6, for the [RemoteGroundDiag] measurement (~0.26 m gap, matching neither halfBodyZ
        // nor 0) that this fixes.
        public float PelvisToFootZ { get; set; }
        // The "Hover" SHAPE slider (VisualParam id 11001, avatar_lad.xml: group="0", range -2..2m,
        // default 0, <param_skeleton/> EMPTY — i.e. it carries no bone distortion at all, so
        // AvatarShapeService.ComputeDistortions silently drops it entirely; it's a pure position
        // offset the real viewer reads via a completely different path: LLVOAvatar::
        // updateRootPositionAndRotation's `root_pos.mdV[VZ] += getVisualParamWeight(AVATAR_HOVER);`
        // — applied EARLY, directly onto the raw network pelvis Z, BEFORE the halfBodySize/
        // PelvisToFoot correction. NOT the same mechanism as HoverOffsetZ on AvatarComponent (the
        // separate AppearanceHover network field, round 9) — SL has TWO independent "hover"
        // concepts: this one is a regular shape slider transmitted in VisualParams like any other,
        // while AppearanceHover is a dedicated per-agent network field mesh-body wearers commonly
        // configure via llSetHoverHeight. Missing this term left a ~7.8cm residual sink for a
        // remote avatar whose account has no AppearanceHover set but DOES have a nonzero Hover
        // shape slider (2026-07-22, round 11 — see claude-handover-height.md).
        public float AvatarHoverParamZ { get; set; }
        // SLNG-specific: Combined height offset from Shoe Base (Height Adjuster) wearables,
        // calculated as 0.08 * ParamHeelHeight + 0.07 * ParamPlatformHeight.
        // Added to BodySizeZ and PelvisToFootZ to prevent floating/sinking.
        public float ShoeOffsetZ { get; set; }
        // Per-avatar system-body-part Skin cache (bone binds + boneName->slot map), keyed by part
        // name. Used to be a single static dictionary shared across every avatar because the bind
        // matrices only depended on the neutral skeleton rest — true under the OLD (Godot-native
        // scale-compounding) design. Now that each bind injects THIS avatar's own BoneOwnScale
        // (see AddSkinSlot), sharing across avatars would bake one avatar's scale into every other
        // avatar's mesh, so the cache moved here (per-AvatarVisual) and is invalidated/rebuilt in
        // RebuildBodyMorphs whenever shape changes.
        public Dictionary<string, (Skin Skin, Dictionary<string, int> Slots)> PartSkins { get; } = new();
        // Worn RIGGED mesh attachments currently loaded on this avatar (append-only, like
        // BomAttachments — stale entries whose Mi was freed on detach/replace are simply skipped
        // via IsInstanceValid at read time rather than pruned). Needed to rebuild each attachment's
        // Skin (see BuildRiggedMeshInstance's InjectOwnScale step) whenever this avatar's shape
        // changes: a mesh that finished loading BEFORE shape/VisualParams first arrived was bound
        // with an empty BoneOwnScale (no scale injected at all), and — unlike system body parts,
        // which are vertex-morphed AND re-skinned every shape update via RebuildBodyMorphs — a worn
        // mesh's own vertices never change with shape, so nothing else would ever revisit its Skin.
        public List<(MeshInstance3D Mi, MeshData MeshData, Guid MeshId)> RiggedAttachments { get; } = new();
        public Godot.Control? NameTag { get; set; }
        /// <summary>Tracks the previous frame's SittingOnLocalId to detect sit→stand transitions.
        /// When transitioning from sitting to standing, the animation player must be forcefully
        /// stopped so the sit pose doesn't persist while the server sends new standing animations.</summary>
        public uint PreviousSittingOnLocalId { get; set; }

        public AvatarVisual()
        {
            Root = new Node3D();
            Root.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;
        }

        public void QueueFree()
        {
            if (Root != null && GodotObject.IsInstanceValid(Root)) Root.QueueFree();
            if (NameTag != null && GodotObject.IsInstanceValid(NameTag))
            {
                NameTag.QueueFree();
            }
        }
    }

    private World? _world;
    private AssetService? _assetService;
    private GpuCache? _gpuCache;
    private SLNG.Net.GridSession? _session;
    // FEAT-AVATAR-01: last-logged self bake-channel signature, to dedupe the [SelfBake] diagnostic.
    private string _lastSelfBakeSig = "";
    private AvatarSkeleton? _avatarSkeleton;
    // Throttles the [RootApply] ground-truth diagnostic in UpdateVisual to ~1/sec.
    private double _timeSinceRootPosLog = 0;
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
    // mesh on its placeholder texture. AvatarEntityId is carried alongside so RemoveVisual can
    // find the owning AvatarVisual and revert this mesh's pelvis fixup contribution (see
    // AvatarVisual.PelvisFixups) once the entity itself (not just the mesh) is torn down —
    // OnEntityRemoved only survives the CallDeferred hop as a bare Guid, so the owner has to be
    // captured here at attach time rather than re-looked-up from the (by-then-gone) entity.
    private readonly Dictionary<Guid, (Guid MeshId, FaceTexture[]? Faces, FaceTexture DefaultFace, Guid AvatarEntityId)> _attachmentMeshIds = new();
    private Godot.CanvasLayer? _nameTagLayer;


    // Bump this string with every fix and check it's actually printed at the top of the log
    // before trusting anything else in it — this session got burned repeatedly by stale/
    // incrementally-rebuilt assemblies silently running old code despite a fresh-looking
    // DLL timestamp. If this line is missing or shows an old tag, the client is NOT running
    // the code you think it is; close it fully (not just the window) and re-run
    // tools/run-client.ps1 before drawing any conclusion from the rest of the log.
    private const string BuildMarker = "2026-07-23-character-dir-empty-assembly-location-fix";

    /// <summary>Resolves the "linden/character" directory the LibreMetaverse NuGet package
    /// deploys .llm/.xml character files to, next to SLNG.Assets.dll in the build output.
    /// Assembly.Location can be an EMPTY STRING (not null) for a published/exported .NET build --
    /// confirmed live: an exported client rendered every avatar as the fallback capsule because
    /// Path.GetDirectoryName("") also returns "" (not null), so a plain `?? AppContext.
    /// BaseDirectory` null-coalesce never triggered and the computed directory silently became
    /// the CWD-relative "linden\character", which doesn't exist next to an installed
    /// PurisViewer.exe. Same failure mode already fixed for LibreMetaverse's own ResourceDir in
    /// GridSession.cs -- this applies the identical IsNullOrEmpty-checked fallback. Previously
    /// duplicated (with this same bug, twice) at both call sites -- CreateVisual's skeleton/body
    /// load and RebuildBodyMorphs' shape-distortion computation both need this same directory.</summary>
    private static string GetCharacterDir()
    {
        var asmLocation = typeof(AvatarBodyMeshService).Assembly.Location;
        var asmDir = string.IsNullOrEmpty(asmLocation)
            ? AppContext.BaseDirectory
            : System.IO.Path.GetDirectoryName(asmLocation);
        if (string.IsNullOrEmpty(asmDir)) asmDir = AppContext.BaseDirectory;
        return System.IO.Path.Combine(asmDir, "linden", "character");
    }

    public void Initialize(World world, AssetService assetService, GpuCache gpuCache, SLNG.Net.GridSession? session = null)
    {
        // GD.Print($"[AvatarRenderer] BUILD MARKER: {BuildMarker}");
        _world = world;
        _assetService = assetService;
        _session = session;
        _gpuCache = gpuCache;

        _nameTagLayer = new Godot.CanvasLayer { Layer = 1, Name = "AvatarNameTags" };
        AddChild(_nameTagLayer);

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
            // GD.Print($"[AvatarRenderer] Loaded Bento skeleton: {_avatarSkeleton.Bones.Count} entries");
        }
        catch (Exception)
        {
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
        if (e.Component is AttachmentComponent || e.Component is PrimitiveComponent
            || (e.Component is TransformComponent && e.Entity.GetComponent<AttachmentComponent>() != null))
        {
            CallDeferred(nameof(UpdateAttachment), e.Entity.Id.ToString());
        }

        if (e.Component is AvatarComponent || (e.Component is TransformComponent && e.Entity.GetComponent<AvatarComponent>() != null))
        {
            CallDeferred(nameof(UpdateVisual), e.Entity.Id.ToString());
        }
    }

    private void CreateVisual(string entityIdStr)
    {
        if (!Guid.TryParse(entityIdStr, out var entityId)) return;
        if (_visuals.ContainsKey(entityId)) return;
        if (_world == null) return;

        var entity = _world.GetEntity(entityId);
        if (entity == null || entity.GetComponent<AvatarComponent>() == null) return;

        var avatar = entity.GetComponent<AvatarComponent>()!;
        var visual = new AvatarVisual { AgentId = avatar.AgentId };

        // FEAT-ANIM-01: warm the animation cache with the built-in locomotion set the moment the
        // self avatar appears, so the first local walk/turn/fly prediction is a cache hit, not a
        // live asset fetch + decode (which is half the "kommt zu spät wenn es laggt").
        if (avatar.IsLocalAgent && !_locomotionPrefetchStarted && _assetService != null)
        {
            _locomotionPrefetchStarted = true;
            var svc = _assetService;
            GD.Print($"[Locomotion] prefetching {SelfLocomotion.Prefetch.Count} built-in locomotion animations");
            // Deferred + strictly sequential: firing 18 fetch/decode tasks at once during the
            // login texture storm starved the thread pool and coincided with an
            // Image.CreateFromData AccessViolation on a decode worker (v0.21.9). The prediction's
            // first real need is a walk key, which is rarely in the first ~4 s of a session.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(4000).ConfigureAwait(false);
                int ok = 0;
                var missing = new List<string>();
                foreach (var id in SelfLocomotion.Prefetch)
                {
                    var data = await svc.GetAnimationAsync(id).ConfigureAwait(false);
                    if (data != null) ok++; else missing.Add(id.ToString()[..8]);
                }
                GD.Print($"[Locomotion] prefetch done: {ok}/{SelfLocomotion.Prefetch.Count} resolved" +
                         (missing.Count > 0 ? $" -- missing: {string.Join(" ", missing)}" : ""));
            });
        }

        // Add a collision capsule so raycasts can identify the avatar
        var staticBody = new Godot.StaticBody3D 
        { 
            Name = "AvatarPhysics",
            CollisionLayer = 2,
            CollisionMask = 2
        };
        var capsuleShape = new Godot.CollisionShape3D
        {
            Shape = new Godot.CapsuleShape3D { Radius = 0.45f, Height = 1.9f },
            Position = new Godot.Vector3(0, 0.95f, 0) // Shift up so bottom is at origin
        };
        staticBody.AddChild(capsuleShape);
        staticBody.SetMeta("EntityId", entityIdStr);
        staticBody.SetMeta("LocalId", "Avatar");
        visual.Root.AddChild(staticBody);

        // Add the visual root to the tree first so all sub-nodes inherit the active scene tree lifecycle
        AddChild(visual.Root);

        var nameText = avatar.FirstName;
        if (!string.IsNullOrEmpty(avatar.LastName) && avatar.LastName != "Resident")
        {
            nameText += $" {avatar.LastName}";
        }
        if (!string.IsNullOrEmpty(avatar.DisplayName) && avatar.DisplayName != nameText)
        {
            nameText = $"{avatar.DisplayName}\n{nameText}";
        }

        var panel = new Godot.PanelContainer { Name = "NameTag" };
        var styleBox = new Godot.StyleBoxFlat
        {
            BgColor = new Godot.Color(0, 0, 0, 0.5f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        panel.AddThemeStyleboxOverride("panel", styleBox);

        var label = new Godot.Label
        {
            Name = "Label",
            Text = nameText,
            HorizontalAlignment = Godot.HorizontalAlignment.Center,
            VerticalAlignment = Godot.VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 14);
        panel.AddChild(label);

        visual.NameTag = panel;
        if (_nameTagLayer != null) _nameTagLayer.AddChild(panel);

        // Neutral skin tone as placeholder — replaced by baked textures once they arrive.
        var color = new Color(0.76f, 0.60f, 0.46f);

        if (_avatarSkeleton != null)
        {
            var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
            skeleton.Name = "Skeleton3D";
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;

            // Establish SL-accurate Rest + BoneOwnScale (see ApplyShape's doc comment) from the
            // very first frame, using each bone's BASE scale only (zero distortion — the real
            // appearance packet may arrive later, or for some avatars never). Without this, an
            // avatar whose VisualParams never arrives would keep SkeletonBuilder.Build's raw
            // placeholder rest forever, which reintroduces the pre-M4-8 bug via each bone's own
            // BASE scale alone (e.g. collision volumes' bounding-box scale) compounding down the
            // chain instead of applying SL's one-level-only rule — measured on live avatars whose
            // appearance never arrived: a 0.25-0.57 m skinning gap that never healed on its own.
            ApplyShape(visual, skeleton, _avatarSkeleton,
                new Dictionary<string, (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Position)>());

            var charDir = GetCharacterDir();
            // GD.Print($"[AvatarRenderer] character dir: {charDir}");
            var bodyData = AvatarBodyMeshService.Load(charDir);

            if (bodyData != null)
            {
                foreach (var part in bodyData.Parts)
                {
                    // Base mesh here (no weights yet); morphs are applied by the UpdateVisual call
                    // at the end of CreateVisual once VisualParams are present (RebuildBodyMorphs).
                    var mi = BuildSkinnedMeshInstance(visual, part, skeleton, color, weights: null);
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
        // FEAT-ANIM-01: a teleport re-keys the self entity -- the next UpdateVisual sets this
        // again for the new id; clearing it here just avoids a stale lookup in between.
        if (entityId == _selfEntityId) _selfEntityId = Guid.Empty;
        if (_attachmentNodes.TryGetValue(entityId, out var attachNode))
        {
            if (GodotObject.IsInstanceValid(attachNode)) attachNode.QueueFree();
            _attachmentNodes.Remove(entityId);
        }
        MeshInstance3D? removedRigged = null;
        if (_riggedAttachments.TryGetValue(entityId, out var riggedMesh))
        {
            removedRigged = riggedMesh;
            if (GodotObject.IsInstanceValid(riggedMesh)) riggedMesh.QueueFree();
            _riggedAttachments.Remove(entityId);
        }
        // Un-worn rigged mesh: revert any pelvis fixup it contributed to its OWNER avatar
        // (viewer parity: LLVOAvatar::removeAttachmentOverridesForObject calls
        // removePelvisFixup(mesh_id)). The owner has to come from _attachmentMeshIds, captured
        // at attach time, because the entity itself is already gone from the World by the time
        // this deferred call runs.
        if (_attachmentMeshIds.TryGetValue(entityId, out var removedMeshInfo)
            && _visuals.TryGetValue(removedMeshInfo.AvatarEntityId, out var ownerVisual))
        {
            ownerVisual.PelvisFixups.Remove(removedMeshInfo.MeshId);
            // M4-7: a detached BoM mesh body/head must un-hide the system part it was covering.
            // QueueFree above is deferred, so drop the entry by reference here — RecomputeMesh-
            // Visibility's IsInstanceValid sweep would still see it live this frame.
            if (removedRigged != null) ownerVisual.BomAttachments.RemoveAll(e => e.Mi == removedRigged);
            RecomputeMeshVisibility(ownerVisual);
        }
        _attachmentMeshIds.Remove(entityId);
        if (_hudNodes.TryGetValue(entityId, out var hudNode))
        {
            if (GodotObject.IsInstanceValid(hudNode)) hudNode.QueueFree();
            _hudNodes.Remove(entityId);
        }
        _hudPlacements.Remove(entityId);
        _hudContent.Remove(entityId);
        _hudTriangles.Remove(entityId);
    }

    /// <summary>BUG-AVATAR-01 / Ctrl+Alt+R: the client-side half of the reference viewer's
    /// <c>LLVOAvatarSelf::forceBakeAllTextures</c> (llvoavatarself.cpp) -- evict the self avatar's
    /// baked textures from the GPU cache and rebuild the visual, so they re-download from the bake
    /// CDN. SLNG previously only did the SSB <c>{ cof_version }</c> cap POST; if the sim returned
    /// the same bake ids, nothing visibly happened. Forcing a re-fetch recovers a stale / stuck /
    /// previously-failed bake channel (the "blank avatar" this button exists for) and shows the
    /// default skin for a beat until the fresh bake lands -- the "kurz grau" seen in Firestorm.</summary>
    public void ForceRebakeSelf()
    {
        if (_world == null) return;
        var selfEntity = _world.Query<AvatarComponent>()
            .FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);
        if (selfEntity == null)
        {
            GD.Print("[Rebake] no local-agent entity yet -- nothing to rebake");
            return;
        }

        var bakes = selfEntity.GetComponent<AvatarComponent>()?.BakedTextures;
        int forgotten = 0;
        if (bakes != null && _gpuCache != null)
        {
            foreach (var id in bakes.Values)
            {
                if (id == Guid.Empty) continue;
                _gpuCache.Forget(id);
                forgotten++;
            }
        }
        GD.Print($"[Rebake] forced self rebake: evicted {forgotten} bake texture(s), rebuilding visual");
        UpdateVisual(selfEntity.Id.ToString());
    }

    public void UpdateVisual(string entityIdStr)
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
        // Keep AgentId current -- CreateVisual sets it too, but AvatarComponent can be added before
        // its AgentId is populated; the bake-texture CDN URL depends on this being right.
        if (visual.AgentId == Guid.Empty) visual.AgentId = avatar.AgentId;
        visual.IsSelf = avatar.IsLocalAgent;

        if (visual.NameTag is Godot.PanelContainer panel)
        {
            var label = panel.GetNodeOrNull<Godot.Label>("Label");
            if (label != null)
            {
                string nameText = avatar.FirstName;
                if (!string.IsNullOrEmpty(avatar.LastName) && avatar.LastName != "Resident") 
                    nameText += $" {avatar.LastName}";
                if (!string.IsNullOrEmpty(avatar.DisplayName) && avatar.DisplayName != nameText)
                    nameText = $"{avatar.DisplayName}\n{nameText}";

                if (label.Text != nameText)
                    label.Text = nameText;
            }
        }

        // 1. Transform root position/rotation
        var transform = entity.GetComponent<TransformComponent>();
        if (transform != null)
        {
            // Position the avatar root node (floating-origin relative; see RenderConfig)
            var rootPos = RenderConfig.ToGodot(entity.RegionHandle, transform.Position);

            float halfBodyZ = 0.5f * visual.BodySizeZ;

            if (avatar.SittingOnLocalId != 0)
            {
                // MVP2-1 seated fix, source-verified against LLVOAvatar::updateRootPositionAndRotation
                // (scratch/slviewer/indra/newview/llvoavatar.cpp:4725-4733 — the object-sit branch is a
                // completely separate, much shorter path than the standing/ground-sit branch below):
                //   pos = mDrawable->getPosition();       // seat-parent-relative wire position, verbatim
                //   pos += getHoverOffset() * mDrawable->getRotation();
                //   mRoot->setPosition(pos);
                // None of halfBodyZ/FootOffsetY/PelvisToFootZ/AvatarHoverParamZ/pelvisFixupZ run while
                // sitting on an object — they belong to the standing/ground-sit branch only (confirmed:
                // AVATAR_HOVER shape slider only read at line 4635, inside `if (!isSitting...)`; pelvis
                // fixup only applied in getRenderPosition(), gated on isRoot(), which a seated/parented
                // avatar never is). GridSession.ResolveSeatedTransform already computes the same
                // seat-relative-to-world position LLDrawable::updateXform does, and mRoot is coincident
                // with the pelvis (llappearance/llavatarappearance.cpp:878, :1021) — same conversion the
                // remote standing branch below already applies (transform.Position.Z is a pelvis value),
                // just without any of the ground-correction terms.
                int sitPelvisBone = visual.Skeleton != null ? visual.Skeleton.FindBone("mPelvis") : -1;
                float sitPelvisY = (sitPelvisBone >= 0 && visual.Skeleton != null) ? GetBoneRootRelativeY(visual.Skeleton, sitPelvisBone) : 1.046f;
                rootPos.Y = transform.Position.Z - sitPelvisY + avatar.HoverOffsetZ;
            }
            else if (!avatar.IsLocalAgent)
            {
                // Source-verified fix (2026-07-22, round 12): a REMOTE avatar's transform.Position.Z
                // (the raw SL/OpenSim wire value) is the CAPSULE CENTER, not the pelvis!
                // In linden_llvoavatar.cpp:
                // root_pos.Z = simPos.Z - 0.5*mBodySize.Z + mPelvisToFoot.
                // Since the SL skeleton's foot is at -mPelvisToFoot relative to the root, the foot
                // in the LL viewer ends up exactly at `simPos.Z - 0.5*mBodySize.Z`. This proves
                // simPos.Z is a capsule center.
                // Our LOCAL avatar's clampTargetZ is exactly `groundHeight + halfBodyZ`, which
                // is the exact same convention. Thus, no pelvis-to-capsule conversion is needed.
                //
                // + visual.AvatarHoverParamZ (round 11): LLVOAvatar adds getVisualParamWeight(
                // AVATAR_HOVER) directly onto the raw network Z, BEFORE the halfBodySize/
                // PelvisToFoot correction — the "Hover" SHAPE SLIDER (id 11001), a completely
                // different mechanism from AvatarComponent.HoverOffsetZ (round 9's AppearanceHover
                // network field). Missing this term left a real, unexplained ~7.8cm residual sink
                // for an avatar whose account has no AppearanceHover configured but DOES have a
                // nonzero Hover shape slider. See AvatarVisual.AvatarHoverParamZ's doc comment and
                // claude-handover-height.md, round 11.
            }

              // Viewer parity (LLVOAvatar::updateRootPositionAndRotation):
              if (avatar.SittingOnLocalId != 0)
              {
                  // Already computed above — the standing/ground formulas below don't apply while
                  // sitting on an object (llvoavatar.cpp:4725-4733 is a completely separate branch).
              }
              else if (avatar.IsLocalAgent)
              {
                  // The LOCAL avatar's simPos.Z is locally generated by AvatarController as exactly
                  // groundHeight + halfBodyZ. We want the geometric foot to sit perfectly on the ground.
                  // By subtracting (halfBodyZ + visual.FootOffsetY), the foot lands perfectly at groundHeight.
                  // 
                  // WHY THIS MUST NEVER BE CHANGED TO THE LLVOAVATAR FORMULA:
                  // The SL protocol computes mBodySize.Z (and PelvisToFootZ) by adding local bone translations 
                  // on the Z axis only, ignoring bone rotations. When a user wears rigged mesh boots that rotate 
                  // the ankle downward, the true leg length increases (measured by Godot's true live bone poses as 
                  // mPelvisY - visual.FootOffsetY, which correctly ignores scale compounding). However, SL's 
                  // PelvisToFootZ does NOT increase because it ignores the rotation. 
                  // 
                  // To fix this in Firestorm, users wear a "Shoe Base" which directly adds to the avatar's Z offset. 
                  // For a remote avatar, the Shoe Base height is baked into the simPos.Z we receive from the server, 
                  // so the LLVOAvatar formula below algebraically CANCELS OUT the missing offset and renders the 
                  // remote avatar perfectly (including recreating Firestorm's exact 5cm float). 
                  // 
                  // BUT for the LOCAL avatar, SLNG generates simPos.Z locally WITHOUT parsing Shoe Base data. 
                  // If we apply the LLVOAvatar formula locally, the missing Shoe Base data causes the local avatar 
                  // to sink into the ground by ~20cm. The formula below uses the true live leg length 
                  // (visual.FootOffsetY) to guarantee the foot sits exactly on the capsule floor, hiding the 
                  // missing Shoe Base data and keeping the local avatar perfectly flush.
                  rootPos.Y = transform.Position.Z + visual.AvatarHoverParamZ - halfBodyZ - visual.FootOffsetY;
              }
              else
              {
                  // REMOTE avatars receive simPos.Z as the raw SL network position (the capsule center).
                  // The server includes the wearer's Shoe Base height in this simPos.Z. We MUST apply the exact 
                  // LLVOAvatar formula to reproduce viewer parity. This formula mathematically combines with the 
                  // Shoe Base in simPos.Z to exactly reproduce Firestorm's absolute rendering height (including 
                  // natural gaps or floats).
                  // root_pos -= (0.5 * BodySize) - PelvisToFoot.
                  int pelvisBone = visual.Skeleton != null ? visual.Skeleton.FindBone("mPelvis") : -1;
                  float mPelvisY = (pelvisBone >= 0 && visual.Skeleton != null) ? GetBoneRootRelativeY(visual.Skeleton, pelvisBone) : 1.046f;
                  
                  float slRootPos = transform.Position.Z + visual.AvatarHoverParamZ - halfBodyZ + visual.PelvisToFootZ;
                  rootPos.Y = slRootPos - mPelvisY;
              }

            if (avatar.SittingOnLocalId == 0)
            {
                float pelvisFixupZ = 0f;
                if (TryGetActivePelvisFixup(visual, out pelvisFixupZ))
                    rootPos.Y += pelvisFixupZ;

                // Viewer parity (round 9 — see claude-handover-height.md): LLVOAvatar::
                // updateRootPositionAndRotation adds `root_pos += LLVector3d(getHoverOffset())`,
                // a SEPARATE per-avatar correction from the halfBodySize/PelvisToFoot term above —
                // the real "Hover" shape-slider-style adjustment many SL/mesh-body users configure to
                // fix a specific mesh body/shoe's ground contact, transmitted via AvatarAppearance's
                // AppearanceHover field (same packet as VisualParams; see AvatarAppearanceEvent's doc
                // comment) and applied by the real viewer only for !isSelf() — remote avatars
                // specifically. Our local avatar's own Position.Z is entirely our own construction
                // (AvatarController's ground-clamp), so this only ever has an effect for remote
                // avatars (avatar.HoverOffsetZ stays 0 unless a real AvatarAppearance event set it).
                // Round 8's [RemoteGroundDiag] measured a residual ~7.8cm sink after the pelvis/
                // capsule-center conversion (round 6) and the distortions-aliasing fix (round 7) — a
                // magnitude entirely consistent with a real user-configured Hover value, which nothing
                // in this pipeline read or applied before now.
                rootPos.Y += avatar.HoverOffsetZ;
            }
            // (seated: HoverOffsetZ is already folded into rootPos.Y above, per llvoavatar.cpp:4729)

            visual.Root.Position = rootPos;

            // A world-Z readout is only useful where the avatar actually IS, and a shape apply
            // happens once at login — so re-report it whenever the self avatar has SETTLED at a
            // materially different height (BUG-AVATAR-07's cube comparison needs the number from
            // wherever the user parked). Stationary-gated so walking doesn't stream lines, and
            // rate-limited so a jitter loop can't either.
            if (visual.IsSelf)
            {
                bool stationary = System.Math.Abs(rootPos.Y - visual.LastRootY) < 0.001f;
                visual.LastRootY = rootPos.Y;
                double now = Time.GetTicksMsec() / 1000.0;
                if (stationary && System.Math.Abs(rootPos.Y - visual.LastLoggedRootY) > 0.05f &&
                    now - visual.LastHeightLogTime > 2.0)
                {
                    visual.LastLoggedRootY = rootPos.Y;
                    visual.LastHeightLogTime = now;
                    LogAvatarHeight(visual, "moved", transform.Position.Z);
                }
            }

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
                var charDir = GetCharacterDir();
                // One effective-weight map drives BOTH the skeletal distortions (bone scale/pos)
                // and the vertex morphs (body silhouette) — so they can never disagree on a slider.
                var weights = AvatarShapeService.ComputeEffectiveWeights(avatar.VisualParams, charDir);

                // "Hover" shape slider (id 11001) — see AvatarVisual.AvatarHoverParamZ's doc
                // comment. Cached here (rather than read inline in UpdateVisual's position step,
                // which runs BEFORE this shape-apply step in the same method) so the position
                // formula can use the last-known value on every call, same pattern as BodySizeZ/
                // PelvisToFootZ. weights.TryGetValue defaults to 0f (no Hover applied) if the
                // param wasn't in this avatar's transmitted Group0 array at all.
                const int HoverVisualParamId = 11001;
                visual.AvatarHoverParamZ = weights.TryGetValue(HoverVisualParamId, out var hoverW) ? hoverW : 0f;

                const int ParamHeelHeight = 198;
                const int ParamPlatformHeight = 503;
                float heelW = weights.TryGetValue(ParamHeelHeight, out var h) ? h : 0f;
                float platformW = weights.TryGetValue(ParamPlatformHeight, out var p) ? p : 0f;
                visual.ShoeOffsetZ = heelW * 0.08f + platformW * 0.07f;

                var distortions = AvatarShapeService.ComputeDistortions(avatar.VisualParams, charDir);

                ApplyShape(visual, visual.Skeleton, _avatarSkeleton, distortions.BoneMods, visual.JointPosOverrides);
                visual.Skeleton.ResetBonePoses();

                RecomputeFootOffset(visual, distortions.BoneMods);

                // Deform the system body into this avatar's real proportions (male/muscle/breast/…
                // sliders are vertex morphs, not bone scales — see AvatarMorphService).
                RebuildBodyMorphs(visual, weights);

                // Worn rigged meshes (clothing/mesh body/head) don't re-morph, but their skin
                // binds DO need this avatar's fresh BoneOwnScale — see RebuildRiggedAttachmentSkins.
                RebuildRiggedAttachmentSkins(visual);
                RefreshStaticAttachmentOffsets(visual);

                // [FEAT-RENDER-05] The two numbers that decide "head too big or hair too small".
                // A rigged mesh follows the SKELETON only, so its fit is set by mHead/mSkull's own
                // scale; the system head additionally follows the vertex MORPHS, which no worn
                // mesh can track (true in the real viewer too). So a mismatch is either an mHead
                // scale we compute differently from the viewer, or morphs that inflate the head
                // past what the hair was fitted to -- and these two lines tell them apart. Read
                // against [RiggedMesh]'s bind-pose size for the hair mesh.
                {
                    visual.BoneOwnScale.TryGetValue("mHead", out var headScale);
                    visual.BoneOwnScale.TryGetValue("mSkull", out var skullScale);
                    var headSize = visual.Parts.TryGetValue("head", out var headMi) && headMi.Mesh != null
                        ? headMi.Mesh.GetAabb().Size : Godot.Vector3.Zero;
                    // Ungated (BUG-AVATAR-07): this is the number the "head too small vs Firestorm"
                    // report is actually about, and a shape apply happens a handful of times per
                    // login, not per frame. JointScaleLocks says whether a worn fitted mesh froze
                    // these scales the way the reference viewer does.
                    GD.Print($"[HeadSize] mHead own scale ({headScale.X:0.###}, {headScale.Y:0.###}, {headScale.Z:0.###}), " +
                             $"mSkull ({skullScale.X:0.###}, {skullScale.Y:0.###}, {skullScale.Z:0.###}), " +
                             $"morphed head mesh {headSize.X:0.###} x {headSize.Y:0.###} x {headSize.Z:0.###} m, " +
                             $"scaleLocks={visual.JointScaleLocks.Count}, bodySizeZ={visual.BodySizeZ:0.###} m");
                    LogAvatarHeight(visual, "shape");
                }
            }
        }

        // 3. Texture streaming / Bakes-on-Mesh
        // v0.20.56 taught [SelfBake] to report "every channel is empty". It still could not report
        // the state one step worse than that -- BakedTextures being NULL outright -- because the
        // whole block is gated on it being non-null, so the local agent's most broken appearance
        // state printed nothing at all. Live 2026-09-04: blank head, uncut system hair, and not one
        // [SelfBake] line in the log.
        if (avatar.IsLocalAgent && avatar.BakedTextures == null && _lastSelfBakeSig != "(null)")
        {
            _lastSelfBakeSig = "(null)";
            GD.Print("[SelfBake] channels  (null) -- no AvatarAppearance has ever been applied to " +
                     "this avatar. The head renders blank and the system hair as an uncut helmet " +
                     "until one arrives; GridSession recovers the ids from our own ObjectUpdate.");
        }

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

            // FEAT-AVATAR-01: dump the self avatar's bake channels whenever they change, so a live
            // test can see whether the head bake (channel 8 = head+eyelashes) actually arrived and
            // is a real id vs an EMPTY placeholder. Deduplicated so it isn't per-frame spam.
            // Deliberately NOT gated on anyBakeChanged. anyBakeChanged only turns true for a
            // NON-empty id, so an avatar whose every channel is Guid.Empty -- the sim never sent us
            // our own AvatarAppearance -- printed nothing at all. That is the single most important
            // state this diagnostic exists to report, and it was the one it could not reach: the
            // avatar renders with an uncut system-hair helmet and a blank head while the log stays
            // silent (live, Agni 2026-09-03). The signature compare below is what keeps it from
            // being per-frame spam, so running it every update costs one string build on a dozen
            // entries and nothing else.
            if (avatar.IsLocalAgent)
            {
                string line = string.Join("  ", avatar.BakedTextures.OrderBy(k => k.Key)
                    .Select(k => $"{k.Key}={(k.Value == Guid.Empty ? "EMPTY" : k.Value.ToString("N")[..8])}"));
                if (line != _lastSelfBakeSig)
                {
                    _lastSelfBakeSig = line;
                    bool none = avatar.BakedTextures.Count == 0 || avatar.BakedTextures.Values.All(v => v == Guid.Empty);
                    GD.Print("[SelfBake] channels  " + (line.Length == 0 ? "(none)" : line) +
                        (none ? "  -- NO BAKE AT ALL: the sim has not sent our own appearance. " +
                                "System hair renders as an uncut helmet and the head blank until it does; " +
                                "GridSession nudges a re-composite automatically, Ctrl+Alt+R forces one."
                              : ""));
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

        // 4. Animation playback — detect changes in ActiveAnimations. Compared as a SET, not by
        // list position: the sim resends the full active-animation list on every priority/state
        // change, and nothing guarantees it repeats the same order for an otherwise-unchanged set
        // (SL's animation priority handling can reorder the list server-side even when nothing the
        // avatar is actually playing changed). The previous positional compare treated any reorder
        // as a change, re-triggering LoadAndStartAnimationsAsync -> SetActiveAnimations on every
        // such resend. SetActiveAnimations itself is set-based and doesn't restart an animation
        // that's already in _active, so this was likely wasted work rather than a visible replay --
        // but a transient async fetch hiccup on one entry (see LoadAndStartAnimationsAsync: a
        // GetAnimationAsync that returns null this call but succeeds next) meant an unrelated
        // reorder-only resend could momentarily hand SetActiveAnimations a SHORTER "loaded" list,
        // making it drop and then immediately re-add that animation from InPoint on the very next
        // real update -- a genuine pose restart with no actual change in what the avatar is doing,
        // exactly the kind of discontinuity that reads as a pop while walking/turning (turning is
        // when a new animation, e.g. a turn blend, would first need fetching and be most likely to
        // race).
        // Detect sit→stand transition: when SittingOnLocalId goes from non-zero to zero,
        // the avatar just stood up. Clear LoadedAnimationIds to force the animation change
        // detection below to fire immediately — SetActiveAnimations will cleanly swap the
        // sit animation for the new stand/walk animation without a T-pose gap (it removes
        // old anims and adds new ones in one atomic call). Do NOT call AnimPlayer.Stop()
        // here: that resets all bones to rest pose, causing a visible T-pose flash while
        // LoadAndStartAnimationsAsync fetches the new animation data.
        if (visual.PreviousSittingOnLocalId != 0 && avatar.SittingOnLocalId == 0)
        {
            if (Diagnostics.Enabled) GD.Print("[AnimPlayer] Sit→stand transition detected, forcing animation re-evaluation");
            visual.LoadedAnimationIds = null;
        }
        visual.PreviousSittingOnLocalId = avatar.SittingOnLocalId;

        if (avatar.IsLocalAgent) _selfEntityId = entityId;

        ApplyActiveAnimations(entityId, visual, avatar);
    }

    /// <summary>Reconciles a visual's playing animation set with what it should be.
    ///
    /// <para>For a remote avatar that is simply <c>avatar.ActiveAnimations</c> (the sim's echo).
    /// For the SELF avatar (FEAT-ANIM-01) the built-in locomotion ids are stripped out of the
    /// network set and replaced with <see cref="_selfPredictedLocomotion"/> — decided locally from
    /// input by <see cref="AvatarController"/> and pushed via
    /// <see cref="SetSelfPredictedLocomotion"/> — so the walk cycle starts on the frame the key
    /// goes down instead of after the round-trip. A custom AO animation is not a built-in id, so
    /// it stays in the set and wins per bone via its authored priority (matching the reference
    /// viewer, which also plays the built-in gait locally and lets the AO override it).</para></summary>
    private void ApplyActiveAnimations(Guid entityId, AvatarVisual visual, AvatarComponent avatar)
    {
        if (_assetService == null || visual.Skeleton == null) return;

        List<Guid> desired;
        if (avatar.IsLocalAgent && _selfPredictedLocomotion is { } predicted)
        {
            // Local prediction active (not sitting): strip the sim's echo of the built-in
            // locomotion ids and substitute our own, so the gait is frame-latency, not RTT.
            desired = new List<Guid>();
            if (avatar.ActiveAnimations != null)
                foreach (var id in avatar.ActiveAnimations)
                    if (!SelfLocomotion.All.Contains(id)) desired.Add(id);
            desired.Add(predicted);
        }
        else
        {
            // Remote avatar, or the self avatar while sitting (prediction is null) -- the sim's
            // set is authoritative, including its SIT / stand-up animations.
            if (avatar.ActiveAnimations == null) return;
            desired = new List<Guid>(avatar.ActiveAnimations);
        }

        // FEAT-ANIM-01: while the self avatar is moving, boost the predicted gait over a
        // still-lagging AO stand for ~2 s; a resting predicted pose (Stand/Hover/Crouch) is not
        // boosted so a resting AO pose keeps winning.
        if (avatar.IsLocalAgent)
        {
            if (_selfPredictedLocomotion is { } pid && SelfLocomotion.IsMoving(pid))
                visual.AnimPlayer.SetLocomotionBoost(pid);
            else
                visual.AnimPlayer.ClearLocomotionBoost();
        }

        bool changed = visual.LoadedAnimationIds == null
            || !new HashSet<Guid>(visual.LoadedAnimationIds).SetEquals(desired);
        if (!changed) return;

        // Behind --diag: fires on every gait change while walking through an AO and flooded the
        // log, burying the alpha diagnostics it competes with.
        if (avatar.IsLocalAgent && Diagnostics.Enabled)
            GD.Print($"[Locomotion] self anim set -> [{string.Join(" ", desired.Select(d => d.ToString()[..8]))}] (predicted={_selfPredictedLocomotion?.ToString()[..8] ?? "none"})");

        visual.LoadedAnimationIds = new List<Guid>(desired);
        _ = LoadAndStartAnimationsAsync(visual, desired);
    }

    /// <summary>FEAT-ANIM-01: the self avatar's locomotion animation as decided from local input
    /// this frame by <see cref="AvatarController"/> (<see cref="SelfLocomotion.Predict"/>), or
    /// <see langword="null"/> while sitting. Applied immediately, ahead of the sim's echo.</summary>
    private Guid? _selfPredictedLocomotion;
    private Guid _selfEntityId;
    private bool _locomotionPrefetchStarted;

    /// <summary>Called every frame by <see cref="AvatarController"/> with the locally-predicted
    /// self locomotion animation. No-op unless it changed.</summary>
    public void SetSelfPredictedLocomotion(Guid? animId)
    {
        if (animId == _selfPredictedLocomotion) return;
        var previous = _selfPredictedLocomotion;
        _selfPredictedLocomotion = animId;

        AvatarVisual? visual = null;
        bool haveVisual = _selfEntityId != Guid.Empty && _visuals.TryGetValue(_selfEntityId, out visual);
        var avatar = haveVisual ? _world?.GetEntity(_selfEntityId)?.GetComponent<AvatarComponent>() : null;
        if (haveVisual && visual != null && avatar != null)
        {
            ApplyActiveAnimations(_selfEntityId, visual, avatar);
        }
        else if (Diagnostics.Enabled)
        {
            // Kept, only under --diag: this branch means the predicted walk could not be applied,
            // a real "why isn't my avatar animating" signal, not per-step chatter.
            string from = previous?.ToString()[..8] ?? "(none)";
            string to = animId?.ToString()[..8] ?? "(none)";
            GD.Print($"[Locomotion] predict {from} -> {to} (NOT applied: selfEntityId={(_selfEntityId == Guid.Empty ? "unset" : "set")}, haveVisual={haveVisual})");
        }
    }

    /// <summary>A single scale component, forced finite and strictly positive (min 1e-4) so a
    /// degenerate shape param can't make a bone basis singular. See ApplyShape's use site.</summary>
    private static float SafePositive(float v) => float.IsFinite(v) && v > 1e-4f ? v : 1e-4f;

    private static bool IsFinite(System.Numerics.Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFiniteTransform(Transform3D t)
    {
        var b = t.Basis;
        return b.X.IsFinite() && b.Y.IsFinite() && b.Z.IsFinite() && t.Origin.IsFinite();
    }

    /// <summary>Rebuilds every bone's Godot Rest from this avatar's shape, SL-accurately: verified
    /// against LLXformMatrix::update()/LLMatrix4::initAll (indra/llmath/xform.cpp, m4math.cpp),
    /// scale does NOT inherit down the joint chain in the real viewer — a joint's own world matrix
    /// uses ONLY its own local scale; a parent's scale offsets only how far the child's position
    /// sits (one level), never the child's own scale. Godot's Skeleton3D, like any standard
    /// hierarchical-transform system, compounds scale (and any shear from rotation mixed with
    /// non-uniform scale) down the whole chain by default — measured on a real asset as a rigged
    /// sleeve landing 1.2-2.7 m off its own dominant joint and rendering 4-6x too large, from
    /// shape data that rendered correctly in the real viewer.
    ///
    /// Fix: keep every bone's Rest.Basis a PURE rotation (no scale at all) so Godot's native pose
    /// composition only ever inherits position/rotation — exactly SL's rule — and separately bake
    /// each bone's own scale into <see cref="AvatarVisual.BoneOwnScale"/> plus (pre-multiplied, SL
    /// rule: mWorldPosition.scaleVec(parentScale)) into each CHILD's rest position, since Godot's
    /// composition has no other way to reproduce that one-level offset once Rest carries no
    /// scale. Skinning binds must inject BoneOwnScale back in afterward — see
    /// ComputeSlAccurateGlobalRest / BuildRiggedMeshInstance / BuildPartResources.</summary>
    private void ApplyShape(AvatarVisual visual, Skeleton3D skeleton, AvatarSkeleton avatarSkeleton,
        Dictionary<string, (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Position)> distortions,
        Dictionary<string, System.Numerics.Vector3>? posOverrides = null)
    {
        visual.BoneOwnScale.Clear();

        // Bone index order matches AvatarSkeleton.Bones order (SkeletonBuilder.Build adds bones
        // in that exact sequence), which is guaranteed parent-before-child (XML depth-first parse,
        // a bone element is added before its children are parsed) — so by the time we process a
        // child, BoneOwnScale already has its parent's entry.
        for (int idx = 0; idx < skeleton.GetBoneCount(); idx++)
        {
            string name = skeleton.GetBoneName(idx);
            var bone = avatarSkeleton.GetBone(name);
            if (bone == null) continue;

            var slPos = bone.Position;
            var slScale = bone.Scale;

            if (distortions.TryGetValue(name, out var dist))
            {
                // A fitted mesh body/head that declares lock_scale_if_joint_position freezes this
                // joint's scale at the skeleton default — the real viewer drops the shape sliders'
                // skeletal scale distortion here entirely. See AvatarVisual.JointScaleLocks and
                // SlJointComposer.IsScaleLocked (BUG-AVATAR-07). Position is unaffected: the
                // viewer keeps position overrides and scale overrides on separate maps.
                if (!visual.JointScaleLocks.Contains(name)) slScale += dist.Scale;
                slPos += dist.Position;
            }

            // A zero / negative / non-finite scale component here — an extreme shape param, a
            // distortion that cancels the base scale, or NaN propagated from bad param math —
            // makes the bone's rest basis singular, and ComputeSlAccurateGlobalRest().AffineInverse()
            // in the skinning bind then yields NaN/Inf: Godot logs "Vector3 cannot be normalized,
            // the elements must be finite" and the skinned mesh collapses. Real SL joints never
            // have non-positive scale; clamp to a harmless epsilon.
            slScale = new System.Numerics.Vector3(
                SafePositive(slScale.X), SafePositive(slScale.Y), SafePositive(slScale.Z));
            if (!IsFinite(slPos)) slPos = bone.Position;

            // Joint-position override from a worn rigged mesh wins over base + shape
            // distortion (viewer: LLJoint::setPosition/updatePos — active override replaces the
            // local position outright; rotation and scale are untouched). Verified this is NOT
            // exempt from the parent-scale step below — LLJoint::setPosition stores the override
            // as the same mPosition field LLXformMatrix::update() later scales by the parent.
            if (posOverrides != null && posOverrides.TryGetValue(name, out var ov))
            {
                slPos = ov;
            }
            if (bone.ParentName != null && visual.BoneOwnScale.TryGetValue(bone.ParentName, out var parentScale))
            {
                slPos *= parentScale;
            }

            visual.BoneOwnScale[name] = slScale;

            var godotPos = new Godot.Vector3(slPos.X, slPos.Z, -slPos.Y);

            // Same SL→Godot rotation-order conversion as SkeletonBuilder.Build — see
            // SlEulerDegToGodotBasis's doc comment for the full derivation. Must stay in sync;
            // this rebuilds EVERY bone's rest on each shape update, so any divergence between
            // the two would silently undo one or the other. Deliberately NO .Scaled(...) here —
            // see this method's own doc comment.
            var basis = SkeletonBuilder.SlEulerDegToGodotBasis(bone.Rotation);

            var rest = new Transform3D(basis, godotPos);

            // BUG-NET-13: a NaN/Inf here becomes a non-finite Skeleton3D bone Rest, and Godot then
            // re-normalizes it EVERY frame -> the "Vector3 cannot be normalized" flood that starts
            // on the frame after a teleport (the self avatar rebuilt from a reset AvatarComponent).
            // The scale/pos guards above cover their inputs; this is the last net before the write.
            if (!IsFiniteTransform(rest))
            {
                if (!_boneRestNaNLogged)
                {
                    _boneRestNaNLogged = true;
                    GD.PushWarning($"[NaNGuard] bone-rest non-finite for '{name}' (idx {idx}) -- substituting base rest. slPos={slPos} slScale={slScale} rot={bone.Rotation}");
                }
                var baseGodotPos = new Godot.Vector3(bone.Position.X, bone.Position.Z, -bone.Position.Y);
                rest = new Transform3D(Basis.Identity, baseGodotPos.IsFinite() ? baseGodotPos : Godot.Vector3.Zero);
            }

            skeleton.SetBoneRest(idx, rest);
        }
    }

    private bool _boneRestNaNLogged;

    /// <summary>The world Rest transform of <paramref name="boneIdx"/> WITH its own SL-accurate
    /// scale injected — Godot's native <see cref="ComputeGlobalRestTransform"/> composes only
    /// position/rotation (see ApplyShape's doc comment for why); this adds back exactly the one
    /// thing SL's own joint would have in its world matrix: this bone's OWN scale, applied to its
    /// own basis, never compounded with any ancestor's.</summary>
    private static Transform3D ComputeSlAccurateGlobalRest(Skeleton3D skeleton, int boneIdx, AvatarVisual visual)
    {
        var t = ComputeGlobalRestTransform(skeleton, boneIdx);
        if (visual.BoneOwnScale.TryGetValue(skeleton.GetBoneName(boneIdx), out var s))
            t.Basis = t.Basis.Scaled(new Godot.Vector3(s.X, s.Z, s.Y));
        return t;
    }

    /// <summary>Pre-multiplies a scale-only transform into <paramref name="bind"/> so that Godot's
    /// own per-frame skinning (which always computes <c>livePose * bind</c>, using a livePose that
    /// — per ApplyShape's design — carries only rotation/position, never a bone's own scale) ends
    /// up computing exactly <c>livePose * Diag(ownScale) * bind</c>: the one thing SL's joint world
    /// matrix has that ours doesn't. Pose-independent (verified algebraically: for W = the
    /// no-scale live pose and W' = W with ownScale injected into its own basis,
    /// W.AffineInverse()*W' reduces to a pure Diag(ownScale) transform — the shared rotation and
    /// position cancel out), so this only needs to run once, not every frame.</summary>
    private static Transform3D InjectOwnScale(Transform3D bind, System.Numerics.Vector3 ownScaleSl)
    {
        var scaleXform = new Transform3D(Basis.Identity.Scaled(new Godot.Vector3(ownScaleSl.X, ownScaleSl.Z, ownScaleSl.Y)), Vector3.Zero);
        return scaleXform * bind;
    }

    private async System.Threading.Tasks.Task LoadAndApplyTextureAsync(AvatarVisual visual, int bakeIndex, Guid textureId)
    {
        if (_assetService == null || _gpuCache == null) return;

        // initialRefCount: 1 -- pins this bake texture so GpuCache.EvictIfNeeded can never select
        // it (RefCount<=0 is the eviction condition), unlike ObjectRenderer (which properly
        // AddRef/ReleaseRefs per-visual via SetTexturesForVisual). AvatarRenderer has no
        // equivalent per-avatar ref-counting yet, so an unpinned (RefCount 0) bake texture was
        // eligible for eviction the moment the shared cache went over its 1.5 GB budget -- e.g.
        // right after a teleport, when the new region's terrain/objects/other-avatar textures
        // arrive in a burst. Since GpuCache disposes an evicted Resource's native RID immediately
        // (not just drops it from the cache dict), evicting a bake texture still assigned to a
        // LIVE MeshInstance3D's material destroyed it out from under the renderer -- exactly the
        // "RenderingServer::get_singleton() is null" error reported right after teleporting.
        // Trade-off: pinned avatar textures are never reclaimed for the app's lifetime (a slow,
        // bounded-by-avatars-seen leak) rather than a real dispose-tracked lifecycle; safe default
        // until AvatarRenderer gets proper AddRef/ReleaseRef bookkeeping like ObjectRenderer's.
        // bakeChannel: bakeIndex -- BUG-AVATAR-02: a bake texture needs SL's dedicated
        // bake-texture host, not the generic per-face fetch every other texture uses. See
        // GridSession.FetchBakeTextureDataAsync's doc comment for why. bakeAgentId: this visual's
        // own avatar -- the CDN URL keys on the WEARING avatar, so a remote avatar's system bake
        // 403s if we send our own id (BUG-AVATAR-02 follow-up, found live on Agni 2026-09-02).
        var godotTexture = await _gpuCache.GetOrUploadTextureAsync(textureId, _assetService, generateMipmaps: true, initialRefCount: 1, rejectDegraded: true, bakeChannel: bakeIndex, bakeAgentId: visual.AgentId);

        if (godotTexture == null)
        {
            // Not silent: unlike BuildFaceMaterialAsync's equivalent guard, this used to fail
            // quietly, leaving whichever body part this bake targets on its construction-time
            // placeholder material (opaque, no alpha) -- indistinguishable at a glance from "the
            // bake never arrived yet" but actually a decode/fetch failure that will never retry.
            GD.PrintErr($"[AvatarRenderer] bake {bakeIndex} texture {textureId} fetch/decode returned null -- part stays on placeholder material");
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

        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => {
            if (visual.Root == null || !IsInstanceValid(visual.Root)) return;

            var targets = partsForBake != null
                ? partsForBake.Select(n => visual.Parts.TryGetValue(n, out var m) ? m : null)
                              .Where(m => m != null)
                              .Cast<MeshInstance3D>()
                : visual.Parts.Values.Cast<MeshInstance3D>();

            var targetNames = string.Join(", ", targets.Select(m => m.Name));
            // GD.Print($"[AvatarRenderer] Applying bake {bakeIndex} (ID: {textureId}) to meshes: {targetNames}");

            foreach (var meshInstance in targets)
            {
                if (!IsInstanceValid(meshInstance)) continue;
                // FEAT-RENDER-01 Phase 3: the system bake runs on the shader family's Avatar
                // surface, which carries the cull_disabled this path used to set explicitly.
                var mat = meshInstance.MaterialOverride as ShaderMaterial;
                if (mat == null)
                {
                    mat = new ShaderMaterial();
                    meshInstance.MaterialOverride = mat;
                }
                mat.SetShaderParameter(PrimShaderFamily.AlbedoTexture, godotTexture);
                mat.SetShaderParameter(PrimShaderFamily.HasAlbedoTexture, true);
                mat.SetShaderParameter(PrimShaderFamily.AlbedoColor, Godot.Colors.White); // Reset placeholder tint!

                // SL avatars use baked alpha to hide the system body when wearing mesh bodies/
                // clothing (alpha-layer wearables painted by the user, composited server-side
                // into this bake's alpha channel; hidden RGB is conventionally black). Always
                // enable AlphaHash — never gate on DetectAlpha() to decide whether to apply one
                // at all, which is unreliable (see ApplyAlphaCutout below and the
                // godot-material-transparency-gotchas note).
                //
                // Unconditional AlphaHash here, NOT the Blend-vs-Hash choice ApplyAlphaCutout
                // uses for worn-mesh-attachment faces (2026-07-22, reverted after this system-
                // bake path briefly used that same Blend branch and the system body/head reappeared
                // fully opaque, replacing an "invisible bald head + patchy chest" for a visible
                // one — plausible mechanism: `Transparency.Alpha` never writes depth in Godot even
                // at high alpha, unlike AlphaHash/Scissor's discard-based opaque-queue depth write,
                // so this material's draw-order relative to the enclosing mesh body/head — both now
                // competing in the transparent queue — stopped being depth-test-safe). This bake's
                // ENTIRE job is to disappear; a dithered discard pattern on something that's
                // ~90%+ alpha=0 anyway costs nothing visually, so there's no tradeoff here worth
                // risking reliability for — unlike hair, which is actually meant to be seen.
                //
                // MIGRATION NOTE (Phase 3): what runs here is AlphaScissor at 0.5, and the comment
                // block above still describes AlphaHash. That disagreement is REAL and deliberately
                // parked -- 175d308 switched the code and said so in its own message, because
                // AlphaScissor(0.5) is the state the avatar was last visually confirmed correct in,
                // while the documented objection (a hard 0.5 cutoff blotches the soft gradients SL's
                // alpha-layer wearables paint into the bake) still stands unanswered. This migration
                // carries the CODE across unchanged, not the comment. Settle it with a live A/B, not
                // by picking whichever of the two reads more convincingly.
                mat.Shader = PrimShaderFamily.Select(PrimShaderFamily.Kind.Scissor, PrimShaderFamily.Surface.Avatar);
                mat.SetShaderParameter(PrimShaderFamily.AlphaScissorThreshold, 0.5f);
                // alpha_to_coverage is baked into the scissor variant's render_mode. MSAA 4x is on
                // project-wide (app/project.godot) specifically so this edge resolves smoothly.
            }
        }, label: "avatar.bake");
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

        // HUD points (31–38) are screen-space overlays with no world bone — route them to the
        // dedicated orthographic HUD overlay instead of the skeleton path below (which would
        // render the HUD mesh as a small object floating in 3D world space on the avatar).
        if (AttachmentPointMap.IsHudPoint(attachment.AttachmentPoint))
        {
            UpdateHudAttachment(entityId, attachment, prim, transform);
            return;
        }

        // Entity moved from a HUD point back to a body point — drop its screen-overlay visuals.
        if (_hudNodes.TryGetValue(entityId, out var staleHud))
        {
            staleHud.QueueFree();
            _hudNodes.Remove(entityId);
            _hudPlacements.Remove(entityId);
            _hudContent.Remove(entityId);
            _hudTriangles.Remove(entityId);
        }

        // The attachment-point bone only matters for STATIC attachments. A rigged mesh
        // (mesh body, mesh clothing) carries its own skin weights and ignores the point — so
        // never drop a mesh attachment just because its point is an unmapped BODY slot.
        var boneName = AttachmentPointMap.GetBoneName(attachment.AttachmentPoint);
        bool isMeshAttachment = prim is { IsMesh: true } && prim.MeshId != Guid.Empty;
        if (boneName == null && !isMeshAttachment) return;
        boneName ??= "mPelvis";

        Logger.Debug($"[Attachment] pt {attachment.AttachmentPoint} bone {boneName} " +
                 $"mesh {(isMeshAttachment ? prim!.MeshId.ToString() : "no")} entity {entityId:N}");

        // Avatar must already be rendered.
        if (!_visuals.TryGetValue(attachment.AvatarEntityId, out var avatarVisual)) return;
        if (avatarVisual.Skeleton == null) return;

        var defaultFace = isMeshAttachment
            ? new FaceTexture(prim!.TextureId, prim.RenderMaterialId, prim.LegacyMaterialId, prim.ColorTint, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f, Fullbright: prim.Fullbright)
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

        // Re-use existing node or create a new BoneAttachment3D on the avatar skeleton. The cached
        // node can be a stale reference to one freed with a torn-down skeleton (relog, region
        // change, or -- BUG-NET-03 -- neighbor avatar updates churning the local agent entity):
        // _attachmentNodes is not cleared when the skeleton goes, so a later call here (a queued
        // CallDeferred, or a fresh update) would hit GetChildren() on a disposed BoneAttachment3D
        // (ObjectDisposedException). Validate before reuse and rebuild if it's gone.
        if (!_attachmentNodes.TryGetValue(entityId, out var boneAttach) || !GodotObject.IsInstanceValid(boneAttach))
        {
            boneAttach = new BoneAttachment3D { Name = $"WornItem_{entityId:N}" };
            int boneIdx = avatarVisual.Skeleton.FindBone(boneName);
            if (boneIdx >= 0) boneAttach.BoneIdx = boneIdx;
            boneAttach.BoneName = boneName;
            avatarVisual.Skeleton.AddChild(boneAttach);
            _attachmentNodes[entityId] = boneAttach;
        }

        boneAttach.SetMeta("AttachPoint", (int)attachment.AttachmentPoint);
        boneAttach.SetMeta("BoneName", boneName);

        // Clear previous static attachment visuals
        foreach (var child in boneAttach.GetChildren())
        {
            child.QueueFree();
        }

        // An SL attachment point is not the joint's origin: it carries its own offset and
        // rotation on that joint (Skull = 0.15 m above mHead, turned 90 degrees; see
        // AttachmentPointMap's doc comment). BoneAttachment3D can't hold that itself — it
        // overwrites its own transform from the bone pose every frame — so the offset lives on
        // this intermediate node, and all worn visuals hang off it instead of off the bone
        // directly. Rebuilt each time because the loop above frees every child.
        var pointNode = new Node3D { Name = "PointOffset" };
        if (AttachmentPointMap.GetPoint(attachment.AttachmentPoint) is { } apPoint)
        {
            // [FEAT-RENDER-05] The offset is a JOINT-LOCAL position, so SL's joint rule applies
            // to it exactly as it does to every bone in ApplyShape: LLXformMatrix::update scales
            // a local position by the PARENT's scale (mWorldPosition.scaleVec(parentScale)).
            // ApplyShape already does this for the skeleton (`slPos *= parentScale`); the
            // attachment point did not, so it sat at a fixed 0.15 m above mHead no matter how big
            // the head actually is. On an avatar whose mHead own scale is 1.1 that is 1.5 cm too
            // low -- prim hair (which cannot follow head morphs in ANY viewer) sinks into the
            // skull by that much and the scalp comes through at the crown. The error grows with
            // head size, which is why it reads as "the head is too big for the hair".
            var apPos = apPoint.Position;
            if (avatarVisual.BoneOwnScale.TryGetValue(boneName, out var jointScale))
                apPos *= jointScale;
            pointNode.Position = new Godot.Vector3(apPos.X, apPos.Z, -apPos.Y);
            pointNode.Basis = SkeletonBuilder.SlEulerDegToGodotBasis(apPoint.RotationDeg);
        }
        boneAttach.AddChild(pointNode);
        
        // Clear previous rigged attachment visuals
        if (_riggedAttachments.TryGetValue(entityId, out var oldRigged))
        {
            oldRigged.QueueFree();
            _riggedAttachments.Remove(entityId);
        }
        // Same item being re-rezzed with a NEW mesh id at this attachment point (e.g. an
        // applier swapping mesh assets, not just face textures): the OLD mesh's pelvis fixup
        // must go too, or a removed/replaced fitted item leaves the avatar's height shifted
        // forever (viewer parity: removeAttachmentOverridesForObject on detach).
        if (_attachmentMeshIds.TryGetValue(entityId, out var priorMeshInfo))
            avatarVisual.PelvisFixups.Remove(priorMeshInfo.MeshId);

        // Kick off mesh/texture load for mesh attachments.
        if (prim != null)
        {
            if (prim.IsMesh && prim.MeshId != Guid.Empty)
            {
                // Set BEFORE kicking off the async load (not after it completes) so a second
                // redundant UpdateAttachment arriving while this load is still in flight sees
                // it immediately and hits the early-return guard above instead of starting an
                // overlapping duplicate load.
                _attachmentMeshIds[entityId] = (prim.MeshId, prim.Faces, defaultFace, attachment.AvatarEntityId);
                Logger.Debug($"[Attachment] REQUESTING MESH {prim.MeshId} for entity {entityId}");
                _ = LoadAndApplyAttachmentMeshAsync(pointNode, avatarVisual, prim.MeshId,
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
                // Prim or sculpt attachment: build its REAL geometry, the same way ObjectRenderer
                // already does for world objects. This used to draw a solid BoxMesh placeholder,
                // which on sculpt-prim content (classic SL hair especially) looked like a cluster
                // of broken white shards around the head — see LoadAndApplyPrimAttachmentAsync.
                _ = LoadAndApplyPrimAttachmentAsync(pointNode, avatarVisual, prim,
                    prim.Faces, defaultFace,
                    new System.Numerics.Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z),
                    transform != null ? transform.Position : System.Numerics.Vector3.Zero,
                    transform != null ? transform.Rotation : System.Numerics.Quaternion.Identity,
                    entityId);
            }
        }
    }

    /// <summary>Fetches and applies a rigged/static MESH attachment (an item with a real LLMesh
    /// asset). Prim- and sculpt-based attachments go through
    /// <see cref="LoadAndApplyPrimAttachmentAsync"/> instead — both end in the same
    /// <see cref="ApplyAttachmentMeshDataAsync"/>, since all three geometry sources produce the
    /// same neutral <see cref="MeshData"/>.</summary>
    private async System.Threading.Tasks.Task LoadAndApplyAttachmentMeshAsync(
        Node3D attachParent, AvatarVisual avatarVisual, Guid meshId,
        FaceTexture[]? faces, FaceTexture defaultFace, System.Numerics.Vector3 slScale,
        System.Numerics.Vector3 slPos, System.Numerics.Quaternion slRot, Guid entityId)
    {
        if (_assetService == null) return;

        var meshData = await _assetService.GetMeshAsync(meshId).ConfigureAwait(false);
        if (meshData == null)
        {
            Logger.Warn($"[Attachment] mesh {meshId} failed to fetch/decode — skipped");
            return;
        }
        Logger.Debug($"[Attachment] mesh {meshId}: {meshData.Submeshes.Count} submeshes, rigged={meshData.Skin != null}");
        ApplyAttachmentMeshDataAsync(meshData, attachParent, avatarVisual, meshId,
            faces, defaultFace, slScale, slPos, slRot, entityId);
    }

    /// <summary>Builds the geometry for a PRIM or SCULPT attachment — the classic, non-LLMesh
    /// content that a great deal of worn SL content (notably older hair, which is typically a
    /// cluster of sculpted prims) is still made of.
    ///
    /// Until this existed, <see cref="UpdateAttachment"/> drew such attachments as a solid
    /// <c>BoxMesh</c> placeholder tinted with the prim's colour. On a sculpt-prim hairstyle that
    /// renders as a cluster of flat, untextured white boxes clumped around the head — which reads
    /// as "the hair mesh exploded into shards" rather than "this content type isn't implemented",
    /// and is invisible to every mesh/texture/skinning diagnostic because none of that code ever
    /// runs for it. The geometry pipeline itself was already there and already used for world
    /// objects (see ObjectRenderer's sculpt/procedural-prim branches); it just was never wired
    /// into the attachment path.</summary>
    private async System.Threading.Tasks.Task LoadAndApplyPrimAttachmentAsync(
        Node3D attachParent, AvatarVisual avatarVisual, PrimitiveComponent prim,
        FaceTexture[]? faces, FaceTexture defaultFace, System.Numerics.Vector3 slScale,
        System.Numerics.Vector3 slPos, System.Numerics.Quaternion slRot, Guid entityId)
    {
        if (_assetService == null) return;

        MeshData? meshData;
        if (prim.IsSculpt && prim.SculptId != Guid.Empty)
        {
            meshData = await _assetService.GetSculptMeshAsync(prim.SculptId, prim.SculptType).ConfigureAwait(false);
            if (meshData == null)
            {
                Logger.Warn($"[Attachment] sculpt {prim.SculptId} failed to fetch/decode — skipped");
                return;
            }
        }
        else
        {
            meshData = await System.Threading.Tasks.Task.Run(
                () => SLNG.Assets.PrimMeshService.Generate(prim.Shape)).ConfigureAwait(false);
            if (meshData == null)
            {
                Logger.Warn($"[Attachment] prim shape for entity {entityId} failed to mesh — skipped");
                return;
            }
        }

        Logger.Debug($"[Attachment] prim/sculpt entity {entityId}: {meshData.Submeshes.Count} submeshes, sculpt={prim.IsSculpt}");
        ApplyAttachmentMeshDataAsync(meshData, attachParent, avatarVisual, Guid.Empty,
            faces, defaultFace, slScale, slPos, slRot, entityId);
    }

    /// <summary>Shared tail of both attachment paths: turns already-obtained
    /// <paramref name="meshData"/> into a scene node — skinned to the avatar skeleton when it
    /// carries skin data, otherwise bolted statically to its attachment bone.</summary>
    private void ApplyAttachmentMeshDataAsync(
        MeshData meshData, Node3D attachParent, AvatarVisual avatarVisual, Guid meshId,
        FaceTexture[]? faces, FaceTexture defaultFace, System.Numerics.Vector3 slScale,
        System.Numerics.Vector3 slPos, System.Numerics.Quaternion slRot, Guid entityId)
    {
        var skeleton = avatarVisual.Skeleton;

        // Rigged / fitted mesh (worn mesh bodies and clothing) carries skin data: skin it to
        // the avatar skeleton so it deforms and animates with the body, instead of bolting it
        // statically to one bone (which collapses it into a blob).
        if (meshData.Skin != null && skeleton != null)
        {
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () =>
            {
                if (!IsInstanceValid(skeleton)) return;
                // The mesh may be rigged to shifted joint positions (mesh bodies/heads).
                // Apply its joint-position overrides to the skeleton BEFORE binding, like the
                // viewer does, so invBind·jointWorld cancels at the intended pose.
                ApplyJointPositionOverrides(avatarVisual, skeleton, meshData.Skin, meshId);
                var mi = BuildRiggedMeshInstance(meshData, skeleton, meshId, avatarVisual, faces, defaultFace, out var faceIndices);
                if (mi == null) return;
                mi.Name = "RiggedMesh";
                
                // Set a generous CustomAabb to prevent Godot from culling the mesh if the bind pose is far away
                mi.CustomAabb = new Aabb(new Godot.Vector3(-4, -4, -4), new Godot.Vector3(8, 8, 8));
                
                skeleton.AddChild(mi);
                _riggedAttachments[entityId] = mi;
                avatarVisual.RiggedAttachments.Add((mi, meshData, meshId));

                // Skin is already assigned on the instance; the skeleton path must be set after
                // the node is in the tree so Godot can resolve and drive the skinning.
                mi.Skeleton = mi.GetPathTo(skeleton);
                RegisterBomAndUpdateVisibility(avatarVisual, mi, faceIndices, faces, defaultFace, meshId);
                _ = ApplyFaceMaterialsAsync(mi, faceIndices, faces, defaultFace, avatarVisual, meshId);
            }, label: "avatar.rig");
            return;
        }

        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () =>
        {
            if (!IsInstanceValid(attachParent)) return;

            var arrayMesh = new ArrayMesh();
            var faceIndices = new List<int>();

            // BUG-RENDER-12: same authored-order surface merging as the rigged path -- see
            // BuildRiggedMeshInstance's submesh loop for the viewer citations and the reasoning.
            // A non-rigged worn attachment (sculpt/prim hair, a mesh hat) reaches Godot's
            // transparent queue exactly the same way, so it has the same reorder problem.
            SurfaceTool? st = null;
            var runFace = default(FaceTexture);
            int runVertexBase = 0;

            void FlushRun()
            {
                if (st == null) return;
                st.GenerateTangents();
                st.Commit(arrayMesh);
                st = null;
            }

            foreach (var sub in meshData.Submeshes)
            {
                if (sub.Indices.Length == 0) continue;

                var subFace = ResolveFaceTexture(faces, defaultFace, sub.FaceIndex);
                if (st == null || !subFace.Equals(runFace))
                {
                    FlushRun();
                    st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    runFace = subFace;
                    runVertexBase = 0;
                    faceIndices.Add(sub.FaceIndex);
                }

                int indexBase = runVertexBase;
                // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front — left
                // uncorrected, every SL-sourced triangle rasterizes as a backface (masked by
                // CullMode.Disabled, needed just to make anything render), and Godot's
                // double-sided handling flips the normal for perceived backfaces, inverting
                // diffuse lighting while leaving shadows (depth-only) unaffected. Confirmed this
                // session via a T-pose + debug shader + a gizmo pointing at the real light
                // direction. Fix: submit each triangle's 3 vertices in reversed order.
                for (int i = 0; i < sub.Positions.Length; i++)
                {
                    var p = sub.Positions[i];
                    var n = sub.Normals[i];
                    var uv = sub.UVs[i];
                    
                    st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
                    st.SetUV(new Godot.Vector2(uv.X, 1.0f - uv.Y));
                    st.AddVertex(new Godot.Vector3(p.X * slScale.X, p.Z * slScale.Z, -p.Y * slScale.Y));
                }

                for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
                {
                    st.AddIndex(indexBase + sub.Indices[t]);
                    st.AddIndex(indexBase + sub.Indices[t + 2]);
                    st.AddIndex(indexBase + sub.Indices[t + 1]);
                }

                runVertexBase += sub.Positions.Length;
            }
            FlushRun();

            var mi = new MeshInstance3D { Name = "AttachMesh", Mesh = arrayMesh };
            mi.Position = new Godot.Vector3(slPos.X, slPos.Z, -slPos.Y);
            mi.Quaternion = new Godot.Quaternion(slRot.X, slRot.Z, -slRot.Y, slRot.W);
            attachParent.AddChild(mi);
            RegisterBomAndUpdateVisibility(avatarVisual, mi, faceIndices.ToArray(), faces, defaultFace, meshId);
            _ = ApplyFaceMaterialsAsync(mi, faceIndices.ToArray(), faces, defaultFace, avatarVisual, meshId);
        }, label: "avatar.attach");
    }

    /// <summary>BUG-RENDER-12: resolves a submesh's SL face record exactly the way
    /// <see cref="ApplyFaceMaterialsAsync"/> does, so "will these two submeshes get the same
    /// material?" can be answered at MESH-BUILD time, before any material exists.
    ///
    /// <see cref="FaceTexture"/> is a <c>readonly record struct</c>, so equality here is full
    /// value equality over every field the material is built from -- texture id, both material
    /// ids, tint, repeats, offsets, rotation, texgen and fullbright. Two faces that compare equal
    /// cannot produce different materials, which is what makes merging them invisible.</summary>
    private static FaceTexture ResolveFaceTexture(FaceTexture[]? faces, FaceTexture defaultFace, int faceIndex) =>
        (faces != null && faceIndex >= 0 && faceIndex < faces.Length) ? faces[faceIndex] : defaultFace;

    /// <summary>Applies one material per mesh surface, picking each surface's SL face texture
    /// (texture id + colour tint) from <paramref name="faces"/> by its face index. Worn items
    /// texture each face independently — many parts are flat colour tints with no texture.
    /// <paramref name="avatarVisual"/> (when the mesh is worn on an avatar) resolves
    /// Bakes-on-Mesh faces to that avatar's server-baked textures.</summary>
    private async System.Threading.Tasks.Task ApplyFaceMaterialsAsync(
        MeshInstance3D mi, int[] faceIndices, FaceTexture[]? faces, FaceTexture defaultFace,
        AvatarVisual? avatarVisual = null, Guid meshId = default)
    {
        if (mi.Mesh is not ArrayMesh am) return;
        int surfaceCount = am.GetSurfaceCount();

        // Prefetch every distinct material this mesh's faces reference, up front, in ONE go.
        // BuildFaceMaterialAsync awaits GetLegacyMaterialAsync / GetMaterialAsync per face, and
        // the face loop below awaits each face in turn -- so on a mesh with many differently-
        // materialled faces the requests arrive >100 ms apart and the 100 ms batch window in
        // AssetService can't coalesce them: ~one single-id RenderMaterials cap POST per face.
        // For two nearby avatars that was ~200 tiny POSTs, saturating the caps rate limiter
        // ("Caps rate limiter queue full") and stalling texture fetches queued behind it (live
        // 2026-09-03). Firing all the ids together lets AssetService send them as one batch, and
        // every per-face await below is then a cache hit.
        if (_assetService != null && faces != null)
        {
            var legacyIds = new System.Collections.Generic.HashSet<Guid>();
            var pbrIds = new System.Collections.Generic.HashSet<Guid>();
            foreach (var f in faces)
            {
                if (f.LegacyMaterialId != Guid.Empty) legacyIds.Add(f.LegacyMaterialId);
                if (f.RenderMaterialId != Guid.Empty) pbrIds.Add(f.RenderMaterialId);
            }
            if (legacyIds.Count + pbrIds.Count > 0)
            {
                var pre = new System.Collections.Generic.List<System.Threading.Tasks.Task>(legacyIds.Count + pbrIds.Count);
                foreach (var id in legacyIds) pre.Add(_assetService.GetLegacyMaterialAsync(id));
                foreach (var id in pbrIds) pre.Add(_assetService.GetMaterialAsync(id));
                try { await System.Threading.Tasks.Task.WhenAll(pre).ConfigureAwait(false); }
                catch { /* a per-id failure is handled again in BuildFaceMaterialAsync */ }
            }
        }

        for (int surf = 0; surf < surfaceCount; surf++)
        {
            int faceIndex = surf < faceIndices.Length ? faceIndices[surf] : 0;
            FaceTexture ft = (faces != null && faceIndex >= 0 && faceIndex < faces.Length)
                ? faces[faceIndex] : defaultFace;

            var material = await BuildFaceMaterialAsync(ft, avatarVisual, meshId, faceIndex).ConfigureAwait(false);
            int s = surf;
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () =>
            {
                if (IsInstanceValid(mi) && s < ((ArrayMesh)mi.Mesh).GetSurfaceCount())
                    mi.SetSurfaceOverrideMaterial(s, material);
            }, label: "avatar.face_material");
        }
    }

    /// <summary>Builds a material for one SL face: optional albedo texture modulated by the
    /// face colour tint, with alpha-cutout when the texture has alpha. Texture decode runs off
    /// the main thread; only the GPU upload is marshalled back.</summary>
    /// <summary>
    /// FEAT-RENDER-01 Phase 3: avatar and worn-attachment faces are built on the same shader
    /// family as world prims, on its TWO-SIDED variants -- these faces ran on
    /// <c>CullMode.Disabled</c> before and the migration is required to be visually identical, so
    /// the cull mode comes across unchanged rather than being "corrected" on the way.
    ///
    /// Transparency is now a shader swap instead of a property, because <c>render_mode</c> is
    /// compile-time; see <see cref="PrimShaderFamily"/>. The alpha DECISIONS below are unchanged
    /// -- every threshold and every branch is the measured behaviour described at
    /// <see cref="ClassifyAlpha"/>, only their expression moved.
    /// </summary>
    private async System.Threading.Tasks.Task<ShaderMaterial> BuildFaceMaterialAsync(
        FaceTexture ft, AvatarVisual? avatarVisual = null, Guid meshId = default, int faceIndex = -1,
        PrimShaderFamily.Surface surface = PrimShaderFamily.Surface.Avatar)
    {
        if (ft.TextureId == new Guid("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903")
            || (ft.Color != default && ft.Color.W <= 0.001f))
        {
            return new ShaderMaterial { Shader = PrimShaderFamily.Hidden };
        }

        var tint = ft.Color == default
            ? new Color(1, 1, 1, 1)
            : new Color(ft.Color.X, ft.Color.Y, ft.Color.Z, ft.Color.W);

        // PrimShaderFamily.Select is the only place that turns a (transparency, surface) pair into
        // a shader, so the alpha decisions below stay a plain Kind and the caller's surface rides
        // along untouched.
        var kind = PrimShaderFamily.Kind.Opaque;
        float scissorThreshold = 0f;

        var material = new ShaderMaterial { Shader = PrimShaderFamily.Select(kind, surface) };
        material.SetShaderParameter(PrimShaderFamily.AlbedoColor, tint);

        // FEAT-RENDER-06: a worn-mesh face can be fullbright too (LLTextureEntry::getFullbright).
        // The HUD surface is already `unshaded`, so this is a no-op there and meaningful only on
        // world-space attachment faces.
        material.SetShaderParameter(PrimShaderFamily.Fullbright, ft.Fullbright);

        // Per-face UV repeats/offsets, same convention as ObjectRenderer's world-prim materials --
        // HUD buttons in particular are classically ONE texture atlas with per-face repeat/offset
        // picking out each icon; without this every face shows the whole atlas.
        material.SetShaderParameter(PrimShaderFamily.UvScale, new Godot.Vector2(ft.RepeatU, ft.RepeatV));

        // Phase 3 acceptance: real per-face rotation on avatar faces. This used to be a ±π
        // approximation -- a half-turn was faked by negating both repeats (a point-mirror, which
        // it genuinely is) and every other angle was logged as unsupported and drawn unrotated,
        // because a StandardMaterial3D's UV transform has scale and offset but no rotation. The
        // shader rotates in the vertex stage, so the raw angle now goes through untouched; see
        // prim_common.gdshaderinc for why it needs no sign correction despite the flipV meshes.
        material.SetShaderParameter(PrimShaderFamily.UvRotation, ft.Rotation);

        // Centered like SL (u' = (u-0.5)*repeat + 0.5 + off), with the 0.5 term folded in so the
        // shader stays a plain multiply-add. NOTE THE MINUS ON V: these meshes are built flipV
        // (v = 1-t) and the texture rows are top-origin too, so v_tex = 1 - t_tex turns the
        // viewer's t_tex = (t-0.5)*magT + 0.5 + offT (llface.cpp:734-756) into
        // v_tex = (v-0.5)*magT + 0.5 - offT -- the flip negates the offset and leaves the scale
        // term alone. Same asymmetry as ObjectRenderer's, see the fuller derivation there.
        material.SetShaderParameter(PrimShaderFamily.UvOffset, new Godot.Vector2(
            0.5f - 0.5f * ft.RepeatU + ft.OffsetU,
            0.5f - 0.5f * ft.RepeatV - ft.OffsetV));

        // SL's per-face colour alpha (LLTextureEntry::getColor()) is a genuine transparency/blend
        // factor, independent of whatever alpha channel the texture image itself carries — many
        // worn items set a face's tint alpha to 0 specifically to hide it while keeping the object
        // structurally attached/rigged. Under StandardMaterial3D this needed Transparency set
        // explicitly or AlbedoColor.A was ignored outright (a "makeup"/decoration mesh meant to be
        // invisible showed up as an extra visible patch); here it needs a variant that writes
        // ALPHA at all, which is the same requirement wearing different clothes.
        bool tintTranslucent = tint.A < 1f;
        if (tintTranslucent)
            kind = PrimShaderFamily.Kind.Blend;

        // Authoritative alpha signal: when this face carries a real glTF PBR material
        // (RenderMaterialID != null, e.g. modern PBR-authored clothing), its own declared
        // alphaMode is a CREATOR-DECLARED fact, not a pixel-content guess — exactly the signal
        // LLDrawPoolAlpha uses to route MASK content through the opaque/depth-writing pass and
        // only true BLEND content through the unsorted/no-depth-write pass (see this session's
        // hair-alpha investigation notes). Skips the pixel-heuristic in ApplyAlphaCutout
        // entirely below when present — no reason to guess when we were told. Legacy
        // no-material content (this hair applier included) has no such record and falls through
        // to that heuristic unchanged.
        bool hasExplicitAlpha = false;
        if (!tintTranslucent && ft.RenderMaterialId != Guid.Empty && _assetService != null)
        {
            var pbrMat = await _assetService.GetMaterialAsync(ft.RenderMaterialId).ConfigureAwait(false);
            if (pbrMat != null)
            {
                hasExplicitAlpha = true;
                switch (pbrMat.AlphaMode)
                {
                    case SLNG.Assets.PbrAlphaMode.Blend:
                        kind = PrimShaderFamily.Kind.Blend;
                        break;
                    case SLNG.Assets.PbrAlphaMode.Mask:
                        // alpha-to-coverage is baked into the scissor variant's render_mode
                        // rather than set per material -- it was on every scissor face here and
                        // on no other, which is exactly the condition for folding it in.
                        kind = PrimShaderFamily.Kind.Scissor;
                        scissorThreshold = pbrMat.AlphaCutoff;
                        break;
                    default:
                        kind = PrimShaderFamily.Kind.Opaque;
                        break;
                }
            }
        }

        // Bakes-on-Mesh: a face carrying one of the IMG_USE_BAKED_* magic ids wants the AVATAR's
        // server-baked texture for that channel, not the magic id itself (which is just a red UV
        // placeholder asset). Viewer: LLViewerObject::getBakedTextureForMagicId. If the bake
        // hasn't arrived yet, leave the face untextured — the bake-arrival hook in UpdateVisual
        // re-runs ApplyFaceMaterialsAsync for registered BoM meshes.
        Guid texId = ft.TextureId;
        bool wasBom = false; int bomIndex = -1;
        if (avatarVisual != null && SLNG.Assets.BakedTextureIds.TryGetBakeIndex(texId, out int bakeIdx))
        {
            wasBom = true; bomIndex = bakeIdx;
            texId = avatarVisual.LoadedTextures.TryGetValue(bakeIdx, out var bakeTexId) ? bakeTexId : Guid.Empty;
            LogBomFace(meshId, faceIndex, bomIndex, texId);
        }

        if (texId == Guid.Empty || _assetService == null || _gpuCache == null)
        {
            // This was the one blind spot in the face-material diagnostics: a face that ends up
            // with no texture id renders as flat AlbedoColor -- pure opaque WHITE for the usual
            // untinted face -- and used to return here without logging anything at all. For hair
            // that is maximally misleading: hair geometry is a bundle of flat quad CARDS that only
            // becomes strands because the alpha texture cuts them, so an untextured hair mesh
            // renders as a spray of solid white rectangles that reads as "the geometry exploded"
            // rather than "the texture is missing". Log it, distinguishing the two ways to get
            // here: an unresolved Bakes-on-Mesh channel (magic id whose avatar bake hasn't
            // arrived) versus a face that genuinely carries no texture id at all.
            Logger.Debug($"[FaceTex] mesh {meshId} face {faceIndex} has no texture -> renders flat AlbedoColor" +
                (wasBom ? $" (Bakes-on-Mesh channel {bomIndex} not resolved yet)" : " (face carries no texture id)"));
            LogHudFace(surface, meshId, faceIndex, ft, tint,
                wasBom ? $"NO TEXTURE (BoM channel {bomIndex} unresolved)" : "NO TEXTURE (face carries no texture id)");
            return FinishFaceMaterial(material, kind, scissorThreshold, surface);
        }

        // initialRefCount: 1 -- see LoadAndApplyTextureAsync's identical call for why (a
        // per-face/attachment texture pinned here is just as capable of being live on a
        // MeshInstance3D as a bake, so it needs the same eviction-immunity).
        //
        // rejectDegraded is NOT unconditional, and that distinction is the whole reason worn HUDs
        // rendered as solid black rectangles (2026-08-27). AssetService marks EVERY CoreJ2K-fallback
        // decode degraded -- including a truncated codestream that decoded perfectly well, which is
        // the normal state of many OpenSim texture assets: measured on this machine, five HUD
        // textures arrived as 600 bytes (exactly the SL protocol's FIRST_PACKET_SIZE) whose first
        // tile-part declares ~17 kB, Magick refused them, and CoreJ2K decoded all of them cleanly
        // at their declared 256x256. Rejecting that result threw away a usable image and left the
        // face untextured, so the prim's own dark tint was all that remained.
        //
        // Strictness stays where it was earned: an avatar BAKE (LoadAndApplyTextureAsync) and
        // sculpt maps, where a gap-filled decode is speckle noise on skin / corrupted vertex
        // positions -- both worse than nothing. A HUD is an unshaded overlay with no bake or
        // geometry semantics, and a soft button beats an invisible one. It is also what the real
        // viewer does: KDU decodes a truncated progressive codestream on purpose, which is exactly
        // why Firestorm shows these same HUDs while we did not.
        bool rejectDegraded = surface != PrimShaderFamily.Surface.Hud;
        // BUG-AVATAR-02: a BoM face's texId IS a bake channel's texture (resolved just above from
        // the magic id) -- fetch it through SL's dedicated bake-texture host, same as the system
        // mesh path (LoadAndApplyTextureAsync). See GridSession.FetchBakeTextureDataAsync's doc
        // comment for why the generic per-face path 403s every one of these. bakeAgentId is the
        // avatar wearing the mesh (avatarVisual) -- the CDN URL path keys on it, so an id other
        // than the wearer's 403s (BUG-AVATAR-02 follow-up: every other mesh body was untextured
        // because we always sent our own id, found live on Agni 2026-09-02).
        var built = await _gpuCache.GetOrUploadTextureAsync(texId, _assetService, generateMipmaps: true, initialRefCount: 1, rejectDegraded: rejectDegraded, bakeChannel: wasBom ? bomIndex : null, bakeAgentId: wasBom && avatarVisual != null ? avatarVisual.AgentId : default).ConfigureAwait(false);
        if (built == null)
        {
            // Not silent: an untextured face renders as flat AlbedoColor (usually white), which
            // is visually indistinguishable from a face-index mapping bug — that ambiguity cost a
            // whole diagnostic round on the HUD-texture investigation. One line per failed id.
            GD.PrintErr($"[FaceTex] texture {texId} fetch/decode returned null — face renders untextured" +
                (rejectDegraded ? " (degraded decodes refused for this surface)" : ""));
            LogHudFace(surface, meshId, faceIndex, ft, tint, $"NULL from fetch/decode (tex {texId})");

            // This face references a REAL texture id (texId != Empty above) that hasn't loaded --
            // still fetching, or permanently 403 (BUG-RENDER-10). Neither of the obvious fallbacks
            // is acceptable on an avatar mesh: a Blend-from-tint face is a translucent DOUBLE-SIDED
            // card (the Avatar surface shader is cull_disabled) that sorts wrong against its
            // neighbours ("hair inside-out"), and forcing Opaque (tried v0.20.42) turns every
            // slow-loading hair/clothing card into a blocky solid patch across other avatars.
            // Render it INVISIBLE instead: when the texture arrives, ApplyFaceMaterialsAsync
            // re-runs and the face appears correctly; for a permanently-denied texture a missing
            // strand beats a block or a ghost. HUD faces keep their own path (a soft button beats
            // an invisible one -- see rejectDegraded comment above).
            if (surface != PrimShaderFamily.Surface.Hud)
            {
                material.SetShaderParameter(PrimShaderFamily.AlbedoColor, new Godot.Color(1f, 1f, 1f, 0f));
                material.SetShaderParameter(PrimShaderFamily.HasAlbedoTexture, false);
                return FinishFaceMaterial(material, PrimShaderFamily.Kind.Blend, scissorThreshold, surface);
            }

            return FinishFaceMaterial(material, kind, scissorThreshold, surface);
        }

        LogHudFace(surface, meshId, faceIndex, ft, tint, $"textured {built.GetWidth()}x{built.GetHeight()}");

        // The sampler and its flag are a PAIR. Setting albedo_texture without has_albedo_texture
        // is not an error -- the shader keeps sampling its default white texture and the face
        // renders as if the texture never arrived.
        material.SetShaderParameter(PrimShaderFamily.AlbedoTexture, built);
        material.SetShaderParameter(PrimShaderFamily.HasAlbedoTexture, true);

        if (!hasExplicitAlpha)
            (kind, scissorThreshold) = ClassifyAlpha(kind, built, texId, wasBom ? bomIndex : -1);

        // BUG-RENDER-09. A Bakes-on-Mesh bake carries the wearer's alpha-layer wearable as a SOFT
        // gradient, and ClassifyAlpha lands such a face on Scissor -- a binary test, which turns
        // the fade into the reported sawtooth ("die Haut über dem Alpha sieht komisch zerrissen
        // aus"), and leaves two overlapping alpha layers with no way to fade into each other.
        //
        // The obvious fix, Kind.Blend, was tried in v0.20.41 and REVERTED in v0.20.46: it also
        // caught BoM HEAD/BODY faces (their bake carries a soft neck-blend alpha, so they classify
        // as Scissor too), and an avatar face is cull_disabled, so dropping depth write let the
        // head's own overlapping faces sort against each other = blocky see-through chunks across
        // the face ("sieht richtig kaputt aus"). Blend is off the table for any BoM face.
        //
        // Kind.Hash was tried next (v0.20.52) and is ALSO wrong -- live screenshot, legs: large
        // axis-aligned rectangular patches of skin dropping out. Godot's hashed alpha is Wyman &
        // McGuire's algorithm: the per-pixel threshold is hash(floor(pix_scale * position)), and
        // pix_scale = 1/(alpha_hash_scale * max screen-space derivative of position). Close up on
        // a large surface that derivative is big, so pix_scale is small and whole BLOCKS of
        // object space share one threshold -- a coarse patchwork, not a fine dither. Tuning
        // alpha_hash_scale only trades block size for per-pixel speckle on skin. Kind.Hash and its
        // two shaders are kept (they are correct, and a dither is right for some content) but
        // nothing routes to them; do not re-point BoM faces at it.
        //
        // What the measurement actually says: a face only reaches Scissor from ClassifyAlpha when
        // fracMid <= 6% and fracClear <= 50% -- i.e. predominantly opaque with a THIN anti-aliased
        // border. There is no wide gradient to dither. The defect is purely WHERE the binary cut
        // falls: at 0.25 it lands in the middle of that border, so the ragged contour sits in
        // visible, near-opaque skin. Moving it down to BomAlphaScissorThreshold puts the same
        // ragged contour where the mask is already ~96% transparent -- nothing left there to look
        // torn -- while keeping the depth write, the shader, and the sort behaviour exactly as
        // they are. alpha_to_coverage (prim_scissor_avatar) still antialiases the remaining edge.
        if (wasBom && kind == PrimShaderFamily.Kind.Scissor)
            scissorThreshold = BomAlphaScissorThreshold;

        return FinishFaceMaterial(material, kind, scissorThreshold, surface);
    }

    /// <summary>One line per worn-HUD face, unconditional (not behind --diag) and deduplicated.
    ///
    /// Every other outcome of a HUD face is either invisible or ambiguous on screen: an untextured
    /// face renders as flat AlbedoColor, so a black-tinted HUD panel whose icon texture never
    /// arrived is pixel-identical to a HUD panel that is simply black -- and the "face carries no
    /// texture id" path logged only through <c>Logger.Debug</c>, which nothing below --diag can
    /// reach. Two rounds of this investigation were spent unable to tell those apart. A worn HUD
    /// has a handful of faces and they are rebuilt only when their content changes, so the cost of
    /// saying it out loud is a handful of lines per session.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _hudFaceLogged = new();

    private static readonly System.Collections.Generic.HashSet<string> _bomFaceLogged = new();

    /// <summary>FEAT-AVATAR-01: reports every Bakes-on-Mesh face and whether its channel actually
    /// resolved to an avatar bake. Unconditional (not behind --diag) and deduplicated per
    /// mesh/face/outcome, so it costs a handful of lines per session.
    ///
    /// It exists because "the mesh head renders grey" has two completely different causes that look
    /// identical on screen: the face never carried a BoM magic id at all (so there is nothing to
    /// resolve), or it did and the avatar's bake for that channel has not arrived. The existing
    /// [FaceTex] line covers only the second and is Logger.Debug, i.e. unreachable without --diag —
    /// the same blind spot that cost two diagnostic rounds on FEAT-RENDER-06's black HUDs.</summary>
    private static void LogBomFace(Guid meshId, int faceIndex, int bakeIndex, Guid resolved)
    {
        if (!Diagnostics.Enabled) return;
        string outcome = resolved == Guid.Empty ? "UNRESOLVED" : resolved.ToString("N")[..8];
        string key = $"{meshId:N}:{faceIndex}:{bakeIndex}:{outcome}";
        lock (_bomFaceLogged)
        {
            if (!_bomFaceLogged.Add(key)) return;
        }

        GD.Print($"[BomFace] mesh={meshId.ToString("N")[..8]} face={faceIndex} " +
            $"channel={bakeIndex} -> {(resolved == Guid.Empty ? "UNRESOLVED (avatar bake not loaded)" : "bake " + outcome)}");
    }

    private static void LogHudFace(PrimShaderFamily.Surface surface, Guid meshId, int faceIndex,
        FaceTexture ft, Color tint, string outcome)
    {
        if (surface != PrimShaderFamily.Surface.Hud) return;

        string key = $"{meshId:N}:{faceIndex}:{ft.TextureId:N}:{outcome}";
        lock (_hudFaceLogged)
        {
            if (!_hudFaceLogged.Add(key)) return;
        }

        string tex = ft.TextureId == Guid.Empty ? "(none)" : ft.TextureId.ToString()[..8];
        if (!Diagnostics.Enabled) return;
        GD.Print($"[HudFace] mesh={meshId.ToString("N")[..8]} face={faceIndex} tex={tex} " +
            $"tint=({tint.R:0.##},{tint.G:0.##},{tint.B:0.##},{tint.A:0.##}) " +
            $"fullbright={ft.Fullbright} -> {outcome}");
    }

    /// <summary>Applies the variant choice. Split out because the four exit paths of
    /// <see cref="BuildFaceMaterialAsync"/> must all go through it -- an early return that skipped
    /// it would leave a translucent face on the opaque shader, which renders it fully solid with
    /// no error anywhere.</summary>
    private static ShaderMaterial FinishFaceMaterial(ShaderMaterial material, PrimShaderFamily.Kind kind,
        float scissorThreshold, PrimShaderFamily.Surface surface)
    {
        material.Shader = PrimShaderFamily.Select(kind, surface);
        if (kind == PrimShaderFamily.Kind.Scissor)
            material.SetShaderParameter(PrimShaderFamily.AlphaScissorThreshold, scissorThreshold);
        // BUG-RENDER-09: Kind.Hash carries no threshold -- the dither's probability IS the pixel's
        // alpha. 1.0 is Godot's own default noise scale; it only tunes grain, never the cutoff.
        // Set explicitly rather than relying on the uniform default, because a material reaching
        // Hash may have been built for another Kind first and shader parameters survive the swap.
        else if (kind == PrimShaderFamily.Kind.Hash)
            material.SetShaderParameter(PrimShaderFamily.AlphaHashScale, 1.0f);
        // A Hud face asking for Hash falls back to the Scissor variant (see PrimShaderFamily.Select),
        // which then needs its threshold set or it keeps whatever the uniform default is.
        if (kind == PrimShaderFamily.Kind.Hash && surface == PrimShaderFamily.Surface.Hud)
            material.SetShaderParameter(PrimShaderFamily.AlphaScissorThreshold, scissorThreshold);
        return material;
    }

    // Fallback heuristic for LEGACY no-material content only — every face with a real glTF PBR
    // material (RenderMaterialID present) is decided authoritatively in BuildFaceMaterialAsync
    // from that material's own declared alphaMode and never reaches here (hasExplicitAlpha
    // guard). This function only ever sees a face with NO material record: clothing, mesh
    // body/head, and hair applied as an ordinary (non-BOM) face texture by a HUD applier are all
    // indistinguishable at that point (a non-BOM hair applier just sets a normal texture id on a
    // normal face, same as a shirt) — so classification has to come from the decoded pixels.
    //
    // 2026-07-22 (hair-alpha investigation), three-way split by REAL per-pixel alpha content,
    // verified against ~65 real dumped avatar/attachment face textures on this machine (4
    // confirmed hair variants, several confirmed-opaque controls, plus every other face texture
    // this session's client happened to load):
    //   - No transparent texel anywhere (min==255): fully opaque control textures measured this
    //     session all show avgA=1.00 exactly. Skip transparency entirely — strictly cheaper and
    //     sharper than running an always-passing AlphaHash test for nothing.
    //   - Otherwise, compute fracMid = fraction of pixels with alpha STRICTLY between 16 and 239
    //     (excludes both hard-transparent and hard-opaque texels, so an ordinary hard cutout's
    //     AA-edge sliver doesn't count as "graded"). Real measured data clustered cleanly into
    //     two groups with a wide, empty gap between them: hard-cutout content (boots, gloves,
    //     accessories with an anti-aliased silhouette edge) topped out at fracMid<=0.032; every
    //     genuinely graded-alpha sample (the 4 confirmed hair variants at 0.199-0.254, plus
    //     several other worn parts with the same soft-gradient profile) started at fracMid>=0.101.
    //     GradedThreshold=0.06 sits in the middle of that gap. This is NOT the same as Godot's own
    //     binary DetectAlpha() verdict (kept only for the diagnostic log below) — DetectAlpha()
    //     would call any texture with even one AA-edge pixel "Blend", which is wrong for "hard
    //     cutout with soft edges" and was the proximate cause of the clothing-invisible regression
    //     the first time a Blend/Hash split lived here.
    //   - fracMid > threshold → genuinely graded (hair-like): real Alpha blend, no per-pixel
    //     dithering. Deliberately NOT DepthDrawMode.Always (tried previously; breaks layered hair
    //     cards) — left at Godot's default (no depth write) for true Alpha, same as before.
    //   - Otherwise (hard cutout with an anti-aliased edge): alpha SCISSOR + alpha-to-coverage.
    //     Like AlphaHash this stays in the depth-tested/written opaque queue, so misclassifying
    //     ordinary clothing here can never cause the cross-layer occlusion loss true Alpha did
    //     (see godot-material-transparency-gotchas / avatar-alpha-hash-vs-scissor memory notes) —
    //     but it does NOT dither. AlphaHash decides each pixel by comparing alpha against a
    //     per-pixel noise value, so a partially-transparent edge becomes a random stipple of fully
    //     on/off pixels. On hair that is very visible: the strand tips break into a spray of
    //     speckles with the background showing through the gaps (2026-07-25, reported directly
    //     off a sculpt-hair render). Scissor makes the same binary decision from a fixed
    //     threshold instead, so the silhouette is a clean edge, and alpha-to-coverage (MSAA 4x is
    //     on project-wide) resolves that edge with real coverage-based AA. The real viewer never
    //     dithers either — LLDrawPoolAlpha blends or masks, so this is also the closer parity.
    //     Threshold deliberately well BELOW 0.5: hair strands are mostly low-alpha, and the
    //     memory note above records 0.5 visibly eating soft SL alpha content.
    //
    // 2026-07-25: tried unconditionally forcing AlphaHash for graded/avatar-attachment alpha, on
    // the theory that a multi-piece hairstyle's several overlapping true-Alpha MeshInstance3D
    // pieces sort against each other unpredictably. REVERTED, confirmed live: the "looks like a
    // scarf" artifact was completely unaffected, and AlphaHash's dithering visibly degraded the
    // OTHER (previously correct) hair texture into a grainy/speckled hairline. Whatever causes
    // the scarf artifact, it is NOT cross-instance alpha-blend sort order — do not re-attempt this
    // fix without new evidence.
    //
    // Mip-safety: Image.GetData() returns Godot's raw internal buffer, which for an image that's
    // already had GenerateMipmaps() called on it (both callers do, before this runs) is EVERY mip
    // level concatenated starting at level 0 — verified against Godot 4.7's own core/io/image.cpp.
    // Godot's OWN DetectAlpha() self-corrects for this (it recomputes its scan length via
    // _get_mipmap_offset_and_size(1, ...) to mip 0's byte size before scanning), so it was never
    // actually the source of a bad verdict here — but our manual per-pixel scan below did NOT have
    // that guard before this fix, and blurred/graded alpha values from smaller generated mips could
    // inflate a hard-edged cutout texture's apparent "graded pixel" fraction. Slice to mip 0 only
    // (RGBA8 is uncompressed with no block padding, so mip 0's byte span is exactly
    // width*height*4) to remove that risk regardless of call order.
    private const float GradedAlphaThreshold = 0.06f;

    /// <summary>Alpha cutoff for the hard-cutout branch. Low on purpose — SL hair and lace keep a
    /// lot of detail in the 0.2–0.4 alpha range, and a 0.5 cutoff visibly thins them out.</summary>
    private const float HardCutoutScissorThreshold = 0.25f;

    /// <summary>BUG-RENDER-09. The cut point for a Bakes-on-Mesh face, whose alpha is the wearer's
    /// alpha-layer wearable composited server-side. 0.25 puts the binary contour inside the mask's
    /// soft border, where the skin is still nearly opaque, so the contour reads as a torn/sawtooth
    /// edge (and as venetian-blind banding once alpha_to_coverage steps it). 0.04 puts the same
    /// contour where the mask is already ~96% transparent: the visible silhouette then follows the
    /// mask's own painted shape instead of a threshold crossing. Deliberately not 0 -- a genuinely
    /// clear texel must still be discarded, or the face renders as its full uncut card.</summary>
    private const float BomAlphaScissorThreshold = 0.04f;

    /// <summary>Fraction of fully-transparent texels above which a texture is treated as a cutout
    /// SHEET (hair cards, lace, foliage) rather than a solid surface with a trimmed edge, and so
    /// gets real alpha blending. Measured: the hair in the 2026-07-25 investigation is 97% clear;
    /// ordinary opaque-bodied clothing and skin sit far below half.</summary>
    private const float MostlyClearThreshold = 0.5f;

    /// <summary>
    /// Returns the variant this texture needs, and the scissor threshold that goes with it.
    ///
    /// FEAT-RENDER-01 Phase 3 turned this from a mutator on a StandardMaterial3D into a pure
    /// classifier, because transparency is now a shader swap. Every threshold, every branch and
    /// every measured constant above is unchanged -- this is the same decision, returned instead
    /// of assigned. alpha-to-coverage is no longer set here because it is baked into the scissor
    /// variant's render_mode, which is where it always effectively lived: it was set on every
    /// scissor face and on no other.
    /// </summary>
    /// <summary>Ids already reported by [AvatarAlpha], keyed by id+verdict so a face that later
    /// classifies differently (its bake changed) still gets a line.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _avatarAlphaLogged = new();

    private static (PrimShaderFamily.Kind Kind, float Threshold) LogAlphaVerdict(
        Guid texId, int bomChannel, int min, float fracMid, float fracClear,
        PrimShaderFamily.Kind kind, float threshold)
    {
        // Unconditional (not behind --diag) and deduplicated: two rounds of BUG-RENDER-09 were
        // spent guessing which branch an avatar face took, because nothing said so. A handful of
        // lines per session -- the same trade [BomFace] and [HudFace] already make.
        string key = $"{texId:N}:{kind}";
        lock (_avatarAlphaLogged)
        {
            if (!_avatarAlphaLogged.Add(key)) return (kind, threshold);
        }
        if (!Diagnostics.Enabled) return (kind, threshold);
        GD.Print($"[AvatarAlpha] {texId.ToString()[..8]} bom={(bomChannel >= 0 ? bomChannel.ToString() : "-")} " +
                 $"minA={min} fracMid={fracMid:0.###} fracClear={fracClear:0.###} -> {kind}" +
                 (kind == PrimShaderFamily.Kind.Scissor ? $" @{threshold:0.##}" : ""));
        return (kind, threshold);
    }

    private static (PrimShaderFamily.Kind Kind, float Threshold) ClassifyAlpha(
        PrimShaderFamily.Kind current, ImageTexture tex, Guid texId = default, int bomChannel = -1)
    {
        // A face already routed to blending -- by a translucent per-face tint -- is not
        // reconsidered from pixel content. Same guard as the old `Transparency == Alpha` return.
        if (current == PrimShaderFamily.Kind.Blend) return (current, 0f);

        var img = tex.GetImage();
        if (img == null)
            return (PrimShaderFamily.Kind.Scissor, HardCutoutScissorThreshold);


        var data = img.GetData();
        int w = img.GetWidth(), h = img.GetHeight();
        int mip0Bytes = Math.Min(data.Length, w * h * 4);
        int pixelCount = mip0Bytes / 4;

        int min = 255; int midCount = 0; int clearCount = 0;
        for (int i = 3; i < mip0Bytes; i += 4)
        {
            byte a = data[i];
            if (a < min) min = a;
            if (a > 16 && a < 239) midCount++;
            if (a <= 16) clearCount++;
        }
        float fracMid = pixelCount > 0 ? (float)midCount / pixelCount : 0f;
        float fracClear = pixelCount > 0 ? (float)clearCount / pixelCount : 0f;

        if (min == 255)
            return LogAlphaVerdict(texId, bomChannel, min, fracMid, fracClear, PrimShaderFamily.Kind.Opaque, 0f);

        // fracMid alone is not enough to recognise content that NEEDS blending. Hair measures
        // only ~1.2% mid-alpha (97% fully clear, 2% fully opaque) and so reads as "hard cutout" —
        // but those few percent of partial texels ARE the soft strand tips, and discarding them
        // turns fine hair into thick, blocky tubes (confirmed side-by-side against Firestorm,
        // 2026-07-25: "die transparenzen fehlen"). What actually distinguishes such an asset is
        // that it is mostly HOLE: a texture whose pixels are predominantly fully transparent is a
        // cutout sheet (hair cards, lace, foliage) whose whole appearance lives in its edges,
        // whereas ordinary clothing/skin is predominantly opaque with a comparatively thin
        // anti-aliased border. Blend the former, keep the cheap depth-correct scissor for the
        // latter — which is also where the historical cross-layer occlusion regression came from,
        // so the risky path stays limited to assets that visibly need it.
        if (fracMid > GradedAlphaThreshold || fracClear > MostlyClearThreshold)
            return LogAlphaVerdict(texId, bomChannel, min, fracMid, fracClear, PrimShaderFamily.Kind.Blend, 0f);

        return LogAlphaVerdict(texId, bomChannel, min, fracMid, fracClear,
            PrimShaderFamily.Kind.Scissor, HardCutoutScissorThreshold);
    }

    /// <summary>Registers a worn mesh's Bakes-on-Mesh usage on its avatar, then recomputes which
    /// system body parts stay hidden — the viewer's LLVOAvatar::updateMeshVisibility.
    /// Main thread only (mutates node visibility).</summary>
    private void RegisterBomAndUpdateVisibility(
        AvatarVisual avatarVisual, MeshInstance3D mi, int[] faceIndices, FaceTexture[]? faces, FaceTexture defaultFace, Guid meshId = default)
    {
        bool usesBom = false;
        var channels = new System.Collections.Generic.SortedSet<int>();
        void Scan(FaceTexture f)
        {
            if (SLNG.Assets.BakedTextureIds.TryGetBakeIndex(f.TextureId, out int ch))
            {
                usesBom = true;
                channels.Add(ch);
            }
        }
        if (faces != null) foreach (var f in faces) Scan(f);
        Scan(defaultFace);

        if (usesBom)
            avatarVisual.BomAttachments.Add((mi, faceIndices, faces, defaultFace));

        // FEAT-AVATAR-01: says whether a worn mesh takes part in Bakes-on-Mesh at all. A mesh head
        // that registers NO channels renders from its own textures (or grey, if it has none) and no
        // amount of fixing the bake pipeline will change it -- a completely different diagnosis from
        // "registered channel 8 but the bake never arrived". Deduplicated via LogBomFace's set.
        string key = $"reg:{meshId:N}:{string.Join(',', channels)}";
        bool announce;
        lock (_bomFaceLogged) { announce = _bomFaceLogged.Add(key); }
        if (announce)
        {
            if (Diagnostics.Enabled) GD.Print($"[BomFace] mesh={meshId.ToString("N")[..8]} registered " +
                (usesBom ? $"BoM channels [{string.Join(", ", channels)}] over {faceIndices.Length} face(s)"
                         : $"NO BoM channels over {faceIndices.Length} face(s) -- renders from its own textures"));

            // FEAT-AVATAR-01: a mesh that registered channels at login and NO channels after an
            // appearance send has had its face textures replaced somewhere between the wire and
            // here -- and which ids it now carries is the difference between "the sim resent it
            // without a texture entry", "something wrote the resolved bake id back over the magic
            // one", and "it genuinely stopped using BoM". Only the ids themselves separate those.
            if (!usesBom)
            {
                var ids = (faces ?? System.Array.Empty<FaceTexture>())
                    .Take(6)
                    .Select(f => f.TextureId == Guid.Empty ? "EMPTY" : f.TextureId.ToString("N")[..8]);
                if (Diagnostics.Enabled) GD.Print($"[BomFace]   face ids: [{string.Join(" ", ids)}]  " +
                    $"default={(defaultFace.TextureId == Guid.Empty ? "EMPTY" : defaultFace.TextureId.ToString("N")[..8])}" +
                    $"  facesArray={(faces == null ? "null" : faces.Length.ToString())}");
            }
        }

        RecomputeMeshVisibility(avatarVisual);
    }

    /// <summary>M4-7: rebuilds <see cref="AvatarVisual.AttachmentBakeChannels"/> from the live
    /// <see cref="AvatarVisual.BomAttachments"/> list and re-applies system-part visibility. This
    /// is the viewer's LLVOAvatar::updateMeshVisibility, which runs on every attachment change —
    /// add AND remove. The set used to be add-only, so taking off a BoM mesh body/head left the
    /// matching system part invisible until relog. Rebuilding from survivors also handles a
    /// body-for-body swap correctly (a channel the new body still uses stays hidden).
    /// Main thread only (mutates node visibility).</summary>
    private void RecomputeMeshVisibility(AvatarVisual avatarVisual)
    {
        avatarVisual.BomAttachments.RemoveAll(e => !IsInstanceValid(e.Mi));

        var ch = avatarVisual.AttachmentBakeChannels;
        ch.Clear();
        foreach (var e in avatarVisual.BomAttachments)
        {
            void Scan(FaceTexture f)
            {
                if (SLNG.Assets.BakedTextureIds.TryGetBakeIndex(f.TextureId, out int b))
                    ch.Add(b);
            }
            if (e.Faces != null) foreach (var f in e.Faces) Scan(f);
            Scan(e.DefaultFace);
        }

        // Hide base parts per consumed channel (head bake also covers the eyelashes part, exactly
        // like MESH_ID_EYELASH in the viewer's updateMeshVisibility).
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

    // -------------------------------------------------------------------------
    // SL HUD attachments (points 31–38) — screen-space orthographic overlay
    // -------------------------------------------------------------------------
    //
    // Verified against secondlife/viewer @ develop (render_hud_attachments() in
    // llviewerdisplay.cpp, mScreen joint setup in llvoavatarself.cpp, anchor table in
    // character/avatar_lad.xml): HUD attachments are genuine 3D meshes hung off a virtual
    // "mScreen" joint and rendered by a dedicated ORTHOGRAPHIC camera pass after the world —
    // not 2D sprites. The ortho view volume is exactly 1 unit tall (±0.5 = top/bottom screen
    // edge) and ±aspect/2 wide; the 8 attachment points are fixed anchors at the volume's
    // corners/edges/center, and the object's own local position stacks on its anchor (runtime-
    // clamped to MAX_ATTACHMENT_DIST = 3.5 in llviewerjointattachment.cpp — NOT the 2.0 that
    // avatar_lad.xml advertises). mScreen carries scale (1, aspect, 1), and per SL's joint rule
    // (see BoneOwnScale) a parent's scale offsets a child's POSITION one level without scaling
    // the child itself — so anchors' horizontal coordinates are aspect-scaled to reach the
    // screen edges while HUD geometry itself stays unstretched. The real viewer also has a HUD
    // zoom (HUDScaleFactor * mHUDTargetZoom) — both default 1.0 and the latter only changes
    // while EDITING a HUD, so v1 omits it.
    //
    // Godot realization: a transparent SubViewport with its OWN World3D (no scene lights/fog —
    // materials are forced Unshaded, which is also how HUDs read in the real viewer: flat,
    // environment-independent), an orthographic Camera3D with Size=1/KeepHeight (exactly SL's
    // volume), composited on a CanvasLayer at Layer=-1: above the 3D world, below the viewer's
    // own root-canvas UI (login/chat) and the Layer=1 position HUD. Mouse events pass through
    // (clicking HUD buttons is not implemented yet — rendering only).
    private SubViewport? _hudViewport;

    private Node3D? _hudRoot;
    // HUD entity id → its Node3D in the overlay, its (point, SL-local offset) placement (kept
    // for aspect-ratio repositioning on window resize), and a content signature mirroring
    // _attachmentMeshIds' duplicate-load guard (see that field's doc comment).
    private readonly struct HudTriangle
    {
        public readonly int FaceIndex;
        public readonly Godot.Vector3 P0;
        public readonly Godot.Vector3 P1;
        public readonly Godot.Vector3 P2;
        public readonly System.Numerics.Vector2 UV0;
        public readonly System.Numerics.Vector2 UV1;
        public readonly System.Numerics.Vector2 UV2;

        public HudTriangle(
            int faceIndex,
            Godot.Vector3 p0, Godot.Vector3 p1, Godot.Vector3 p2,
            System.Numerics.Vector2 uv0, System.Numerics.Vector2 uv1, System.Numerics.Vector2 uv2)
        {
            FaceIndex = faceIndex;
            P0 = p0; P1 = p1; P2 = p2;
            UV0 = uv0; UV1 = uv1; UV2 = uv2;
        }
    }

    private readonly Dictionary<Guid, Node3D> _hudNodes = new();
    private readonly Dictionary<Guid, (byte Point, System.Numerics.Vector3 SlOffset)> _hudPlacements = new();
    private readonly Dictionary<Guid, (object ShapeKey, FaceTexture[]? Faces, FaceTexture DefaultFace)> _hudContent = new();
    private readonly Dictionary<Guid, HudTriangle[]> _hudTriangles = new();

    /// <summary>Anchor offset of one HUD attachment point in SL's HUD frame (X=depth,
    /// Y=left(+)/right(−), Z=up(+)/down(−)) — the exact `position` attributes of points 31–38
    /// in avatar_lad.xml. ±0.5 is exactly the screen edge of the 1-unit-tall HUD volume.</summary>
    private static System.Numerics.Vector3 HudAnchorSl(byte point) => point switch
    {
        32 => new(0f, -0.5f, 0.5f),  // Top Right
        33 => new(0f, 0f, 0.5f),     // Top
        34 => new(0f, 0.5f, 0.5f),   // Top Left
        36 => new(0f, 0.5f, -0.5f),  // Bottom Left
        37 => new(0f, 0f, -0.5f),    // Bottom
        38 => new(0f, -0.5f, -0.5f), // Bottom Right
        _ => System.Numerics.Vector3.Zero, // 31 Center 2, 35 Center
    };

    private void EnsureHudViewport()
    {
        if (_hudViewport != null && IsInstanceValid(_hudViewport)) return;

        var layer = new CanvasLayer { Name = "SlHudLayer", Layer = -1 };
        AddChild(layer);

        var container = new SubViewportContainer
        {
            Name = "SlHudContainer",
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(container);

        // OwnWorld3D gives the viewport "a unique COPY of the World3D defined in world_3d"
        // (Godot docs, confirmed live the hard way): the World3D property itself stays whatever
        // you set it to — null by default (a click's DirectSpaceState query crashed on that), or
        // a template instance whose physics space stays forever EMPTY because the viewport's
        // children actually register in the internal copy, not in it (confirmed live: a
        // hand-verified-correct ray through 345 present bodies missed everything, both before
        // and after force-activating the template's space). Anything that needs the world the
        // viewport ACTUALLY uses — ray queries in TryClickHud above all — must go through
        // Viewport.FindWorld3D(), never the World3D property.
        _hudViewport = new SubViewport { Name = "SlHudViewport", TransparentBg = true, OwnWorld3D = true };
        container.AddChild(_hudViewport);
        // Window resize changes the aspect ratio, which moves every horizontal anchor.
        _hudViewport.SizeChanged += () =>
        {
            foreach (var id in _hudPlacements.Keys) PositionHudNode(id);
        };

        // SL's HUD camera sits on the -X side looking down +X with Z-up (llviewerdisplay.cpp)
        // — under our standard SL→Godot map that is: look down world +X with +Y up, so screen
        // right = world +Z and screen up = world +Y. Content sits near x≈0; ±3.5 m offsets stay
        // comfortably inside Near/Far from x=-4.5.
        _hudViewport.AddChild(new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 1.0f,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Position = new Godot.Vector3(-4.5f, 0f, 0f),
            RotationDegrees = new Godot.Vector3(0f, -90f, 0f),
            Near = 0.01f,
            Far = 20f,
        });

        _hudRoot = new Node3D { Name = "SlHudRoot" };
        _hudViewport.AddChild(_hudRoot);
    }

    private void UpdateHudAttachment(
        Guid entityId, AttachmentComponent attachment, PrimitiveComponent? prim, TransformComponent? transform)
    {
        // Only the local agent's own HUDs. A HUD is a private screen overlay — grids only send
        // HUD objects to their owner anyway, but guard defensively so another avatar's HUD can
        // never paint over our screen.
        var avatarEntity = _world?.GetEntity(attachment.AvatarEntityId);
        if (avatarEntity?.GetComponent<AvatarComponent>()?.IsLocalAgent != true) return;

        // Entity moved from a body point to a HUD point — drop its world-space visuals.
        if (_attachmentNodes.TryGetValue(entityId, out var staleBone))
        {
            staleBone.QueueFree();
            _attachmentNodes.Remove(entityId);
        }
        MeshInstance3D? hudMovedRigged = null;
        if (_riggedAttachments.TryGetValue(entityId, out var staleRigged))
        {
            hudMovedRigged = staleRigged;
            staleRigged.QueueFree();
            _riggedAttachments.Remove(entityId);
        }
        // Moved off the body entirely (onto a HUD point) — same "un-worn" case as detach for
        // pelvis-fixup purposes; revert this mesh's contribution on its (body) owner avatar.
        if (_attachmentMeshIds.TryGetValue(entityId, out var hudMovedMeshInfo)
            && _visuals.TryGetValue(hudMovedMeshInfo.AvatarEntityId, out var hudOwnerVisual))
        {
            hudOwnerVisual.PelvisFixups.Remove(hudMovedMeshInfo.MeshId);
            // M4-7: same as detach — a mesh moved onto a HUD point no longer hides the body.
            if (hudMovedRigged != null) hudOwnerVisual.BomAttachments.RemoveAll(e => e.Mi == hudMovedRigged);
            RecomputeMeshVisibility(hudOwnerVisual);
        }
        _attachmentMeshIds.Remove(entityId);

        EnsureHudViewport();

        if (!_hudNodes.TryGetValue(entityId, out var hudNode) || !IsInstanceValid(hudNode))
        {
            hudNode = new Node3D { Name = $"Hud_{entityId:N}" };
            _hudRoot!.AddChild(hudNode);
            _hudNodes[entityId] = hudNode;
            Logger.Debug($"[HUD] pt {attachment.AttachmentPoint} entity {entityId:N} added to overlay");
        }

        var slOffset = transform?.Position ?? System.Numerics.Vector3.Zero;
        // Viewer parity: LLViewerJointAttachment::clampObjectPosition, MAX_ATTACHMENT_DIST=3.5 (roots only).
        if (transform?.ParentLocalId == 0 && slOffset.Length() > 3.5f)
            slOffset = System.Numerics.Vector3.Normalize(slOffset) * 3.5f;
        _hudPlacements[entityId] = (attachment.AttachmentPoint, slOffset);
        PositionHudNode(entityId);
        if (transform != null)
            hudNode.Quaternion = new Godot.Quaternion(
                transform.Rotation.X, transform.Rotation.Z, -transform.Rotation.Y, transform.Rotation.W);

        if (prim == null) return;

        // Duplicate-load guard, mirroring _attachmentMeshIds (see its doc comment): reload only
        // when the geometry source or any face texture actually changed. ShapeKey is the mesh
        // asset id for mesh HUDs, or the full PrimShape (equatable — ObjectRenderer keys a
        // dictionary with it) for classic prim HUDs.
        var defaultFace = new FaceTexture(
            prim.TextureId, prim.RenderMaterialId, prim.LegacyMaterialId, prim.ColorTint,
            prim.RepeatU, prim.RepeatV, prim.OffsetU, prim.OffsetV, prim.Rotation,
            prim.TexGen, prim.Fullbright);
        object shapeKey = prim.IsMesh && prim.MeshId != Guid.Empty ? prim.MeshId : prim.Shape;
        if (_hudContent.TryGetValue(entityId, out var cur)
            && Equals(cur.ShapeKey, shapeKey)
            && cur.DefaultFace == defaultFace
            && (cur.Faces == prim.Faces || (cur.Faces != null && prim.Faces != null && cur.Faces.SequenceEqual(prim.Faces))))
            return;
        _hudContent[entityId] = (shapeKey, prim.Faces != null ? (FaceTexture[])prim.Faces.Clone() : null, defaultFace);

        _ = LoadHudContentAsync(hudNode, entityId, prim, defaultFace);
    }

    /// <summary>Recomputes one HUD entity's overlay position from its anchor + SL-local offset.
    /// Re-run for every HUD on viewport resize: the anchor's HORIZONTAL coordinate scales with
    /// the aspect ratio (SL's mScreen joint scale (1, aspect, 1) offsetting child positions one
    /// level), while the object's own offset below the anchor does not.</summary>
    private void PositionHudNode(Guid entityId)
    {
        if (_hudViewport == null || !_hudPlacements.TryGetValue(entityId, out var p)) return;
        if (!_hudNodes.TryGetValue(entityId, out var node) || !IsInstanceValid(node)) return;

        var size = _hudViewport.Size;
        float aspect = size.Y > 0 ? (float)size.X / size.Y : 1f;
        var a = HudAnchorSl(p.Point);
        // Standard SL→Godot map (X, Z, −Y): SL Y(left+) → Godot −Z (camera right = +Z world).
        node.Position = new Godot.Vector3(
            a.X + p.SlOffset.X,
            a.Z + p.SlOffset.Z,
            -(a.Y * aspect) - p.SlOffset.Y);
    }

    private async System.Threading.Tasks.Task LoadHudContentAsync(
        Node3D hudNode, Guid entityId, PrimitiveComponent prim, FaceTexture defaultFace)
    {
        if (_assetService == null) return;

        bool isMeshAsset = prim.IsMesh && prim.MeshId != Guid.Empty;
        var faces = prim.Faces;
        var scale = new System.Numerics.Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z);

        var meshData = isMeshAsset
            ? await _assetService.GetMeshAsync(prim.MeshId).ConfigureAwait(false)
            : await _assetService.GetPrimMeshAsync(prim.Shape).ConfigureAwait(false);
        if (meshData == null || meshData.Submeshes.Count == 0)
        {
            GD.PrintErr($"[HUD] geometry failed to load/mesh ({(isMeshAsset ? prim.MeshId.ToString() : "prim shape")}) — skipped");
            return;
        }

        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () =>
        {
            if (!IsInstanceValid(hudNode)) return;
            foreach (var child in hudNode.GetChildren()) child.QueueFree();

            // flipV:true for BOTH mesh assets and prims — MeshFoundry's prim UVs are vertically
            // inverted vs the real viewer (see ObjectRenderer's prim path for the llvolume.cpp
            // verification); mesh assets need the flip too (SL bottom-left origin → Godot top-left).
            var arrayMesh = BuildHudArrayMesh(meshData, flipV: true, scale, out var faceIndices, out var triangles);
            if (arrayMesh.GetSurfaceCount() == 0) return;

            _hudTriangles[entityId] = triangles;

            var mi = new MeshInstance3D { Name = "HudMesh", Mesh = arrayMesh };
            hudNode.AddChild(mi);
            _ = ApplyHudFaceMaterialsAsync(mi, faceIndices, faces, defaultFace);

            // Click detection: one combined trimesh collision shape per HUD prim. Tagged
            // with the owning entity id so TryClickHud can resolve a raycast hit back to a LocalId.
            var body = new StaticBody3D { Name = "HudCollision" };
            body.SetMeta("EntityId", entityId.ToString());
            var shape = new CollisionShape3D { Shape = arrayMesh.CreateTrimeshShape() };
            body.AddChild(shape);
            hudNode.AddChild(body);
        }, label: "avatar.hud_mesh");
    }

    /// <summary>Static (unskinned, unlit) ArrayMesh for HUD content, prim scale baked into the
    /// vertices. UV V-flip only for LLMesh ASSETS (bottom-left origin, like the worn-mesh path);
    /// PrimMesher output already matches Godot's convention (world prims render correctly
    /// without a flip in ObjectRenderer.BuildArrayMesh). No normals/tangents — HUD materials are
    /// forced Unshaded (the overlay's World3D has no lights). Winding is still reversed like
    /// every other SL-mesh builder (see BuildPartMesh) for consistency, though it is visually
    /// moot without lighting.</summary>
    private static ArrayMesh BuildHudArrayMesh(
        MeshData meshData, bool flipV, System.Numerics.Vector3 slScale,
        out int[] faceIndices, out HudTriangle[] triangles)
    {
        var arrayMesh = new ArrayMesh();
        var faceList = new List<int>();
        var triList = new List<HudTriangle>();
        foreach (var sub in meshData.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var p = sub.Positions[i];
                var uv = sub.UVs[i];
                st.SetUV(new Godot.Vector2(uv.X, flipV ? 1.0f - uv.Y : uv.Y));
                st.AddVertex(new Godot.Vector3(p.X * slScale.X, p.Z * slScale.Z, -p.Y * slScale.Y));
            }

            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                int i0 = sub.Indices[t];
                int i1 = sub.Indices[t + 2];
                int i2 = sub.Indices[t + 1];
                st.AddIndex(i0);
                st.AddIndex(i1);
                st.AddIndex(i2);

                var p0 = sub.Positions[i0];
                var p1 = sub.Positions[i1];
                var p2 = sub.Positions[i2];
                triList.Add(new HudTriangle(
                    sub.FaceIndex,
                    new Godot.Vector3(p0.X * slScale.X, p0.Z * slScale.Z, -p0.Y * slScale.Y),
                    new Godot.Vector3(p1.X * slScale.X, p1.Z * slScale.Z, -p1.Y * slScale.Y),
                    new Godot.Vector3(p2.X * slScale.X, p2.Z * slScale.Z, -p2.Y * slScale.Y),
                    sub.UVs[i0],
                    sub.UVs[i1],
                    sub.UVs[i2]));
            }
            st.Commit(arrayMesh);
            faceList.Add(sub.FaceIndex);
        }
        faceIndices = faceList.ToArray();
        triangles = triList.ToArray();
        return arrayMesh;
    }

    /// <summary>Per-surface face materials for a HUD mesh — same resolution as
    /// <see cref="ApplyFaceMaterialsAsync"/> but forced Unshaded: the HUD SubViewport's own
    /// World3D has no lights (shaded materials would render black), and HUDs read as flat/
    /// environment-independent in the real viewer anyway.</summary>
    private async System.Threading.Tasks.Task ApplyHudFaceMaterialsAsync(
        MeshInstance3D mi, int[] faceIndices, FaceTexture[]? faces, FaceTexture defaultFace)
    {
        if (mi.Mesh is not ArrayMesh am) return;
        int surfaceCount = am.GetSurfaceCount();

        for (int surf = 0; surf < surfaceCount; surf++)
        {
            int faceIndex = surf < faceIndices.Length ? faceIndices[surf] : 0;
            FaceTexture ft = (faces != null && faceIndex >= 0 && faceIndex < faces.Length)
                ? faces[faceIndex] : defaultFace;

            // Surface.Hud carries what `ShadingMode = Unshaded` used to say here: unshaded is a
            // render_mode, so it is part of which variant gets compiled rather than a property.
            // faceIndex is passed purely so the [HudFace] diagnostic can name the face; the HUD
            // path has no avatarVisual (no Bakes-on-Mesh) and no mesh id to report.
            var material = await BuildFaceMaterialAsync(ft, faceIndex: faceIndex, surface: PrimShaderFamily.Surface.Hud)
                .ConfigureAwait(false);
            int s = surf;
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () =>
            {
                if (IsInstanceValid(mi) && s < ((ArrayMesh)mi.Mesh).GetSurfaceCount())
                    mi.SetSurfaceOverrideMaterial(s, material);
            }, label: "avatar.hud_material");
        }
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
    // Rigged meshes already reported by the [RiggedSkin] line below — an outfit re-attaches and
    // re-decodes the same asset several times per login, and the facts being reported are a
    // property of the ASSET, so once per mesh id per session is the whole signal.
    private readonly HashSet<Guid> _loggedSkinMeshes = new();

    // Mesh ids already reported by [RenderExtent]. The measurement is a property of the mesh plus
    // the current skeleton, and RebuildRiggedAttachmentSkins re-enters this method for every worn
    // mesh on every shape change — once per id is the signal, the rest is noise.
    private readonly HashSet<Guid> _loggedRenderExtent = new();

    private void ApplyJointPositionOverrides(AvatarVisual visual, Skeleton3D skeleton, SLNG.Assets.MeshSkin skinData, Guid meshId)
    {
        var alt = skinData.AltInverseBindMatrices;
        int jointCount = skinData.JointNames.Length;

        // Ungated, once per mesh: the three facts that decide whether a worn mesh body/head can
        // move this skeleton at all. Only meshes that actually carry alternate bind matrices are
        // reported (plain rigged clothing never does), so a full outfit adds a handful of lines,
        // not one per attachment. Without this, "the fitted body did / did not freeze the shape's
        // bone scaling" is indistinguishable from "that code path was never reached" — which is
        // exactly where BUG-AVATAR-07 stalled once already.
        if (alt is { Length: > 0 } && (visual.IsSelf || Diagnostics.Enabled) && _loggedSkinMeshes.Add(meshId))
        {
            int candidates = 0;
            for (int j = 0; j < jointCount && j < alt.Length; j++)
            {
                if (System.Math.Abs(alt[j].M44 - 1f) > 0.01f) continue;
                string n = _avatarSkeleton?.ResolveBoneName(skinData.JointNames[j]) ?? skinData.JointNames[j];
                var bd = _avatarSkeleton?.GetBone(n);
                if (bd == null) continue;
                var pos = new System.Numerics.Vector3(alt[j].M41, alt[j].M42, alt[j].M43);
                if ((pos - bd.Position).Length() > 0.0001f) candidates++;
            }
            GD.Print($"[RiggedSkin] {(visual.IsSelf ? "SELF" : visual.AgentId.ToString()[..8])} {meshId}: " +
                     $"joints={jointCount} altBinds={alt.Length} " +
                     $"aboveThreshold={candidates} lock_scale_if_joint_position={skinData.LockScaleIfJointPosition}" +
                     (alt.Length != jointCount ? "  -- SKIPPED: altBinds != joints, no overrides applied" : ""));
        }

        // Scale-Lock detection:
        // Meshes declaring LockScaleIfJointPosition: lock all joints influenced by the mesh
        // to default scale 1.0, discarding shape slider distortions for those joints.
        int locked = 0;

        if (skinData.LockScaleIfJointPosition)
        {
            foreach (var n in skinData.JointNames)
            {
                string resolved = _avatarSkeleton?.ResolveBoneName(n) ?? n;
                if (visual.JointScaleLocks.Add(resolved)) locked++;
            }
        }

        // Viewer rule: overrides only count when EVERY joint has one (bindCnt == jointCnt).
        if (alt == null || alt.Length != jointCount)
        {
            if (locked > 0)
            {
                if (_avatarSkeleton != null)
                {
                    ApplyShape(visual, skeleton, _avatarSkeleton, visual.LastDistortions, visual.JointPosOverrides);
                }
                skeleton.ResetBonePoses();
                RebuildRiggedAttachmentSkins(visual);
                RefreshBodyPartSkins(visual);
                RefreshStaticAttachmentOffsets(visual);

                GD.Print($"[ScaleLock] {(visual.IsSelf ? "SELF" : visual.AgentId.ToString()[..8])} mesh {meshId}: " +
                         $"{locked} joint scale(s) locked to skeleton default " +
                         $"(lock_scale: {skinData.LockScaleIfJointPosition}) — " +
                         $"total scale-locked joints: {visual.JointScaleLocks.Count}");

                RecomputeFootOffset(visual, visual.LastDistortions);
                LogAvatarHeight(visual, "scale lock");
            }
            return;
        }

        int applied = 0;
        // True only when this mesh actually MOVED the skeleton (a new/different position override,
        // or a newly locked joint scale). A worn outfit hands the same overrides in again mesh
        // after mesh; re-applying the shape and rebuilding every bind for each of those is pure
        // waste, and skipping it keeps the refresh below affordable.
        bool skeletonChanged = (locked > 0);
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
            var boneDef = _avatarSkeleton?.GetBone(boneName);
            var basePos = boneDef?.Position ?? System.Numerics.Vector3.Zero;
            float delta = (slPos - basePos).Length();
            if (delta <= 0.0001f) continue;

            // Viewer parity (LLVOAvatar::addAttachmentOverridesForObject, indra/newview/
            // llvoavatar.cpp): a skin that sets lock_scale_if_joint_position pins the SCALE of
            // every joint it also position-overrides to pJoint->getDefaultScale(), discarding the
            // shape sliders' skeletal scale distortion there for good (ApplyShape honors this).
            // Recorded BEFORE the root-joint skip below, because that skip is SLNG's own deviation
            // on the POSITION channel only — the viewer's scale lock is keyed purely on this
            // joint having passed the position threshold, which mPelvis can and does. The lock is
            // a property of the JOINT, so a mesh BODY declaring it also freezes the scale a
            // separately-worn mesh HEAD renders at (BUG-AVATAR-07).
            if (skinData.LockScaleIfJointPosition && visual.JointScaleLocks.Add(boneName))
            {
                locked++;
                skeletonChanged = true;
            }

            // The skeleton ROOT (mPelvis — the one bone with no ParentName, see
            // avatar_skeleton.xml) is excluded here, even though the real viewer's
            // addAttachmentOverridesForObject applies no such exclusion and would (per its literal
            // source, verified against scratch/slviewer/indra/newview/llvoavatar.cpp:6776-6804)
            // take the same jointPos = mAlternateBindMatrix[i].getTranslation() at face value.
            // Reason: for every OTHER joint, "local position override" means "relative to this
            // joint's PARENT", which is exactly the "custom fit" a fitted mesh legitimately needs.
            // mPelvis has no parent — its "local position" IS the pelvis's height above the
            // avatar's world anchor, so an override here does not mean "fit this bone to my mesh",
            // it means "relocate the entire avatar's shared Skeleton3D root" (every other bone
            // hangs off mPelvis, and SetBoneRest below is NOT scoped to this one mesh/attachment —
            // it mutates the single Skeleton3D instance the whole avatar body and every other
            // attachment share). A legitimate whole-avatar Z relocation already has its own
            // sanctioned, reversible channel: skinData.PelvisOffset / PelvisFixups below (mirrors
            // LLAvatarAppearance::addPelvisFixup), which every asset seen so far (including this
            // one — PelvisOffset prints 0) sets correctly when it's actually intended. A nonzero
            // mPelvis position override on top of PelvisOffset==0 is not a coherent combination a
            // correctly-authored asset would produce; measured on two real assets (coat
            // 32d87071-617d-e3f4-54e1-e02bfbba6017 and boots 8a6f4154-1907-0b68-aace-978da4d9f85c,
            // both from the same content, both otherwise well-formed — every other joint's delta is
            // sub-centimeter) it was a ~9.6 m shift, 10x the joint's own rest Z with the wrong sign,
            // i.e. garbage relative to the sane small deltas on every sibling joint in the SAME
            // asset. Applying it verbatim would drag the entire shared skeleton (avatar body +
            // every attachment) up by ~9.6 m — not a "shorter than reference" symptom, so this is
            // NOT the cause of the separately-reported height-vs-cube mismatch (confirmed: neither
            // the real viewer's LLAvatarAppearance::computeBodySize() nor our
            // SlJointComposer.ComputeBodySize port ever reads a joint's POSITION override for
            // "mPelvis", only its SCALE, which this override does not touch — see ComputeBodySize's
            // pelvisScaleZ). It is a real, separate hazard worth guarding regardless.
            if (boneDef != null && boneDef.ParentName == null)
            {
                if (Diagnostics.Enabled) GD.Print($"[JointOverride] mesh {meshId}: SKIPPED root-joint \"{boneName}\" override " +
                         $"(would-be shift {delta:0.###} m, raw pos {slPos.X:0.###},{slPos.Y:0.###},{slPos.Z:0.###}) " +
                         "— root/pelvis position overrides are not applied, see ApplyJointPositionOverrides doc comment");
                continue;
            }

            if (!visual.JointPosOverrides.TryGetValue(boneName, out var prevPos) || prevPos != slPos)
                skeletonChanged = true;
            visual.JointPosOverrides[boneName] = slPos;
            applied++;
            if (delta > maxDelta) maxDelta = delta;
        }

        // Viewer parity (LLVOAvatar::applyAttachmentOverrides): 
        // Worn meshes often only provide joint overrides for one side (e.g. mFootLeft).
        // The viewer implicitly mirrors them to the other side (mFootRight) by negating the Y axis
        // (which is the left/right axis in SL local bone space).
        var toAdd = new Dictionary<string, System.Numerics.Vector3>();
        var toLock = new List<string>();
        foreach (var kvp in visual.JointPosOverrides)
        {
            string name = kvp.Key;
            var pos = kvp.Value;
            
            if (name.EndsWith("Left"))
            {
                string rightName = name.Substring(0, name.Length - 4) + "Right";
                if (!visual.JointPosOverrides.ContainsKey(rightName))
                {
                    toAdd[rightName] = new System.Numerics.Vector3(pos.X, -pos.Y, pos.Z);
                    if (visual.JointScaleLocks.Contains(name)) toLock.Add(rightName);
                }
            }
            else if (name.EndsWith("Right"))
            {
                string leftName = name.Substring(0, name.Length - 5) + "Left";
                if (!visual.JointPosOverrides.ContainsKey(leftName))
                {
                    toAdd[leftName] = new System.Numerics.Vector3(pos.X, -pos.Y, pos.Z);
                    if (visual.JointScaleLocks.Contains(name)) toLock.Add(leftName);
                }
            }
        }
        
        foreach (var kvp in toAdd)
        {
            visual.JointPosOverrides[kvp.Key] = kvp.Value;
            skeletonChanged = true;
            // Don't increment applied count for implicitly added bones so logging remains accurate to the asset
        }
        // The mirrored sibling inherits the lock too — its position override is synthetic, so
        // leaving its scale slider-driven while the real side is frozen would make the two
        // asymmetric, which is the one thing the mirroring exists to prevent.
        foreach (var boneName in toLock)
            if (visual.JointScaleLocks.Add(boneName)) skeletonChanged = true;

        if (skeletonChanged)
        {
            if (_avatarSkeleton != null)
            {
                ApplyShape(visual, skeleton, _avatarSkeleton, visual.LastDistortions, visual.JointPosOverrides);
            }
            skeleton.ResetBonePoses();

            // ApplyShape just rewrote every bone's rest AND visual.BoneOwnScale — and a skinning
            // bind BAKES that scale in (see InjectOwnScale). Every mesh already bound therefore
            // still renders at the PREVIOUS skeleton, which is why this used to look like "the
            // change had no effect at all": the stale bind carries the old scale, so the result is
            // pixel-identical rather than merely approximate. The shape-change path has always
            // refreshed both (RebuildRiggedAttachmentSkins + RebuildBodyMorphs' skin eviction);
            // this path mutates exactly the same state and must do the same. Order matters for a
            // worn mesh BODY declaring lock_scale_if_joint_position: it loads after the mesh HEAD
            // as often as not, and it is the body's flag that frees the head's mHead scale
            // (BUG-AVATAR-07). The mesh being bound by THIS call isn't in RiggedAttachments yet,
            // so it picks the new values up on its own, a few lines later.
            RebuildRiggedAttachmentSkins(visual);
            RefreshBodyPartSkins(visual);
            RefreshStaticAttachmentOffsets(visual);

            // Ungated, unlike the per-mesh detail below: this fires at most a couple of times per
            // login (only a mesh that declares the flag AND contributes NEW locks reaches it) and
            // it is the one line that separates "the fitted body froze the shape's bone scaling,
            // as the reference viewer does" from "SLNG never saw the flag" (BUG-AVATAR-07).
            if (locked > 0)
                GD.Print($"[JointOverride] mesh {meshId}: {locked} joint scale(s) locked to the skeleton " +
                         $"default (lock_scale_if_joint_position) — the shape's bone scaling is now off for " +
                         $"{visual.JointScaleLocks.Count} joint(s) on this avatar");
            if (Diagnostics.Enabled) GD.Print($"[JointOverride] mesh {meshId}: {applied}/{jointCount} joint positions overridden (max shift {maxDelta:0.###} m)" +
                     (skinData.LockScaleIfJointPosition ? $", {locked} joint scale(s) locked to skeleton default (lock_scale_if_joint_position)" : ""));

            // A fitted mesh can override leg/spine joints (e.g. an alternate-bind mesh body/legs)
            // that move mFootLeft — refresh the measured foot offset now rather than waiting for
            // the next shape change. See RecomputeFootOffset's doc comment.
            RecomputeFootOffset(visual, visual.LastDistortions);
            LogAvatarHeight(visual, "joint override");
        }
        // Viewer parity: LLAvatarAppearance::addPelvisFixup (indra/llappearance/
        // llavatarappearance.cpp) — this offset does NOT move the mPelvis joint's local
        // position; it shifts the whole avatar's world RENDER position every frame
        // (LLVOAvatar::getRenderPosition: "pos[VZ] += fixup", applied only when isRoot()).
        // Store it keyed by mesh id (mirrors LLVector3OverrideMap) so it can be reverted if
        // this mesh is un-worn (see the _attachmentMeshIds-driven cleanup in RemoveVisual /
        // UpdateAttachment / UpdateHudAttachment) and combined with any other worn mesh's
        // fixup via TryGetActivePelvisFixup — applied in UpdateVisual's root-position step.
        if (System.Math.Abs(skinData.PelvisOffset) > 0.0001f)
        {
            visual.PelvisFixups[meshId] = skinData.PelvisOffset;
            if (Diagnostics.Enabled) GD.Print($"[JointOverride] mesh {meshId}: pelvis offset {skinData.PelvisOffset:0.###} m applied to avatar root");
        }
        else
        {
            // A previously-nonzero fixup for this exact mesh id could only become zero by the
            // asset content changing under a stable id, which doesn't happen — but keep this
            // symmetrical with "removed" cleanup rather than leaving a stale zero-ish entry.
            visual.PelvisFixups.Remove(meshId);
        }
    }

    /// <summary>Refreshes the position of static attachment points (e.g. hair, hats) when
    /// the skeleton's bone scales change due to shape sliders or scale-locking. Keeps static
    /// items aligned with the avatar's scaled joints without requiring a re-rez.</summary>
    private void RefreshStaticAttachmentOffsets(AvatarVisual visual)
    {
        if (visual?.Skeleton == null) return;
        foreach (var kvp in _attachmentNodes)
        {
            var boneAttach = kvp.Value;
            if (!GodotObject.IsInstanceValid(boneAttach) || !boneAttach.IsInsideTree()) continue;
            if (boneAttach.GetParent() != visual.Skeleton) continue;

            if (boneAttach.HasMeta("AttachPoint") && boneAttach.HasMeta("BoneName"))
            {
                byte apByte = (byte)boneAttach.GetMeta("AttachPoint").AsInt32();
                string bName = boneAttach.GetMeta("BoneName").AsString();
                var pointNode = boneAttach.GetNodeOrNull<Node3D>("PointOffset");
                if (pointNode != null && AttachmentPointMap.GetPoint(apByte) is { } apPoint)
                {
                    var apPos = apPoint.Position;
                    if (visual.BoneOwnScale.TryGetValue(bName, out var jointScale))
                        apPos *= jointScale;
                    pointNode.Position = new Godot.Vector3(apPos.X, apPos.Z, -apPos.Y);
                    pointNode.Basis = SkeletonBuilder.SlEulerDegToGodotBasis(apPoint.RotationDeg);
                }
            }
        }
    }

    /// <summary>
    /// Picks the pelvis Z fixup to apply to the avatar root this frame, mirroring
    /// <c>LLVector3OverrideMap::findActiveOverride</c> (indra/llcharacter/lljoint.cpp): when
    /// more than one worn rigged mesh carries a nonzero pelvis offset simultaneously, exactly
    /// ONE wins — the entry whose mesh id key compares greatest. That tie-break isn't
    /// semantically meaningful in the viewer either (it's an artifact of using std::max_element
    /// over the map's keys); the only real contract is "one deterministic winner", which Guid's
    /// ordering gives us too. The overwhelmingly common case is a single contributor (one
    /// fitted body/outfit mesh), where this is just that mesh's offset.
    /// </summary>
    /// <summary>Recomputes <see cref="AvatarVisual.FootOffsetY"/> by measuring mFootLeft's actual
    /// CURRENT position directly from the live <see cref="Skeleton3D"/> — see that field's doc
    /// comment for why this replaced an earlier formula-based (<c>SlJointComposer.ComputeBodySize</c>
    /// / <c>mBodySize</c>/<c>mPelvisToFoot</c>) approach that provably never affected the rendered
    /// result. <paramref name="distortions"/> is accepted (and cached into
    /// <see cref="AvatarVisual.LastDistortions"/>) only so <see cref="ApplyJointPositionOverrides"/>
    /// can trigger a recompute later without re-deriving it from VisualParams — the measurement
    /// itself needs no distortion data, since by the time this runs <see cref="ApplyShape"/> and/or
    /// <see cref="ApplyJointPositionOverrides"/> have already baked every distortion and joint
    /// override into the skeleton's bone rests and called <c>ResetBonePoses()</c>, so
    /// <c>GetBoneGlobalPose</c> already reflects all of it. Called from UpdateVisual's shape-change
    /// block AND whenever <see cref="ApplyJointPositionOverrides"/> applies a fitted-mesh joint
    /// override — both places already call <c>ResetBonePoses()</c> immediately beforehand, which
    /// this method depends on for the measurement to be current.</summary>
    private void RecomputeFootOffset(AvatarVisual visual,
        IReadOnlyDictionary<string, (System.Numerics.Vector3 Scale, System.Numerics.Vector3 Position)> distortions)
    {
        if (_avatarSkeleton == null) return;

        // Bug fix (2026-07-22, round 7): ComputeBodySize MUST read `distortions` before anything
        // below mutates it. ApplyJointPositionOverrides calls this method with
        // visual.LastDistortions passed AS `distortions` (deliberately, to reuse the last-known
        // shape distortions without re-deriving them from VisualParams) — since Dictionary is a
        // reference type, `distortions` and visual.LastDistortions are then THE SAME OBJECT. The
        // old code order called visual.LastDistortions.Clear() FIRST, which — being the same
        // object — also wiped `distortions` out from under the ComputeBodySize call below,
        // silently computing BodySizeZ/PelvisToFootZ from an EMPTY dictionary every single time a
        // worn rigged mesh with joint-position overrides triggered this path (i.e. routinely, for
        // any fitted-mesh outfit/body). Confirmed live: BodySizeZ/PelvisToFootZ landing on EXACTLY
        // SlJointComposerTests' zero-distortion reference values (1.7067/0.979) for an avatar
        // independently confirmed (via [ShapeDataDiag]) to have rich, non-default VisualParams —
        // the precise signature of "computed from an empty dict", not "computed from a different
        // avatar's real shape" (which would have produced SOME other nontrivial number, not
        // exactly the textbook zero-distortion constants). This canceled out invisibly for the
        // LOCAL avatar (AvatarController's ground-clamp and this render formula both read the same
        // — equally wrong — BodySizeZ, so it canceled algebraically either way) but round 6's
        // PelvisToFootZ conversion for REMOTE avatars has no such cancellation, so it directly
        // exposed this as a live, visible float plus wrong apparent proportions.
        // Bug fix (2026-07-22, round 12): ComputeBodySize MUST also receive visual.JointPosOverrides!
        // Without it, BodySizeZ stays at the natural body size (e.g., 1.99m) even when fitted mesh
        // boots override the foot/ankle joints to make the avatar 2.19m tall. Firestorm grounds the
        // avatar using the full 2.19m bounding-box/skeleton offset, pushing the avatar up. SLNG
        // received the pushed-up simPos.Z but subtracted the too-small 1.99m halfBodyZ, resulting in
        // the Godot foot floating significantly above the ground.
        var body = SLNG.Core.SlJointComposer.ComputeBodySize(
            _avatarSkeleton, distortions, visual.JointPosOverrides, visual.JointScaleLocks);
        visual.BodySizeZ = body.BodySizeZ + visual.ShoeOffsetZ;
        visual.PelvisToFootZ = body.PelvisToFoot + visual.ShoeOffsetZ;

        // Skip entirely when `distortions` IS visual.LastDistortions (the aliased case above) —
        // it's already correct by definition, and Clear()-then-copy on itself would just erase it.
        if (!ReferenceEquals(distortions, visual.LastDistortions))
        {
            visual.LastDistortions.Clear();
            foreach (var kv in distortions)
                visual.LastDistortions[kv.Key] = kv.Value;
        }

        if (visual.Skeleton == null) return;
        int footBone = visual.Skeleton.FindBone("mFootLeft");
        if (footBone >= 0)
        {
            visual.FootOffsetY = GetBoneRootRelativeY(visual.Skeleton, footBone);
        }
    }

    /// <summary>Prints the self avatar's rendered vertical geometry in WORLD metres — the one
    /// measurement that can be compared against a reference viewer without any camera/projection
    /// guesswork, because a prim rezzed at a fixed Z is at that Z in both. Answers "is my avatar
    /// standing lower, or is it actually shorter?", which a screenshot cannot separate.
    /// Self only, and only where the shape/skeleton actually changed, so it stays a handful of
    /// lines per login (BUG-AVATAR-07).</summary>
    private void LogAvatarHeight(AvatarVisual visual, string why, float simZ = float.NaN)
    {
        if (!visual.IsSelf || visual.Skeleton == null) return;
        int skullIdx = visual.Skeleton.FindBone("mSkull");
        int headIdx = visual.Skeleton.FindBone("mHead");
        int footIdx = visual.Skeleton.FindBone("mFootLeft");
        if (skullIdx < 0 || headIdx < 0 || footIdx < 0) return;

        float skullY = GetBoneRootRelativeY(visual.Skeleton, skullIdx);
        float headY = GetBoneRootRelativeY(visual.Skeleton, headIdx);
        float footY = GetBoneRootRelativeY(visual.Skeleton, footIdx);
        // Root.GlobalPosition is whatever UpdateVisual's position step last produced; before the
        // first frame it is still the origin, hence the marker rather than a silent wrong number.
        bool positioned = IsInstanceValid(visual.Root) && visual.Root.GlobalPosition != Godot.Vector3.Zero;
        float rootZ = positioned ? visual.Root.GlobalPosition.Y : float.NaN;

        GD.Print($"[AvatarHeight] ({why}) skeleton foot->mSkull {skullY - footY:0.###} m | " +
                 (float.IsNaN(simZ) ? "" : $"sim agent Z {simZ:0.###} | ") +
                 $"world Z: mSkull {rootZ + skullY:0.###}  mHead(jaw) {rootZ + headY:0.###}  mFootLeft {rootZ + footY:0.###}  root {rootZ:0.###}" +
                 (positioned ? "" : "  (root not positioned yet — world Z meaningless)") +
                 $" | bodySizeZ {visual.BodySizeZ:0.###} pelvisToFoot {visual.PelvisToFootZ:0.###} " +
                 $"hoverParam {visual.AvatarHoverParamZ:0.###} footOffsetY {visual.FootOffsetY:0.###} shoeOffset {visual.ShoeOffsetZ:0.###}");
    }

    private static float GetBoneRootRelativeY(Skeleton3D skeleton, int boneIdx)
    {
        Transform3D globalRest = Transform3D.Identity;
        int current = boneIdx;
        while (current >= 0)
        {
            var rest = skeleton.GetBoneRest(current);
            globalRest = rest * globalRest;
            current = skeleton.GetBoneParent(current);
        }
        return globalRest.Origin.Y;
    }

    private static bool TryGetActivePelvisFixup(AvatarVisual visual, out float fixupZ)
    {
        fixupZ = 0f;
        bool found = false;
        Guid bestKey = default;
        foreach (var (meshId, z) in visual.PelvisFixups)
        {
            if (!found || meshId.CompareTo(bestKey) > 0)
            {
                bestKey = meshId;
                fixupZ = z;
                found = true;
            }
        }
        return found;
    }

    public bool TryGetBodySizeZ(Guid entityId, out float bodySizeZ)
    {
        bodySizeZ = 1.90f;
        if (!_visuals.TryGetValue(entityId, out var visual)) return false;
        bodySizeZ = visual.BodySizeZ;
        return true;
    }

    /// <summary>Diagnostic accessor only — AvatarController's ground-clamp does NOT use this for
    /// its clamp math anymore (see that file: it clamps straight to <c>groundHeight</c>, no
    /// compensation, because <see cref="UpdateVisual"/> now applies the ENTIRE foot/ground
    /// correction itself via <see cref="AvatarVisual.FootOffsetY"/> — see that field's doc comment
    /// for why a previous split-across-two-files subtract-then-re-add design was a no-op
    /// tautology). Kept so AvatarController's <c>[GroundClamp]</c> log can print the SAME
    /// FootOffsetY + active pelvis-fixup value <see cref="UpdateVisual"/> is using, for direct
    /// cross-verification against its own <c>[RootApply]</c> log. Returns false (correctionZ = 0)
    /// if this avatar isn't tracked yet.</summary>
    public bool TryGetVerticalRenderCorrection(Guid entityId, out float correctionZ)
    {
        correctionZ = 0f;
        if (!_visuals.TryGetValue(entityId, out var visual)) return false;

        correctionZ = visual.FootOffsetY;
        if (TryGetActivePelvisFixup(visual, out var fixupZ)) correctionZ += fixupZ;
        return true;
    }

    private MeshInstance3D? BuildRiggedMeshInstance(MeshData meshData, Skeleton3D skeleton, Guid meshId,
        AvatarVisual visual, FaceTexture[]? faces, FaceTexture defaultFace, out int[] faceIndices)
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

            // ApplyShape keeps every Rest a pure rotation (no scale — see its doc comment), so
            // Godot's own live pose (used internally every frame for skinning: pose * bind) never
            // carries any bone's own scale either. Inject it into the BIND instead — algebraically
            // equivalent and pose-independent: for W = livePose (no scale) and W' = W with this
            // bone's own scale injected into its basis, W.AffineInverse()*W' reduces to a pure
            // Diag(ownScale) transform (the rotation/position cancel out), so pre-multiplying it
            // once here reproduces exactly SL's invBind·jointWorld(with own scale) every frame
            // without needing to recompute anything per-pose.
            if (visual.BoneOwnScale.TryGetValue(skeleton.GetBoneName(bone), out var ownScale))
                bind = InjectOwnScale(bind, ownScale);

            // A NaN/Inf bind — a degenerate inverse-bind matrix in the mesh asset, or a singular
            // rest — poisons every vertex weighted to this joint and Godot then warns "Vector3
            // cannot be normalized, the elements must be finite". Fall back to the computed rest
            // (scale-clamped in ApplyShape, so finite) rather than feed the skin palette garbage.
            if (!IsFiniteTransform(bind))
            {
                GD.PushWarning($"[Avatar] non-finite skin bind for bone '{skeleton.GetBoneName(bone)}' — using computed rest");
                bind = ComputeGlobalRestTransform(skeleton, bone).AffineInverse();
                if (!IsFiniteTransform(bind)) bind = Transform3D.Identity;
            }

            slotForJoint[j] = skin.GetBindCount();
            skin.AddBind(bone, bind);
        }
        if (skin.GetBindCount() == 0) return null;

        var arrayMesh = new ArrayMesh();
        var faceList = new List<int>();

        // BUG-AVATAR-07: the REST-POSE SKINNED extent, i.e. how big and where this mesh actually
        // renders, in metres. Every other size number in this method is pre-skinning and therefore
        // says nothing (see the no-rejection comment below). This one is the exact palette Godot
        // will use at rest — globalRest(bone) * bind, the same product its own skinning computes —
        // applied to the same vertices, so "is the mesh head the right size / in the right place"
        // stops being a question about screenshots. Self only, once per mesh id.
        var palette = new Transform3D[skin.GetBindCount()];
        for (int sIdx = 0; sIdx < palette.Length; sIdx++)
            palette[sIdx] = ComputeGlobalRestTransform(skeleton, skin.GetBindBone(sIdx)) * skin.GetBindPose(sIdx);
        bool measureRender = visual.IsSelf && !_loggedRenderExtent.Contains(meshId);
        var rMin = new Godot.Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var rMax = new Godot.Vector3(float.MinValue, float.MinValue, float.MinValue);

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

        // Counts influences whose joint reference had to be remapped to stay in range (see
        // AddInfluence). Before that method was fixed to match the viewer these were DROPPED, and
        // the resulting renormalization snapped affected vertices onto an unrelated bone — the
        // "hair tears into flat shards" bug. A nonzero count here means this mesh is one that
        // relies on the viewer's clamping behavior.
        int remappedInfluences = 0;

        // Diagnostic: which bone this mesh is mostly weighted to, and how much of its total
        // vertex weight lands there. Points straight at a shape/scale bug on a specific bone
        // (e.g. an unexpectedly huge mHead scale) without having to guess from bind-pose extent
        // alone, which is meaningless pre-skinning (see the no-rejection comment below).
        var slotWeightSum = new float[skin.GetBindCount()];

        // BUG-RENDER-12: consecutive submeshes that resolve to the SAME face record are committed
        // as ONE surface, in their authored order.
        //
        // This is the half of the reference viewer's rigged-alpha design that makes its depth
        // write safe. llvovolume.cpp:6332-6335, with Linden's own comment:
        //     if (rigged) {
        //         if (!distance_sort) // <--- alpha "sort" rigged faces by maintaining original draw order
        //             std::sort(faces, faces + face_count, CompareBatchBreakerRigged());
        //     }
        // alpha_sort is always true (:6146), so for RIGGED alpha that branch sorts nothing at all:
        // worn mesh faces are batched in the order the creator authored them and are never
        // distance-sorted, unlike unrigged alpha (which does sort, and even then only re-sorts once
        // the view angle has moved more than 0.64 -- llspatialpartition.cpp:667-674).
        //
        // Godot has no equivalent knob: every transparent SURFACE is re-sorted by AABB-centre
        // distance each frame. Splitting a mesh into one surface per SL face therefore hands Godot
        // six independently reorderable pieces of what the creator authored as one ordered stream.
        // Measured on the live hair: three rigged meshes of six faces each, all six carrying the
        // identical texture (`face ids: [b9af3b5f x 6]`) -- 18 co-located transparent draws whose
        // order reshuffled on the smallest camera move.
        //
        // Merging a run restores the authored order as a single draw call, because within one
        // surface Godot draws triangles in index order and sorts nothing. Only CONSECUTIVE runs
        // are merged, never scattered matches: merging non-adjacent faces would interleave
        // triangles the creator ordered deliberately, which is the very thing being preserved.
        // Faces are merged only when their whole FaceTexture record compares equal, so the
        // surviving surface's material is bit-identical to the ones it replaces.
        SurfaceTool? st = null;
        var runFace = default(FaceTexture);
        int runVertexBase = 0;

        void FlushRun()
        {
            if (st == null) return;
            st.GenerateTangents();
            st.Commit(arrayMesh);
            st = null;
        }

        foreach (var sub in meshData.Submeshes)
        {
            if (sub.Indices.Length == 0 || sub.Weights == null) continue;

            var subFace = ResolveFaceTexture(faces, defaultFace, sub.FaceIndex);
            if (st == null || !subFace.Equals(runFace))
            {
                FlushRun();
                st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                runFace = subFace;
                runVertexBase = 0;
                faceList.Add(sub.FaceIndex);
            }

            // Indices are submesh-local, so a submesh appended to a run in progress has to shift
            // them past everything already in the SurfaceTool.
            int indexBase = runVertexBase;

            // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front — left
            // uncorrected, every SL-sourced triangle rasterizes as a backface (masked by
            // CullMode.Disabled, needed just to make anything render), and Godot's double-sided
            // handling flips the normal for perceived backfaces, inverting diffuse lighting while
            // leaving shadows (depth-only) unaffected. Confirmed this session via a T-pose +
            // debug shader + a gizmo pointing at the real light direction. Fix: submit each
            // triangle's 3 vertices in reversed order — every per-vertex step below (weight
            // resolution, bpMin/bpMax, slotWeightSum) is order-independent across the mesh, so
            // only the ORDER the 3 indices of each triangle are visited changes.
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                // Mesh-local → bind pose (SL coords) via the bind-shape matrix, then SL→Godot.
                var pSL = System.Numerics.Vector3.Transform(sub.Positions[i], bindShape);
                var nSL = System.Numerics.Vector3.TransformNormal(sub.Normals[i], bindShapeNormalMatrix);
                if (nSL.LengthSquared() > 1e-8f) nSL = System.Numerics.Vector3.Normalize(nSL);
                var uv = sub.UVs[i];
                var w = sub.Weights[i];

                bpMin = System.Numerics.Vector3.Min(bpMin, pSL);
                bpMax = System.Numerics.Vector3.Max(bpMax, pSL);

                var bones = new int[4];
                var wts = new float[4];
                int c = 0; float sum = 0f;
                AddInfluence(w.Joint0, w.Weight0, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remappedInfluences);
                AddInfluence(w.Joint1, w.Weight1, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remappedInfluences);
                AddInfluence(w.Joint2, w.Weight2, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remappedInfluences);
                AddInfluence(w.Joint3, w.Weight3, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remappedInfluences);
                totalVerts++;
                if (sum > 1e-5f) { for (int k = 0; k < 4; k++) wts[k] /= sum; }
                else { bones[0] = 0; wts[0] = 1f; orphanedVerts++; } // orphaned vertex — pin to first bound bone
                for (int k = 0; k < 4; k++) if (wts[k] > 0f) slotWeightSum[bones[k]] += wts[k];

                if (measureRender)
                {
                    var vG = new Godot.Vector3(pSL.X, pSL.Z, -pSL.Y);
                    var acc = Godot.Vector3.Zero;
                    float wSum = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        if (wts[k] <= 0f || bones[k] < 0 || bones[k] >= palette.Length) continue;
                        acc += palette[bones[k]] * vG * wts[k];
                        wSum += wts[k];
                    }
                    if (wSum > 1e-6f)
                    {
                        acc /= wSum;
                        rMin = new Godot.Vector3(Mathf.Min(rMin.X, acc.X), Mathf.Min(rMin.Y, acc.Y), Mathf.Min(rMin.Z, acc.Z));
                        rMax = new Godot.Vector3(Mathf.Max(rMax.X, acc.X), Mathf.Max(rMax.Y, acc.Y), Mathf.Max(rMax.Z, acc.Z));
                    }
                }

                st.SetBones(bones);
                st.SetWeights(wts);
                st.SetNormal(new Godot.Vector3(nSL.X, nSL.Z, -nSL.Y));
                // Same SL→Godot V-flip as the system body parts (see BuildPartResources):
                // SL UVs are authored bottom-left origin; Godot samples top-left.
                st.SetUV(new Godot.Vector2(uv.X, 1.0f - uv.Y));
                st.AddVertex(new Godot.Vector3(pSL.X, pSL.Z, -pSL.Y));
            }

            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                st.AddIndex(indexBase + sub.Indices[t]);
                st.AddIndex(indexBase + sub.Indices[t + 2]);
                st.AddIndex(indexBase + sub.Indices[t + 1]);
            }

            runVertexBase += sub.Positions.Length;
        }
        FlushRun();

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
        // BUG-RENDER-12: says how many transparent draw calls this mesh will cost. If it does not
        // read "6 submeshes -> 1 surface" for a single-texture hair mesh, the merge did not fire
        // and the sort instability is back. Behind --diag: it fired 442 times in one session,
        // which is the same log flood BUG-RENDER-11 spent a round clearing out.
        if (Diagnostics.Enabled && arrayMesh.GetSurfaceCount() != meshData.Submeshes.Count)
            GD.Print($"[RiggedMesh] mesh={meshId.ToString("N")[..8]} merged " +
                     $"{meshData.Submeshes.Count} submeshes -> {arrayMesh.GetSurfaceCount()} surface(s) " +
                     "(same-material run, authored order preserved)");

        if (measureRender && rMax.Y > rMin.Y)
        {
            _loggedRenderExtent.Add(meshId);
            float rootZ = IsInstanceValid(visual.Root) ? visual.Root.GlobalPosition.Y : 0f;
            var size = rMax - rMin;
            GD.Print($"[RenderExtent] {meshId}: rendered size ({size.X:0.###} x {size.Z:0.###} x {size.Y:0.###} m), " +
                     $"world Z {rootZ + rMin.Y:0.###} .. {rootZ + rMax.Y:0.###}, " +
                     $"dominant joint \"{topBoneName}\" ({topShare:P0}), {totalVerts} verts");
        }

        Logger.Debug($"[RiggedMesh] mesh {meshId} joints {resolved}/{jointCount} resolved, binds {skin.GetBindCount()}, " +
                 $"bind-pose size ({bpSize.X:0.##}, {bpSize.Y:0.##}, {bpSize.Z:0.##}) at ({bpCenter.X:0.#}, {bpCenter.Y:0.#}, {bpCenter.Z:0.#}), " +
                 $"dominant joint \"{topBoneName}\" ({topShare:P0}), orphaned verts {orphanedVerts}/{totalVerts}, " +
                 $"remapped influences {remappedInfluences}" +
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

    /// <summary>Adds one of a vertex's up-to-4 bone influences, matching the real viewer's
    /// handling of malformed joint references (verified against
    /// scratch/slviewer/indra/newview/llskinningutil.cpp).
    ///
    /// Viewer parity, and the bug this used to have: an influence whose joint index is out of
    /// range is CLAMPED into range and KEPT — never dropped. `getPerVertexSkinMatrix` (:250) does
    /// `idx[k] = llclamp((S32) floorf(w), 0, max_joints-1)`, and `scrubSkinWeights` (:209-222)
    /// pre-clamps the stored weights the same way; likewise `scrubInvalidJoints` (:112-125)
    /// rewrites a joint NAME the avatar doesn't have to "mPelvis" and `initJointNums` (:314-315)
    /// falls back to joint num 0 — again remapping, never discarding. This method previously
    /// `return`ed early in both cases, silently discarding that influence. Because the caller then
    /// renormalizes the surviving weights (`wts[k] /= sum`), a vertex that should have been, say,
    /// 60% neck / 40% head became 100% head — snapping it to an unrelated bone while its
    /// neighbours stayed put. Whole triangles get stretched between the two, which reads as the
    /// mesh tearing into scattered flat shards even though its bind pose, textures, UVs and
    /// position are all correct. It also leaves the `orphanedVerts` counter at 0 (at least one
    /// influence survives per vertex), so the existing diagnostic could not see it.</summary>
    private static void AddInfluence(int joint, float weight, int[] slotForJoint, int jointCount,
        int[] bones, float[] wts, ref int count, ref float sum, ref int remapped)
    {
        if (count >= 4 || weight <= 0f || jointCount <= 0) return;

        int j = joint;
        if (j < 0 || j >= jointCount)
        {
            j = System.Math.Clamp(j, 0, jointCount - 1);
            remapped++;
        }

        int slot = slotForJoint[j];
        if (slot < 0)
        {
            // The joint name resolved to no bone in OUR skeleton. Viewer: remap to mPelvis /
            // joint 0 rather than dropping. Slot 0 is this mesh's first successfully bound joint
            // — the nearest available analog to that fallback.
            slot = 0;
            remapped++;
        }

        bones[count] = slot;
        wts[count] = weight;
        sum += weight;
        count++;
    }

    // -------------------------------------------------------------------------
    // Skinned-mesh helpers
    // -------------------------------------------------------------------------

    // The base body MESH geometry (positions/normals/UVs before morphing) is identical for every
    // avatar — the same .llm files — but the skin's BIND MATRICES are not: each bind now injects
    // this avatar's own BoneOwnScale (see AddSkinSlot), so binds must be built per avatar (cached
    // on AvatarVisual.PartSkins, not shared statically — see that field's doc comment). The MESH
    // geometry itself bakes in the avatar's vertex morphs (male, muscle, breast, … sliders) and so
    // is rebuilt per avatar / on every shape change regardless (RebuildBodyMorphs).

    /// <summary>
    /// Returns a <see cref="MeshInstance3D"/> for one SL body-part mesh, with correct per-vertex
    /// bone indices and weights, and the avatar's vertex morphs applied when
    /// <paramref name="weights"/> is supplied (null = base mesh, e.g. before appearance arrives).
    /// The instance must be added as a DIRECT child of the <see cref="Skeleton3D"/> for Godot's
    /// built-in skinning to take effect.
    /// </summary>
    private MeshInstance3D? BuildSkinnedMeshInstance(
        AvatarVisual visual, AvatarBodyPartMesh part, Skeleton3D skeleton, Color baseColor, IReadOnlyDictionary<int, float>? weights)
    {
        if (part.Indices.Length == 0) return null;

        var (skin, slots) = GetPartSkin(visual, part, skeleton);

        // Vertex morphs (LLPolyMorphTarget) deform the base mesh into this avatar's real shape.
        // With no weights yet, render the neutral base mesh.
        var (positions, normals) = weights != null
            ? AvatarMorphService.Apply(part, weights)
            : (part.Positions, part.Normals);

        // FEAT-RENDER-01 Phase 3: a system body part starts on the family's opaque Avatar
        // variant, which is also what the bake path expects to find -- it casts MaterialOverride
        // to ShaderMaterial and updates it in place, so leaving a StandardMaterial3D here would
        // silently make every bake allocate a replacement instead. The placeholder tint stays: it
        // is what the body shows in the window between mesh build and bake arrival.
        var placeholder = new ShaderMaterial
        {
            Shader = PrimShaderFamily.Select(PrimShaderFamily.Kind.Opaque, PrimShaderFamily.Surface.Avatar)
        };
        placeholder.SetShaderParameter(PrimShaderFamily.AlbedoColor, baseColor);

        return new MeshInstance3D
        {
            Mesh             = BuildPartMesh(part, positions, normals, slots),
            Skin             = skin,
            MaterialOverride = placeholder
        };
    }

    /// <summary>Builds (once, then cached on <paramref name="visual"/>) this avatar's skin resource
    /// + boneName→slot map for one body part. NOT shape-independent: each bind bakes in this
    /// avatar's current <see cref="AvatarVisual.BoneOwnScale"/>, so callers must evict the cache
    /// entry (<c>visual.PartSkins.Remove(part.Name)</c>) before calling again after a shape change —
    /// see RebuildBodyMorphs.</summary>
    private static (Skin Skin, Dictionary<string, int> Slots) GetPartSkin(AvatarVisual visual, AvatarBodyPartMesh part, Skeleton3D skeleton)
    {
        if (visual.PartSkins.TryGetValue(part.Name, out var cached)) return cached;

        var skin = new Skin();
        var slots = new Dictionary<string, int>(); // boneName → slot index in Skin
        for (int vi = 0; vi < part.Positions.Length; vi++)
        {
            AddSkinSlot(part.Bone1Names[vi], skin, skeleton, slots, visual);
            AddSkinSlot(part.Bone2Names[vi], skin, skeleton, slots, visual);
        }

        var result = (skin, slots);
        visual.PartSkins[part.Name] = result;
        return result;
    }

    /// <summary>Builds the (per-avatar) ArrayMesh for one body part from already-morphed
    /// <paramref name="positions"/>/<paramref name="normals"/> (SL space, indexed by the part's
    /// base vertex ids).</summary>
    private static ArrayMesh BuildPartMesh(
        AvatarBodyPartMesh part, System.Numerics.Vector3[] positions, System.Numerics.Vector3[] normals,
        Dictionary<string, int> skinSlots)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front — left
        // uncorrected, every SL-sourced triangle rasterizes as a backface (masked by
        // CullMode.Disabled, needed just to make anything render), and Godot's double-sided
        // handling flips the normal for perceived backfaces, inverting diffuse lighting while
        // leaving shadows (depth-only) unaffected. Confirmed this session via a T-pose + debug
        // shader + a gizmo pointing at the real light direction. Fix: submit each triangle's 3
        // vertices in reversed order — every per-vertex step below is order-independent, so only
        // the ORDER the 3 indices of each triangle are visited changes.
        //
        // Local helper: submits one vertex (shared skinning/normal/UV logic used both for the
        // base N vertices below and for the handful of degenerate-triangle duplicates further
        // down) and returns the index Godot assigned it.
        int nextIndex = 0;
        int SubmitVertex(int srcIndex, Godot.Vector2 uv)
        {
            var p = positions[srcIndex];
            var n = normals[srcIndex];

            int s1 = 0, s2 = 0;
            if (part.Bone1Names[srcIndex] != null && skinSlots.TryGetValue(part.Bone1Names[srcIndex]!, out int ss1)) s1 = ss1;
            if (part.Bone2Names[srcIndex] != null && skinSlots.TryGetValue(part.Bone2Names[srcIndex]!, out int ss2)) s2 = ss2;

            float w1 = part.Bone1Weights[srcIndex];
            float w2 = part.Bone2Weights[srcIndex];

            // Normalize the two SL weights so they sum to 1 — Godot expects normalized
            // skin weights and a zero-sum vertex would not deform at all.
            float wsum = w1 + w2;
            if (wsum > 0.0001f) { w1 /= wsum; w2 /= wsum; }
            else { w1 = 1f; w2 = 0f; }

            // SL is Z-up; Godot is Y-up: SL(X,Y,Z) → Godot(X,Z,−Y)
            st.SetBones(new int[] { s1, s2, 0, 0 });
            st.SetWeights(new float[] { w1, w2, 0f, 0f });
            st.SetNormal(new Godot.Vector3(n.X, n.Z, -n.Y));
            st.SetUV(uv);
            st.AddVertex(new Godot.Vector3(p.X, p.Z, -p.Y));
            return nextIndex++;
        }

        for (int i = 0; i < part.Positions.Length; i++)
        {
            var uv = part.UVs[i];
            // SL/OpenGL texture origin is bottom-left (V grows up); Godot/Vulkan is top-left
            // (V grows down) and Magick decodes row 0 = top. Flip V so the baked skin lands
            // on the correct body parts instead of mirrored (front texture on the back, etc).
            SubmitVertex(i, new Godot.Vector2(uv.X, 1.0f - uv.Y));
        }

        // BUG-RENDER-04: LL's own avatar_head/eye/upper_body/hair .llm meshes contain a small
        // number of genuinely degenerate triangles — measured via a throwaway probe against the
        // real .llm data (SLNG.Assets.AvatarBodyMeshService's raw output; every NORMAL is
        // well-formed, this is purely a triangle-shape issue), two different ways:
        //
        //   - ~152 triangles have ~zero area in POSITION space (two or three of their vertices
        //     coincide or are collinear) — a known real quirk of this vintage of hand-authored
        //     mesh, used to close a UV chart without adding a visible sliver. Genuinely invisible
        //     either way: zero screen-space area whether present or dropped.
        //   - ~7 triangles have ~zero area in UV space only (their positions are fine, but their
        //     three UV coordinates are collinear/coincident) — real seam vertices sharing
        //     collapsed UV coordinates.
        //
        // GenerateTangents() computes each triangle's tangent from the UV gradient scaled by the
        // position edges; either kind of degeneracy drives that computation toward 0/0, and Godot's
        // engine prints "Vector3 cannot be normalized... Making (0, 0, 0) as a fallback" once per
        // affected vertex — (152 + 7) × 3 ≈ 477 lines of pure console noise at every avatar
        // (re)build, matching what was actually observed (474).
        //
        // Fix, one for each kind — neither changes what renders:
        //   - Position-degenerate: drop the triangle from the index buffer entirely. It has zero
        //     screen-space area, so omitting it is visually identical to submitting it, and it
        //     removes the degenerate input at its source instead of asking GenerateTangents() to
        //     cope with it.
        //   - UV-degenerate only: duplicate just that triangle's 3 vertices and nudge one UV by a
        //     sub-texel 1/8192 offset (invisible at any resolution this project bakes to) so
        //     GenerateTangents() has a non-degenerate UV to compute from. Every other, well-formed
        //     triangle keeps sharing the original indexed vertices exactly as before, so this
        //     cannot introduce a seam anywhere that wasn't already zero-area.
        const float DegenerateAreaEpsilon = 1e-10f;
        const float DegenerateUvAreaEpsilon = 1e-8f;
        const float UvNudge = 1f / 8192f;

        for (int t = 0; t + 2 < part.Indices.Length; t += 3)
        {
            int ia = part.Indices[t], ib = part.Indices[t + 1], ic = part.Indices[t + 2];

            var pa = positions[ia];
            var faceNormal = System.Numerics.Vector3.Cross(positions[ib] - pa, positions[ic] - pa);
            if (faceNormal.LengthSquared() < DegenerateAreaEpsilon)
            {
                continue; // zero screen-space area — dropping it changes nothing visible
            }

            var uvA = part.UVs[ia];
            var uvB = part.UVs[ib];
            var uvC = part.UVs[ic];
            float uvArea = (uvB.X - uvA.X) * (uvC.Y - uvA.Y) - (uvC.X - uvA.X) * (uvB.Y - uvA.Y);

            if (MathF.Abs(uvArea) >= DegenerateUvAreaEpsilon)
            {
                st.AddIndex(ia);
                st.AddIndex(ic);
                st.AddIndex(ib);
                continue;
            }

            int na = SubmitVertex(ia, new Godot.Vector2(uvA.X + UvNudge, 1.0f - uvA.Y));
            int nb = SubmitVertex(ib, new Godot.Vector2(uvB.X, 1.0f - uvB.Y));
            int nc = SubmitVertex(ic, new Godot.Vector2(uvC.X, 1.0f - uvC.Y));
            st.AddIndex(na);
            st.AddIndex(nc);
            st.AddIndex(nb);
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
            if (part == null || part.Morphs == null || part.Morphs.Count == 0) continue;                       // nothing to morph
            if (!visual.Parts.TryGetValue(name, out var mi) || !IsInstanceValid(mi)) continue;
            if (visual.Skeleton == null) continue;

            // ApplyShape (called just before this, for the same shape update) just repopulated
            // BoneOwnScale — evict this part's cached skin so GetPartSkin rebuilds its binds with
            // the new scale instead of reusing stale ones from the previous shape.
            visual.PartSkins.Remove(name);
            var (skin, slots) = GetPartSkin(visual, part, visual.Skeleton);
            mi.Skin = skin;

            var (positions, normals) = AvatarMorphService.Apply(part, weights);
            mi.Mesh = BuildPartMesh(part, positions, normals, slots);
        }
    }

    /// <summary>Rebuilds the Skin (bind matrices only) of every already-built SYSTEM body part, so
    /// each bind picks up the avatar's just-recomputed <see cref="AvatarVisual.BoneOwnScale"/>.
    /// The morphed geometry is left alone — it depends on the VisualParam weights, which a joint
    /// override does not touch; only the binds bake in bone scale. <c>GetPartSkin</c> walks the
    /// part's vertices in a fixed order, so the regenerated slot map is identical to the one the
    /// existing mesh's BONES array already indexes into, and swapping just the Skin is safe.
    /// (The shape-change path gets this via <see cref="RebuildBodyMorphs"/>, which evicts the same
    /// cache before rebuilding the geometry too.)</summary>
    private void RefreshBodyPartSkins(AvatarVisual visual)
    {
        if (visual.Skeleton == null) return;
        foreach (var (name, part) in visual.BodyPartData)
        {
            if (part == null) continue;
            if (!visual.Parts.TryGetValue(name, out var mi) || !IsInstanceValid(mi)) continue;
            visual.PartSkins.Remove(name);
            mi.Skin = GetPartSkin(visual, part, visual.Skeleton).Skin;
        }
    }

    /// <summary>Rebuilds the Skin (bind matrices only — a worn mesh's own vertices never change
    /// with shape) of every currently-loaded rigged attachment on <paramref name="visual"/>, so
    /// each bind picks up the avatar's just-recomputed <see cref="AvatarVisual.BoneOwnScale"/>.
    /// Called right after ApplyShape, alongside RebuildBodyMorphs, whenever shape changes — see
    /// <see cref="AvatarVisual.RiggedAttachments"/>'s doc comment for why this is needed: a mesh
    /// that loaded before shape/VisualParams first arrived was bound with no scale injected at
    /// all (an empty BoneOwnScale at that moment), and nothing else ever revisits it afterward.</summary>
    private void RebuildRiggedAttachmentSkins(AvatarVisual visual)
    {
        if (visual.Skeleton == null) return;
        foreach (var (mi, meshData, meshId) in visual.RiggedAttachments)
        {
            if (!IsInstanceValid(mi) || meshData.Skin == null) continue;
            // Only the Skin is taken from the rebuild, so the face records -- and therefore
            // BUG-RENDER-12's surface merging -- are irrelevant here; the geometry is discarded.
            var rebuilt = BuildRiggedMeshInstance(meshData, visual.Skeleton, meshId, visual, null, default, out _);
            if (rebuilt == null) continue;
            mi.Skin = rebuilt.Skin;

            // BUG-RENDER-13: and the discarded node has to be FREED, not just dropped. A Godot Node
            // is not reference-counted -- one that was never added to the tree keeps its RIDs (a
            // RendererSceneCull::Instance, and through its ArrayMesh a MeshStorage::Mesh plus the
            // index/vertex buffers) until the process exits. Letting the local go out of scope
            // leaked one set per rigged attachment per shape change, which is what produced
            // `ERROR: N RID allocations of type 'N10RendererRD11MeshStorage4MeshE' were leaked at
            // exit` with N tracking how busy the session had been (34 in a teleport-heavy one, 20
            // in a quiet one). The Skin survives: it is a Resource, and `mi` now holds it.
            rebuilt.QueueFree();
        }
    }

    /// <summary>
    /// Adds a named bind to <paramref name="skin"/> for <paramref name="boneName"/> if not
    /// already present. The bind transform is the inverse of the bone's global rest transform,
    /// WITH <paramref name="visual"/>'s own <see cref="AvatarVisual.BoneOwnScale"/> for that bone
    /// injected back in (see <see cref="InjectOwnScale"/>'s doc comment — same reasoning as
    /// BuildRiggedMeshInstance's bind computation), so the avatar mesh appears unchanged, at this
    /// avatar's own shape, when the skeleton is in T-pose.
    /// </summary>
    private static void AddSkinSlot(
        string? boneName, Skin skin, Skeleton3D skeleton, Dictionary<string, int> skinSlots, AvatarVisual visual)
    {
        if (boneName == null || skinSlots.ContainsKey(boneName)) return;

        int boneIdx = skeleton.FindBone(boneName);
        if (boneIdx < 0) return;

        // AffineInverse (not Inverse): Inverse() assumes an orthonormal basis and only transposes
        // it, which is wrong once a bone's own scale is injected below (same reasoning as
        // BuildRiggedMeshInstance's identical bind computation).
        Transform3D bind = ComputeGlobalRestTransform(skeleton, boneIdx).AffineInverse();
        if (visual.BoneOwnScale.TryGetValue(boneName, out var ownScale))
            bind = InjectOwnScale(bind, ownScale);

        // Bind by explicit skeleton bone index rather than by name. Named binds rely on
        // a name-resolution pass that has proven unreliable here; AddBind ties the skin
        // slot directly to the bone that drives it. The slot index is the bind's position.
        int slot = skin.GetBindCount();
        skin.AddBind(boneIdx, bind);
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

    // Standing dev tools (F7/F8/F9) for diagnosing avatar rendering/lighting bugs — kept
    // permanently rather than ripped out once the M4-8/winding-order investigations that
    // motivated them concluded, since the same techniques generalize to any future rendering
    // bug on this renderer. Pair with Boot.cs's sun gizmo (F5) and post-FX toggle (F2).
    private bool _shadowsDisabled = false;
    private bool _ndotlDebugActive = false;
    private readonly Dictionary<MeshInstance3D, Material?> _ndotlOriginalMaterials = new();
    private bool _tposeActive = false;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey keyEvt && keyEvt.Pressed && !keyEvt.Echo)
        {
            if (keyEvt.Keycode == Key.F9)
                ToggleNdotLDebugMaterial();
            else if (keyEvt.Keycode == Key.F7)
                ToggleAvatarShadowCasting();
            else if (keyEvt.Keycode == Key.F8)
                ToggleTPose();
            return;
        }

        // Plain left-click (not Alt+LMB, which AvatarController reserves for camera orbit):
        // touch whatever HUD prim is under the cursor, if any. This uses _Input (fires for every
        // event regardless of GUI consumption), so it had NO guard against clicking a window that
        // happens to render over the same screen area as a worn HUD attachment (e.g. a body-shape
        // HUD) -- live-tested: clicking inside the Inventory panel also sent a touch to whatever
        // HUD prim sat behind it on screen. GuiGetHoveredControl() is the same direct check used
        // for the analogous world-click leak in ObjectSelectionController.
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left && !mb.AltPressed)
        {
            var hovered = GetViewport().GuiGetHoveredControl();
            if (hovered == null)
            {
                if (TryClickHud(mb.Position))
                {
                    GetViewport().SetInputAsHandled();
                }
            }
            else
            {
                // Named, because "which Control" is the whole question when a HUD stops
                // responding: the SubViewportContainer itself is set to MouseFilter.Ignore
                // precisely so it never lands here, so anything reported is something else
                // covering the screen.
                GD.Print($"[HUD] click at {mb.Position} swallowed by GUI control "
                         + $"'{hovered.Name}' ({hovered.GetType().Name})");
            }
        }
    }

    /// <summary>Raycasts a screen click into the HUD overlay's own isolated World3D (via its own
    /// orthographic camera) and, on a hit, sends an SL touch (GridSession.ClickObjectAsync — a
    /// grab/de-grab pair, which is what fires touch_start/touch_end on the object's script) for
    /// the entity the hit collision body is tagged with. Returns true if a HUD collider was hit
    /// and processed so caller can mark input as handled.</summary>
    private bool TryClickHud(Godot.Vector2 screenPos)
    {
        if (_hudViewport == null || _session == null || _world == null)
        {
            GD.Print($"[HUD] click ignored: viewport={_hudViewport != null} "
                     + $"session={_session != null} world={_world != null}");
            return false;
        }
        var cam = _hudViewport.GetCamera3D();
        if (cam == null)
        {
            GD.Print("[HUD] click ignored: viewport has no Camera3D");
            return false;
        }

        // FindWorld3D(), NOT the World3D property: with OwnWorld3D the viewport's children live
        // in an internal COPY of the world, and only FindWorld3D returns that copy — see the
        // comment where the viewport is created for the two dead ends this replaced.
        var world3d = _hudViewport.FindWorld3D();
        if (world3d == null)
        {
            GD.PrintErr("[HUD] click: viewport has no effective World3D yet — ignoring");
            return false;
        }
        var spaceState = world3d.DirectSpaceState;
        if (spaceState == null)
        {
            GD.PrintErr("[HUD] click: World3D has no DirectSpaceState yet — ignoring");
            return false;
        }

        var from = cam.ProjectRayOrigin(screenPos);
        var dir = cam.ProjectRayNormal(screenPos);
        var query = PhysicsRayQueryParameters3D.Create(from, from + dir * 20f);
        query.HitBackFaces = false;
        var hit = spaceState.IntersectRay(query);

        // Every exit below used to be silent, which is why "the HUD does not react" could not be
        // told apart from "the click never got here".
        //
        // Unconditional GD.Print, deliberately, and not behind --diag: the SUCCESS line below
        // already prints unconditionally, so gating only the failures made a broken click quieter
        // than a working one -- backwards, and it cost a round trip to discover. A click is
        // user-initiated and rare, so one line per click is not the per-asset spam the quiet-log
        // decision was about.
        if (hit.Count == 0)
        {
            GD.Print($"[HUD] click at {screenPos}: ray missed every collider "
                     + $"({_hudPlacements.Count} HUD attachment(s) placed)");
            return false;
        }

        if (hit["collider"].As<Node>() is not { } collider || !collider.HasMeta("EntityId"))
        {
            GD.Print("[HUD] click hit a body with no EntityId meta -- it is not one of ours");
            return false;
        }
        if (!Guid.TryParse(collider.GetMeta("EntityId").AsString(), out var entityId))
        {
            GD.Print($"[HUD] click hit '{collider.Name}' whose EntityId meta does not parse");
            return false;
        }

        var entity = _world.GetEntity(entityId);
        if (entity == null)
        {
            GD.Print($"[HUD] click hit entity {entityId:N}, which is no longer in the world");
            return false;
        }

        // Convert hit position and normal from Godot to SL space: SL (X, Y, Z) = Godot (X, -Z, Y)
        var hitPosGodot = hit.TryGetValue("position", out var hp) ? hp.AsVector3() : Godot.Vector3.Zero;
        var hitNormGodot = hit.TryGetValue("normal", out var hn) ? hn.AsVector3() : Godot.Vector3.Zero;
        var hitPosSl = new System.Numerics.Vector3(hitPosGodot.X, -hitPosGodot.Z, hitPosGodot.Y);
        var hitNormSl = new System.Numerics.Vector3(hitNormGodot.X, -hitNormGodot.Z, hitNormGodot.Y);

        int hitFaceIndex = 0;
        var hitUvSl = System.Numerics.Vector3.Zero;
        int hitTriIdx = hit.TryGetValue("face_index", out var fi) ? fi.AsInt32() : -1;
        if (_hudTriangles.TryGetValue(entityId, out var triMap) && hitTriIdx >= 0 && hitTriIdx < triMap.Length)
        {
            var tri = triMap[hitTriIdx];
            hitFaceIndex = tri.FaceIndex;

            var localHit = collider is Node3D cNode ? cNode.ToLocal(hitPosGodot) : hitPosGodot;
            var v0 = tri.P1 - tri.P0;
            var v1 = tri.P2 - tri.P0;
            var v2 = localHit - tri.P0;
            float d00 = v0.Dot(v0);
            float d01 = v0.Dot(v1);
            float d11 = v1.Dot(v1);
            float d20 = v2.Dot(v0);
            float d21 = v2.Dot(v1);
            float denom = d00 * d11 - d01 * d01;
            float v = System.MathF.Abs(denom) > 1e-8f ? (d11 * d20 - d01 * d21) / denom : 0f;
            float w = System.MathF.Abs(denom) > 1e-8f ? (d00 * d21 - d01 * d20) / denom : 0f;
            float u = 1.0f - v - w;

            var uv = u * tri.UV0 + v * tri.UV1 + w * tri.UV2;
            hitUvSl = new System.Numerics.Vector3(uv.X, uv.Y, 0f);
        }

        // BUG-AVATAR-07: clicking a reference prim is the cheapest way to get a hard, world-space
        // number to compare an avatar's rendered height against — the prim is at the same Z in
        // every viewer, so "where does its lower edge sit on the head" needs no camera assumptions.
        // Printed for any clicked object; the Z/size pair is what makes it usable.
        var clickedT = entity.GetComponent<TransformComponent>();
        var clickedP = entity.GetComponent<PrimitiveComponent>();
        string where = clickedT != null
            ? $" pos=({clickedT.Position.X:0.###}, {clickedT.Position.Y:0.###}, {clickedT.Position.Z:0.###})"
            : "";
        string how = clickedP != null
            ? $" scale=({clickedP.Scale.X:0.###}, {clickedP.Scale.Y:0.###}, {clickedP.Scale.Z:0.###})" +
              (clickedT != null ? $" -> Z bottom {clickedT.Position.Z - clickedP.Scale.Z / 2f:0.###} top {clickedT.Position.Z + clickedP.Scale.Z / 2f:0.###}" : "")
            : "";
        GD.Print($"[HUD] clicked entity {entityId:N} (LocalId {entity.LocalId}) face={hitFaceIndex} uv=({hitUvSl.X:0.###}, {hitUvSl.Y:0.###}){where}{how}");

        _ = _session.ClickObjectAsync(
            entity.LocalId,
            faceIndex: hitFaceIndex,
            position: hitPosSl,
            normal: hitNormSl,
            uvCoord: hitUvSl,
            stCoord: hitUvSl);
        return true;
    }

    /// <summary>F8: freeze every avatar in its rest (T-)pose and stop animation playback, so a
    /// before/after comparison (e.g. F9's debug material) can be screenshotted from a pixel-
    /// identical frame — idle-sway/breathing animation otherwise shifts the pose between two
    /// shots taken moments apart, which reads as noise indistinguishable from a real bug.</summary>
    private void ToggleTPose()
    {
        _tposeActive = !_tposeActive;
        if (_tposeActive)
        {
            foreach (var visual in _visuals.Values)
                visual.Skeleton?.ResetBonePoses();
        }
        GD.Print($"[DBG-LIGHT] T-pose freeze {(_tposeActive ? "ON — animations paused" : "OFF — animations resumed")}.");
    }

    /// <summary>Runs <paramref name="action"/> on every currently-loaded avatar's MeshInstance3D
    /// nodes: direct children of the Skeleton3D (system body parts, rigged attachments) plus one
    /// level of nesting (static/non-rigged attachment meshes and procedural box-man parts, which
    /// sit under a BoneAttachment3D child of the skeleton). Returns how many were touched.</summary>
    private int ForEachAvatarMeshInstance(System.Action<MeshInstance3D> action)
    {
        int count = 0;
        foreach (var visual in _visuals.Values)
        {
            if (visual.Skeleton == null) continue;
            foreach (var node in visual.Skeleton.GetChildren())
            {
                if (node is MeshInstance3D directMi) { action(directMi); count++; }
                foreach (var grandchild in node.GetChildren())
                    if (grandchild is MeshInstance3D nestedMi) { action(nestedMi); count++; }
            }
        }
        return count;
    }

    /// <summary>Toggles GeometryInstance3D.CastShadow off/on for every avatar mesh, to isolate
    /// whether a specific dark patch (e.g. a hand-shaped shadow on a coat) is a true shadow-map
    /// self-shadow versus something else (lighting/AO — both already ruled out separately). If
    /// the patch disappears with shadows off, it's a genuine self-shadow (likely a bias/precision
    /// issue between two close skinned meshes); if it persists, the cause is elsewhere.</summary>
    private void ToggleAvatarShadowCasting()
    {
        _shadowsDisabled = !_shadowsDisabled;
        var setting = _shadowsDisabled ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;
        int count = ForEachAvatarMeshInstance(mi => mi.CastShadow = setting);
        GD.Print($"[DBG-LIGHT] avatar shadow-casting {(_shadowsDisabled ? "OFF" : "ON")} for {count} mesh instances.");
    }

    /// <summary>Toggles every currently-loaded avatar mesh's material between its real material
    /// and an unshaded debug shader that colors each surface by N·L against the scene's actual
    /// DirectionalLight3D direction (read live from that node — not hand-derived from its
    /// RotationDegrees, to rule out a rotation-order mistake in the diagnostic itself): GREEN
    /// where the surface faces the light (should look lit), RED where it faces away (should look
    /// shadowed/dark). Toggling (rather than one-way) lets the SAME frozen camera angle be
    /// screenshotted debug-on and debug-off back to back — comparing across two different poses/
    /// angles isn't a valid pixel check. Each mesh's original MaterialOverride is remembered the
    /// first time it's seen and restored on toggle-off (MaterialOverride takes priority over
    /// per-surface materials, so touching only it is enough either way).</summary>
    private void ToggleNdotLDebugMaterial()
    {
        _ndotlDebugActive = !_ndotlDebugActive;

        if (!_ndotlDebugActive)
        {
            int restored = ForEachAvatarMeshInstance(mi =>
            {
                if (_ndotlOriginalMaterials.TryGetValue(mi, out var orig)) mi.MaterialOverride = orig;
            });
            GD.Print($"[DBG-LIGHT] NdotL debug OFF — restored {restored} mesh instances to their real materials.");
            return;
        }

        var sun = GetTree().Root.FindChild("DirectionalLight3D", true, false) as DirectionalLight3D;
        if (sun == null)
        {
            GD.PrintErr("[DBG-LIGHT] no DirectionalLight3D found — can't build NdotL debug material");
            _ndotlDebugActive = false;
            return;
        }

        // Godot convention: a light's local -Z is the direction it shines TOWARD; the direction
        // FROM a lit surface TOWARD the light source is therefore +Z in its own world basis.
        var towardLight = sun.GlobalTransform.Basis.Z;

        var shader = new Shader
        {
            Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform vec3 debug_light_to_dir = vec3(0.0, 1.0, 0.0);

void fragment() {
    vec3 n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    float ndotl = dot(n, normalize(debug_light_to_dir));
    ALBEDO = vec3(max(-ndotl, 0.0), max(ndotl, 0.0), 0.0);
}
"
        };
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("debug_light_to_dir", towardLight);

        int swapped = ForEachAvatarMeshInstance(mi =>
        {
            if (!_ndotlOriginalMaterials.ContainsKey(mi)) _ndotlOriginalMaterials[mi] = mi.MaterialOverride;
            mi.MaterialOverride = mat;
        });

        GD.Print($"[DBG-LIGHT] NdotL debug ON for {swapped} mesh instances. towardLight(world)={towardLight}. " +
                 "GREEN = facing the light (should look lit), RED = facing away (should look shadowed). " +
                 "Press F9 again (same camera angle!) to compare against the real materials.");
    }

    private double _cullAccum = 0;

    public override void _Process(double delta)
    {
        using var _phase = MainThreadPhase.Enter("avatar-render");

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
        
        var viewport = GetViewport();
        var camera = viewport?.GetCamera3D();
        Godot.Vector3? camPos = camera?.GlobalPosition;
        float nameTagMaxDist = 20.0f;
        float nameTagFadeStart = 15.0f;

        foreach (var visual in _visuals.Values)
        {
            if (doCull && haveAgent)
            {
                bool visible = visual.Root.Position.DistanceSquaredTo(agentPos) <= maxSq;
                if (visual.Root.Visible != visible) visual.Root.Visible = visible;
            }

            if (visual.Root.Visible && visual.AnimPlayer.IsPlaying && !_tposeActive)
            {
                visual.AnimPlayer.Advance(dt);
            }

            if (camPos.HasValue && visual.NameTag != null && IsInstanceValid(visual.NameTag))
            {
                if (global::Boot.IsLoadingScreenVisible)
                {
                    if (visual.NameTag.Visible) visual.NameTag.Visible = false;
                    continue;
                }

                float dist = visual.Root.GlobalPosition.DistanceTo(camPos.Value);
                if (dist > nameTagMaxDist)
                {
                    if (visual.NameTag.Visible) visual.NameTag.Visible = false;
                }
                else
                {
                    var headPos3D = visual.Root.GlobalPosition + new Godot.Vector3(0, 2.3f, 0);
                    if (camera!.IsPositionBehind(headPos3D))
                    {
                        if (visual.NameTag.Visible) visual.NameTag.Visible = false;
                    }
                    else
                    {
                        if (!visual.NameTag.Visible) visual.NameTag.Visible = true;
                        
                        var pos2D = camera.UnprojectPosition(headPos3D);
                        
                        // Godot 4 Vulkan bug: if UnprojectPosition yields NaN, Infinity, or massively huge coordinates
                        // (e.g. when the point is extremely close to the camera plane), assigning it to a Control's 
                        // Position will crash the UI_PASS in nvoglv64.dll due to vertex bounds overflow.
                        if (float.IsNaN(pos2D.X) || float.IsNaN(pos2D.Y) || 
                            float.IsInfinity(pos2D.X) || float.IsInfinity(pos2D.Y) ||
                            Mathf.Abs(pos2D.X) > 100000f || Mathf.Abs(pos2D.Y) > 100000f)
                        {
                            visual.NameTag.Visible = false;
                            continue;
                        }

                        var size = visual.NameTag.GetMinimumSize();
                        visual.NameTag.Position = pos2D - (size / 2);

                        if (dist > nameTagFadeStart)
                        {
                            float alpha = 1.0f - ((dist - nameTagFadeStart) / (nameTagMaxDist - nameTagFadeStart));
                            visual.NameTag.Modulate = new Godot.Color(1, 1, 1, alpha);
                        }
                        else
                        {
                            visual.NameTag.Modulate = new Godot.Color(1, 1, 1, 1);
                        }
                    }
                }
            }
        }
    }

    private async System.Threading.Tasks.Task LoadAndStartAnimationsAsync(AvatarVisual visual, List<Guid> animIds)
    {
        if (_assetService == null) return;

        // Logger.Debug($"[AvatarRenderer] Loading {animIds.Count} animation(s): {string.Join(", ", animIds)}");

        var loaded = new List<(Guid id, AnimationData data)>();
        foreach (var animId in animIds)
        {
            try
            {
                var data = await _assetService.GetAnimationAsync(animId);
                if (data != null)
                {
                    loaded.Add((animId, data));
                }
                else if (SelfLocomotion.All.Contains(animId))
                {
                    // FEAT-ANIM-01 diagnostic: a built-in locomotion id the prediction emitted
                    // failed to resolve as an asset on this grid -> the gait can't play, and a
                    // res:// bundled copy is needed.
                    GD.Print($"[Locomotion] anim {animId} did not resolve as an asset -- gait will not play");
                }
            }
            catch (Exception)
            {
            }
        }

        // Logger.Debug($"[AvatarRenderer] Starting {loaded.Count}/{animIds.Count} animation(s)");

        // Apply on main thread via CallDeferred
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => {
            if (visual.Root == null || !IsInstanceValid(visual.Root)) return;
            visual.AnimPlayer.SetActiveAnimations(loaded);
        }, label: "avatar.animations");
    }

}
