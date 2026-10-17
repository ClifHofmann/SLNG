using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using Kind = SLNG.App.PrimShaderFamily.Kind;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// FEAT-PERF-23 Phase 0: the outfit probe on a hand-built avatar with two worn rigged items.
    ///
    /// <list type="number">
    /// <item>Item A: an opaque face and a blended face; binds (bone 0, I), (bone 1, T1).
    /// Item B: an opaque face with A's face record and a scissor face; binds (bone 1, T1), (bone 2, T2).</item>
    /// <item><c>[OutfitProbe]</c> counts 2 items in 2 linksets, 4 binds of which 3 distinct, and 2 material
    /// keys per avatar (A's and B's opaque faces share one) but 3 per linkset.</item>
    /// <item>The per-avatar merge hides both originals and detaches their skins, builds one instance with
    /// 2 surfaces on a 3-bind skin plus one sorted instance at A's exact SortingOffset, and every merged
    /// influence points at the same (bone, bind) its original did.</item>
    /// <item>Switching it off restores both originals (shown, skin path back) and takes the merged
    /// instances out of the tree; the per-linkset merge builds one bucket per item.</item>
    /// </list>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestOutfitProbe(SceneTree tree)
    {
        var problems = new List<string>();
        bool keepArrays = _outfitProbeKeepArrays;
        var visual = new AvatarVisual { EntityId = Guid.NewGuid() };
        try
        {
            _outfitProbeKeepArrays = true;
            tree.Root.AddChild(visual.Root);
            var skeleton = new Skeleton3D();
            for (int bone = 0; bone < 3; bone++) skeleton.AddBone($"mProbeBone{bone}");
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;
            _visuals[visual.EntityId] = visual;

            var t1 = new Transform3D(Basis.Identity, new Godot.Vector3(0.1f, 0.2f, 0.3f));
            var t2 = new Transform3D(new Basis(Godot.Vector3.Up, 0.4f), new Godot.Vector3(0f, 0.5f, 0f));
            var shared = new FaceTexture(Guid.NewGuid(), Guid.Empty, Guid.Empty, System.Numerics.Vector4.One, 1f, 1f, 0f, 0f, 0f);
            var blended = shared with { TextureId = Guid.NewGuid() };
            var cut = shared with { TextureId = Guid.NewGuid() };

            var skinA = new Skin();
            skinA.AddBind(0, Transform3D.Identity);
            skinA.AddBind(1, t1);
            var skinB = new Skin();
            skinB.AddBind(1, t1);
            skinB.AddBind(2, t2);
            var a = AddOutfitProbeTestItem(visual, skinA, new[] { shared, blended }, new[] { 0, 1 },
                                           new[] { Kind.Opaque, Kind.Blend }, 0.0301f);
            var b = AddOutfitProbeTestItem(visual, skinB, new[] { shared, shared, cut }, new[] { 0, 2 },
                                           new[] { Kind.Opaque, Kind.Scissor }, 0.0302f);

            // Probe 1.
            var (perAvatar, others) = RunOutfitProbe();
            if (perAvatar.Count != 1 || others.Avatars != 1)
                problems.Add($"probe saw {perAvatar.Count} avatar(s) / {others.Avatars} other(s), expected 1 / 1");
            else
            {
                var t = perAvatar[0].Tally;
                string got = $"items={t.Items} linksets={t.Linksets} opaque={t.Opaque} scissor={t.Scissor} blend={t.Blend} " +
                             $"keysAvatar={t.KeysPerAvatar} keysLinkset={t.KeysPerLinkset} binds={t.Binds} " +
                             $"skinWrites={t.SkinWrites} distinct={t.DistinctBinds} surfNow={t.SurfacesNow} " +
                             $"surfAvatar={t.SurfacesPerAvatar} surfLinkset={t.SurfacesPerLinkset} " +
                             $"instAvatar={t.InstancesPerAvatar} instLinkset={t.InstancesPerLinkset} staged={t.StagedItems}";
                const string want = "items=2 linksets=2 opaque=2 scissor=1 blend=1 keysAvatar=2 keysLinkset=3 binds=4 " +
                                    "skinWrites=4 distinct=3 surfNow=4 surfAvatar=3 surfLinkset=4 instAvatar=2 instLinkset=3 staged=0";
                if (got != want) problems.Add($"counts: got {got}; want {want}");
            }

            // Probe 3, per avatar.
            var (mode, _) = ToggleOutfitMergeProbe(OutfitMergeProbeMode.PerAvatar);
            if (mode != OutfitMergeProbeMode.PerAvatar) problems.Add($"toggle on returned {mode}");
            foreach (var (name, mi) in new[] { ("A", a.Mi), ("B", b.Mi) })
                if (mi.Visible || !mi.Skeleton.IsEmpty) problems.Add($"original {name} still drawn or bound while merged");
            var merged = skeleton.GetNodeOrNull<MeshInstance3D>(OutfitProbeMergedName);
            var sorted = skeleton.GetNodeOrNull<MeshInstance3D>($"{OutfitProbeSortedName}0");
            if (merged == null || sorted == null)
                problems.Add("the merged or the sorted instance is missing");
            else
            {
                if (merged.Mesh is not ArrayMesh mm || mm.GetSurfaceCount() != 2)
                    problems.Add($"merged mesh has {(merged.Mesh as ArrayMesh)?.GetSurfaceCount()} surface(s), expected 2");
                if (merged.Skin == null || merged.Skin.GetBindCount() != 3)
                    problems.Add($"outfit skin has {merged.Skin?.GetBindCount()} binds, expected 3");
                if (merged.Skeleton.IsEmpty || sorted.Skeleton.IsEmpty) problems.Add("a merged instance is not bound to the skeleton");
                if (!ReferenceEquals(sorted.Skin, merged.Skin)) problems.Add("the sorted instance does not share the outfit skin");
                // One engine skeleton binding for the shared outfit skin (register_skin, as FEAT-PERF-24 checks).
                ulong r1 = merged.GetSkinReference()?.GetInstanceId() ?? 0;
                ulong r2 = sorted.GetSkinReference()?.GetInstanceId() ?? 0;
                if (r1 == 0 || r1 != r2) problems.Add("the merged and the sorted instance did not get one skeleton binding");
                if (sorted.SortingOffset != 0.0301f) problems.Add($"sorted offset {sorted.SortingOffset}, expected 0.0301");
                if (sorted.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) problems.Add("the sorted instance casts a shadow");
                if (!ReferenceEquals(sorted.GetSurfaceOverrideMaterial(0), a.Mi.GetSurfaceOverrideMaterial(1)))
                    problems.Add("the sorted instance does not wear A's blended material");
                if (merged.Mesh is ArrayMesh mesh && merged.Skin != null)
                    CheckOutfitProbeRemap(mesh, merged.Skin, skinA, skinB, problems);
            }

            // Still counted as drawn while the probe hides them.
            var (whileMerged, _) = RunOutfitProbe();
            if (whileMerged.Count == 1 && whileMerged[0].Tally.Items != 2)
                problems.Add($"while merged the count probe saw {whileMerged[0].Tally.Items} drawn item(s), expected 2");

            (mode, _) = ToggleOutfitMergeProbe(OutfitMergeProbeMode.PerAvatar);
            if (mode != OutfitMergeProbeMode.Off) problems.Add($"toggle off returned {mode}");
            foreach (var (name, mi) in new[] { ("A", a.Mi), ("B", b.Mi) })
                if (!mi.Visible || mi.Skeleton.IsEmpty) problems.Add($"original {name} not restored (visible={mi.Visible}, bound={!mi.Skeleton.IsEmpty})");
            if (merged != null && IsInstanceValid(merged) && merged.GetParent() != null) problems.Add("the merged instance is still in the tree");
            if (_outfitProbeHidden.Count != 0 || _outfitProbeMerges.Count != 0) problems.Add("probe state left behind after off");

            // Probe 3, per linkset: one bucket per item here (no world, so every item is its own linkset).
            (mode, _) = ToggleOutfitMergeProbe(OutfitMergeProbeMode.PerLinkset);
            if (mode != OutfitMergeProbeMode.PerLinkset) problems.Add($"per-linkset toggle returned {mode}");
            var bucketA = skeleton.GetNodeOrNull<MeshInstance3D>($"{OutfitProbeMergedName}0");
            var bucketB = skeleton.GetNodeOrNull<MeshInstance3D>($"{OutfitProbeMergedName}1");
            int surfacesA = (bucketA?.Mesh as ArrayMesh)?.GetSurfaceCount() ?? -1;
            int surfacesB = (bucketB?.Mesh as ArrayMesh)?.GetSurfaceCount() ?? -1;
            if (surfacesA != 1 || surfacesB != 2) problems.Add($"per-linkset buckets have {surfacesA} / {surfacesB} surfaces, expected 1 / 2");
            (mode, _) = ToggleOutfitMergeProbe(OutfitMergeProbeMode.PerLinkset);
            if (mode != OutfitMergeProbeMode.Off || !a.Mi.Visible || !b.Mi.Visible) problems.Add("per-linkset toggle did not restore");

            // Leaving: an avatar that is reduced while merged gets its originals back first.
            ToggleOutfitMergeProbe(OutfitMergeProbeMode.PerAvatar);
            ReleaseOutfitMergeProbe(visual);
            if (!a.Mi.Visible || a.Mi.Skeleton.IsEmpty || _outfitProbeMerges.Count != 0) problems.Add("release did not restore the originals");
            _outfitMergeProbe = OutfitMergeProbeMode.Off;
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _outfitProbeKeepArrays = keepArrays;
            _outfitMergeProbe = OutfitMergeProbeMode.Off;
            _outfitProbeMerges.Clear();
            _outfitProbeHidden.Clear();
            foreach (var entity in new List<Guid>(_riggedAttachments.Keys))
            {
                _riggedAttachments.Remove(entity);
                _attachmentToAvatar.Remove(entity);
                _attachmentMeshIds.Remove(entity);
            }
            _visuals.Remove(visual.EntityId);
            visual.Root.GetParent()?.RemoveChild(visual.Root);
            visual.Root.Free();
        }

        return problems.Count == 0
            ? (true, "2 items counted (3 of 4 binds distinct, 2 keys per avatar, 3 per linkset); merged into 1 instance + 1 sorted at the same offset with every influence on the same bind; off restores both")
            : (false, string.Join("; ", problems));
    }

    /// <summary>A worn rigged item for <see cref="SelfTestOutfitProbe"/>: one quad per surface, each vertex
    /// weighted 0.75 / 0.25 to the item's skin slots 0 and 1, arrays and face indices retained as the real
    /// commit retains them.</summary>
    private (Guid Entity, MeshInstance3D Mi) AddOutfitProbeTestItem(AvatarVisual visual, Skin skin, FaceTexture[] faces,
        int[] surfaceFaces, Kind[] kinds, float sortingOffset)
    {
        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array[surfaceFaces.Length];
        for (int s = 0; s < surfaceFaces.Length; s++)
        {
            float z = s * 0.1f;
            var a = new Godot.Collections.Array();
            a.Resize((int)Mesh.ArrayType.Max);
            a[(int)Mesh.ArrayType.Vertex] = Variant.CreateFrom(new[]
            {
                new Godot.Vector3(0, 0, z), new Godot.Vector3(1, 0, z), new Godot.Vector3(0, 1, z), new Godot.Vector3(1, 1, z),
            });
            a[(int)Mesh.ArrayType.Normal] = Variant.CreateFrom(new[] { Godot.Vector3.Back, Godot.Vector3.Back, Godot.Vector3.Back, Godot.Vector3.Back });
            a[(int)Mesh.ArrayType.TexUV] = Variant.CreateFrom(new[]
            {
                new Godot.Vector2(0, 0), new Godot.Vector2(1, 0), new Godot.Vector2(0, 1), new Godot.Vector2(1, 1),
            });
            a[(int)Mesh.ArrayType.Bones] = Variant.CreateFrom(new[] { 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0 });
            a[(int)Mesh.ArrayType.Weights] = Variant.CreateFrom(new[]
            {
                0.75f, 0.25f, 0f, 0f, 0.75f, 0.25f, 0f, 0f, 0.75f, 0.25f, 0f, 0f, 0.75f, 0.25f, 0f, 0f,
            });
            a[(int)Mesh.ArrayType.Index] = Variant.CreateFrom(new[] { 0, 2, 1, 1, 2, 3 });
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, a);
            arrays[s] = a;
        }
        MeshSurfaceCache.Store(mesh, arrays);
        RememberRigSurfaceFaces(mesh, surfaceFaces);

        var mi = new MeshInstance3D { Mesh = mesh, Skin = skin, SortingOffset = sortingOffset };
        for (int s = 0; s < kinds.Length; s++)
        {
            var material = new ShaderMaterial { Shader = PrimShaderFamily.Select(kinds[s], PrimShaderFamily.Surface.Avatar) };
            if (kinds[s] == Kind.Scissor) material.SetShaderParameter(PrimShaderFamily.AlphaScissorThreshold, 0.5f);
            mi.SetSurfaceOverrideMaterial(s, material);
        }
        visual.Skeleton!.AddChild(mi);
        mi.Skeleton = mi.GetPathTo(visual.Skeleton);

        var entity = Guid.NewGuid();
        _riggedAttachments[entity] = mi;
        _attachmentToAvatar[entity] = visual.EntityId;
        _attachmentMeshIds[entity] = (Guid.NewGuid(), faces, faces[0], visual.EntityId, MeshDetailLevel.High);
        return (entity, mi);
    }

    /// <summary>The merged opaque surface holds A's first quad then B's: every influence must name, through
    /// the outfit skin, the same bone and bind it named through its own item's skin.</summary>
    private static void CheckOutfitProbeRemap(ArrayMesh mesh, Skin outfit, Skin skinA, Skin skinB, List<string> problems)
    {
        if (!_outfitProbeArrays.TryGetValue(mesh, out var built) || built.Length == 0)
        {
            problems.Add("the merged arrays were not kept for the check");
            return;
        }
        var bones = built[0][(int)Mesh.ArrayType.Bones].AsInt32Array();
        var weights = built[0][(int)Mesh.ArrayType.Weights].AsFloat32Array();
        var index = built[0][(int)Mesh.ArrayType.Index].AsInt32Array();
        if (bones.Length != 8 * 4 || index.Length != 12)
        {
            problems.Add($"merged opaque surface has {bones.Length / 4} verts / {index.Length} indices, expected 8 / 12");
            return;
        }
        int[] original = { 0, 1, 0, 0 };
        for (int v = 0; v < 8; v++)
        {
            var own = v < 4 ? skinA : skinB;
            for (int k = 0; k < 2; k++)
            {
                int slot = bones[v * 4 + k];
                int ownSlot = original[k];
                if (outfit.GetBindBone(slot) != own.GetBindBone(ownSlot) || outfit.GetBindPose(slot) != own.GetBindPose(ownSlot)
                    || weights[v * 4 + k] != (k == 0 ? 0.75f : 0.25f))
                {
                    problems.Add($"merged vertex {v} influence {k} points at slot {slot} (bone {outfit.GetBindBone(slot)}), " +
                                 $"its own skin had bone {own.GetBindBone(ownSlot)}");
                    return;
                }
            }
        }
        if (index[6] < 4) problems.Add("the second part's indices were not offset");
    }
}
