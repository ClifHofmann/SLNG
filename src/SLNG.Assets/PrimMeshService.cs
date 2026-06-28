using System;
using System.Collections.Generic;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using SLNG.Core;
using Vector3 = System.Numerics.Vector3;
using Vector2 = System.Numerics.Vector2;
using LMVector3 = LibreMetaverse.Vector3;

namespace SLNG.Assets;

/// <summary>
/// Turns a neutral <see cref="PrimShape"/> into real prim geometry using LibreMetaverse's
/// MeshFoundry renderer, emitting an engine-neutral <see cref="MeshData"/>. No LibreMetaverse
/// type crosses this boundary. Geometry is generated in unit prim space (a 1 m cube); the
/// renderer applies the prim's scale, exactly as it does for mesh assets.
/// </summary>
public static class PrimMeshService
{
    // MeshFoundry instances are cheap; use a fresh one per call so meshing can run on
    // several worker threads without sharing renderer state.
    [ThreadStatic] private static MeshFoundry? _renderer;

    /// <summary>
    /// Generates a mesh for the given prim shape, or null if it can't be meshed.
    /// CPU-bound — call from a worker thread (see <see cref="AssetService.GetPrimMeshAsync"/>).
    /// </summary>
    public static MeshData? Generate(PrimShape shape, DetailLevel lod = DetailLevel.Medium)
    {
        try
        {
            var prim = new Primitive
            {
                Scale = new LMVector3(1, 1, 1),
                PrimData = new Primitive.ConstructionData
                {
                    profileCurve     = shape.ProfileCurve,
                    PathCurve        = (PathCurve)shape.PathCurve,
                    PathBegin        = shape.PathBegin,
                    PathEnd          = shape.PathEnd,
                    PathScaleX       = shape.PathScaleX,
                    PathScaleY       = shape.PathScaleY,
                    PathShearX       = shape.PathShearX,
                    PathShearY       = shape.PathShearY,
                    PathTaperX       = shape.PathTaperX,
                    PathTaperY       = shape.PathTaperY,
                    PathTwist        = shape.PathTwist,
                    PathTwistBegin   = shape.PathTwistBegin,
                    PathRadiusOffset = shape.PathRadiusOffset,
                    PathSkew         = shape.PathSkew,
                    PathRevolutions  = shape.PathRevolutions,
                    ProfileBegin     = shape.ProfileBegin,
                    ProfileEnd       = shape.ProfileEnd,
                    ProfileHollow    = shape.ProfileHollow,
                    PCode            = PCode.Prim,
                }
            };

            var renderer = _renderer ??= new MeshFoundry();
            var faceted = renderer.GenerateFacetedMesh(prim, lod);
            if (faceted?.Faces == null || faceted.Faces.Count == 0) return null;

            // Merge all faces into one submesh (single surface = one draw call). Per-face
            // texturing can split this later if needed.
            var positions = new List<Vector3>();
            var normals   = new List<Vector3>();
            var uvs       = new List<Vector2>();
            var indices   = new List<int>();

            foreach (var face in faceted.Faces)
            {
                int baseIndex = positions.Count;
                foreach (var v in face.Vertices)
                {
                    positions.Add(new Vector3(v.Position.X, v.Position.Y, v.Position.Z));
                    normals.Add(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z));
                    uvs.Add(new Vector2(v.TexCoord.X, v.TexCoord.Y));
                }
                foreach (var idx in face.Indices)
                    indices.Add(baseIndex + idx);
            }

            if (indices.Count == 0) return null;

            var submesh = new MeshSubmesh(positions.ToArray(), normals.ToArray(), uvs.ToArray(), indices.ToArray());
            return new MeshData(new[] { submesh });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrimMeshService] mesh generation failed: {ex.Message}");
            return null;
        }
    }
}
