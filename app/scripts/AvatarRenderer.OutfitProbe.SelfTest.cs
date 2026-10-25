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
    /// </list>
    /// The merge toggle (probe 3) was retired with Phase 1; <c>SelfTestOutfitMerge</c> covers the real merge.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestOutfitProbe(SceneTree tree)
    {
        var problems = new List<string>();
        var visual = new AvatarVisual { EntityId = Guid.NewGuid() };
        try
        {
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
            AddOutfitProbeTestItem(visual, skinA, new[] { shared, blended }, new[] { 0, 1 },
                                           new[] { Kind.Opaque, Kind.Blend }, 0.0301f);
            AddOutfitProbeTestItem(visual, skinB, new[] { shared, shared, cut }, new[] { 0, 2 },
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
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
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
            ? (true, "2 items counted (3 of 4 binds distinct, 2 keys per avatar, 3 per linkset)")
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
}
