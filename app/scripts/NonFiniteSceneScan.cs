using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace SLNG.App;

/// <summary>
/// A one-off, read-only walk of the scene tree that looks for what makes the engine itself print
/// "Vector3 cannot be normalized" (BUG-RENDER-40): a node whose world transform is NaN or infinite,
/// or whose basis is singular (a zero scale on any axis -- inverting it gives NaN, and the engine
/// inverts transforms for culling, light and particle sorting every frame), and skeleton bones with
/// a non-finite or zero-scale pose.
///
/// <para>Triggered by <see cref="EngineWarningTap"/> when a frame draws a burst of engine warnings;
/// never runs on its own. Rate-limited (once per <see cref="MinIntervalMs"/>), capped (nodes
/// visited, findings printed) and strictly reading: it does not mutate the tree and swallows its own
/// exceptions, because a diagnostic must not be the thing that breaks the frame.</para>
///
/// <para>Output is one <c>[NonFiniteScan]</c> summary (counts by node class, which is the fastest
/// answer to "what kind of thing is it") plus a line per ROOT CAUSE: a bad node whose parent is fine.
/// Everything below a singular node is singular too, so listing every descendant would be noise.</para>
/// </summary>
internal static class NonFiniteSceneScan
{
    /// <summary>Minimum gap between two scans.</summary>
    internal const long MinIntervalMs = 30_000;

    private const int MaxNodes = 100_000;
    private const int MaxFindings = 20;

    /// <summary>|determinant| below this counts as singular. A sane SL scene never gets near it:
    /// the smallest prim scale the renderer allows is 0.001, so a unit-ish basis has |det| ~ 1e-9
    /// at the very worst.</summary>
    private const float SingularDeterminant = 1e-12f;

    private static long _lastRunMs = long.MinValue;

    /// <summary>What the last scan found: non-finite transforms, singular transforms, bad bones.
    /// Exists so the self test can assert on the scan without parsing the log.</summary>
    internal static (int NonFinite, int Singular, int BadBones) LastResult { get; private set; }

