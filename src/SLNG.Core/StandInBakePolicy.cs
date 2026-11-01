using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// BUG-AVATAR-19: what a reduced (over-cap) avatar loads and how it is drawn. Until now a reduced avatar fetched no bakes and was
/// painted in a flat jelly-doll colour; the reference viewer draws such an avatar as a textured impostor. A reduced avatar here shows
/// its system body with its bakes, each part switching from the jelly colour to its bake when that bake has arrived
/// (docs/specs/BUG-AVATAR-19-reduced-avatar-bakes.md).
/// </summary>
public static class StandInBakePolicy
{
    /// <summary>The bake channels a stand-in draws: head (8, with the eyelashes), upper (9), lower (10), eyes (11), hair (20). The skirt (19)
    /// has no part on the stand-in and is never requested. Numbers are <c>AvatarTextureIndex</c>.</summary>
    public static readonly int[] SystemBakeChannels = { 8, 9, 10, 11, 20 };

    /// <summary>Where the bake's alpha says "this part of the body is hidden" (an alpha layer under a mesh body): below it the stand-in
    /// shows the jelly colour instead of a hole or black. The full avatar cuts at the same 0.5 (Scissor).</summary>
    public const float HiddenAlphaCutoff = 0.5f;

    /// <summary>The smallest screen area (pixels squared) a stand-in's bake is asked for, so the cache never reads "0" as "full size".</summary>
    public const float MinPixelArea = 64f;

    /// <summary>The (channel, texture id) pairs a stand-in requests from an avatar's appearance: the system channels with a real id.</summary>
    public static IReadOnlyList<KeyValuePair<int, Guid>> WantedBakes(IReadOnlyDictionary<int, Guid>? baked)
    {
        var wanted = new List<KeyValuePair<int, Guid>>(SystemBakeChannels.Length);
        if (baked == null) return wanted;
        foreach (int channel in SystemBakeChannels)
        {
            if (baked.TryGetValue(channel, out var id) && id != Guid.Empty)
                wanted.Add(new KeyValuePair<int, Guid>(channel, id));
        }
        return wanted;
    }

    /// <summary>The screen area handed to the texture cache for a stand-in's bake, so it is prepared at what the avatar can show and
    /// is never read as 0, which for a bake means "full size, never shrink".</summary>
    public static float PixelAreaForStandIn(float measuredArea) =>
        float.IsNaN(measuredArea) || measuredArea < MinPixelArea ? MinPixelArea : measuredArea;

    /// <summary>
    /// How a part shows its bake on a stand-in. A <b>body surface</b> (head, upper, lower, eyes) is drawn opaque, with the jelly colour where
    /// the bake's alpha says the body is hidden. The <b>hair</b> and the <b>eyelashes</b> are the opposite: their alpha IS their shape (the
    /// hair is a shell cut out by its bake; filling the cut with jelly would bring back the uncut system-hair helmet), so they show the
    /// part's own alpha-cut material, exactly as a full avatar does.
    /// </summary>
    public static bool IsBodySurface(string partName) => partName is "head" or "upper_body" or "lower_body" or "eye";

    /// <summary>True when a part is drawn with its bake rather than the jelly colour: it has one that has been applied.</summary>
    public static bool ShowsBake(bool bakeApplied) => bakeApplied;
}
