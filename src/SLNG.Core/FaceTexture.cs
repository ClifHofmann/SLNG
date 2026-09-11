using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// Protocol-neutral texture/material for a single prim face. SL prims texture each face
/// independently (a box has 6, a cut/hollow prim more), so the renderer applies one of these
/// per mesh surface. No LibreMetaverse types cross this boundary.
/// </summary>
public readonly record struct FaceTexture(
    Guid TextureId,
    /// <summary>The face's glTF PBR material (LibreMetaverse's RenderMaterialID, documented
    /// there as "PBR / GLTF render material asset UUID"). Called MaterialId until
    /// FEAT-RENDER-04, which was actively misleading: a face carries TWO independent material
    /// ids, and the other one -- the legacy Blinn-Phong material below -- has at least as good
    /// a claim to the bare name.</summary>
    Guid RenderMaterialId,
    /// <summary>The face's LEGACY Blinn-Phong material (LibreMetaverse's MaterialID): a normal
    /// map and a specular map layered on top of the diffuse texture, resolved through the
    /// region's RenderMaterials capability rather than as an asset. Empty for most faces.
    /// Independent of <see cref="RenderMaterialId"/> -- a face can carry either, both or
    /// neither. See SLNG.Core.LegacyMaterialData.</summary>
    Guid LegacyMaterialId,
    Vector4 Color,
    float RepeatU,
    float RepeatV,
    float OffsetU,
    float OffsetV,
    float Rotation,
    /// <summary>SL's per-face texture mapping mode, stored as the RAW protocol value:
    /// Default = 0, Planar = 2, Spherical = 4, Cylindrical = 6 (LLTextureEntry::eTexGen,
    /// lltextureentry.h:79; LibreMetaverse MappingType, TextureEntry.cs:98). Default uses the
    /// mesh's own UVs; Planar projects the texture from the face's plane instead, so the texture
    /// scale stays independent of the prim's size. Planar is the norm on architectural content --
    /// floors and walls -- which is why ignoring it shows up as textures sitting in visibly the
    /// wrong place. The viewer implements it in LLFace::getGeometryVolume (llface.cpp:914).
    ///
    /// COMPARE AGAINST <see cref="TexGenPlanar"/>, NEVER AGAINST 1. This field carried the raw
    /// enum from the day it was added while its own doc comment and the renderer's diagnostic
    /// both said planar was 1. The two never met, so the "PLANAR (not implemented!)" log line
    /// could not fire even on a face that was demonstrably planar -- which is why the missing
    /// implementation stayed invisible for as long as it did.</summary>
    byte TexGen = 0,
    /// <summary>SL's per-face "fullbright" flag (LLTextureEntry::getFullbright). A fullbright
    /// face ignores scene lighting and renders at its full unlit texture colour -- signs,
    /// screens, neon, anything meant to look self-lit. The renderer routes it through EMISSION
    /// with ALBEDO zeroed (FEAT-RENDER-06). Separate from glow and from glTF emissive.</summary>
    bool Fullbright = false,
    /// <summary>SL's LEGACY per-face shininess, the build tool's Shiny: none / low / medium /
    /// high, as the raw 0-3 protocol value (LLTextureEntry::getShiny).
    ///
    /// This is a different system from <see cref="LegacyMaterialId"/>'s specular MAP, and the
    /// viewer treats them as alternatives: it packs shininess into the vertex alpha only
    /// "if we don't have a specular map" (llface.cpp:1412). The renderer must do the same --
    /// a specular map wins, and this drives the highlight for every other face.
    ///
    /// Until FEAT-RENDER-19 nothing read this at all, and prim_common handed every face
    /// Godot's default SPECULAR 0.5 instead: a 4% highlight on matte faces that SL says have
    /// none, and the same 4% on faces SL says are polished. Both wrong, in opposite
    /// directions.</summary>
    byte Shiny = 0)
{
    /// <summary>The viewer's SHININESS_TO_ALPHA (llface.cpp:1420): the glossiness that a legacy
    /// shiny level feeds into the specular LUT. Index is the raw 0-3 value; anything out of
    /// range is treated as none, as the viewer asserts it cannot be.</summary>
    public float ShinyGlossiness => Shiny switch
    {
        1 => 0.25f,
        2 => 0.50f,
        3 => 0.75f,
        _ => 0.0f,
    };

    /// <summary>SL TEX_GEN_DEFAULT -- the face uses the mesh's own UVs.</summary>
    public const byte TexGenDefault = 0;

    /// <summary>SL TEX_GEN_PLANAR. Two, not one.</summary>
    public const byte TexGenPlanar = 2;

    /// <summary>True when this face wants the planar projection rather than the mesh UVs.</summary>
    public bool IsPlanar => TexGen == TexGenPlanar;
}