    /// <summary>Runs the scan if the rate limit allows (<paramref name="ignoreRateLimit"/> is for the
    /// self test only). Main thread only. Returns whether it ran.</summary>
    public static bool TryRun(SceneTree? tree, int warningsInBurstFrame, bool ignoreRateLimit = false)
    {
        if (tree?.Root == null) return false;

        long now = (long)Time.GetTicksMsec();
        if (!ignoreRateLimit && _lastRunMs != long.MinValue && now - _lastRunMs < MinIntervalMs) return false;
        _lastRunMs = now;

        try
        {
            Run(tree, warningsInBurstFrame);
        }
        catch (Exception ex)
        {
            GD.Print($"[NonFiniteScan] scan failed: {ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    private static void Run(SceneTree tree, int warningsInBurstFrame)
    {
        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        var roots = new List<string>();
        var rootsSingular = new List<string>();
        var boneFindings = new List<string>();
        var classCounts = new Dictionary<string, int>();
        int visited = 0, node3d = 0, nonFinite = 0, singular = 0, badBones = 0;
        bool capped = false;

        // Iterative DFS: the tree is deep under an avatar, and a recursion limit is not something a
        // diagnostic should be able to hit. Each entry carries whether its parent was already bad.
        var stack = new Stack<(Node Node, bool ParentBad)>();
        stack.Push((tree.Root, false));

        while (stack.Count > 0)
        {
            var (node, parentBad) = stack.Pop();
            if (++visited > MaxNodes) { capped = true; break; }

            bool bad = false;
            if (node is Node3D n3 && n3.IsInsideTree())
            {
                node3d++;
                string? problem = Classify(n3.GlobalTransform);
                if (problem != null)
                {
                    bad = true;
                    string cls = node.GetClass();
                    classCounts[cls] = classCounts.GetValueOrDefault(cls) + 1;
                    bool isNonFinite = problem.StartsWith("non-finite", StringComparison.Ordinal);
                    if (isNonFinite) nonFinite++; else singular++;

                    // Only the topmost bad node is a root cause; its descendants inherit the problem.
                    var bucket = isNonFinite ? roots : rootsSingular;
                    if (!parentBad && bucket.Count < MaxFindings)
                        bucket.Add(Describe(n3, problem));
                }

                if (node is Skeleton3D skeleton) badBones += ScanSkeleton(skeleton, boneFindings);
            }

            int children = node.GetChildCount();
            for (int i = 0; i < children; i++)
                stack.Push((node.GetChild(i), bad));
        }

        LastResult = (nonFinite, singular, badBones);
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks) * 1000.0
                           / System.Diagnostics.Stopwatch.Frequency;

        var summary = new StringBuilder();
        summary.Append($"[NonFiniteScan] after a burst of {warningsInBurstFrame} engine warnings in one frame: ")
               .Append($"visited {visited} nodes ({node3d} Node3D){(capped ? " [capped]" : "")} in {elapsedMs:0.0} ms; ")
               .Append($"non-finite transforms {nonFinite}, singular (zero-scale) transforms {singular}, bad bones {badBones}");
        if (classCounts.Count > 0)
        {
            summary.Append("; by class: ");
            bool first = true;
            foreach (var (cls, count) in SortedByCount(classCounts))
            {
                if (!first) summary.Append(", ");
                summary.Append(cls).Append(" x").Append(count);
                first = false;
            }
        }
        GD.Print(summary.ToString());

        int printed = 0;
        foreach (var list in new[] { roots, rootsSingular, boneFindings })
        {
            foreach (var line in list)
            {
                if (printed >= MaxFindings) break;
                GD.Print($"[NonFiniteScan]   {line}");
                printed++;
            }
        }
        if (nonFinite + singular + badBones == 0)
            GD.Print("[NonFiniteScan]   nothing non-finite or singular in the scene tree -- the vector is not a node transform or bone pose");
    }

    /// <summary>Null when the transform is fine, otherwise what is wrong with it.</summary>
    private static string? Classify(Transform3D t)
    {
        if (!t.Origin.IsFinite() || !t.Basis.X.IsFinite() || !t.Basis.Y.IsFinite() || !t.Basis.Z.IsFinite())
            return "non-finite transform";
        float det = t.Basis.Determinant();
        if (!float.IsFinite(det)) return "non-finite transform (determinant overflows)";
        if (MathF.Abs(det) < SingularDeterminant) return $"singular basis (det {det:0.###e+0})";
        return null;
    }

    private static string Describe(Node3D node, string problem)
    {
        var text = new StringBuilder();
        text.Append(problem).Append(": ").Append(node.GetClass()).Append(' ').Append(node.GetPath());

        string? entity = FindEntityId(node);
        if (entity != null) text.Append(" entity=").Append(entity);

        Transform3D local = node.Transform;
        text.Append($" | local scale ({local.Basis.Scale.X:0.####}, {local.Basis.Scale.Y:0.####}, {local.Basis.Scale.Z:0.####})");
        text.Append($" origin {local.Origin}");
        text.Append($" | visible={node.IsVisibleInTree()}");
        if (node.GetParent() is Node3D parent)
        {
            Transform3D pg = parent.GlobalTransform;
            text.Append($" | parent {parent.GetClass()} '{parent.Name}' scale ({pg.Basis.Scale.X:0.####}, {pg.Basis.Scale.Y:0.####}, {pg.Basis.Scale.Z:0.####})");
        }
        if (node is CpuParticles3D particles)
            text.Append($" | particles emitting={particles.Emitting} amount={particles.Amount} localCoords={particles.LocalCoords}");
        return text.ToString();
    }

    /// <summary>Our own entity id when the node or an ancestor names one (<c>Obj_&lt;id&gt;</c>,
    /// <c>WornItem_&lt;id&gt;</c>, ...), else null.</summary>
    private static string? FindEntityId(Node node)
    {
        if (node is ObjectParticles op && op.EmitterEntityId != Guid.Empty)
            return op.EmitterEntityId.ToString("N");

        Node? cursor = node;
        for (int hops = 0; cursor != null && hops < 8; hops++, cursor = cursor.GetParent())
        {
            string name = cursor.Name;
            int underscore = name.IndexOf('_');
            if (underscore < 0 || name.Length < underscore + 33) continue;
            if (Guid.TryParseExact(name.AsSpan(underscore + 1, 32), "N", out var id)) return id.ToString("N");
        }
        return null;
    }

    /// <summary>Counts bones with a non-finite or zero-scale pose or rest, adding a line per skeleton
    /// (the first few bones named) to <paramref name="findings"/>.</summary>
    private static int ScanSkeleton(Skeleton3D skeleton, List<string> findings)
    {
        int bad = 0;
        List<string>? names = null;
        int count = skeleton.GetBoneCount();
        for (int i = 0; i < count; i++)
        {
            Transform3D pose = skeleton.GetBonePose(i);
            Transform3D rest = skeleton.GetBoneRest(i);
            if (!IsUsable(pose) || !IsUsable(rest))
            {
                bad++;
                if (names == null || names.Count < 4) (names ??= new List<string>()).Add(skeleton.GetBoneName(i));
            }
        }
        if (bad > 0 && findings.Count < MaxFindings)
        {
            string? entity = FindEntityId(skeleton);
            findings.Add($"bad bone pose/rest: Skeleton3D {skeleton.GetPath()}{(entity != null ? " entity=" + entity : "")} " +
                         $"has {bad} of {count} bone(s) non-finite or zero-scale, e.g. {string.Join(", ", names!)}");
        }
        return bad;

        static bool IsUsable(Transform3D t) =>
            t.Origin.IsFinite() && t.Basis.X.IsFinite() && t.Basis.Y.IsFinite() && t.Basis.Z.IsFinite()
            && t.Basis.X.LengthSquared() > 1e-12f && t.Basis.Y.LengthSquared() > 1e-12f
            && t.Basis.Z.LengthSquared() > 1e-12f;
    }

    private static List<KeyValuePair<string, int>> SortedByCount(Dictionary<string, int> counts)
    {
        var list = new List<KeyValuePair<string, int>>(counts);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        return list;
    }
}
