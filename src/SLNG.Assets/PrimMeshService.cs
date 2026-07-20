using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using SkiaSharp;
using SLNG.Core;
using LMVector3 = LibreMetaverse.Vector3;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

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
                    profileCurve = shape.ProfileCurve,
                    PathCurve = (PathCurve)shape.PathCurve,
                    PathBegin = shape.PathBegin,
                    PathEnd = shape.PathEnd,
                    PathScaleX = shape.PathScaleX,
                    PathScaleY = shape.PathScaleY,
                    PathShearX = shape.PathShearX,
                    PathShearY = shape.PathShearY,
                    PathTaperX = shape.PathTaperX,
                    PathTaperY = shape.PathTaperY,
                    PathTwist = shape.PathTwist,
                    PathTwistBegin = shape.PathTwistBegin,
                    PathRadiusOffset = shape.PathRadiusOffset,
                    PathSkew = shape.PathSkew,
                    PathRevolutions = shape.PathRevolutions,
                    ProfileBegin = shape.ProfileBegin,
                    ProfileEnd = shape.ProfileEnd,
                    ProfileHollow = shape.ProfileHollow,
                    PCode = (PCode)shape.PCode,
                }
            };

            if (prim.PrimData.PCode == PCode.Tree || prim.PrimData.PCode == PCode.NewTree || prim.PrimData.PCode == PCode.Grass)
            {
                return GenerateCrossedPlanes();
            }

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
    /// face, tagged with its face number so the renderer can texture each face independently.
    /// The face number is the face's POSITION in the list, NOT <c>face.ID</c>:
    /// MeshFoundry.GenerateFacetedMesh never assigns ID (it defaults to 0 on every face), which
    /// textured every surface of a multi-face prim with face 0's entry — a prim with its real
    /// texture on face 4 and blank white (or fully transparent) elsewhere rendered all-white
    /// (or invisible), which is exactly how the HUD's Reset button / logo / icons broke while
    /// single-texture prims looked fine. GenerateFacetedMesh emits faces in SL face order
    /// (<c>for i in 0..numPrimFaces</c>, each picking <c>Textures.GetFace(i)</c>), so the list
    /// index is the true SL face number.</summary>
    private static MeshData? Convert(FacetedMesh? faceted)
    {
        if (faceted?.Faces == null || faceted.Faces.Count == 0) return null;

        var submeshes = new List<MeshSubmesh>(faceted.Faces.Count);
        for (int faceNumber = 0; faceNumber < faceted.Faces.Count; faceNumber++)
        {
            var face = faceted.Faces[faceNumber];
            if (face.Vertices == null || face.Indices == null || face.Indices.Count == 0) continue;

            int n = face.Vertices.Count;
            var positions = new Vector3[n];
            var normals = new Vector3[n];
            var uvs = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var v = face.Vertices[i];
                positions[i] = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                normals[i] = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
                uvs[i] = new Vector2(v.TexCoord.X, v.TexCoord.Y);
            }

            var indices = new int[face.Indices.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = face.Indices[i];

            submeshes.Add(new MeshSubmesh(positions, normals, uvs, indices, faceNumber));
        }

        return submeshes.Count == 0 ? null : new MeshData(submeshes);
    }

    private static MeshData GenerateCrossedPlanes()
    {
        // Two crossed planes (X shape) for generic SL trees.
        // Plane 1: XZ plane, centered on Y (Y=0)
        // Plane 2: YZ plane, centered on X (X=0)
        // SL uses Z-up. Planes span X/Y from -0.5 to 0.5, and Z from -0.5 to 0.5.

        var positions = new Vector3[8];
        var normals = new Vector3[8];
        var uvs = new Vector2[8];
        var indices = new int[12];

        // Plane 1 (XZ plane, Y=0)
        // vertices: bottom-left, bottom-right, top-right, top-left
        positions[0] = new Vector3(-0.5f, 0f, -0.5f); uvs[0] = new Vector2(0, 1);
        positions[1] = new Vector3(0.5f, 0f, -0.5f); uvs[1] = new Vector2(1, 1);
        positions[2] = new Vector3(0.5f, 0f, 0.5f); uvs[2] = new Vector2(1, 0);
        positions[3] = new Vector3(-0.5f, 0f, 0.5f); uvs[3] = new Vector2(0, 0);

        normals[0] = normals[1] = normals[2] = normals[3] = new Vector3(0, 1, 0);

        indices[0] = 0; indices[1] = 1; indices[2] = 2;
        indices[3] = 0; indices[4] = 2; indices[5] = 3;

        // Plane 2 (YZ plane, X=0)
        positions[4] = new Vector3(0f, -0.5f, -0.5f); uvs[4] = new Vector2(0, 1);
        positions[5] = new Vector3(0f, 0.5f, -0.5f); uvs[5] = new Vector2(1, 1);
        positions[6] = new Vector3(0f, 0.5f, 0.5f); uvs[6] = new Vector2(1, 0);
        positions[7] = new Vector3(0f, -0.5f, 0.5f); uvs[7] = new Vector2(0, 0);

        normals[4] = normals[5] = normals[6] = normals[7] = new Vector3(1, 0, 0);

        indices[6] = 4; indices[7] = 5; indices[8] = 6;
        indices[9] = 4; indices[10] = 6; indices[11] = 7;

        var submesh = new MeshSubmesh(positions, normals, uvs, indices, 0);
        return new MeshData(new List<MeshSubmesh> { submesh });
    }
}
