using System;
using Godot;
using SLNG.Core.Camera;

namespace SLNG.App;

// FEAT-UI-55 -- picking an avatar by its skeleton.
//
// The Alt+Click focus ray used to find a person through the one 0.22 m physics capsule on the
// Avatars layer, which is narrower than a torso with arms: a click that passed beside it hit the
// foliage or the ground behind. The reference viewer tests the click ray against the avatar's
// skeleton collision volumes instead (see docs/specs/camera-zoom-parity-firestorm-vs-slng.md,
// "Focus and look-at"). This does the equivalent with capsules between SL joint pairs, read from the
// live Skeleton3D so an animated arm moves its own target.
public partial class AvatarRenderer
{
    // Bone pairs and the radius of the capsule between them. SL joint names; a pair whose bones are
    // missing is skipped. The hands (wrist plus a little way along the forearm) and the head sphere
    // are handled separately below because they have no second joint of their own.
    private static readonly (string A, string B, float Radius)[] PickLimbs =
    {
        ("mPelvis", "mTorso", 0.19f),
        ("mTorso", "mChest", 0.20f),
        ("mChest", "mNeck", 0.20f),
        ("mNeck", "mHead", 0.07f),
        ("mCollarLeft", "mShoulderLeft", 0.07f),
        ("mShoulderLeft", "mElbowLeft", 0.06f),
        ("mElbowLeft", "mWristLeft", 0.05f),
        ("mCollarRight", "mShoulderRight", 0.07f),
        ("mShoulderRight", "mElbowRight", 0.06f),
        ("mElbowRight", "mWristRight", 0.05f),
        ("mHipLeft", "mKneeLeft", 0.10f),
        ("mKneeLeft", "mAnkleLeft", 0.07f),
        ("mAnkleLeft", "mFootLeft", 0.06f),
        ("mHipRight", "mKneeRight", 0.10f),
        ("mKneeRight", "mAnkleRight", 0.07f),
        ("mAnkleRight", "mFootRight", 0.06f),
    };

    // (elbow, wrist) per side: the hand is a short capsule from the wrist along the forearm.
    private static readonly (string Elbow, string Wrist)[] PickHands =
    {
        ("mElbowLeft", "mWristLeft"),
        ("mElbowRight", "mWristRight"),
    };

    private const float PickHandLength = 0.08f;
    private const float PickHandRadius = 0.05f;
    private const float PickHeadRadius = 0.13f;
    private const float PickHeadLift = 0.10f;
    private const float PickFallbackRadius = 0.30f;

    /// <summary>
    /// Casts a ray against every visible avatar's skeleton capsules (world space) and returns the nearest hit.
    /// <paramref name="body"/> is that avatar's <c>AvatarPhysics</c> body (so a focus can follow it);
    /// <paramref name="point"/> is the surface point and <paramref name="dist"/> the distance along the ray.
    /// An avatar whose bones cannot be resolved falls back to one 0.30 m capsule on its physics body.
    /// Allocation-light but meant for a click, not for every frame.
    /// </summary>
    public bool TryPickAvatar(Vector3 rayOrigin, Vector3 rayDir, float maxDist,
        out CollisionObject3D? body, out Vector3 point, out float dist)
    {
        body = null;
        point = default;
        dist = 0f;
        if (!rayDir.IsFinite() || rayDir.LengthSquared() < 1e-12f || !rayOrigin.IsFinite()) return false;

        var dir = rayDir.Normalized();
        var o = ToNumerics(rayOrigin);
        var d = ToNumerics(dir);
        float best = maxDist;
        CollisionObject3D? bestBody = null;

        foreach (var visual in _visuals.Values)
        {
            if (visual.Root == null || !GodotObject.IsInstanceValid(visual.Root) || !visual.Root.IsVisibleInTree()) continue;
            var owner = visual.CapsuleShape?.GetParent() as CollisionObject3D;
            if (owner == null || !GodotObject.IsInstanceValid(owner)) continue;

            float before = best;
            bool anyPair = visual.Skeleton != null && GodotObject.IsInstanceValid(visual.Skeleton)
                && PickSkeleton(visual.Skeleton, o, d, ref best);

            if (!anyPair)
            {
                // No joint resolved (no skeleton yet, or an unexpected one): one fat capsule on the body.
                float height = (visual.CapsuleShape?.Shape as CapsuleShape3D)?.Height ?? 1.9f;
                var basePos = ToNumerics(owner.GlobalPosition);
                var a = basePos + new System.Numerics.Vector3(0, PickFallbackRadius, 0);
                var b = basePos + new System.Numerics.Vector3(0, MathF.Max(PickFallbackRadius, height - PickFallbackRadius), 0);
                TestCapsule(o, d, a, b, PickFallbackRadius, ref best);
            }

            if (best < before) bestBody = owner;
        }

        if (bestBody == null) return false;
        body = bestBody;
        dist = best;
        point = rayOrigin + dir * best;
        return true;
    }

