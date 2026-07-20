using System.Collections.Generic;
using Godot;
using SLNG.Core;

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

            skeleton.SetBoneRest(idx, SlBoneToGodotLocalTransform(bone));
        }

        return skeleton;
    }

    /// <summary>Converts one SL bone definition's own (position, rotation) into a Godot local
    /// <see cref="Transform3D"/> — no hierarchy involved, just this bone's own numbers, and
    /// deliberately NO scale (see AvatarRenderer.ApplyShape's doc comment: a real SL joint's
    /// world matrix uses only its OWN scale, never compounded with ancestors, which Godot's
    /// Skeleton3D does by default unless Rest carries no scale at all). This is only ever a
    /// transient placeholder: <see cref="Build"/>'s caller (AvatarRenderer.CreateVisual) always
    /// immediately calls ApplyShape right after, which overwrites every bone's rest with the
    /// SL-accurate version (parent-scale-offset position, own scale tracked separately in
    /// AvatarVisual.BoneOwnScale for skinning binds to inject). Scale is intentionally dropped
    /// here rather than computed "correctly," since any bone here that has a non-unit base scale
    /// (e.g. collision volumes' bounding-box size) would otherwise compound down the hierarchy for
    /// the brief window before ApplyShape runs.</summary>
    internal static Transform3D SlBoneToGodotLocalTransform(BoneDefinition bone)
    {
        // Convert SL position to Godot position
        // SL: X=East, Y=North, Z=Up
        // Godot: X=Right, Y=Up, Z=Back (negative forward)
        // Transform: Godot.X = SL.X, Godot.Y = SL.Z, Godot.Z = -SL.Y
        var slPos = bone.Position;
        var godotPos = new Vector3(slPos.X, slPos.Z, -slPos.Y);

        var basis = SlEulerDegToGodotBasis(bone.Rotation);

        return new Transform3D(basis, godotPos);
    }

    /// <summary>
    /// Converts an SL bone's Euler rotation (degrees, avatar_skeleton.xml "rot" attribute) into
    /// a Godot <see cref="Basis"/>, in the SAME composed orientation the SL viewer actually
    /// builds — this is NOT just relabeling XYZ components into a Godot axis order.
    ///
    /// The viewer's real runtime skeleton setup (<c>LLAvatarAppearance::setupBone</c>) calls
    /// <c>joint-&gt;setRotation(mayaQ(rot.X, rot.Y, rot.Z, LLQuaternion::XYZ))</c>, and
    /// <c>mayaQ</c>'s XYZ case computes <c>xQ * yQ * zQ</c> — under SL's row-vector convention
    /// (<c>v * q</c>) that means: rotate about SL's X axis FIRST, then Y, then Z LAST.
    /// (The OTHER rotation-building function in the viewer's own source,
    /// <c>LLAvatarBoneInfo::getJointMatrix</c>, uses a different order — but that one is only
    /// used by the mesh-upload preview tool, not real avatar rendering; don't be misled by it.)
    ///
    /// Godot's own <c>Basis.FromEuler(Vector3)</c> defaults to YXZ order (Z first, then X, then
    /// Y), which is a DIFFERENT composition — silently using it here reproduces the wrong pose
    /// for any bone with rotation on more than one axis. The base body/torso bones mostly have
    /// single-axis (or zero) rotation, where composition order is irrelevant, so this stayed
    /// hidden; the deeply-nested Bento face bones (eyebrows, lips, jaw, etc.) commonly rotate on
    /// multiple axes at once, and the resulting error compounds down the (long) parent chain —
    /// this is what made a small worn face-mesh render as a wildly stretched spike while the
    /// body looked fine.
    ///
    /// Deriving the correct Godot-space composition: SL(x,y,z) → Godot(x,z,−y) is a proper
    /// (orthogonal, determinant +1) axis conversion C. Conjugating the SL rotation
    /// Rz_sl(c)∘Ry_sl(b)∘Rx_sl(a) (apply order: X first, Y, Z last) by C — i.e. C∘Ω∘C⁻¹, so it
    /// acts correctly on already-converted Godot-space points/vertices — maps each axis-angle
    /// factor to a DIFFERENT Godot axis (since C permutes axes) while preserving their order:
    /// SL's X-rotation stays a Godot X-rotation (C leaves x̂ fixed); SL's Y-rotation becomes a
    /// Godot Z-rotation with the angle NEGATED (C maps ŷ_sl to −ẑ_godot); SL's Z-rotation
    /// becomes a Godot Y-rotation (C maps ẑ_sl to ŷ_godot). Composition order is preserved by
    /// conjugation, so the correct result is: apply Godot-X(a) first, then Godot-Z(−b), then
    /// Godot-Y(c) last — expressed as a Basis product (Godot's `*`, like GLM, applies the
    /// RIGHTMOST factor first): <c>Ry(c) * Rz(-b) * Rx(a)</c>.
    /// </summary>
    internal static Basis SlEulerDegToGodotBasis(System.Numerics.Vector3 slRotDeg)
    {
        float a = Mathf.DegToRad(slRotDeg.X);
        float b = Mathf.DegToRad(slRotDeg.Y);
        float c = Mathf.DegToRad(slRotDeg.Z);
        return new Basis(Vector3.Up, c) * new Basis(Vector3.Back, -b) * new Basis(Vector3.Right, a);
    }
}
