using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// BUG-ANIM-07: the local position a joint takes while an animation's POSITION key drives it, in
/// Second Life space (Z up, relative to the parent joint).
/// </summary>
/// <remarks>
/// <para>A keyframe motion hands the curve value straight to the joint state
/// (<c>LLKeyframeMotion::applyKeyframes</c>, llkeyframemotion.cpp:414-417) and the blender writes
/// <c>setPosition(blended_pos + added_pos)</c> (llpose.cpp:388) with
/// <c>apply_attachment_overrides</c> left false (lljoint.h:233). So for every joint a key is an
/// ABSOLUTE local position, and while it drives the joint it beats a worn mesh's joint-position
/// override and the shape's position distortion. Only the parent's scale then applies, because
/// <c>LLXformMatrix::update</c> scales a child's offset by its parent's own scale.</para>
///
/// <para><b>The one exception is <c>mPelvis</c>.</b> Its key is measured from the skeleton root.
/// A real avatar's root sits at pelvis height (llvoavatar.cpp:4677), so a stand animation's pelvis
/// key is ~0 and SLNG, whose skeleton root is at the feet, adds the rest position
/// (<see cref="PelvisMode.RestPlusKey"/>). A control avatar's root is the prim itself, so there the
/// key is the whole position (<see cref="PelvisMode.Absolute"/>).</para>
///
/// <para>Treating every other joint's key as <c>rest + key</c> -- what BUG-ANIM-01 first did --
/// counts it twice, which is how the tongue joints "moved": a real animation's tongue keys equal
/// the skeleton defaults exactly. Dropping them (what it settled on) loses the shapes that
/// add-on HUDs pose with position-only animations.</para>
/// </remarks>
public static class AnimatedJointPosition
{
    /// <summary>The joint whose key is measured from the skeleton root rather than its parent.</summary>
    public const string PelvisJointName = "mPelvis";

    /// <summary>How the pelvis' key is read.</summary>
    public enum PelvisMode
    {
        /// <summary>An ordinary avatar: the key is an offset on the pelvis' rest position.</summary>
        RestPlusKey,

        /// <summary>A control avatar (animesh): the key is the pelvis' whole local position.</summary>
        Absolute,
    }

    /// <summary>True for the joint whose key is not a plain absolute local position.</summary>
    public static bool IsPelvis(string jointName) => string.Equals(jointName, PelvisJointName, System.StringComparison.Ordinal);

    /// <summary>The position a joint takes while <paramref name="key"/> drives it.</summary>
    /// <param name="isPelvis">The joint is <see cref="PelvisJointName"/>.</param>
    /// <param name="pelvisMode">How the pelvis' key is read; ignored for any other joint.</param>
    /// <param name="rest">The joint's position without the animation: the skeleton default plus the
    /// shape's distortion, or the active mesh override. Only the pelvis in
    /// <see cref="PelvisMode.RestPlusKey"/> reads it -- for every other joint the key REPLACES it,
    /// which is the point.</param>
    /// <param name="key">The animation's position key at this time.</param>
    /// <param name="parentScale">The PARENT joint's own scale (not compounded with its ancestors').</param>
    public static Vector3 Resolve(bool isPelvis, PelvisMode pelvisMode, Vector3 rest, Vector3 key, Vector3 parentScale)
    {
        if (isPelvis)
            return pelvisMode == PelvisMode.Absolute ? key : rest + key;

        return key * parentScale;
    }
}
