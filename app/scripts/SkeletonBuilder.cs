using Godot;
using SLNG.Core;
using System.Collections.Generic;

namespace SLNG.App;

/// <summary>
/// Builds a Godot Skeleton3D from the parsed SL Bento skeleton definition.
/// Handles the SL Z-up to Godot Y-up coordinate transform.
/// </summary>
public static class SkeletonBuilder
{
    /// <summary>
    /// Creates a Skeleton3D from the given AvatarSkeleton, including collision-volume
    /// bones (BELLY, PELVIS, L_UPPER_LEG, …). Fitted / rigged mesh weights reference these
    /// collision volumes; without them those vertices fail to bind and collapse. They are
    /// children of standard bones, so they follow the body and add no animation cost.
    /// </summary>
    public static Skeleton3D Build(AvatarSkeleton avatarSkeleton)
    {
        var skeleton = new Skeleton3D();

        // We need to add bones in parent-first order. The XML is already parsed
        // in depth-first order, so parents come before children (a collision volume
        // always follows its enclosing bone).
        var boneIndices = new Dictionary<string, int>();

        foreach (var bone in avatarSkeleton.Bones)
        {
            int idx = skeleton.AddBone(bone.Name);
            boneIndices[bone.Name] = idx;

            // Set parent
            if (bone.ParentName != null && boneIndices.TryGetValue(bone.ParentName, out int parentIdx))
            {
                skeleton.SetBoneParent(idx, parentIdx);
            }

            // Convert SL position to Godot position
            // SL: X=East, Y=North, Z=Up
            // Godot: X=Right, Y=Up, Z=Back (negative forward)
            // Transform: Godot.X = SL.X, Godot.Y = SL.Z, Godot.Z = -SL.Y
            var slPos = bone.Position;
            var godotPos = new Vector3(slPos.X, slPos.Z, -slPos.Y);

            // SL rotation is in degrees (euler), convert to Godot
            var slRot = bone.Rotation;
            var godotRotDeg = new Vector3(slRot.X, slRot.Z, -slRot.Y);
            var godotRotRad = new Vector3(
                Mathf.DegToRad(godotRotDeg.X),
                Mathf.DegToRad(godotRotDeg.Y),
                Mathf.DegToRad(godotRotDeg.Z)
            );

            var basis = Basis.FromEuler(godotRotRad);

            // Scale
            var slScale = bone.Scale;
            basis = basis.Scaled(new Vector3(slScale.X, slScale.Z, slScale.Y));

            var rest = new Transform3D(basis, godotPos);
            skeleton.SetBoneRest(idx, rest);
        }

        return skeleton;
    }
}
