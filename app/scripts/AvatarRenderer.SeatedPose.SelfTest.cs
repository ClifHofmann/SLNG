using Godot;
using System.Collections.Generic;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// BUG-AVATAR-12: an avatar sitting on an object has its PELVIS on the seat-resolved wire position,
    /// whatever rotation the seat gives it -- the viewer's mRoot is the pelvis (llavatarappearance.cpp:878)
    /// and takes the seat rotation (llvoavatar.cpp:4745-4751). Our Root is at the feet, so this builds a
    /// real Root/Skeleton3D pair, places it with <see cref="SeatedRootPosition"/> for an upright, a lying
    /// (pitched 90 degrees), an upside-down and a swinging (tilted, with hover) sit, and reads the pelvis
    /// back through Godot's own node and bone composition. Each tilted case also checks that the
    /// pre-fix placement (pelvis offset taken off world Y only) misses by a real distance, so the check
    /// cannot pass on a case the old code already got right.
    /// </summary>
    internal static (bool Passed, string Detail) SelfTestSeatedPelvis(SceneTree tree)
    {
        var problems = new List<string>();
        var root = new Node3D { Name = "SeatedPoseSelfTest" };
        try
        {
            tree.Root.AddChild(root);
            var skeleton = new Skeleton3D();
            root.AddChild(skeleton);
            int pelvis = skeleton.GetBoneCount();
            skeleton.AddBone("mPelvis");
            var pelvisRest = new Vector3(0.02f, 1.067f, -0.01f);
            skeleton.SetBoneRest(pelvis, new Transform3D(Basis.Identity, pelvisRest));
            int torso = skeleton.GetBoneCount();
            skeleton.AddBone("mTorso");
            skeleton.SetBoneParent(torso, pelvis);
            skeleton.SetBoneRest(torso, new Transform3D(Basis.Identity, new Vector3(0f, 0.084f, 0f)));
            skeleton.ResetBonePoses();

            var seat = new Vector3(118.4f, 22.6f, -42.9f);
            var cases = new (string Name, Quaternion Rotation, float Hover, bool Tilted)[]
            {
                ("upright, turned 90 deg", new Quaternion(Vector3.Up, Mathf.Pi / 2f), 0f, false),
                ("lying (pitched 90 deg)", new Quaternion(Vector3.Right, -Mathf.Pi / 2f), 0f, true),
                ("upside down", new Quaternion(Vector3.Forward, Mathf.Pi), 0f, true),
                ("swinging (tilted 20 deg, hover 0.1, not unit length)",
                    (new Quaternion(Vector3.Up, 0.7f) * new Quaternion(Vector3.Right, Mathf.DegToRad(20f))) * 1.02f, 0.1f, true),
            };

            foreach (var c in cases)
            {
                var unit = c.Rotation.Normalized();
                root.Position = SeatedRootPosition(seat, c.Rotation, pelvisRest, c.Hover);
                root.Quaternion = unit;
                var pelvisWorld = root.GlobalTransform * skeleton.GetBoneGlobalPose(pelvis).Origin;
                var expected = seat + unit * new Vector3(0f, c.Hover, 0f);
                float miss = (pelvisWorld - expected).Length();
                if (!(miss < 1e-3f))
                    problems.Add($"{c.Name}: pelvis {pelvisWorld} is {miss:0.###} m from the seat point {expected}");

                var preFixRoot = new Vector3(seat.X, seat.Y - pelvisRest.Y + c.Hover, seat.Z);
                float preFixMiss = (preFixRoot + unit * pelvisRest - expected).Length();
                if (c.Tilted && preFixMiss < 0.3f)
                    problems.Add($"{c.Name}: the pre-fix placement only missed by {preFixMiss:0.###} m, the case proves nothing");
            }

            // A zero wire rotation must not turn into NaN.
            var zero = SeatedRootPosition(seat, new Quaternion(0f, 0f, 0f, 0f), pelvisRest, 0f);
            if (!zero.IsFinite() || (zero - (seat - pelvisRest)).Length() > 1e-4f)
                problems.Add($"a zero rotation placed Root at {zero}, expected {seat - pelvisRest}");

            return problems.Count == 0
                ? (true, $"{cases.Length} seat rotations put the pelvis on the seat point")
                : (false, string.Join("; ", problems));
        }
        finally
        {
            root.QueueFree();
        }
    }
}
