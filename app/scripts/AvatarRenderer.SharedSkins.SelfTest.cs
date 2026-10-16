using Godot;
using SLNG.Assets;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// FEAT-PERF-24: identical rigged skins on one avatar are one <see cref="Skin"/>, and the engine
    /// then keeps one skeleton binding for them -- the thing that makes Godot write their binds once
    /// per re-pose instead of once per mesh.
    ///
    /// <list type="number">
    /// <item>The same joints and inverse-bind matrices give back the same Skin, with the same slots.</item>
    /// <item>A joint that names no bone of ours does not split a skin: only the bound slots count,
    /// while each mesh keeps its own joint-to-slot map.</item>
    /// <item>One differing matrix element gives a different Skin.</item>
    /// <item>Two meshes holding the shared Skin get the same engine skeleton (SkinReference); a third
    /// with other binds gets its own.</item>
    /// <item>A shape rebuild (RebuildRiggedAttachmentSkins) makes new skins, and the rebuilt ones
    /// share again.</item>
    /// <item><c>--no-share-skins</c> gives every mesh its own Skin.</item>
    /// </list>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestSharedSkins(SceneTree tree)
    {
        var problems = new List<string>();
        bool previous = RenderConfig.ShareSkins;
        var visual = new AvatarVisual();
        try
        {
            RenderConfig.ShareSkins = true;
            tree.Root.AddChild(visual.Root);
            var skeleton = new Skeleton3D();
            for (int bone = 0; bone < 3; bone++) skeleton.AddBone($"mTestBone{bone}");
            visual.Root.AddChild(skeleton);
            visual.Skeleton = skeleton;

            var m1 = Matrix4x4.CreateTranslation(0.1f, 0.2f, 0.3f);
            var m2 = Matrix4x4.CreateRotationZ(0.4f) * Matrix4x4.CreateTranslation(0f, 0.5f, 0f);
            var a = new MeshSkin(new[] { "mTestBone0", "mTestBone1", "mTestNoSuchBone", "mTestBone2" },
                                 new[] { Matrix4x4.Identity, m1, m2, m2 }, Matrix4x4.Identity, 0f);
            var aAgain = new MeshSkin((string[])a.JointNames.Clone(), (Matrix4x4[])a.InverseBindMatrices.Clone(), Matrix4x4.Identity, 0f);
            // The same bound binds, with the unknown joint elsewhere.
            var aShifted = new MeshSkin(new[] { "mTestNoSuchBone", "mTestBone0", "mTestBone1", "mTestBone2" },
                                        new[] { m1, Matrix4x4.Identity, m1, m2 }, Matrix4x4.Identity, 0f);
            var m2Off = m2;
            m2Off.M41 += 1e-3f;
            var b = new MeshSkin(a.JointNames, new[] { Matrix4x4.Identity, m1, m2, m2Off }, Matrix4x4.Identity, 0f);

            var skinA = BindRiggedSkin(a, skeleton, visual, out var slotsA);
            var skinA2 = BindRiggedSkin(aAgain, skeleton, visual, out var slotsA2);
            var skinShifted = BindRiggedSkin(aShifted, skeleton, visual, out var slotsShifted);
            var skinB = BindRiggedSkin(b, skeleton, visual, out _);

            if (skinA == null || skinA2 == null || skinShifted == null || skinB == null)
                return (false, "a skin with bound joints came back null");
            if (!ReferenceEquals(skinA, skinA2)) problems.Add("two identical skins were not shared");
            if (string.Join(",", slotsA) != "0,1,-1,2" || string.Join(",", slotsA2) != "0,1,-1,2")
                problems.Add($"slots changed under sharing: [{string.Join(",", slotsA)}] / [{string.Join(",", slotsA2)}]");
            if (!ReferenceEquals(skinA, skinShifted)) problems.Add("an unbound joint in another place split an identical skin");
            if (string.Join(",", slotsShifted) != "-1,0,1,2")
                problems.Add($"the shifted mesh did not keep its own joint slots: [{string.Join(",", slotsShifted)}]");
            if (ReferenceEquals(skinA, skinB)) problems.Add("a skin with a different bind matrix was shared");
            if (skinA.GetBindCount() != 3) problems.Add($"the shared skin has {skinA.GetBindCount()} binds, expected 3");

            // The engine side: one binding per distinct Skin.
            MeshInstance3D Mesh(Skin skin)
            {
                var mi = new MeshInstance3D { Skin = skin };
                skeleton.AddChild(mi);
                mi.Skeleton = mi.GetPathTo(skeleton);
                return mi;
            }
            var first = Mesh(skinA);
            var second = Mesh(skinA2);
            var other = Mesh(skinB);
            // Compared by SkinReference, not by its skeleton RID: headless, the dummy renderer hands
            // out RID 0 for every skeleton. register_skin returns one SkinReference per Skin.
            ulong r1 = first.GetSkinReference()?.GetInstanceId() ?? 0;
            ulong r2 = second.GetSkinReference()?.GetInstanceId() ?? 0;
            ulong r3 = other.GetSkinReference()?.GetInstanceId() ?? 0;
            if (r1 == 0 || r1 != r2) problems.Add("two meshes holding the shared skin did not get one skeleton binding");
            if (r3 == 0 || r3 == r1) problems.Add("a mesh with other binds did not get a binding of its own");

            // A shape rebuild: new skins, shared again among themselves.
            var meshData = new MeshData(Array.Empty<MeshSubmesh>(), a);
            visual.RiggedAttachments.Add((first, meshData, Guid.NewGuid()));
            visual.RiggedAttachments.Add((second, meshData, Guid.NewGuid()));
            RebuildRiggedAttachmentSkins(visual);
            if (ReferenceEquals(first.Skin, skinA)) problems.Add("the rebuild kept the old skin (the cache was not cleared)");
            if (!ReferenceEquals(first.Skin, second.Skin)) problems.Add("the rebuilt identical skins were not shared");

            // The A/B switch.
            RenderConfig.ShareSkins = false;
            var off1 = BindRiggedSkin(a, skeleton, visual, out _);
            var off2 = BindRiggedSkin(a, skeleton, visual, out _);
            if (ReferenceEquals(off1, off2)) problems.Add("--no-share-skins still shared a skin");
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.ShareSkins = previous;
            visual.Root.GetParent()?.RemoveChild(visual.Root);
            visual.Root.Free();
        }

        return problems.Count == 0
            ? (true, "identical skins shared (slots kept), different ones not, one engine binding per shared skin, rebuild and switch behave")
            : (false, string.Join("; ", problems));
    }
}
