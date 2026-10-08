using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// Keeps non-finite numbers out of the engine (BUG-RENDER-40). Godot's own mesh, skinning and culling
/// code normalises vectors it is handed and, when one is NaN or infinite, prints
/// "Vector3 cannot be normalized, the elements must be finite" with no idea which object it was --
/// so every place that hands vertex data to the engine goes through here first, and a bad mesh is
/// repaired and named ONCE (<c>[MeshGuard]</c>) instead of being a flood of anonymous engine lines.
///
/// <para>Two entry points. <see cref="Sanitize(Godot.Collections.Array, Func{string})"/> repairs a
/// finished surface-array set before <c>AddSurfaceFromArrays</c>. <see cref="VertexGuard"/> repairs
/// the values one vertex at a time on their way INTO a <see cref="SurfaceTool"/>, whose finished
/// arrays are not cheap to read back. Both are linear scans, safe on any thread, and a clean mesh
/// costs one pass and no allocation beyond the array copies Godot's accessors make.</para>
///
/// <para>The replacements are the neutral ones: position 0, normal up, tangent (1,0,0,+1), UV 0,
/// skin weights renormalised (or the first influence at 1 when nothing finite is left). A repaired
/// vertex is wrong, but it is wrong where the engine can still draw it, which beats a NaN that
/// poisons every later computation on the mesh.</para>
/// </summary>
internal static class MeshArrayGuard
{
    /// <summary>Distinct labels remembered for deduplication. Past it a new bad mesh is still
    /// repaired and counted, only no longer named -- the log must not become the flood.</summary>
    private const int MaxLoggedLabels = 512;

    private static readonly ConcurrentDictionary<string, byte> Logged = new();
    private static long _badMeshes;

    /// <summary>How many meshes (or vertex builds) have needed repair since the client started.</summary>
    public static long BadMeshCount => Interlocked.Read(ref _badMeshes);

    // ---------------------------------------------------------------- surface-array entry point

    /// <summary>Repairs <paramref name="arrays"/> in place. Returns whether anything was bad.</summary>
    public static bool Sanitize(Godot.Collections.Array arrays, string label) => Sanitize(arrays, () => label);

    /// <summary><paramref name="label"/> is only evaluated when something is bad, so building it can
    /// be as descriptive as it likes without costing a clean mesh anything.</summary>
    public static bool Sanitize(Godot.Collections.Array arrays, Func<string> label) =>
        SanitizeCore(arrays, label, log: true);

