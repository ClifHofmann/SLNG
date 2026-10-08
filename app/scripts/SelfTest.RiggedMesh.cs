using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// BUG-PERF-06: a worn mesh's geometry is now prepared on a worker thread -- SLNG.Assets'
    /// RiggedMeshBuilder plus Godot's own MikkTSpace pass through <c>SurfaceTool.CreateFromArrays</c> --
    /// instead of being fed to a <c>SurfaceTool</c> vertex by vertex on the main thread. This builds a
    /// small rigged mesh both ways, the new one on a thread-pool thread, and requires every engine array
    /// to come out identical: positions, normals, tangents, UVs, bones, weights and indices, surface by
    /// surface. The old build is kept below verbatim as the oracle. The mesh has non-uniform bind-shape
    /// scale, two faces that share a texture (one merged surface) and one that does not, a mirrored-UV
    /// triangle (negative tangent handedness), an out-of-range joint and a joint that names no bone.
    /// </summary>
    private static Check CheckRiggedMeshPreparedOffMainThread()
    {
        const string Name = "rigged mesh prepared off the main thread";
        var problems = new List<string>();
        try
        {
            var (mesh, slots, faces, defaultFace) = SyntheticRiggedMesh();

            var prepared = Task.Run(() => AvatarRenderer.PrepareRiggedMesh(mesh, slots, faces, defaultFace))
                .GetAwaiter().GetResult();
            var legacy = LegacyRiggedSurfaceArrays(mesh, slots, faces, defaultFace, out var legacyFaces);

            if (prepared.SurfaceArrays.Length != legacy.Count)
                problems.Add($"{prepared.SurfaceArrays.Length} surfaces, the old build made {legacy.Count}");
            if (!prepared.Geometry.FaceIndices().AsSpan().SequenceEqual(legacyFaces.ToArray()))
                problems.Add($"face list [{string.Join(",", prepared.Geometry.FaceIndices())}] vs [{string.Join(",", legacyFaces)}]");

            for (int s = 0; s < Math.Min(prepared.SurfaceArrays.Length, legacy.Count); s++)
            {
                var a = prepared.SurfaceArrays[s];
                var b = legacy[s];
                // Equal-but-missing would pass the comparison below, so the tangents must exist.
                int vertexCount = a[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                if (vertexCount == 0 || a[(int)Mesh.ArrayType.Tangent].AsFloat32Array().Length != 4 * vertexCount)
                    problems.Add($"surface {s}: {vertexCount} vertices, tangents missing");
                Compare(problems, s, "vertex", a[(int)Mesh.ArrayType.Vertex].AsVector3Array(), b[(int)Mesh.ArrayType.Vertex].AsVector3Array());
                Compare(problems, s, "normal", a[(int)Mesh.ArrayType.Normal].AsVector3Array(), b[(int)Mesh.ArrayType.Normal].AsVector3Array());
                Compare(problems, s, "tangent", a[(int)Mesh.ArrayType.Tangent].AsFloat32Array(), b[(int)Mesh.ArrayType.Tangent].AsFloat32Array());
                Compare(problems, s, "uv", a[(int)Mesh.ArrayType.TexUV].AsVector2Array(), b[(int)Mesh.ArrayType.TexUV].AsVector2Array());
                Compare(problems, s, "bones", a[(int)Mesh.ArrayType.Bones].AsInt32Array(), b[(int)Mesh.ArrayType.Bones].AsInt32Array());
                Compare(problems, s, "weights", a[(int)Mesh.ArrayType.Weights].AsFloat32Array(), b[(int)Mesh.ArrayType.Weights].AsFloat32Array());
                Compare(problems, s, "index", a[(int)Mesh.ArrayType.Index].AsInt32Array(), b[(int)Mesh.ArrayType.Index].AsInt32Array());
            }

            // And the engine takes the worker's arrays as they are.
            var arrayMesh = new ArrayMesh();
            foreach (var arrays in prepared.SurfaceArrays)
                arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (arrayMesh.GetSurfaceCount() != prepared.SurfaceArrays.Length)
                problems.Add($"ArrayMesh took {arrayMesh.GetSurfaceCount()} of {prepared.SurfaceArrays.Length} surfaces");

            return problems.Count == 0
                ? new Check(Name, true, $"{prepared.SurfaceArrays.Length} surfaces, {prepared.Geometry.TotalVertices} vertices identical to the per-vertex build")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Compare<T>(List<string> problems, int surface, string what, T[] actual, T[] expected)
        where T : IEquatable<T>
    {
        if (actual.Length != expected.Length)
        {
            problems.Add($"surface {surface} {what}: {actual.Length} values, expected {expected.Length}");
            return;
        }
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i].Equals(expected[i])) continue;
            problems.Add($"surface {surface} {what}[{i}]: {actual[i]} vs {expected[i]}");
            return;
        }
    }

    private static (MeshData Mesh, int[] Slots, FaceTexture[] Faces, FaceTexture DefaultFace) SyntheticRiggedMesh()
    {
        var hair = default(FaceTexture) with { TextureId = Guid.Parse("b9af3b5f-0000-0000-0000-000000000001") };
        var skin = default(FaceTexture) with { TextureId = Guid.Parse("b9af3b5f-0000-0000-0000-000000000002") };
        var faces = new[] { hair, hair, skin };

        // A 3x3 vertex grid of two-triangle quads, UVs varied so the tangents are not trivial.
        MeshSubmesh Grid(int face, float z, bool mirrorU)
        {
            var positions = new List<System.Numerics.Vector3>();
            var normals = new List<System.Numerics.Vector3>();
            var uvs = new List<System.Numerics.Vector2>();
            var weights = new List<VertexBoneWeights>();
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    positions.Add(new System.Numerics.Vector3(x * 0.1f, y * 0.07f, z + 0.01f * x * y));
                    normals.Add(System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.1f * x, 0.2f, 1f)));
                    float u = x * 0.5f;
                    uvs.Add(new System.Numerics.Vector2(mirrorU ? 1f - u : u, y * 0.4f + 0.05f * x));
                    // Joint 2 names no bone (slot -1 -> remapped); joint 5 is out of range (clamped).
                    weights.Add(new VertexBoneWeights(x % 3, (y + 1) % 3, 5, 0, 0.5f, 0.3f, 0.2f, 0f));
                }
            }
            var indices = new List<int>();
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    int i = y * 3 + x;
                    indices.AddRange(new[] { i, i + 1, i + 4, i, i + 4, i + 3 });
                }
            }
            return new MeshSubmesh(positions.ToArray(), normals.ToArray(), uvs.ToArray(), indices.ToArray(), face, weights.ToArray());
        }

        var bindShape = System.Numerics.Matrix4x4.CreateScale(1.5f, 0.5f, 2f)
                        * System.Numerics.Matrix4x4.CreateRotationZ(0.3f)
                        * System.Numerics.Matrix4x4.CreateTranslation(0.1f, -0.2f, 1.2f);
        var meshSkin = new MeshSkin(
            new[] { "mPelvis", "mChest", "notABone" },
            new[] { System.Numerics.Matrix4x4.Identity, System.Numerics.Matrix4x4.Identity, System.Numerics.Matrix4x4.Identity },
            bindShape,
            0f);
        var mesh = new MeshData(new[] { Grid(0, 0f, false), Grid(1, 0.2f, true), Grid(2, 0.4f, false) }, meshSkin);
        return (mesh, new[] { 0, 1, -1 }, faces, default);
    }

    /// <summary>The per-vertex build as it was before BUG-PERF-06 (AvatarRenderer.BuildRiggedMeshInstance's
    /// submesh loop and AddInfluence), kept unchanged as the oracle for the worker build. Diagnostics
    /// that do not reach the arrays are left out.</summary>
    private static List<Godot.Collections.Array> LegacyRiggedSurfaceArrays(
        MeshData meshData, int[] slotForJoint, FaceTexture[]? faces, FaceTexture defaultFace, out List<int> faceList)
    {
        var skinData = meshData.Skin!;
        int jointCount = skinData.JointNames.Length;
        var bindShape = skinData.BindShapeMatrix;
        var bindShapeNormalMatrix = bindShape;
        if (System.Numerics.Matrix4x4.Invert(bindShape, out var bindShapeInv))
            bindShapeNormalMatrix = System.Numerics.Matrix4x4.Transpose(bindShapeInv);

        FaceTexture Resolve(int faceIndex) =>
            (faces != null && faceIndex >= 0 && faceIndex < faces.Length) ? faces[faceIndex] : defaultFace;

        var result = new List<Godot.Collections.Array>();
        faceList = new List<int>();
        SurfaceTool? st = null;
        var runFace = default(FaceTexture);
        int runVertexBase = 0;
        var bones = new int[4];
        var wts = new float[4];
        var guard = new MeshArrayGuard.VertexGuard();
        int totalVerts = 0, remapped = 0;

        void FlushRun()
        {
            if (st == null) return;
            st.GenerateTangents();
            result.Add(st.CommitToArrays());
            st = null;
        }

        foreach (var sub in meshData.Submeshes)
        {
            if (sub.Indices.Length == 0 || sub.Weights == null) continue;

            var subFace = Resolve(sub.FaceIndex);
            if (st == null || !subFace.Equals(runFace))
            {
                FlushRun();
                st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                runFace = subFace;
                runVertexBase = 0;
                faceList.Add(sub.FaceIndex);
            }

            int indexBase = runVertexBase;
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var pSL = System.Numerics.Vector3.Transform(sub.Positions[i], bindShape);
                var nSL = System.Numerics.Vector3.TransformNormal(sub.Normals[i], bindShapeNormalMatrix);
                if (nSL.LengthSquared() > 1e-8f) nSL = System.Numerics.Vector3.Normalize(nSL);
                var uv = sub.UVs[i];
                var w = sub.Weights[i];

                Array.Clear(bones, 0, 4);
                Array.Clear(wts, 0, 4);
                int c = 0; float sum = 0f;
                LegacyAddInfluence(w.Joint0, w.Weight0, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                LegacyAddInfluence(w.Joint1, w.Weight1, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                LegacyAddInfluence(w.Joint2, w.Weight2, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                LegacyAddInfluence(w.Joint3, w.Weight3, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                totalVerts++;
                if (sum > 1e-5f) { for (int k = 0; k < 4; k++) wts[k] /= sum; }
                else { bones[0] = 0; wts[0] = 1f; }

                st.SetBones(bones);
                st.SetWeights(wts);
                st.SetNormal(guard.Normal(new Vector3(nSL.X, nSL.Z, -nSL.Y), totalVerts));
                st.SetUV(guard.Uv(new Vector2(uv.X, 1.0f - uv.Y), totalVerts));
                st.AddVertex(guard.Position(new Vector3(pSL.X, pSL.Z, -pSL.Y), totalVerts));
            }

            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                st.AddIndex(indexBase + sub.Indices[t]);
                st.AddIndex(indexBase + sub.Indices[t + 2]);
                st.AddIndex(indexBase + sub.Indices[t + 1]);
            }

            runVertexBase += sub.Positions.Length;
        }
        FlushRun();
        return result;
    }

    private static void LegacyAddInfluence(int joint, float weight, int[] slotForJoint, int jointCount,
        int[] bones, float[] wts, ref int count, ref float sum, ref int remapped)
    {
        if (count >= 4 || !float.IsFinite(weight) || weight <= 0f || jointCount <= 0) return;

        int j = joint;
        if (j < 0 || j >= jointCount)
        {
            j = Math.Clamp(j, 0, jointCount - 1);
            remapped++;
        }

        int slot = slotForJoint[j];
        if (slot < 0)
        {
            slot = 0;
            remapped++;
        }

        bones[count] = slot;
        wts[count] = weight;
        sum += weight;
        count++;
    }
}
