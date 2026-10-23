using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// FEAT-PERF-23 Phase 1: "outfit merge skins like separate items". A remote avatar on the real Bento
    /// skeleton wears three rigged items through the real commit path (<see cref="CommitPreparedRig"/>):
    /// A and B in one linkset (B the root), C in another. A's and B's first faces are the same record;
    /// B has two blended faces, C one. Then:
    ///
    /// <list type="number">
    /// <item>The merge runs on a worker and swaps in: two buckets (A+B, C), three sorted instances, one
    /// outfit skin and one engine skin binding, no staging instance left.</item>
    /// <item>Every merged vertex, skinned on the CPU at the rest pose through the outfit skin, lands
    /// within 1e-5 m of where the item's own skin put it; also after a shape change re-binds the
    /// outfit skin in place.</item>
    /// <item>Sorted offsets are exactly today's (the split children's), materials are the items' own
    /// objects, and texture refs are unchanged by the merge.</item>
    /// <item>A face change on a merged item holds its new instance hidden while the bucket draws the
    /// old one, then swaps; detaching one prim re-merges its bucket; detaching a whole linkset frees
    /// its bucket without a job and releases its texture refs like a detach always did.</item>
    /// <item>Switching the setting off puts the remaining item back on its own instance and drops the
    /// merge state.</item>
    /// </list>
    /// Needs the renderer in a scene tree; nothing is fetched (no asset service).
    /// </summary>
    internal (bool Passed, string Detail) SelfTestOutfitMerge()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        const ulong Region = 11437119954698752UL;
        var problems = new List<string>();
        var priorWorld = _world;
        var priorCache = _gpuCache;
        bool priorSetting = RenderConfig.OutfitMerge;
        bool priorKeep = _outfitMergeKeepArrays;
        var world = new World();
        _world = world;
        var cache = new GpuCache(64L << 20);
        _gpuCache = cache;
        AvatarVisual? visual = null;
        var worn = new List<Guid>();
        string summary = "";

        try
        {
            RenderConfig.OutfitMerge = true;
            _outfitMergeApplied = true;
            _outfitMergeKeepArrays = true;

            var wearer = world.GetOrCreateEntity(Region, 1);
            wearer.SetComponent(new AvatarComponent(Guid.NewGuid(), "Merge", "Test", false));
            visual = new AvatarVisual { AgentId = wearer.GetComponent<AvatarComponent>()!.AgentId, EntityId = wearer.Id };
            AddChild(visual.Root);
            var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
            skeleton.Name = "Skeleton3D";
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;
            ApplyShape(visual, skeleton, _avatarSkeleton, visual.LastDistortions);
            skeleton.ResetBonePoses();
            // The avatar's own scale on two bones, so the binds carry InjectOwnScale.
            visual.BoneOwnScale["mChest"] = new System.Numerics.Vector3(1.1f, 0.9f, 1.05f);
            visual.BoneOwnScale["mNeck"] = new System.Numerics.Vector3(0.95f, 1.0f, 1.2f);
            _visuals[wearer.Id] = visual;
            var state0 = visual; // for the closures below

            Guid Worn(uint localId, uint parentLocalId)
            {
                var e = world.GetOrCreateEntity(Region, localId);
                e.SetComponent(new TransformComponent { ParentLocalId = parentLocalId });
                e.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0));
                e.SetComponent(new AttachmentComponent(wearer.Id, 1));
                worn.Add(e.Id);
                return e.Id;
            }

            // Shared IBMs between items, an identity (rest marker) on the pelvis.
            var ibmTorso = System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -1.2f);
            var ibmChest = System.Numerics.Matrix4x4.CreateRotationZ(0.1f) * System.Numerics.Matrix4x4.CreateTranslation(0.01f, 0f, -1.35f);
            var ibmNeck = System.Numerics.Matrix4x4.CreateTranslation(0f, 0.02f, -1.55f);
            var meshA = TestMesh(new[] { "mPelvis", "mTorso", "mChest" }, new[] { System.Numerics.Matrix4x4.Identity, ibmTorso, ibmChest }, faces: 2, x: -0.2f);
            var meshB = TestMesh(new[] { "mChest", "mNeck", "mTorso" }, new[] { ibmChest, ibmNeck, ibmTorso }, faces: 3, x: 0f);
            var meshC = TestMesh(new[] { "mNeck", "mPelvis" }, new[] { ibmNeck, System.Numerics.Matrix4x4.Identity }, faces: 2, x: 0.2f);

            FaceTexture Face(Guid tex, float alpha) =>
                new(tex, Guid.Empty, Guid.Empty, new System.Numerics.Vector4(1f, 1f, 1f, alpha), 1f, 1f, 0f, 0f, 0f);
            var texShared = Guid.NewGuid();
            var texA = Guid.NewGuid();
            var texC = Guid.NewGuid();
            var shared = Face(texShared, 1f);
            var facesA = new[] { shared, Face(texA, 1f) };
            var facesB = new[] { shared, Face(Guid.NewGuid(), 0.5f), Face(Guid.NewGuid(), 0.5f) };
            var facesC = new[] { Face(texC, 1f), Face(Guid.NewGuid(), 0.5f) };

            // Texture refs as RecordAvatarTexture leaves them for A's own and C's own texture.
            var pixels = new TextureData(4, 4, new byte[4 * 4 * 4], false, 4, 4);
            foreach (var tex in new[] { texA, texC })
            {
                cache.SelfTestUpload(tex, pixels, 0f, true, initialRefCount: 1);
                visual.PinnedTextureIds.Add(tex);
                visual.UsedTextureIds.Add(tex);
            }
            visual.TexturesPinned = true;

            uint rootL1 = 100, rootL2 = 200;
            var b = Worn(rootL1, parentLocalId: 1);
            var a = Worn(101, parentLocalId: rootL1);
            var c = Worn(rootL2, parentLocalId: 1);
            var meshes = new Dictionary<Guid, (MeshData Data, FaceTexture[] Faces)>
            {
                [a] = (meshA, facesA),
                [b] = (meshB, facesB),
                [c] = (meshC, facesC),
            };
            foreach (var id in new[] { a, b, c }) CommitTestItem(id, meshes[id].Data, meshes[id].Faces);
            DrainQueue();
            TickWornAlphaOrder(); // BUG-RENDER-50: what _Process does every frame

            // --- today's reference: the staging instances ------------------------------------------
            var palettes = new Dictionary<Guid, Transform3D[]>();
            var materials = new Dictionary<(Guid, int), Material>();
            var offsets = new Dictionary<(Guid, int), float>();
            foreach (var id in new[] { a, b, c })
            {
                if (!_riggedAttachments.TryGetValue(id, out var mi) || mi.Skin == null || mi.Mesh is not ArrayMesh mesh)
                {
                    problems.Add($"item {MergeShort(id)} has no staging instance after its commit");
                    continue;
                }
                palettes[id] = Palette(skeleton, mi.Skin);
                // The retained managed arrays are exactly what the staging instance was given.
                if (!_mergeItems.TryGetValue(id, out var retained) || retained.Rig == null)
                    problems.Add($"item {MergeShort(id)} kept no arrays for the merge");
                else if (!MeshSurfaceCache.TryGetSurfaces(mesh, out var given) || !SameStreams(given, retained.Rig))
                    problems.Add($"item {MergeShort(id)}: the retained arrays differ from the ones its instance was built from");
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    var split = mi.GetNodeOrNull<MeshInstance3D>(SortedSurfaceName(s));
                    materials[(id, s)] = split != null ? split.GetSurfaceOverrideMaterial(0) : mi.GetSurfaceOverrideMaterial(s);
                    offsets[(id, s)] = split?.SortingOffset ?? mi.SortingOffset;
                }
            }
            if (!_riggedAttachments.TryGetValue(b, out var bStaging) || bStaging.GetNodeOrNull(SortedSurfaceName(1)) == null
                || bStaging.GetNodeOrNull(SortedSurfaceName(2)) == null)
                problems.Add("B's two blended surfaces were not split onto their own instances before the merge");
            var refsBefore = (cache.SelfTestGetRefCount(texA), cache.SelfTestGetRefCount(texC), visual.PinnedTextureIds.Count, visual.UsedTextureIds.Count);

            // --- 1. merge ----------------------------------------------------------------------------
            if (!WaitMerged(visual, () => new[] { a, b, c }.All(id => _mergeItems.TryGetValue(id, out var it) && it.InBucket)))
                problems.Add("the merge did not complete within 10 s");
            var state = visual.OutfitMerge;
            if (state == null) return (false, string.Join("; ", problems.Append("no merge state on the avatar")));

            foreach (var id in new[] { a, b, c })
                if (_riggedAttachments.ContainsKey(id)) problems.Add($"item {MergeShort(id)} still has an instance of its own while merged");
            if (bStaging != null && IsInstanceValid(bStaging) && bStaging.GetParent() != null) problems.Add("B's staging instance is still in the tree");

            var l1 = state.Buckets.GetValueOrDefault(rootL1);
            var l2 = state.Buckets.GetValueOrDefault(rootL2);
            int Surfaces(OutfitBucket? bk) => bk == null ? -1 : bk.Instances.Sum(i => (i.Mesh as ArrayMesh)?.GetSurfaceCount() ?? 0);
            if (Surfaces(l1) != 2 || l1!.Sorted.Count != 2) problems.Add($"bucket A+B: {Surfaces(l1)} merged surface(s) and {l1?.Sorted.Count} sorted, expected 2 and 2");
            if (Surfaces(l2) != 1 || l2!.Sorted.Count != 1) problems.Add($"bucket C: {Surfaces(l2)} merged surface(s) and {l2?.Sorted.Count} sorted, expected 1 and 1");

            var nodes = state.Buckets.Values.SelectMany(bk => bk.Instances.Concat(bk.Sorted)).ToList();
            if (nodes.Count != 5) problems.Add($"{nodes.Count} merged instances, expected 5");
            if (state.Skin == null || nodes.Any(n => !ReferenceEquals(n.Skin, state.Skin))) problems.Add("not every merged instance holds the one outfit skin");
            var refs = nodes.Select(n => n.GetSkinReference()?.GetInstanceId() ?? 0).Distinct().ToList();
            if (refs.Count != 1 || refs[0] == 0) problems.Add($"the merged instances got {refs.Count} engine skin bindings, expected 1");
            // Pelvis(rest), torso, chest, neck: four distinct (bone, ibm) keys over seven item slots.
            if (state.Slots.Count != 4) problems.Add($"outfit skin has {state.Slots.Count} slots, expected 4 (exact dedupe)");

            CheckSkinnedLikeItems(state, skeleton, palettes, problems, "merged");
            CheckSortedAndMaterials(state, offsets, materials, problems);

            var refsAfter = (cache.SelfTestGetRefCount(texA), cache.SelfTestGetRefCount(texC), visual.PinnedTextureIds.Count, visual.UsedTextureIds.Count);
            if (refsAfter != refsBefore) problems.Add($"texture refs changed by the merge: {refsBefore} -> {refsAfter}");
            summary = DescribeOutfitMergeCost();

            // --- 2. shape change: the outfit skin is re-bound in place ----------------------------------
            visual.BoneOwnScale["mTorso"] = new System.Numerics.Vector3(1.2f, 1.1f, 0.9f);
            var skinBefore = state.Skin;
            RebuildRiggedAttachmentSkins(visual);
            if (!ReferenceEquals(state.Skin, skinBefore)) problems.Add("the shape change replaced the outfit skin instead of re-binding it");
            var fresh = new Dictionary<Guid, Transform3D[]>();
            foreach (var id in new[] { a, b, c })
                fresh[id] = Palette(skeleton, BindRiggedSkin(meshes[id].Data.Skin!, skeleton, visual, out _)!);
            CheckSkinnedLikeItems(state, skeleton, fresh, problems, "after the shape change");

            // --- 3. a face change on merged B: held, then swapped -------------------------------------
            var facesB2 = new[] { Face(Guid.NewGuid(), 1f), facesB[1], facesB[2] };
            meshes[b] = (meshB, facesB2);
            CommitTestItem(b, meshB, facesB2);
            if (!_riggedAttachments.TryGetValue(b, out var held) || held.Visible || !held.Skeleton.IsEmpty)
                problems.Add("B's new instance is drawn (or bound) while its bucket still draws the old B");
            if (!state.Buckets.TryGetValue(rootL1, out var l1Pending) || !l1Pending.Members.Contains(b))
                problems.Add("B left its bucket before its new version was ready");
            DrainQueue();
            if (held != null && IsInstanceValid(held) && held.Visible) problems.Add("B's held instance was shown by its material pass");
            if (!WaitMerged(visual, () => !_riggedAttachments.ContainsKey(b) && _mergeItems.TryGetValue(b, out var it) && it.InBucket))
                problems.Add("B's face change was not merged within 10 s");
            if (Surfaces(state.Buckets.GetValueOrDefault(rootL1)) != 3)
                problems.Add($"after B's face change bucket A+B has {Surfaces(state.Buckets.GetValueOrDefault(rootL1))} merged surfaces, expected 3");
            palettes[b] = fresh[b];
            CheckSkinnedLikeItems(state, skeleton, fresh, problems, "after B's face change");

            // --- 4. detach one prim (A): its bucket is re-merged ----------------------------------------
            RemoveVisual(a);
            if (!WaitMerged(visual, () => state.Buckets.TryGetValue(rootL1, out var bk) && !bk.Members.Contains(a)))
                problems.Add("detaching A did not re-merge its bucket within 10 s");
            if (Surfaces(state.Buckets.GetValueOrDefault(rootL1)) != 1 || state.Buckets.GetValueOrDefault(rootL1)?.Sorted.Count != 2)
                problems.Add("after detaching A its bucket does not hold B alone");
            if (cache.SelfTestGetRefCount(texA) != 0 || visual.PinnedTextureIds.Contains(texA))
                problems.Add("A's texture ref was not released by its detach");

            // --- 5. detach a whole linkset (C): freed at once, no job ------------------------------------
            var l2Nodes = state.Buckets.GetValueOrDefault(rootL2)?.Instances.Concat(state.Buckets[rootL2].Sorted).ToList() ?? new();
            RemoveVisual(c);
            TickOutfitMerge();
            if (state.Buckets.ContainsKey(rootL2)) problems.Add("detaching C's whole linkset left its bucket");
            if (state.JobInFlight) problems.Add("detaching a whole linkset started a merge job");
            if (l2Nodes.Any(n => IsInstanceValid(n) && n.GetParent() != null)) problems.Add("C's bucket instances are still in the tree");
            if (cache.SelfTestGetRefCount(texC) != 0) problems.Add("C's texture ref was not released by its detach");

            // --- 6. setting off: B goes back to an instance of its own ----------------------------------
            RenderConfig.OutfitMerge = false;
            if (!WaitMerged(visual, () => visual.OutfitMerge == null && _riggedAttachments.ContainsKey(b)))
                problems.Add("switching the merge off did not restore B within 10 s");
            if (!_riggedAttachments.TryGetValue(b, out var restored) || !restored.Visible || restored.Skeleton.IsEmpty)
                problems.Add("B is not drawn on its own instance after the switch");
            // BUG-RENDER-50: A and C were taken off since, so B moved up the alpha order: its restored split
            // surface sits where its CURRENT rank puts it, exactly where a fresh rig would.
            else if (restored.GetNodeOrNull<MeshInstance3D>(SortedSurfaceName(1))?.SortingOffset
                     != WornAlphaOrder.SurfaceOffset(WornAlphaBaseOf(visual, b, float.NaN), 1))
                problems.Add("B's restored split surface does not sit at its rank's offset");
            if (skeleton.GetChildren().OfType<MeshInstance3D>().Any(n => n.Name.ToString().StartsWith(OutfitBucketName, StringComparison.Ordinal)
                                                                         || n.Name.ToString().StartsWith(OutfitSortedName, StringComparison.Ordinal)))
                problems.Add("merged instances are left in the tree after the switch");
            if (_mergeItems.Count != 0 || _mergeHeld.Count != 0) problems.Add($"{_mergeItems.Count} merge item(s) / {_mergeHeld.Count} held instance(s) left after the switch");
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.OutfitMerge = priorSetting;
            _outfitMergeKeepArrays = priorKeep;
            WaitRigWorkersIdle();
            DrainQueue();
            if (visual != null) UnloadOutfitMerge(visual);
            foreach (var id in worn)
            {
                _pendingRigs.TryRemove(id, out _);
                _preparedRigs.TryRemove(id, out _);
                _riggedAttachments.Remove(id);
                _attachmentToAvatar.Remove(id);
                _attachmentMeshIds.Remove(id);
            }
            _mergeItems.Clear();
            _mergeHeld.Clear();
            _mergeTouched.Clear();
            _outfitMergeKept.Clear();
            _outfitMergeApplied = false;
            if (visual != null)
            {
                _visuals.Remove(visual.EntityId);
                visual.Root.GetParent()?.RemoveChild(visual.Root);
                visual.Root.Free();
            }
            _world = priorWorld;
            _gpuCache = priorCache;
        }

        return problems.Count == 0
            ? (true, $"3 items in 2 linksets merged onto one skin (4 of 7 slots after dedupe), skinned within 1e-5 m of their own skins " +
                     $"before and after a shape change, sorted offsets and materials as before, refs unchanged; held face change, " +
                     $"prim detach, linkset detach and switch-off behave | {summary}")
            : (false, string.Join("; ", problems));

        void CommitTestItem(Guid id, MeshData data, FaceTexture[] faces)
        {
            var meshId = Guid.NewGuid();
            _attachmentToAvatar[id] = visual!.EntityId;
            visual.WornAttachmentEntities.Add(id);
            _attachmentMeshIds[id] = (meshId, faces, default, visual.EntityId, MeshDetailLevel.High);
            visual.WornAttachmentFaces[id] = (faces, default);
            var req = new PendingRig(data, visual, visual.Skeleton!, meshId, faces, default, _avatarSkeleton, false);
            var definition = _avatarSkeleton!;
            var slots = RiggedMeshBuilder.SlotsForJoints(data.Skin!.JointNames,
                n => definition.GetBone(definition.ResolveBoneName(n)) != null);
            var prepared = PrepareRiggedMesh(data, slots, faces, default, wantPickChunks: false, keepTangents: true);
            _pendingRigs[id] = req;
            _preparedRigs[id] = new PreparedRig(req, prepared, 0);
            CommitPreparedRig(id);
        }
    }

    private static string MergeShort(Guid id) => id.ToString("N")[..8];

    /// <summary>A rigged test mesh: one 3x3 grid per face, side by side at <paramref name="x"/>, every vertex
    /// weighted between two of the joints (and the pelvis-side first joint on the orphan-free path).</summary>
    private static MeshData TestMesh(string[] joints, System.Numerics.Matrix4x4[] ibms, int faces, float x)
    {
        var submeshes = new List<MeshSubmesh>();
        for (int f = 0; f < faces; f++)
        {
            var positions = new List<System.Numerics.Vector3>();
            var uvs = new List<System.Numerics.Vector2>();
            var weights = new List<VertexBoneWeights>();
            for (int r = 0; r < 3; r++)
            {
                for (int col = 0; col < 3; col++)
                {
                    positions.Add(new System.Numerics.Vector3(x + col * 0.05f, f * 0.03f + 0.1f, 1.0f + r * 0.25f));
                    uvs.Add(new System.Numerics.Vector2(col * 0.5f, r * 0.5f));
                    float w = (r * 3 + col) / 8f;
                    int j1 = (r + f) % joints.Length;
                    int j2 = (r + f + 1) % joints.Length;
                    weights.Add(new VertexBoneWeights(j1, j2, 0, 0, 1f - 0.5f * w, 0.5f * w, 0f, 0f));
                }
            }
            var indices = new List<int>();
            for (int r = 0; r < 2; r++)
            {
                for (int col = 0; col < 2; col++)
                {
                    int i0 = r * 3 + col, i1 = i0 + 1, i2 = i0 + 3, i3 = i2 + 1;
                    indices.AddRange(new[] { i0, i1, i3, i0, i3, i2 });
                }
            }
            submeshes.Add(new MeshSubmesh(positions.ToArray(), positions.Select(_ => System.Numerics.Vector3.UnitY).ToArray(),
                                          uvs.ToArray(), indices.ToArray(), f, weights.ToArray()));
        }
        return new MeshData(submeshes, new MeshSkin(joints, ibms, System.Numerics.Matrix4x4.Identity, 0f));
    }

    /// <summary>Every stream of every surface, bit for bit: the engine arrays a staging instance got
    /// against the managed ones the merge keeps.</summary>
    private static bool SameStreams(Godot.Collections.Array[] given, RetainedRig rig)
    {
        if (given.Length != rig.Surfaces.Length) return false;
        for (int s = 0; s < given.Length; s++)
        {
            var a = given[s];
            var r = rig.Surfaces[s];
            var positions = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = a[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var uvs = a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (positions.Length != r.Positions.Length || normals.Length != r.Normals.Length || uvs.Length != r.UVs.Length) return false;
            for (int v = 0; v < positions.Length; v++)
            {
                if (positions[v] != new Godot.Vector3(r.Positions[v].X, r.Positions[v].Y, r.Positions[v].Z)) return false;
                if (normals[v] != new Godot.Vector3(r.Normals[v].X, r.Normals[v].Y, r.Normals[v].Z)) return false;
                if (uvs[v] != new Godot.Vector2(r.UVs[v].X, r.UVs[v].Y)) return false;
            }
            if (!a[(int)Mesh.ArrayType.Tangent].AsFloat32Array().AsSpan().SequenceEqual(r.Tangents)) return false;
            if (!a[(int)Mesh.ArrayType.Bones].AsInt32Array().AsSpan().SequenceEqual(r.Bones)) return false;
            if (!a[(int)Mesh.ArrayType.Weights].AsFloat32Array().AsSpan().SequenceEqual(r.Weights)) return false;
            if (!a[(int)Mesh.ArrayType.Index].AsInt32Array().AsSpan().SequenceEqual(r.Indices)) return false;
        }
        return true;
    }

    /// <summary>What a skin puts each slot at in the rest pose: globalRest(bone) * bind, the product Godot's
    /// own skinning computes.</summary>
    private static Transform3D[] Palette(Skeleton3D skeleton, Skin skin)
    {
        var palette = new Transform3D[skin.GetBindCount()];
        for (int i = 0; i < palette.Length; i++)
            palette[i] = ComputeGlobalRestTransform(skeleton, skin.GetBindBone(i)) * skin.GetBindPose(i);
        return palette;
    }

    private static Godot.Vector3 SkinVertex(Transform3D[] palette, System.Numerics.Vector3 p, int[] bones, float[] weights, int v)
    {
        var position = new Godot.Vector3(p.X, p.Y, p.Z);
        var acc = Godot.Vector3.Zero;
        for (int k = 0; k < 4; k++)
        {
            float w = weights[v * 4 + k];
            if (w <= 0f) continue;
            acc += palette[bones[v * 4 + k]] * position * w;
        }
        return acc;
    }

    /// <summary>Every vertex of every merged and sorted surface, skinned through the outfit skin, against
    /// the same vertex skinned through its item's own palette.</summary>
    private void CheckSkinnedLikeItems(OutfitMergeAvatar state, Skeleton3D skeleton, Dictionary<Guid, Transform3D[]> itemPalettes,
        List<string> problems, string when)
    {
        if (state.Skin == null) return;
        var outfit = Palette(skeleton, state.Skin);
        float worst = 0f;
        int checkedVertices = 0;
        foreach (var ((avatar, key), result) in _outfitMergeKept)
        {
            if (!state.Buckets.ContainsKey(key)) continue;
            foreach (var surface in result.Surfaces)
            {
                foreach (var range in surface.Ranges)
                    Compare(range.ItemId, range.SurfaceIndex, surface.Arrays, range.FirstVertex, range.VertexCount);
            }
            foreach (var sorted in result.Sorted)
                Compare(sorted.ItemId, sorted.SurfaceIndex, sorted.Arrays, 0, sorted.Arrays.VertexCount);
        }
        if (checkedVertices == 0) problems.Add($"{when}: no merged vertex to check");
        if (worst > 1e-5f) problems.Add($"{when}: a merged vertex is {worst:E2} m from where its own skin puts it (limit 1e-5)");

        void Compare(Guid itemId, int surfaceIndex, OutfitMeshArrays merged, int first, int count)
        {
            if (!_mergeItems.TryGetValue(itemId, out var item) || item.Merged == null || !itemPalettes.TryGetValue(itemId, out var own)) return;
            var source = item.Merged.Rig.Surfaces[surfaceIndex];
            for (int v = 0; v < count; v++)
            {
                var mine = SkinVertex(own, source.Positions[v], source.Bones, source.Weights, v);
                var theirs = SkinVertex(outfit, merged.Positions[first + v], merged.Bones, merged.Weights, first + v);
                worst = Math.Max(worst, mine.DistanceTo(theirs));
                checkedVertices++;
            }
        }
    }

    private static void CheckSortedAndMaterials(OutfitMergeAvatar state, Dictionary<(Guid, int), float> offsets,
        Dictionary<(Guid, int), Material> materials, List<string> problems)
    {
        var own = new HashSet<Material>(materials.Values, ReferenceEqualityComparer.Instance);
        foreach (var bucket in state.Buckets.Values)
        {
            foreach (var ((item, surface), (node, index)) in bucket.DrawnAt)
            {
                var material = node.GetSurfaceOverrideMaterial(index);
                if (material == null || !own.Contains(material)) problems.Add($"a merged surface of {MergeShort(item)} wears a material that is not an item's own");
                if (!bucket.Sorted.Contains(node)) continue;
                if (!ReferenceEquals(material, materials.GetValueOrDefault((item, surface))))
                    problems.Add($"sorted surface {surface} of {MergeShort(item)} does not wear its own material");
                if (node.SortingOffset != offsets.GetValueOrDefault((item, surface), float.NaN))
                    problems.Add($"sorted surface {surface} of {MergeShort(item)} sits at {node.SortingOffset:0.000000}, today {offsets.GetValueOrDefault((item, surface)):0.000000}");
                if (node.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) problems.Add("a sorted instance casts a shadow");
            }
        }
    }

    /// <summary>Pumps the main-thread queue until it is empty (bounded).</summary>
    private static void DrainQueue()
    {
        for (int i = 0; i < 5000 && MainThreadWorkQueue.Depth > 0; i++) MainThreadWorkQueue.Pump(50);
    }

    /// <summary>Runs the merge tick with every dirty bucket due now, pumping the queue, until
    /// <paramref name="done"/> holds and no job is in flight (10 s at most).</summary>
    private bool WaitMerged(AvatarVisual visual, Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 10_000)
        {
            if (visual.OutfitMerge is { } state)
                foreach (var bucket in state.Buckets.Values)
                    if (bucket.IsDirty) bucket.Immediate = true;
            TickOutfitMerge();
            DrainQueue();
            TickWornAlphaOrder();
            bool idle = visual.OutfitMerge is not { JobInFlight: true } && _pendingRigs.IsEmpty && _activeRigWorkers == 0;
            if (idle && done() && (visual.OutfitMerge == null || visual.OutfitMerge.Buckets.Values.All(bk => !bk.IsDirty))) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    private void WaitRigWorkersIdle()
    {
        var clock = Stopwatch.StartNew();
        while ((_activeRigWorkers > 0 || _mergeJobsRunning > 0) && clock.ElapsedMilliseconds < 5_000)
        {
            DrainQueue();
            Thread.Sleep(5);
        }
    }
}
