using System;
using System.Collections.Generic;

namespace SLNG.Assets;

/// <summary>
/// The "magic" texture asset ids a worn mesh face carries to say "render me with the avatar's
/// server-baked texture for this channel" (Bakes-on-Mesh). Values verified against the viewer's
/// indra/llcommon/indra_constants.cpp (IMG_USE_BAKED_*). The viewer substitutes the avatar's
/// baked texture for these at render time (LLViewerObject::getBakedTextureForMagicId) and hides
/// the corresponding system body part (LLVOAvatar::updateMeshVisibility).
/// </summary>
public static class BakedTextureIds
{
    // Magic face-texture id → AvatarTextureIndex of the corresponding server bake, i.e. the key
    // used in the avatar's baked-texture dictionary (AvatarTextureIndex: HeadBaked=8, UpperBaked=9,
    // LowerBaked=10, EyesBaked=11, SkirtBaked=19, HairBaked=20, LeftArmBaked=40, LeftLegBaked=41,
    // Aux1..3Baked=42..44 — per LibreMetaverse's AvatarTextureIndex enum, which matches the
    // viewer's ETextureIndex).
    private static readonly Dictionary<Guid, int> MagicIdToBakeIndex = new()
    {
        [new Guid("5a9f4a74-30f2-821c-b88d-70499d3e7183")] = 8,  // IMG_USE_BAKED_HEAD
        [new Guid("ae2de45c-d252-50b8-5c6e-19f39ce79317")] = 9,  // IMG_USE_BAKED_UPPER
        [new Guid("24daea5f-0539-cfcf-047f-fbc40b2786ba")] = 10, // IMG_USE_BAKED_LOWER
        [new Guid("52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a")] = 11, // IMG_USE_BAKED_EYES
        [new Guid("43529ce8-7faa-ad92-165a-bc4078371687")] = 19, // IMG_USE_BAKED_SKIRT
        [new Guid("09aac1fb-6bce-0bee-7d44-caac6dbb6c63")] = 20, // IMG_USE_BAKED_HAIR
        [new Guid("ff62763f-d60a-9855-890b-0c96f8f8cd98")] = 40, // IMG_USE_BAKED_LEFTARM
        [new Guid("8e915e25-31d1-cc95-ae08-d58a47488251")] = 41, // IMG_USE_BAKED_LEFTLEG
        [new Guid("9742065b-19b5-297c-858a-29711d539043")] = 42, // IMG_USE_BAKED_AUX1
        [new Guid("03642e83-2bd1-4eb9-34b4-4c47ed586d2d")] = 43, // IMG_USE_BAKED_AUX2
        [new Guid("edd51b77-fc10-ce7a-4b3d-011dfc349e4f")] = 44, // IMG_USE_BAKED_AUX3
    };

    /// <summary>True if <paramref name="textureId"/> is a Bakes-on-Mesh magic id;
    /// <paramref name="bakeIndex"/> then holds the AvatarTextureIndex of the server bake the
    /// face wants (the avatar's baked-texture dictionary key).</summary>
    public static bool TryGetBakeIndex(Guid textureId, out int bakeIndex) =>
        MagicIdToBakeIndex.TryGetValue(textureId, out bakeIndex);
}
