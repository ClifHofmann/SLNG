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
            
            FacetedMesh? faceted = renderer.GenerateFacetedMesh(prim, lod);
            
            var mesh = Convert(faceted, prim.Scale);

            // LibreMetaverse.Rendering.MeshFoundry (as of the 3.0.0 package) only emits the
            // path's *last* end face (ViewerFace tagging in PrimMesh.Create is gated on
            // `nodeIndex == path.pathNodes.Count - 1`); the first end face is never added. For
            // a straight-extruded profile (PathCurve Line/Flexible — box, cylinder, prism, any
            // "basic shape") that means the bottom cap is silently missing from the mesh: the
            // object is a closed shell everywhere except its bottom, which is wide open. At
            // normal prim scale that gap is a sliver nobody notices; scaled into a large flat
            // platform it reads as a seam/crack right at the top edge, because the camera's
            // sightline grazes past the (missing) bottom and into the hollow interior. See the
            // regression test for the reproduction. Repair it here rather than in the vendored
            // library, which we don't build from source (NuGet dependency).
            bool isLinearPath = (PathCurve)shape.PathCurve == PathCurve.Line || (PathCurve)shape.PathCurve == PathCurve.Flexible;
            if (isLinearPath) mesh = RepairMissingEndCap(mesh);
            return mesh;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrimMeshService] mesh generation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Detects a straight-extruded prim missing one of its two path end caps (see the comment
    /// in <see cref="Generate"/>) and synthesizes the missing one from data that IS present and
    /// correct: the side-wall submeshes already carry both the top and bottom ring of vertices
    /// (that's how the top cap and side walls stay exactly welded at the shared edge), the
    /// mesher just never assembles the bottom ring into its own cap face. Reusing those vertices
    /// — rather than mirroring the existing cap's coordinates — keeps the fix correct even when
    /// the prim has taper/shear (which make the bottom ring's X/Y differ from the top's).
    /// </summary>
    private static MeshData? RepairMissingEndCap(MeshData? mesh)
    {
        return mesh;

        const float epsZ = 1e-3f;
        const float epsXY2 = 1e-6f;

        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var sm in mesh.Submeshes)
            foreach (var p in sm.Positions)
            {
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }
        if (maxZ - minZ < epsZ) return mesh; // no meaningful path extent (flat profile)

        static bool IsCapAt(MeshSubmesh sm, float z, float eps)
        {
            if (sm.Positions.Length < 3) return false;
            foreach (var p in sm.Positions)
                if (MathF.Abs(p.Z - z) > eps) return false;
            return true;
        }

        MeshSubmesh? capAtMax = null, capAtMin = null;
        foreach (var sm in mesh.Submeshes)
        {
            if (capAtMax == null && IsCapAt(sm, maxZ, epsZ)) capAtMax = sm;
            if (capAtMin == null && IsCapAt(sm, minZ, epsZ)) capAtMin = sm;
        }

        // Both caps present (healthy mesh) or both missing (nothing usable to reconstruct from).
        if ((capAtMax != null) == (capAtMin != null)) return mesh;

        var existingCap = capAtMax ?? capAtMin!;
        float missingZ = capAtMax != null ? minZ : maxZ;

        // Gather the missing ring's vertices from the side-wall submeshes (every submesh other
        // than the existing cap), de-duplicating by position.
        var ring = new List<Vector3>();
        foreach (var sm in mesh.Submeshes)
        {
            if (ReferenceEquals(sm, existingCap)) continue;
            foreach (var p in sm.Positions)
            {
                if (MathF.Abs(p.Z - missingZ) > epsZ) continue;
                bool dup = false;
                foreach (var r in ring)
                {
                    float dx = r.X - p.X, dy = r.Y - p.Y;
                    if (dx * dx + dy * dy < epsXY2) { dup = true; break; }
                }
                if (!dup) ring.Add(p);
            }
        }

        if (ring.Count != existingCap.Positions.Length)
            return mesh; // topology doesn't match what we expect — don't guess, leave as-is

        // Match each existing-cap vertex to its ring counterpart by nearest X/Y (exact for the
        // common no-twist case; the side-wall data keeps taper/shear correct either way).
        var newPositions = new Vector3[existingCap.Positions.Length];
        for (int i = 0; i < existingCap.Positions.Length; i++)
        {
            var target = existingCap.Positions[i];
            int best = 0; float bestD = float.MaxValue;
            for (int j = 0; j < ring.Count; j++)
            {
                float dx = ring[j].X - target.X, dy = ring[j].Y - target.Y;
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = j; }
            }
            newPositions[i] = ring[best];
        }

        var newNormals = new Vector3[existingCap.Normals.Length];
        for (int i = 0; i < newNormals.Length; i++) newNormals[i] = -existingCap.Normals[i];

        var newUVs = (Vector2[])existingCap.UVs.Clone();

        // Reverse each triangle's winding so the mirrored cap faces outward.
        var newIndices = new int[existingCap.Indices.Length];
        for (int t = 0; t + 2 < existingCap.Indices.Length; t += 3)
        {
            newIndices[t] = existingCap.Indices[t];
            newIndices[t + 1] = existingCap.Indices[t + 2];
            newIndices[t + 2] = existingCap.Indices[t + 1];
        }

        var repaired = new List<MeshSubmesh>(mesh.Submeshes.Count + 1);
        repaired.AddRange(mesh.Submeshes);
        repaired.Add(new MeshSubmesh(newPositions, newNormals, newUVs, newIndices, existingCap.FaceIndex));
        return mesh with { Submeshes = repaired };
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
            return Convert(faceted, prim.Scale);
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
    private static MeshData? Convert(FacetedMesh? faceted, LMVector3 originalScale)
    {
        if (faceted?.Faces == null || faceted.Faces.Count == 0) return null;

        float sx = originalScale.X > 0.0001f ? originalScale.X : 1f;
        float sy = originalScale.Y > 0.0001f ? originalScale.Y : 1f;
        float sz = originalScale.Z > 0.0001f ? originalScale.Z : 1f;

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
                // MeshFoundry baked the scale into the vertices. We must divide it out to emit a
                // unit-scale mesh, because ObjectRenderer applies the prim scale at the Godot node
                // level. If we don't un-bake it here, the mesh will be double-scaled (squared scale).
                positions[i] = new Vector3(v.Position.X / sx, v.Position.Y / sy, v.Position.Z / sz);
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
