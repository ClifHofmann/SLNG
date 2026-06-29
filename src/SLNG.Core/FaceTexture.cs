using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// Protocol-neutral texture/material for a single prim face. SL prims texture each face
/// independently (a box has 6, a cut/hollow prim more), so the renderer applies one of these
/// per mesh surface. No LibreMetaverse types cross this boundary.
/// </summary>
public readonly record struct FaceTexture(Guid TextureId, Guid MaterialId, Vector4 Color, float RepeatU, float RepeatV, float OffsetU, float OffsetV, float Rotation);