    /// <param name="log">False only for the self test, which feeds it deliberately bad data and
    /// must not put a warning in the log.</param>
    internal static bool SanitizeCore(Godot.Collections.Array arrays, Func<string> label, bool log)
    {
        List<Finding>? found = null;
        int vertexCount = 0;

        if (arrays.Count > (int)Mesh.ArrayType.Vertex
            && arrays[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.PackedVector3Array)
        {
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            vertexCount = vertices.Length;
            int bad = FixVector3(vertices, Vector3.Zero, out int first);
            if (bad > 0)
            {
                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
                (found ??= new List<Finding>()).Add(new Finding("vertex", bad, first));
            }
        }

        if (arrays.Count > (int)Mesh.ArrayType.Normal
            && arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.PackedVector3Array)
        {
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            int bad = FixVector3(normals, Vector3.Up, out int first);
            if (bad > 0)
            {
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                (found ??= new List<Finding>()).Add(new Finding("normal", bad, first));
            }
        }

        if (arrays.Count > (int)Mesh.ArrayType.Tangent
            && arrays[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.PackedFloat32Array)
        {
            var tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
            int bad = FixTangents(tangents, out int first);
            if (bad > 0)
            {
                arrays[(int)Mesh.ArrayType.Tangent] = tangents;
                (found ??= new List<Finding>()).Add(new Finding("tangent", bad, first));
            }
        }

        foreach (var (slot, name) in new[] { (Mesh.ArrayType.TexUV, "uv"), (Mesh.ArrayType.TexUV2, "uv2") })
        {
            if (arrays.Count <= (int)slot || arrays[(int)slot].VariantType != Variant.Type.PackedVector2Array) continue;
            var uvs = arrays[(int)slot].AsVector2Array();
            int bad = FixVector2(uvs, out int first);
            if (bad > 0)
            {
                arrays[(int)slot] = uvs;
                (found ??= new List<Finding>()).Add(new Finding(name, bad, first));
            }
        }

        if (arrays.Count > (int)Mesh.ArrayType.Weights
            && arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.PackedFloat32Array)
        {
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            // Godot stores 4 or 8 influences per vertex; read the stride off the vertex count when it
            // divides evenly and fall back to 4 otherwise.
            int stride = vertexCount > 0 && weights.Length % vertexCount == 0 ? weights.Length / vertexCount : 4;
            int bad = FixWeights(weights, stride, out int first);
            if (bad > 0)
            {
                arrays[(int)Mesh.ArrayType.Weights] = weights;
                (found ??= new List<Finding>()).Add(new Finding("skin weight", bad, first));
            }
        }

        if (found == null) return false;
        Interlocked.Increment(ref _badMeshes);
        if (log) LogOnce(label, found);
        return true;
    }

    // ---------------------------------------------------------------- pure scans (self-tested)

    /// <summary>Replaces every non-finite element with <paramref name="fallback"/>. Returns how many
    /// were replaced and the index of the first.</summary>
    internal static int FixVector3(Vector3[] values, Vector3 fallback, out int firstBad)
    {
        int bad = 0;
        firstBad = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].IsFinite()) continue;
            if (bad++ == 0) firstBad = i;
            values[i] = fallback;
        }
        return bad;
    }

    internal static int FixVector2(Vector2[] values, out int firstBad)
    {
        int bad = 0;
        firstBad = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].IsFinite()) continue;
            if (bad++ == 0) firstBad = i;
            values[i] = Vector2.Zero;
        }
        return bad;
    }

    /// <summary>Tangents are four floats per vertex (xyz plus handedness). A vertex with ANY bad
    /// component becomes (1,0,0,+1). Returns the number of vertices repaired.</summary>
    internal static int FixTangents(float[] tangents, out int firstBad)
    {
        int bad = 0;
        firstBad = -1;
        for (int i = 0; i + 3 < tangents.Length; i += 4)
        {
            if (float.IsFinite(tangents[i]) && float.IsFinite(tangents[i + 1])
                && float.IsFinite(tangents[i + 2]) && float.IsFinite(tangents[i + 3])) continue;
            if (bad++ == 0) firstBad = i / 4;
            tangents[i] = 1f; tangents[i + 1] = 0f; tangents[i + 2] = 0f; tangents[i + 3] = 1f;
        }
        return bad;
    }

    /// <summary>Skin weights, <paramref name="stride"/> per vertex. A vertex with a non-finite weight
    /// loses that weight and the rest are renormalised to sum to one; with nothing usable left the
    /// first influence takes all of it (an orphaned vertex pinned to its first bone, the same
    /// fallback the mesh builders use). Vertices whose weights were all finite are left untouched.
    /// Returns the number of vertices repaired.</summary>
    internal static int FixWeights(float[] weights, int stride, out int firstBad)
    {
        int bad = 0;
        firstBad = -1;
        if (stride <= 0) return 0;
        for (int v = 0; (v + 1) * stride <= weights.Length; v++)
        {
            int start = v * stride;
            bool vertexBad = false;
            for (int k = 0; k < stride; k++)
                if (!float.IsFinite(weights[start + k])) { vertexBad = true; break; }
            if (!vertexBad) continue;

            if (bad++ == 0) firstBad = v;
            float sum = 0f;
            for (int k = 0; k < stride; k++)
            {
                if (!float.IsFinite(weights[start + k]) || weights[start + k] < 0f) weights[start + k] = 0f;
                sum += weights[start + k];
            }
            if (sum > 1e-6f)
            {
                for (int k = 0; k < stride; k++) weights[start + k] /= sum;
            }
            else
            {
                for (int k = 0; k < stride; k++) weights[start + k] = 0f;
                weights[start] = 1f;
            }
        }
        return bad;
    }

    // ---------------------------------------------------------------- SurfaceTool entry point

    /// <summary>Repairs vertex values one at a time on their way into a <see cref="SurfaceTool"/>.
    /// One instance per mesh build; call <see cref="Report"/> when the build is done.</summary>
    internal sealed class VertexGuard
    {
        private int _badPosition, _badNormal, _badUv;
        private int _firstPosition = -1, _firstNormal = -1, _firstUv = -1;

        public bool AnyBad => _badPosition + _badNormal + _badUv > 0;

        public Vector3 Position(Vector3 value, int index)
        {
            if (value.IsFinite()) return value;
            if (_badPosition++ == 0) _firstPosition = index;
            return Vector3.Zero;
        }

        public Vector3 Normal(Vector3 value, int index)
        {
            if (value.IsFinite()) return value;
            if (_badNormal++ == 0) _firstNormal = index;
            return Vector3.Up;
        }

        public Vector2 Uv(Vector2 value, int index)
        {
            if (value.IsFinite()) return value;
            if (_badUv++ == 0) _firstUv = index;
            return Vector2.Zero;
        }

        /// <summary>Names the mesh once if anything was repaired. Returns whether it was.</summary>
        public bool Report(Func<string> label)
        {
            if (!AnyBad) return false;
            var found = new List<Finding>(3);
            if (_badPosition > 0) found.Add(new Finding("vertex", _badPosition, _firstPosition));
            if (_badNormal > 0) found.Add(new Finding("normal", _badNormal, _firstNormal));
            if (_badUv > 0) found.Add(new Finding("uv", _badUv, _firstUv));
            Interlocked.Increment(ref _badMeshes);
            LogOnce(label, found);
            return true;
        }
    }

    /// <summary>Names a mesh once when a builder that ran off the engine -- SLNG.Assets'
    /// RiggedMeshBuilder, BUG-PERF-06 -- had to repair values. The counts are the ones
    /// <see cref="VertexGuard"/> would have kept. Returns whether anything was repaired.</summary>
    public static bool ReportRepairs(SLNG.Assets.RiggedMeshGeometry geo, Func<string> label)
    {
        if (geo.BadPositions + geo.BadNormals + geo.BadUvs == 0) return false;
        var found = new List<Finding>(3);
        if (geo.BadPositions > 0) found.Add(new Finding("vertex", geo.BadPositions, geo.FirstBadPosition));
        if (geo.BadNormals > 0) found.Add(new Finding("normal", geo.BadNormals, geo.FirstBadNormal));
        if (geo.BadUvs > 0) found.Add(new Finding("uv", geo.BadUvs, geo.FirstBadUv));
        Interlocked.Increment(ref _badMeshes);
        LogOnce(label, found);
        return true;
    }

    // ---------------------------------------------------------------- logging

    private readonly record struct Finding(string Kind, int Count, int FirstIndex);

    private static void LogOnce(Func<string> label, List<Finding> found)
    {
        string name;
        try { name = label(); }
        catch { name = "(label unavailable)"; }

        if (Logged.Count >= MaxLoggedLabels && !Logged.ContainsKey(name)) return;
        if (!Logged.TryAdd(name, 0)) return;

        var text = new StringBuilder();
        foreach (var f in found)
        {
            if (text.Length > 0) text.Append(", ");
            text.Append(f.Count).Append("x ").Append(f.Kind).Append(" (first #").Append(f.FirstIndex).Append(')');
        }
        // GD.PushWarning, like the [NaNGuard] bone-rest repair: it reaches the log and the editor.
        GD.PushWarning($"[MeshGuard] non-finite mesh data in {name}: {text} -- replaced with safe values");
    }
}
