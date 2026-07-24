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
    // MeshFoundry instances are cheap; instantiate a fresh one per call to prevent any
    // cross-call state corruption inside the LibreMetaverse library.

    /// <summary>
    /// Generates a mesh for the given prim shape, or null if it can't be meshed.
    /// CPU-bound — call from a worker thread (see <see cref="AssetService.GetPrimMeshAsync"/>).
    /// </summary>
    public static MeshData? Generate(PrimShape shape, DetailLevel lod = DetailLevel.Highest)
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

            var renderer = new MeshFoundry();

            FacetedMesh? faceted = renderer.GenerateFacetedMesh(prim, lod);

            var mesh = Convert(faceted, prim);

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
            if (isLinearPath) mesh = RepairMissingEndCap(mesh, shape);
            return mesh;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrimMeshService] mesh generation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rebuilds a straight-extruded prim's missing start cap (see <see cref="Generate"/>).
    ///
    /// <para>MeshFoundry does emit a submesh for that face — it just fills it with garbage: for a
    /// unit box the "bottom" submesh is four vertices collapsed onto a single vertical edge, zero
    /// area, zero-length normals. So a box renders with 5 of its 6 faces (total surface area 5.0
    /// instead of 6.0). Which face is missing in world space depends on the prim's rotation, so a
    /// wall built as a thin box can be missing the face pointing at the camera; with
    /// <c>CullMode.Disabled</c> you then see straight into the prim's interior.</para>
    ///
    /// <para>An earlier attempt at this repair was pulled in commit 6ee14fe because it matched the
    /// cap ring to the side walls by nearest-vertex search, which produced self-intersecting
    /// triangles and leaked NaNs. This version never searches: it takes the side walls' own
    /// vertices that already lie in the cap plane, deduplicates them exactly, orders them by angle
    /// around their centroid (valid because every straight-extruded profile SL can produce here is
    /// convex) and fan-triangulates. Every input coordinate is a finite vertex the mesher already
    /// emitted, so no new value is ever computed from a division or a normalization — there is
    /// nothing for a NaN to come from. A hollow prim's cap is an annulus rather than a disc, so a
    /// fan would plate over the hole — that case is handed off to
    /// <see cref="RepairHollowCap"/> instead, which splits the same gathered ring into an outer and
    /// inner loop before triangulating. Anything neither path can prove safe, it declines: fewer
    /// than three distinct ring points, an outer/inner split that isn't clean, and any triangle
    /// that would come out degenerate all return the mesh untouched.</para>
    /// </summary>
    private static MeshData? RepairMissingEndCap(MeshData? mesh, PrimShape shape)
    {
        if (mesh == null || mesh.Submeshes.Count < 2) return mesh;

        // Two shapes of the same bug: a box gets a placeholder submesh filled with zero-area
        // garbage, a cylinder gets no cap submesh at all. capIndex < 0 means the latter — the
        // rebuilt cap is appended instead of replacing anything. Hollow prims essentially never
        // hit the zero-area case (their placeholder, when one exists, is a nonzero-area bridge
        // between the outer and inner ring — see RepairHollowCap), so this is solid-shape-only in
        // practice, but it's harmless to run unconditionally.
        int capIndex = -1;
        for (int i = 0; i < mesh.Submeshes.Count; i++)
        {
            if (TotalArea(mesh.Submeshes[i]) < 1e-6f) { capIndex = i; break; }
        }

        // The extrusion runs along prim-local Z, so the missing cap sits at one Z extreme. Pick
        // whichever extreme the intact faces do NOT already cover with a real cap.
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < mesh.Submeshes.Count; i++)
        {
            if (i == capIndex) continue;
            foreach (var p in mesh.Submeshes[i].Positions)
            {
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }
        }
        if (minZ > maxZ) return mesh;

        bool topIsCapped = HasPlanarFaceAt(mesh, capIndex, maxZ);
        bool bottomIsCapped = HasPlanarFaceAt(mesh, capIndex, minZ);
        if (topIsCapped == bottomIsCapped) return mesh;   // both or neither — not the case we fix
        float capZ = topIsCapped ? minZ : maxZ;
        // Outward is away from the body of the prim.
        float outward = topIsCapped ? -1f : 1f;

        // Gather the side walls' own vertices lying in the cap plane, deduplicated by exact value
        // so the rebuilt cap is welded to the walls rather than merely coincident with them. For a
        // hollow prim this pulls in both the outer boundary AND the inner (hollow) boundary —
        // RepairHollowCap splits them back apart.
        var ring = new List<Vector3>();
        for (int i = 0; i < mesh.Submeshes.Count; i++)
        {
            if (i == capIndex) continue;
            foreach (var p in mesh.Submeshes[i].Positions)
            {
                if (Math.Abs(p.Z - capZ) > 1e-4f) continue;
                bool seen = false;
                foreach (var q in ring) { if (q == p) { seen = true; break; } }
                if (!seen) ring.Add(p);
            }
        }
        if (ring.Count < 3) return mesh;

        if (shape.ProfileHollow > 0f) return RepairHollowCap(mesh, ring, capZ, outward);

        // Order the ring around its centroid. Convex profile => angular order is the boundary order.
        var centroid = Vector3.Zero;
        foreach (var p in ring) centroid += p;
        centroid /= ring.Count;
        SortByAngle(ring, centroid);

        var capNormal = new Vector3(0, 0, outward);
        var positions = ring.ToArray();
        var normals = new Vector3[positions.Length];
        var uvs = new Vector2[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            normals[i] = capNormal;
            // Planar projection, matching SL's cap UVs: the profile lives in [-0.5,0.5].
            uvs[i] = new Vector2(positions[i].X + 0.5f, positions[i].Y + 0.5f);
        }

        // Wind the fan so its geometric normal points the same way as capNormal.
        var indices = new List<int>((positions.Length - 2) * 3);
        for (int i = 1; i + 1 < positions.Length; i++)
        {
            if (!AddWoundTriangle(indices, positions, 0, i, i + 1, capNormal)) return mesh;
        }
        if (indices.Count == 0) return mesh;

        var repaired = new List<MeshSubmesh>(mesh.Submeshes);
        if (capIndex >= 0)
        {
            // Keep the placeholder's SL face number so the builder's texture for that face lands
            // on the rebuilt cap.
            repaired[capIndex] = new MeshSubmesh(positions, normals, uvs, indices.ToArray(),
                                                 mesh.Submeshes[capIndex].FaceIndex);
        }
        else
        {
            // No placeholder existed, so no face number was reserved. Convert() derives the SL
            // face number from list position and MeshFoundry emits faces in SL order, so the
            // missing cap is the next number after the last face that was emitted.
            repaired.Add(new MeshSubmesh(positions, normals, uvs, indices.ToArray(), NextFaceNumber(mesh)));
        }
        return new MeshData(repaired, mesh.Skin);
    }

    /// <summary>
    /// Rebuilds a HOLLOW prim's missing end cap as an annulus (see
    /// <see cref="RepairMissingEndCap"/>, which gathers <paramref name="ring"/> and hands off to
    /// this method when <c>ProfileHollow &gt; 0</c>).
    ///
    /// <para>The gathered ring mixes the outer profile boundary with the inner (hollow) boundary.
    /// For every straight extrusion this repair supports, the hollow boundary is the outer
    /// boundary's own shape scaled toward the same centroid — verified against the raw MeshFoundry
    /// output for a 0.5-hollow box, cylinder, and prism: in every case the ring separates into two
    /// tight clusters of distance-from-centroid (e.g. the box's outer corners all sit at 0.7071 and
    /// its inner corners all sit at 0.3536 — never closer than a factor of two apart) with nothing
    /// in between. That is what lets this split by distance rather than by guessing which vertex
    /// belongs to which loop.</para>
    ///
    /// <para>Once split, each loop is ordered by angle around the shared centroid (same convexity
    /// argument as the solid fan) and the two loops are stitched into a triangle strip by matching
    /// equal angular rank — outer[i] against inner[i] — mirroring the strip MeshFoundry's own
    /// (correctly-generated) opposite cap already uses. Declines the moment that assumption can't
    /// be verified: no clear gap between two distance clusters, fewer than three points on either
    /// side, unequal outer/inner counts (a hollow shape that isn't just a scaled copy of the outer
    /// profile can't be safely paired 1:1 by angle alone), or a degenerate triangle.</para>
    ///
    /// <para>This does not touch the hollow interior's own side-wall submesh, which MeshFoundry
    /// generates separately and which can carry its own unrelated seam artifacts — out of scope
    /// for a missing-end-cap repair.</para>
    /// </summary>
    private static MeshData? RepairHollowCap(MeshData mesh, List<Vector3> ring, float capZ, float outward)
    {
        var centroid = Vector3.Zero;
        foreach (var p in ring) centroid += p;
        centroid /= ring.Count;

        var byDist = new List<(Vector3 p, float d)>(ring.Count);
        foreach (var p in ring) byDist.Add((p, (p - centroid).Length()));
        byDist.Sort((a, b) => a.d.CompareTo(b.d));

        // Find the widest gap in the sorted distances — that's the boundary between the inner
        // (hollow) loop and the outer loop.
        int splitAt = -1;
        float bestGap = 0f;
        for (int i = 0; i + 1 < byDist.Count; i++)
        {
            float gap = byDist[i + 1].d - byDist[i].d;
            if (gap > bestGap) { bestGap = gap; splitAt = i; }
        }
        // Require room for a real gap (at least 3 points on each side) and require it to be a
        // genuine separation, not just the largest of many similar steps in a smooth spread.
        if (splitAt < 2 || bestGap < 1e-4f) return mesh;

        var inner = new List<Vector3>();
        var outer = new List<Vector3>();
        for (int i = 0; i <= splitAt; i++) inner.Add(byDist[i].p);
        for (int i = splitAt + 1; i < byDist.Count; i++) outer.Add(byDist[i].p);
        if (inner.Count < 3 || outer.Count < 3 || inner.Count != outer.Count) return mesh;

        SortByAngle(outer, centroid);
        SortByAngle(inner, centroid);

        int n = outer.Count;
        var positions = new Vector3[2 * n];
        for (int i = 0; i < n; i++) { positions[i] = outer[i]; positions[n + i] = inner[i]; }

        var capNormal = new Vector3(0, 0, outward);
        var normals = new Vector3[positions.Length];
        var uvs = new Vector2[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            normals[i] = capNormal;
            // Planar projection, matching SL's cap UVs: the profile lives in [-0.5,0.5].
            uvs[i] = new Vector2(positions[i].X + 0.5f, positions[i].Y + 0.5f);
        }

        // Strip-triangulate the annulus: each step bridges outer[i]/outer[i+1] to inner[i]/inner[i+1].
        var indices = new List<int>(n * 6);
        for (int i = 0; i < n; i++)
        {
            int i2 = (i + 1) % n;
            if (!AddWoundTriangle(indices, positions, i, i2, n + i, capNormal)) return mesh;
            if (!AddWoundTriangle(indices, positions, i2, n + i2, n + i, capNormal)) return mesh;
        }

        var outerXY = new List<(float X, float Y)>(n);
        var innerXY = new List<(float X, float Y)>(n);
        foreach (var p in outer) outerXY.Add((p.X, p.Y));
        foreach (var p in inner) innerXY.Add((p.X, p.Y));

        var repaired = new List<MeshSubmesh>(mesh.Submeshes);
        int targetIndex = FindHollowCapPlaceholder(mesh, outerXY, innerXY);
        if (targetIndex >= 0)
        {
            repaired[targetIndex] = new MeshSubmesh(positions, normals, uvs, indices.ToArray(),
                                                     mesh.Submeshes[targetIndex].FaceIndex);
        }
        else
        {
            repaired.Add(new MeshSubmesh(positions, normals, uvs, indices.ToArray(), NextFaceNumber(mesh)));
        }
        return new MeshData(repaired, mesh.Skin);
    }

    /// <summary>
    /// A hollow prim's end-cap face slot is sometimes reserved but filled with garbage rather than
    /// left absent: a single quad that bridges the ring's first outer vertex straight to its first
    /// inner vertex, spanning the whole extrusion height instead of lying flat in the cap plane —
    /// unlike a real wall segment, which always connects two vertices from the SAME loop. That shape
    /// is unmistakable once the outer/inner split is known: exactly two distinct (X, Y) positions,
    /// one from each loop. Returns the placeholder's submesh index so <see cref="RepairHollowCap"/>
    /// can replace it (preserving its SL face number); returns -1 when nothing matches — e.g. hollow
    /// cylinders, where MeshFoundry leaves the slot's number entirely unused instead of filling it
    /// with a bad quad, and the cap is appended under a fresh number instead.
    /// </summary>
    private static int FindHollowCapPlaceholder(MeshData mesh, List<(float X, float Y)> outerXY, List<(float X, float Y)> innerXY)
    {
        for (int i = 0; i < mesh.Submeshes.Count; i++)
        {
            var distinct = new List<(float X, float Y)>();
            foreach (var p in mesh.Submeshes[i].Positions)
            {
                var xy = (p.X, p.Y);
                bool seen = false;
                foreach (var q in distinct) { if (q == xy) { seen = true; break; } }
                if (!seen) distinct.Add(xy);
                if (distinct.Count > 2) break;
            }
            if (distinct.Count != 2) continue;

            bool hasOuter = false, hasInner = false;
            foreach (var xy in distinct)
            {
                if (!hasOuter) foreach (var o in outerXY) { if (o == xy) { hasOuter = true; break; } }
                if (!hasInner) foreach (var q in innerXY) { if (q == xy) { hasInner = true; break; } }
            }
            if (hasOuter && hasInner) return i;
        }
        return -1;
    }

    /// <summary>Smallest SL face number not already used by any submesh in <paramref name="mesh"/>
    /// — fills a gap MeshFoundry left in the numbering (e.g. a hollow cylinder's missing bottom
    /// slot), or one past the highest face number in use if the existing numbers are contiguous.</summary>
    private static int NextFaceNumber(MeshData mesh)
    {
        var used = new HashSet<int>();
        int max = -1;
        foreach (var sm in mesh.Submeshes) { used.Add(sm.FaceIndex); if (sm.FaceIndex > max) max = sm.FaceIndex; }
        for (int i = 0; i <= max; i++)
        {
            if (!used.Contains(i)) return i;
        }
        return max + 1;
    }

    /// <summary>Sorts ring points by angle around <paramref name="centroid"/> in the XY plane.
    /// Valid boundary order for any convex profile — every straight-extruded profile SL can
    /// produce, and every hollow loop this repair accepts, is convex.</summary>
    private static void SortByAngle(List<Vector3> ring, Vector3 centroid)
    {
        ring.Sort((a, b) => MathF.Atan2(a.Y - centroid.Y, a.X - centroid.X)
                     .CompareTo(MathF.Atan2(b.Y - centroid.Y, b.X - centroid.X)));
    }

    /// <summary>Adds one triangle to <paramref name="indices"/>, winding it so its geometric normal
    /// points the same way as <paramref name="desiredNormal"/>. Returns false — adding nothing —
    /// if the triangle would be degenerate.</summary>
    private static bool AddWoundTriangle(List<int> indices, Vector3[] positions, int ia, int ib, int ic, Vector3 desiredNormal)
    {
        var a = positions[ia];
        var b = positions[ib];
        var c = positions[ic];
        var geo = Vector3.Cross(b - a, c - a);
        if (geo.Length() < 1e-9f) return false;
        if (Vector3.Dot(geo, desiredNormal) < 0f) (ib, ic) = (ic, ib);

        indices.Add(ia);
        indices.Add(ib);
        indices.Add(ic);
        return true;
    }

    private static float TotalArea(MeshSubmesh sm)
    {
        float area = 0;
        for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
        {
            var a = sm.Positions[sm.Indices[t]];
            var b = sm.Positions[sm.Indices[t + 1]];
            var c = sm.Positions[sm.Indices[t + 2]];
            area += Vector3.Cross(b - a, c - a).Length() * 0.5f;
        }
        return area;
    }

    /// <summary>True if some submesh other than <paramref name="skipIndex"/> is a real (non-zero
    /// area) face lying entirely in the plane z == <paramref name="z"/> — i.e. that end is already
    /// capped.</summary>
    private static bool HasPlanarFaceAt(MeshData mesh, int skipIndex, float z)
    {
        for (int i = 0; i < mesh.Submeshes.Count; i++)
        {
            if (i == skipIndex) continue;
            var sm = mesh.Submeshes[i];
            if (sm.Positions.Length < 3 || TotalArea(sm) < 1e-6f) continue;

            bool planar = true;
            foreach (var p in sm.Positions)
            {
                if (Math.Abs(p.Z - z) > 1e-4f) { planar = false; break; }
            }
            if (planar) return true;
        }
        return false;
    }

    /// <summary>
    /// Generates a sculpted-prim mesh from its decoded sculpt-map (RGBA). The map encodes
    /// vertex positions as RGB, interpreted per <paramref name="sculptType"/> (low 3 bits:
    /// Sphere/Torus/Plane/Cylinder; 0x40/0x80: Invert/Mirror flags). CPU-bound — call from a
    /// worker thread.
    ///
    /// Uses the LOCAL, patched <see cref="SLNG.Assets.PrimMesher.SculptMesh"/> — NOT the NuGet
    /// <c>LibreMetaverse.Rendering.MeshFoundry</c> package's sculpt mesher. MeshFoundry's copy of
    /// this class has a wrap-seam bug (verified against the real SL viewer's
    /// LLVolume::sculptGenerateMapVertices in llvolume.cpp): closing the horizontal wrap ring
    /// branches on the mesh's row count being even/odd, and for the odd case — which is the
    /// common case for any Highest-LOD sculpt, since repeated power-of-two halving lands on an
    /// even pre-wrap size that then gets +1'd to odd — it overwrites column 0 with the FAR
    /// (opposite) edge's sampled column instead of the real viewer's simple "always re-sample
    /// column 0" wrap. That silently swaps in the wrong edge pixel at the seam, producing a real
    /// geometric crease/kink on organic sculpts (confirmed on a tree's branch sculpt). Since we
    /// can't patch a NuGet DLL, the fix lives in our own vendored copy
    /// (PrimMesher/SculptMesh.cs), and this method calls that copy directly instead.
    /// </summary>
    public static MeshData? GenerateSculpt(byte[] rgba, int width, int height, byte sculptType, DetailLevel lod = DetailLevel.Highest)
    {
        if (rgba == null || width <= 0 || height <= 0 || rgba.Length < width * height * 4) return null;

        try
        {
            using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(rgba, 0, bmp.GetPixels(), width * height * 4);

            // Same LOD->grid-resolution budget MeshFoundry used, so LOD behavior is unchanged.
            int mesherLod = 32;
            switch (lod)
            {
                case DetailLevel.Medium: mesherLod /= 2; break;
                case DetailLevel.Low: mesherLod /= 4; break;
            }

            byte baseType = (byte)(sculptType & 0x07);
            bool invert = (sculptType & 0x40) != 0;
            bool mirror = (sculptType & 0x80) != 0;
            var smType = baseType switch
            {
                1 => SLNG.Assets.PrimMesher.SculptMesh.SculptType.sphere,
                2 => SLNG.Assets.PrimMesher.SculptMesh.SculptType.torus,
                3 => SLNG.Assets.PrimMesher.SculptMesh.SculptType.plane,
                4 => SLNG.Assets.PrimMesher.SculptMesh.SculptType.cylinder,
                _ => SLNG.Assets.PrimMesher.SculptMesh.SculptType.plane,
            };

            var mesh = new SLNG.Assets.PrimMesher.SculptMesh(bmp, smType, mesherLod, true, mirror, invert);
            return ConvertSculptMesh(mesh);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PrimMeshService] sculpt generation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Converts a local <see cref="SLNG.Assets.PrimMesher.SculptMesh"/> (built with
    /// <c>viewerMode: true</c>, so coords/normals/uvs are all 1:1 index-aligned and
    /// <c>calcVertexNormals</c> has already produced correct smooth per-vertex normals, including
    /// welding the wrap-seam's normals) into a single-submesh <see cref="MeshData"/>. Sculpts
    /// always have exactly one texture face (face 0).</summary>
    private static MeshData? ConvertSculptMesh(SLNG.Assets.PrimMesher.SculptMesh? mesh)
    {
        if (mesh?.faces == null || mesh.faces.Count == 0 || mesh.coords == null || mesh.coords.Count == 0) return null;

        int n = mesh.coords.Count;
        var positions = new Vector3[n];
        var normals = new Vector3[n];
        var uvs = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var c = mesh.coords[i];
            float x = c.X, y = c.Y, z = c.Z;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
            {
                return null; // genuinely corrupt sculpt map data — fall back to a placeholder
            }
            positions[i] = new Vector3(x, y, z);

            var nc = i < mesh.normals.Count ? mesh.normals[i] : new SLNG.Assets.PrimMesher.Coord(0, 0, 1);
            normals[i] = new Vector3(nc.X, nc.Y, nc.Z);

            var uv = i < mesh.uvs.Count ? mesh.uvs[i] : default;
            uvs[i] = new Vector2(uv.U, uv.V);
        }

        var indices = new int[mesh.faces.Count * 3];
        for (int t = 0; t < mesh.faces.Count; t++)
        {
            var f = mesh.faces[t];
            indices[t * 3] = f.v1;
            indices[t * 3 + 1] = f.v2;
            indices[t * 3 + 2] = f.v3;
        }

        return new MeshData(new List<MeshSubmesh> { new MeshSubmesh(positions, normals, uvs, indices, 0) });
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
    private static MeshData? Convert(FacetedMesh? faceted, Primitive prim)
    {
        if (faceted?.Faces == null || faceted.Faces.Count == 0) return null;

        var submeshes = new List<MeshSubmesh>(faceted.Faces.Count);
        for (int faceNumber = 0; faceNumber < faceted.Faces.Count; faceNumber++)
        {
            var face = faceted.Faces[faceNumber];
            int trueFaceId = faceNumber;
            if (face.TextureFace != null && prim.Textures?.FaceTextures != null)
            {
                for (int i = 0; i < prim.Textures.FaceTextures.Length; i++)
                {
                    if (object.ReferenceEquals(prim.Textures.GetFace((uint)i), face.TextureFace))
                    {
                        trueFaceId = i;
                        break;
                    }
                }
            }
            if (face.Vertices == null || face.Indices == null || face.Indices.Count == 0) continue;

            int n = face.Vertices.Count;
            var positions = new Vector3[n];
            var normals = new Vector3[n];
            var uvs = new Vector2[n];
            // Genuine garbage (NaN/Inf/absurd magnitude) from a MeshFoundry meshing glitch. We drop
            // only the triangles that touch such a vertex — NOT the whole object. Rejecting the
            // entire mesh on a single bad vertex (as this used to) made large/complex sculpt maps
            // (512×512 etc.) vanish completely (MESH NULL) instead of losing one stray triangle.
            var badVertex = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var v = face.Vertices[i];
                float ux = v.Position.X, uy = v.Position.Y, uz = v.Position.Z;
                float nx = v.Normal.X, ny = v.Normal.Y, nz = v.Normal.Z;

                // A sculpt lives in [-0.5, 0.5] unit space; ±5 is a loose "this isn't geometry,
                // it's garbage" bound, not a clamp of legitimate coordinates.
                bool bad = float.IsNaN(ux) || float.IsNaN(uy) || float.IsNaN(uz)
                    || float.IsInfinity(ux) || float.IsInfinity(uy) || float.IsInfinity(uz)
                    || Math.Abs(ux) > 5.0f || Math.Abs(uy) > 5.0f || Math.Abs(uz) > 5.0f
                    || float.IsNaN(nx) || float.IsNaN(ny) || float.IsNaN(nz)
                    || float.IsInfinity(nx) || float.IsInfinity(ny) || float.IsInfinity(nz);
                badVertex[i] = bad;

                // Sanitize so a garbage vertex can never leak a NaN into a neighbouring kept
                // triangle even if an index still references it. Zero-length normals are left
                // as-is (see the triangle loop) — they are NOT garbage.
                positions[i] = bad ? Vector3.Zero : new Vector3(ux, uy, uz);
                normals[i] = bad ? new Vector3(0, 0, 1) : new Vector3(nx, ny, nz);
                uvs[i] = new Vector2(v.TexCoord.X, v.TexCoord.Y);
            }

            var validIndices = new List<int>(face.Indices.Count);
            for (int t = 0; t + 2 < face.Indices.Count; t += 3)
            {
                int i1 = face.Indices[t];
                int i2 = face.Indices[t + 1];
                int i3 = face.Indices[t + 2];

                // Drop a triangle only if it touches a genuinely-garbage vertex. Do NOT drop it
                // merely because a vertex normal is zero-length: at a sculpt's pole/seam the
                // surface legitimately converges to a point and those triangles are real geometry.
                // Deleting them (as the previous "skip degenerate" filter did) is what tore the
                // sharp spiky holes into organic sculpts' branch tips — the jagged-tree symptom.
                if (badVertex[i1] || badVertex[i2] || badVertex[i3])
                {
                    continue;
                }

                validIndices.Add(i1);
                validIndices.Add(i2);
                validIndices.Add(i3);
            }

            if (validIndices.Count == 0) continue;

            submeshes.Add(new MeshSubmesh(positions, normals, uvs, validIndices.ToArray(), trueFaceId));
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
