namespace SLNG.Core;

/// <summary>Maps an SL bake channel index (the wire <c>AvatarTextureIndex</c> values also used
/// throughout <c>GridSession</c> -- 8/9/10/11/19/20 and the four newer BoM channels 40-44) to the
/// slot-name string Second Life's dedicated bake-texture CDN expects in its URL path.
///
/// <b>Why this exists.</b> Avatar bake textures are NOT served through the generic asset CDN
/// (<c>GetTexture</c>/<c>ViewerAsset</c>, <c>{cap}?texture_id={id}</c>) the way every other texture
/// is. Confirmed live via a captured HTTP exchange comparing SLNG against Firestorm on the same
/// bake texture id: SLNG's generic fetch got a raw S3 <c>AccessDenied</c> 403 from
/// <c>asset-cdn.glb.agni.lindenlab.com</c>; Firestorm fetched the SAME id successfully (200, real
/// J2C bytes) from a completely different host and path shape:
/// <c>http://bake-texture.glb.agni.lindenlab.com/texture/&lt;agent-id&gt;/eyes/&lt;texture-id&gt;</c>.
/// This is real, documented SL infrastructure, not a guess: the reference viewer's own HTTP policy
/// header (<c>llappcorehttp.h</c>) lists <c>bake-texture</c> as a distinct connection-pool
/// destination alongside the general asset <c>cdn</c>.
///
/// The slot names below are read directly from the reference viewer's own bake-channel table
/// (<c>indra/llappearance/llavatarappearancedefines.cpp</c>, the 4th constructor argument of each
/// <c>TEX_*_BAKED</c> entry) -- not inferred from the one captured example ("eyes"), which is why
/// all eleven are present and not just the one directly observed.</summary>
public static class BakeChannelNames
{
    /// <summary>The channel index -> URL slot name table, keyed by the same
    /// <c>AvatarTextureIndex</c> values <c>GridSession</c> already uses for bake channels
    /// (8=head, 9=upper, 10=lower, 11=eyes, 19=skirt, 20=hair, 40-44=the BoM universal channels).</summary>
    private static readonly System.Collections.Generic.IReadOnlyDictionary<int, string> Names =
        new System.Collections.Generic.Dictionary<int, string>
        {
            [8] = "head",
            [9] = "upper",
            [10] = "lower",
            [11] = "eyes",
            [19] = "skirt",
            [20] = "hair",
            [40] = "leftarm",
            [41] = "leftleg",
            [42] = "aux1",
            [43] = "aux2",
            [44] = "aux3",
        };

    /// <summary>The bake-texture URL slot name for a channel index, or null if
    /// <paramref name="channel"/> isn't one of the eleven known bake channels.</summary>
    public static string? NameFor(int channel) => Names.TryGetValue(channel, out var name) ? name : null;
}
