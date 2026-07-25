using System.Collections.Generic;
using System.Numerics;

namespace SLNG.App;

/// <summary>
/// Maps Second Life AttachmentPoint byte values to the Bento skeleton joint they hang from AND
/// the point's own offset/rotation on that joint.
///
/// The offsets matter: an SL attachment point is NOT the joint's origin. "Skull" (the point hair
/// uses) sits 0.15 m ABOVE mHead and is turned 90 degrees about Z; "Chest" is 0.15 m forward,
/// 0.1 m down and rotated twice. Attaching worn items straight onto the raw joint — which this
/// map's earlier bone-name-only version forced callers to do — therefore renders every static
/// attachment displaced by exactly that offset. On the Skull point that is a 15 cm drop, which is
/// what "my hair sits far too low, but it's fine in Firestorm" looks like.
///
/// Every joint name, position and rotation below is transcribed from the viewer's own
/// avatar_lad.xml &lt;attachment_point&gt; table (vendored at
/// scratch/libremetaverse_src/LibreMetaverse/linden/character/avatar_lad.xml) — the authoritative
/// source. Several joint names in the previous hand-written map disagreed with it and are
/// corrected here (Left/Right Shoulder are mCollar*, not mShoulder*; the Foot points are mFoot*,
/// not mAnkle*; Mouth/Chin/Ear/Nose all hang off mHead; Stomach is mPelvis; the Pec points are
/// mTorso; Avatar Center is mRoot; Wing points are mWing4*; Alt Ear/Alt Eye/Tongue/Hind Foot
/// differed too).
///
/// Positions/rotations are in SL coordinates (Z-up, metres / degrees) — convert at the point of
/// use. HUD points (31–38) hang off "mScreen" and are handled by the separate HUD overlay path.
/// </summary>
public static class AttachmentPointMap
{
    /// <summary>One attachment point: the joint it hangs from, plus its own offset and rotation
    /// on that joint, both in SL space (metres, Z-up / degrees).</summary>
    public readonly record struct Point(string Joint, Vector3 Position, Vector3 RotationDeg);

    private static readonly Dictionary<byte, Point> _map = new()
    {
        { 1,  new("mChest",            new(0.15f, 0f, -0.1f),           new(0f, 90f, 90f)) },
        { 2,  new("mHead",             new(0f, 0f, 0.15f),              new(0f, 0f, 90f)) },
        { 3,  new("mCollarLeft",       new(0f, 0f, 0.08f),              default) },
        { 4,  new("mCollarRight",      new(0f, 0f, 0.08f),              default) },
        { 5,  new("mWristLeft",        new(0f, 0.08f, -0.02f),          default) },
        { 6,  new("mWristRight",       new(0f, -0.08f, -0.02f),         default) },
        { 7,  new("mFootLeft",         default,                         default) },
        { 8,  new("mFootRight",        default,                         default) },
        { 9,  new("mChest",            new(-0.15f, 0f, -0.1f),          new(0f, -90f, 90f)) },
        { 10, new("mPelvis",           new(0f, 0f, -0.15f),             default) },
        { 11, new("mHead",             new(0.12f, 0f, 0.001f),          default) },
        { 12, new("mHead",             new(0.12f, 0f, -0.04f),          default) },
        { 13, new("mHead",             new(0.015f, 0.08f, 0.017f),      default) },
        { 14, new("mHead",             new(0.015f, -0.08f, 0.017f),     default) },
        { 15, new("mEyeLeft",          default,                         default) },
        { 16, new("mEyeRight",         default,                         default) },
        { 17, new("mHead",             new(0.1f, 0f, 0.05f),            default) },
        { 18, new("mShoulderRight",    new(0.01f, -0.13f, 0.01f),       default) },
        { 19, new("mElbowRight",       new(0f, -0.12f, 0f),             default) },
        { 20, new("mShoulderLeft",     new(0.01f, 0.15f, -0.01f),       default) },
        { 21, new("mElbowLeft",        new(0f, 0.113f, 0f),             default) },
        { 22, new("mHipRight",         default,                         default) },
        { 23, new("mHipRight",         new(-0.017f, 0.041f, -0.310f),   default) },
        { 24, new("mKneeRight",        new(-0.044f, -0.007f, -0.262f),  default) },
        { 25, new("mHipLeft",          default,                         default) },
        { 26, new("mHipLeft",          new(-0.019f, -0.034f, -0.310f),  default) },
        { 27, new("mKneeLeft",         new(-0.044f, -0.007f, -0.261f),  default) },
        { 28, new("mPelvis",           new(0.092f, 0f, 0.088f),         default) },
        { 29, new("mTorso",            new(0.104f, 0.082f, 0.247f),     default) },
        { 30, new("mTorso",            new(0.104f, -0.082f, 0.247f),    default) },
        // 31–38: HUD points — screen-space (joint "mScreen"), routed to the HUD overlay instead.
        { 39, new("mNeck",             default,                         default) },
        { 40, new("mRoot",             default,                         default) },
        { 41, new("mHandRing1Left",    new(-0.006f, 0.019f, -0.002f),   default) },
        { 42, new("mHandRing1Right",   new(-0.006f, -0.019f, -0.002f),  default) },
        { 43, new("mTail1",            default,                         default) },
        { 44, new("mTail6",            new(-0.025f, 0f, 0f),            default) },
        { 45, new("mWing4Left",        default,                         default) },
        { 46, new("mWing4Right",       default,                         default) },
        { 47, new("mFaceJaw",          default,                         default) },
        { 48, new("mFaceEar1Left",     default,                         default) },
        { 49, new("mFaceEar1Right",    default,                         default) },
        { 50, new("mFaceEyeAltLeft",   default,                         default) },
        { 51, new("mFaceEyeAltRight",  default,                         default) },
        { 52, new("mFaceTongueTip",    default,                         default) },
        { 53, new("mGroin",            default,                         default) },
        { 54, new("mHindLimb4Left",    default,                         default) },
        { 55, new("mHindLimb4Right",   default,                         default) },
    };

    /// <summary>Returns the full attachment point (joint + its offset/rotation on that joint),
    /// or null for HUD/unknown points.</summary>
    public static Point? GetPoint(byte attachmentPoint) =>
        _map.TryGetValue(attachmentPoint, out var p) ? p : null;

    /// <summary>Returns the Bento bone name for <paramref name="attachmentPoint"/>,
    /// or null for HUD/unknown points.</summary>
    public static string? GetBoneName(byte attachmentPoint) =>
        _map.TryGetValue(attachmentPoint, out var p) ? p.Joint : null;

    /// <summary>True for the 8 HUD attachment points (31–38) — screen-space overlays with no
    /// world bone, distinct from an unmapped/unrecognised BODY point (which also has no bone,
    /// but should still fall back to somewhere on the body rather than being dropped). Callers
    /// must route these to the orthographic HUD overlay (AvatarRenderer.UpdateHudAttachment)
    /// rather than let them fall through to a default body bone, which would render the HUD
    /// mesh as a small object floating in 3D world space on the avatar instead of on screen.</summary>
    public static bool IsHudPoint(byte attachmentPoint) => attachmentPoint is >= 31 and <= 38;
}