    /// <summary>Tests the skeleton's capsules; true when at least one joint pair or the head resolved
    /// (hit or not), so the caller knows not to fall back.</summary>
    private static bool PickSkeleton(Skeleton3D skeleton, System.Numerics.Vector3 o, System.Numerics.Vector3 d,
        ref float best)
    {
        bool any = false;
        var toWorld = skeleton.GlobalTransform;

        foreach (var (nameA, nameB, radius) in PickLimbs)
        {
            if (!TryBoneWorld(skeleton, toWorld, nameA, out var a) || !TryBoneWorld(skeleton, toWorld, nameB, out var b)) continue;
            any = true;
            TestCapsule(o, d, a, b, radius, ref best);
        }

        foreach (var (elbowName, wristName) in PickHands)
        {
            if (!TryBoneWorld(skeleton, toWorld, elbowName, out var elbow) ||
                !TryBoneWorld(skeleton, toWorld, wristName, out var wrist)) continue;
            any = true;
            var fore = wrist - elbow;
            float len = fore.Length();
            if (len < 1e-5f) continue;
            TestCapsule(o, d, wrist, wrist + fore / len * PickHandLength, PickHandRadius, ref best);
        }

        // Head: a sphere around the skull, a little above the head joint (along the neck, so a tilted
        // or lying avatar keeps it on the head; straight up when there is no neck).
        if (TryBoneWorld(skeleton, toWorld, "mHead", out var head))
        {
            any = true;
            var up = new System.Numerics.Vector3(0, 1, 0);
            if (TryBoneWorld(skeleton, toWorld, "mNeck", out var neck))
            {
                var along = head - neck;
                float len = along.Length();
                if (len > 1e-5f) up = along / len;
            }
            TestCapsule(o, d, head + up * PickHeadLift, head + up * PickHeadLift, PickHeadRadius, ref best);
        }

        return any;
    }

    private static bool TryBoneWorld(Skeleton3D skeleton, Transform3D toWorld, string name,
        out System.Numerics.Vector3 world)
    {
        world = default;
        int idx = skeleton.FindBone(name);
        if (idx < 0) return false;
        var p = toWorld * skeleton.GetBoneGlobalPose(idx).Origin;
        if (!p.IsFinite()) return false;
        world = ToNumerics(p);
        return true;
    }

    private static void TestCapsule(System.Numerics.Vector3 o, System.Numerics.Vector3 d,
        System.Numerics.Vector3 a, System.Numerics.Vector3 b, float radius, ref float best)
    {
        if (RayCapsule.Intersect(o, d, a, b, radius, out float t) && t < best) best = t;
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// FEAT-UI-55: <see cref="TryPickAvatar"/> against a real skeleton in a known pose, headless. Builds the real
    /// SL skeleton under a moved and turned root, then checks that a ray aimed at the pelvis and one at the head hit
    /// near where they were aimed, that the right body comes back, and that a ray beside the avatar, one that
    /// is too short and one that points away all miss. Needs the renderer in a scene tree.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestPickAvatar()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        var key = Guid.NewGuid();
        var visual = new AvatarVisual();
        var body = new StaticBody3D { Name = "AvatarPhysics", CollisionLayer = PhysicsLayers.Avatars };
        var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.22f, Height = 1.9f } };
        body.AddChild(shape);
        visual.Root.AddChild(body);
        visual.CapsuleShape = shape;
        var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
        visual.Root.AddChild(skeleton);
        visual.Skeleton = skeleton;
        AddChild(visual.Root);
        visual.Root.Position = new Vector3(3f, 0f, 4f);
        visual.Root.Rotation = new Vector3(0f, 0.7f, 0f);
        skeleton.ResetBonePoses();
        _visuals[key] = visual;

        try
        {
            var toWorld = skeleton.GlobalTransform;
            if (!TryBoneWorld(skeleton, toWorld, "mPelvis", out var pelvisN) ||
                !TryBoneWorld(skeleton, toWorld, "mHead", out var headN))
                return (false, "mPelvis or mHead is missing from the built skeleton");
            var pelvis = new Vector3(pelvisN.X, pelvisN.Y, pelvisN.Z);
            var head = new Vector3(headN.X, headN.Y, headN.Z);

            var side = new Vector3(0.6f, 0f, 0.8f); // horizontal, not along the avatar's own axes
            var problems = new System.Collections.Generic.List<string>();

            void Expect(string what, Vector3 target, float tolerance)
            {
                var from = target + side * 3f;
                if (!TryPickAvatar(from, -side, 10f, out var picked, out var point, out float dist))
                {
                    problems.Add($"{what}: no hit");
                    return;
                }
                if (picked != body) problems.Add($"{what}: wrong body");
                if (point.DistanceTo(target) > tolerance)
                    problems.Add($"{what}: hit {point.DistanceTo(target):0.00} m from where it was aimed");
                if (MathF.Abs(point.DistanceTo(from) - dist) > 0.001f) problems.Add($"{what}: point and distance disagree");
            }

            Expect("pelvis", pelvis, 0.30f);
            Expect("head", head + Vector3.Up * 0.1f, 0.20f);

            if (TryPickAvatar(pelvis + side * 3f + side.Cross(Vector3.Up) * 1.5f, -side, 10f, out _, out _, out _))
                problems.Add("a ray 1.5 m beside the avatar hit it");
            if (TryPickAvatar(pelvis + side * 3f, -side, 1.0f, out _, out _, out _))
                problems.Add("a ray shorter than the distance hit it");
            if (TryPickAvatar(pelvis + side * 3f, side, 10f, out _, out _, out _))
                problems.Add("a ray pointing away hit it");

            return problems.Count == 0
                ? (true, "rays at the pelvis and the head hit the avatar body near the aim point; side, short and backward rays miss")
                : (false, string.Join("; ", problems));
        }
        finally
        {
            _visuals.Remove(key);
            if (GodotObject.IsInstanceValid(visual.Root)) visual.Root.QueueFree();
        }
    }
}
