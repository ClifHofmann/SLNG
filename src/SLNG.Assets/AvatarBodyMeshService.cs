using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using LibreMetaverse.Rendering;

namespace SLNG.Assets;

/// <summary>
/// Loads SL base-avatar body-part meshes from LibreMetaverse's .llm character files.
/// Emits a protocol-neutral <see cref="AvatarBodyMeshData"/> — no LibreMetaverse type escapes
/// this class. The caller (app layer) is responsible for resolving the character-directory path.
/// </summary>
public static class AvatarBodyMeshService
{
    // Core parts that make a visible avatar; skirt is optional and omitted here.
    private static readonly string[] CoreParts =
    {
        "avatar_head", "avatar_upper_body", "avatar_lower_body",
        "avatar_eye", "avatar_eyelashes", "avatar_hair"
    };

    // The base body mesh is identical for every avatar, so parse the .llm files once and
    // cache the result. Without this, each avatar that appears re-reads and re-decodes six
    // binary meshes — on a busy region (many avatars) that blocks whatever thread calls
    // Load. Keyed by character directory; guarded for the (unlikely) multi-thread caller.
    private static readonly Dictionary<string, AvatarBodyMeshData?> _cache = new();
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Loads all core body-part meshes from <paramref name="characterDir"/>, caching the
    /// parsed result. Returns null if the directory or skeleton file is missing.
    /// </summary>
    public static AvatarBodyMeshData? Load(string characterDir)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(characterDir, out var cached)) return cached;
            var result = LoadUncached(characterDir);
            _cache[characterDir] = result;
            return result;
        }
    }

    private static AvatarBodyMeshData? LoadUncached(string characterDir)
    {
        var skelFile = System.IO.Path.Combine(characterDir, "avatar_skeleton.xml");
        if (!File.Exists(skelFile))
        {
            Console.Error.WriteLine($"[AvatarBodyMeshService] skeleton not found: {skelFile}");
            return null;
        }

        LindenSkeleton skeleton;
        try
        {
            skeleton = LindenSkeleton.Load(skelFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AvatarBodyMeshService] skeleton load failed: {ex.Message}");
            return null;
        }

        var parts = new List<AvatarBodyPartMesh>(CoreParts.Length);
        foreach (var partName in CoreParts)
        {
            var meshFile = System.IO.Path.Combine(characterDir, partName + ".llm");
            if (!File.Exists(meshFile)) continue;

            try
            {
                var lmesh = new LindenMesh(partName, skeleton);
                lmesh.LoadMesh(meshFile);

                if (lmesh.Vertices == null || lmesh.Vertices.Length == 0) continue;

                int n = lmesh.Vertices.Length;
                var positions = new Vector3[n];
                var normals = new Vector3[n];
                var uvs = new Vector2[n];
                var b1Names = new string?[n];
                var b1Weights = new float[n];
                var b2Names = new string?[n];
                var b2Weights = new float[n];

                for (int i = 0; i < n; i++)
                {
                    var v = lmesh.Vertices[i];
                    positions[i] = new Vector3(v.Coord.X, v.Coord.Y, v.Coord.Z);
                    normals[i] = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
                    uvs[i] = new Vector2(v.TexCoord.X, v.TexCoord.Y);

                    if (lmesh.SkinWeights != null && i < lmesh.SkinWeights.Count)
                    {
                        b1Names[i] = lmesh.SkinWeights[i].Bone1;
                        b1Weights[i] = lmesh.SkinWeights[i].Weight1;
                        b2Names[i] = lmesh.SkinWeights[i].Bone2;
                        b2Weights[i] = lmesh.SkinWeights[i].Weight2;
                    }
                    else
                    {
                        b1Weights[i] = 1.0f;
                    }
                }

                // Flatten triangle indices (each LindenMesh.Face is one triangle = 3 Int16s)
                var indices = new List<int>(lmesh.NumFaces * 3);
                if (lmesh.Faces != null)
                    foreach (var face in lmesh.Faces)
                        foreach (var idx in face.Indices)
                            indices.Add(idx);

                var morphTargets = new List<AvatarMorphTarget>();
                if (lmesh.Morphs != null)
                {
                    foreach (var m in lmesh.Morphs)
                    {
                        int morphVertCount = m.NumVertices;
                        if (morphVertCount == 0 || m.Vertices == null) continue;

                        var mIndices = new int[morphVertCount];
                        var mPos = new Vector3[morphVertCount];
                        var mNorm = new Vector3[morphVertCount];

                        for (int i = 0; i < morphVertCount; i++)
                        {
                            var mv = m.Vertices[i];
                            mIndices[i] = (int)mv.VertexIndex;
                            mPos[i] = new Vector3(mv.Coord.X, mv.Coord.Y, mv.Coord.Z);
                            mNorm[i] = new Vector3(mv.Normal.X, mv.Normal.Y, mv.Normal.Z);
                        }

                        morphTargets.Add(new AvatarMorphTarget(m.Name, mIndices, mPos, mNorm));
                    }
                }

                parts.Add(new AvatarBodyPartMesh(
                    partName.Replace("avatar_", ""),
                    positions, normals, uvs, indices.ToArray(),
                    b1Names, b1Weights, b2Names, b2Weights,
                    morphTargets.Count > 0 ? morphTargets : null));

                Console.WriteLine($"[AvatarBodyMeshService] Loaded {partName}: {n} verts, {lmesh.NumFaces} tris");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AvatarBodyMeshService] Failed to load {partName}: {ex.Message}");
            }
        }

        return parts.Count > 0 ? new AvatarBodyMeshData(parts) : null;
    }
}
