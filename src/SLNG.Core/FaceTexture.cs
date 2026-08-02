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
    Guid MaterialId, 
    Vector4 Color,
    float RepeatU,
    float RepeatV,
    float OffsetU,
    float OffsetV,
    float Rotation,
    /// <summary>SL's per-face texture mapping mode. Default (0) uses the mesh's own UVs; Planar
    /// (1) projects the texture from the face's plane instead, so the scale stays independent of
    /// the prim's size. Planar is the norm on architectural content -- floors and walls -- which
    /// is why ignoring it shows up as textures sitting in visibly the wrong place. The viewer
    /// implements it in LLFace::getGeometryVolume (llface.cpp:914, TEX_GEN_PLANAR).</summary>
    byte TexGen = 0);
