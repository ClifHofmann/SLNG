using System.Collections.Generic;

namespace SLNG.App;

/// <summary>
/// Maps Second Life AttachmentPoint byte values to the Bento skeleton bone they attach to.
/// HUD points (31–38) return null — they are screen-space and have no world bone.
/// Source: SL wiki / llsd/attachments.xml.
/// </summary>
public static class AttachmentPointMap
{
    private static readonly Dictionary<byte, string> _map = new()
    {
        { 1,  "mChest" },
        { 2,  "mHead" },
        { 3,  "mShoulderLeft" },
        { 4,  "mShoulderRight" },
        { 5,  "mWristLeft" },
        { 6,  "mWristRight" },
        { 7,  "mAnkleLeft" },
        { 8,  "mAnkleRight" },
        { 9,  "mTorso" },
        { 10, "mPelvis" },
        { 11, "mFaceTeethUpper" },
        { 12, "mFaceChin" },
        { 13, "mFaceEar1Left" },
        { 14, "mFaceEar1Right" },
        { 15, "mEyeLeft" },
        { 16, "mEyeRight" },
        { 17, "mFaceNose" },
        { 18, "mShoulderRight" },
        { 19, "mElbowRight" },
        { 20, "mShoulderLeft" },
        { 21, "mElbowLeft" },
        { 22, "mHipRight" },
        { 23, "mHipRight" },
        { 24, "mKneeRight" },
        { 25, "mHipLeft" },
        { 26, "mHipLeft" },
        { 27, "mKneeLeft" },
        { 28, "mTorso" },
        { 29, "mChest" },
        { 30, "mChest" },
        // 31–38: HUD points — null (screen-space, no world bone)
        { 39, "mNeck" },
        { 40, "mPelvis" },
        { 41, "mHandRing1Left" },
        { 42, "mHandRing1Right" },
        { 43, "mTail1" },
        { 44, "mTail6" },
        { 45, "mWing1Left" },
        { 46, "mWing1Right" },
        { 47, "mFaceJaw" },
        { 48, "mFaceEar2Left" },
        { 49, "mFaceEar2Right" },
        { 50, "mEyeLeft" },
        { 51, "mEyeRight" },
        { 52, "mFaceTongueBase" },
        { 53, "mGroin" },
        { 54, "mHindLimb1Left" },
        { 55, "mHindLimb1Right" },
    };

    /// <summary>Returns the Bento bone name for <paramref name="attachmentPoint"/>,
    /// or null for HUD/unknown points.</summary>
    public static string? GetBoneName(byte attachmentPoint) =>
        _map.TryGetValue(attachmentPoint, out var bone) ? bone : null;

    /// <summary>True for the 8 HUD attachment points (31–38) — screen-space overlays with no
    /// world bone, distinct from an unmapped/unrecognised BODY point (which also has no bone,
    /// but should still fall back to somewhere on the body rather than being dropped). Callers
    /// must route these to the orthographic HUD overlay (AvatarRenderer.UpdateHudAttachment)
    /// rather than let them fall through to a default body bone, which would render the HUD
    /// mesh as a small object floating in 3D world space on the avatar instead of on screen.</summary>
    public static bool IsHudPoint(byte attachmentPoint) => attachmentPoint is >= 31 and <= 38;
}
