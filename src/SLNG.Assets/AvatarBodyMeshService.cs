using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml.Linq;
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

        // avatar_lad.xml maps each .llm to the VisualParams that morph it (see ParseMeshMorphParams).
        // Parsed once here; used per part to attach its morph targets.
        var meshMorphParams = ParseMeshMorphParams(characterDir);

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
                var normals   = new Vector3[n];
                var uvs       = new Vector2[n];
                var b1Names   = new string?[n];
                var b1Weights = new float[n];
                var b2Names   = new string?[n];
                var b2Weights = new float[n];

                for (int i = 0; i < n; i++)
                {
                    var v = lmesh.Vertices[i];
                    positions[i] = new Vector3(v.Coord.X, v.Coord.Y, v.Coord.Z);
                    normals[i]   = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);
                    uvs[i]       = new Vector2(v.TexCoord.X, v.TexCoord.Y);

                    if (lmesh.SkinWeights != null && i < lmesh.SkinWeights.Count)
                    {
                        b1Names[i]   = lmesh.SkinWeights[i].Bone1;
                        b1Weights[i] = lmesh.SkinWeights[i].Weight1;
                        b2Names[i]   = lmesh.SkinWeights[i].Bone2;
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

                // Attach this mesh's vertex morphs: for each VisualParam avatar_lad.xml declares on
                // this .llm, find the same-named morph target inside the mesh and convert its sparse
                // per-vertex deltas to an engine-neutral AvatarMorphTarget. VertexIndex indexes the
                // base vertex array directly (verified against LindenMesh + LLPolyMorphTarget::apply).
                var morphTargets = BuildMorphTargets(partName + ".llm", meshMorphParams, lmesh);

                parts.Add(new AvatarBodyPartMesh(
                    partName.Replace("avatar_", ""),
                    positions, normals, uvs, indices.ToArray(),
                    b1Names, b1Weights, b2Names, b2Weights, morphTargets));

                Console.WriteLine($"[AvatarBodyMeshService] Loaded {partName}: {n} verts, {lmesh.NumFaces} tris, {morphTargets.Count} morphs");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AvatarBodyMeshService] Failed to load {partName}: {ex.Message}");
            }
        }

        return parts.Count > 0 ? new AvatarBodyMeshData(parts) : null;
    }

    /// <summary>Parses avatar_lad.xml's <c>&lt;mesh file_name="X.llm"&gt;</c> blocks, collecting the
    /// VisualParams declared as vertex morphs (those with a <c>&lt;param_morph/&gt;</c> child) on
    /// each mesh. Result: llm file name → list of (paramId, morphName). Returns an empty map if the
    /// file is missing/unparseable so morphs simply don't attach (base mesh still renders).</summary>
    private static Dictionary<string, List<(int ParamId, string MorphName)>> ParseMeshMorphParams(string charDir)
    {
        var result = new Dictionary<string, List<(int, string)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = System.IO.Path.Combine(charDir, "avatar_lad.xml");
            if (!File.Exists(path)) return result;

            var doc = XDocument.Load(path);
            foreach (var mesh in doc.Descendants("mesh"))
            {
                var file = mesh.Attribute("file_name")?.Value;
                if (file == null) continue;

                foreach (var param in mesh.Elements("param"))
                {
                    // Only vertex-morph params (skeletal/driver params live elsewhere or have no
                    // <param_morph/> child). A bare <param_morph/> means "morph target named after
                    // this param, found inside the mesh file".
                    if (param.Element("param_morph") == null) continue;
                    var idAttr = param.Attribute("id");
                    var nameAttr = param.Attribute("name");
                    if (idAttr == null || nameAttr == null || !int.TryParse(idAttr.Value, out int id)) continue;

                    if (!result.TryGetValue(file, out var list))
                    {
                        list = new List<(int, string)>();
                        result[file] = list;
                    }
                    list.Add((id, nameAttr.Value));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AvatarBodyMeshService] morph-param parse failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>Builds the engine-neutral morph targets for one loaded mesh by matching each
    /// avatar_lad.xml morph param to the same-named morph inside the .llm. Falls back to the
    /// pre-"_Driven" base name exactly like the viewer (LLPolyMorphTarget::setInfo,
    /// llpolymorph.cpp:381-392).</summary>
    private static IReadOnlyList<AvatarMorphTarget> BuildMorphTargets(
        string llmFile,
        Dictionary<string, List<(int ParamId, string MorphName)>> meshMorphParams,
        LindenMesh lmesh)
    {
        var targets = new List<AvatarMorphTarget>();
        if (lmesh.Morphs == null || lmesh.Morphs.Length == 0) return targets;
        if (!meshMorphParams.TryGetValue(llmFile, out var paramList)) return targets;

        foreach (var (paramId, morphName) in paramList)
        {
            if (!TryFindMorph(lmesh.Morphs, morphName, out var morph)) continue;
            if (morph.Vertices == null || morph.Vertices.Length == 0) continue;

            int m = morph.Vertices.Length;
            var vidx = new int[m];
            var pdelta = new Vector3[m];
            var ndelta = new Vector3[m];
            for (int k = 0; k < m; k++)
            {
                var mv = morph.Vertices[k];
                vidx[k] = (int)mv.VertexIndex;
                pdelta[k] = new Vector3(mv.Coord.X, mv.Coord.Y, mv.Coord.Z);
                ndelta[k] = new Vector3(mv.Normal.X, mv.Normal.Y, mv.Normal.Z);
            }
            targets.Add(new AvatarMorphTarget(paramId, morphName, vidx, pdelta, ndelta));
        }
        return targets;
    }

    private static bool TryFindMorph(LindenMesh.Morph[] morphs, string name, out LindenMesh.Morph found)
    {
        foreach (var mo in morphs)
        {
            if (mo.Name == name) { found = mo; return true; }
        }
        // Viewer fallback: a "<name>_Driven" param targets the morph named "<name>".
        int pos = name.IndexOf("_Driven", StringComparison.Ordinal);
        if (pos > 0)
        {
            string baseName = name.Substring(0, pos);
            foreach (var mo in morphs)
            {
                if (mo.Name == baseName) { found = mo; return true; }
            }
        }
        found = default;
        return false;
    }
}
