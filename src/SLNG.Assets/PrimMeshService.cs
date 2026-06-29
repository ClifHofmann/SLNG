using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using SkiaSharp;
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
            return Convert(faceted);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrimMeshService] mesh generation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Generates a sculpted-prim mesh from its decoded sculpt-map (RGBA). The map encodes
    /// vertex positions as RGB; MeshFoundry interprets it per <paramref name="sculptType"/>
    /// (Sphere/Torus/Plane/Cylinder). CPU-bound — call from a worker thread.
    /// </summary>
    public static MeshData? GenerateSculpt(byte[] rgba, int width, int height, byte sculptType, DetailLevel lod = DetailLevel.Medium)
    {
        if (rgba == null || width <= 0 || height <= 0 || rgba.Length < width * height * 4) return null;

        try
        {
            using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(rgba, 0, bmp.GetPixels(), width * height * 4);

            var prim = new Primitive
            {
                Scale = new LMVector3(1, 1, 1),
                PrimData = new Primitive.ConstructionData { PCode = PCode.Prim },
                Sculpt = new Primitive.SculptData { Type = (SculptType)sculptType }
            };

            var renderer = _renderer ??= new MeshFoundry();
            var faceted = renderer.GenerateFacetedSculptMesh(prim, bmp, lod);
            return Convert(faceted);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrimMeshService] sculpt generation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Converts a LibreMetaverse FacetedMesh into neutral submeshes — one per prim
    /// face, tagged with its face number so the renderer can texture each face independently.</summary>
    private static MeshData? Convert(FacetedMesh? faceted)
    {
        if (faceted?.Faces == null || faceted.Faces.Count == 0) return null;

        var submeshes = new List<MeshSubmesh>(faceted.Faces.Count);
        foreach (var face in faceted.Faces)
        {
            if (face.Vertices == null || face.Indices == null || face.Indices.Count == 0) continue;

            int n = face.Vertices.Count;
            var positions = new Vector3[n];
            var normals   = new Vector3[n];
            var uvs       = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var v = face.Vertices[i];
                positions[i] = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                normals[i]   = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
                uvs[i]       = new Vector2(v.TexCoord.X, v.TexCoord.Y);
            }

            var indices = new int[face.Indices.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = face.Indices[i];

            submeshes.Add(new MeshSubmesh(positions, normals, uvs, indices, face.ID));
        }

        return submeshes.Count == 0 ? null : new MeshData(submeshes);
    }
}
