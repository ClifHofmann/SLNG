using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using Kind = SLNG.Core.BatchPotentialTally.SurfaceKind;
using Reasons = SLNG.Core.BatchPotentialTally.PrivateReasons;

namespace SLNG.App;

/// <summary>FEAT-PERF-20 Phase 0: the <c>[BatchPotential]</c> line -- how many draws the prims on
/// screen would cost if identical materials were shared (option A), if static prims were merged
/// per 32/64 m cell (option B), or both (C). Counts only; nothing that is drawn changes. The
/// arithmetic is <see cref="BatchPotentialTally"/>; this file only gathers one sample per drawn
/// surface. Spec: docs/specs/FEAT-PERF-20-static-batching.md.
///
/// <para><b>Cost.</b> Its own walk over a snapshot of the visuals, separate from the cull sweep, at
/// most <see cref="BatchPotentialSliceMs"/> of main thread per frame plus one visit (the clock is
/// read before every visit), one pass per <see cref="BatchPotentialIntervalSeconds"/>; it idles in between. Every
/// read is scene-side -- the node's mesh, override materials, shader, next pass, cast-shadow flag --
/// none goes to the RenderingServer, so nothing waits on the render thread (BUG-PERF-15). Off unless
/// <see cref="RenderConfig.BatchPotential"/>. The time it spends is filed as <c>diag.batch</c> in
/// [WorkCost] and as the <c>batch-potential</c> phase in [PhaseCost], and the line reports its own
/// average and worst slice.</para></summary>
public partial class ObjectRenderer
{
    /// <summary>Main-thread budget per frame while a pass runs.</summary>
    internal const double BatchPotentialSliceMs = 0.4;

    /// <summary>One pass, and one line, per this many seconds at most.</summary>
    private const double BatchPotentialIntervalSeconds = 10.0;

    /// <summary>An object counts as static once it has not moved or turned for this long. The
    /// reference viewer makes a moved drawable ACTIVE (its own model matrix, out of the static
    /// batches -- llvovolume.cpp registerFace, lldrawable.cpp makeActive); this is the measurable
    /// stand-in for that state.</summary>
    private const double BatchStaticAfterSeconds = 30.0;

    /// <summary>How many equal-key pairs a pass compares by their real uniform values, and how
    /// often one is picked. The check reads every uniform back (<see cref="MaterialFingerprint"/>),
    /// which is the expensive thing the key exists to avoid, so it is a sample.</summary>
    private const int BatchKeyChecksPerPass = 48;
    private int _bpKeyCheckEvery = 8;

    private readonly BatchPotentialTally _bpTally = new();
    private readonly List<Guid> _bpOrder = new();
    private int _bpCursor;
    private bool _bpRunning;
    private double _bpPassStartedAt = double.NegativeInfinity;

    // The view the pass is measured against, taken once at its start: stand still for a pass.
    private Plane[] _bpFrustum = Array.Empty<Plane>();
    private Godot.Vector3 _bpCamPos;
    private float _bpLayerScale;

    private int _bpObjects, _bpInstanced, _bpAnimesh, _bpUnloaded, _bpOther;
    private double _bpCostMs, _bpWorstSliceMs;
    private int _bpSlices;

    private readonly Dictionary<Shader, ulong> _bpShaderIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PrimMaterialKey, (ShaderMaterial Material, string? Fingerprint)> _bpKeyFirst = new();
    private int _bpKeyRepeats, _bpKeyChecks, _bpKeyMismatches;
    private string? _bpKeyMismatch;

    /// <summary>The line the last finished pass printed, for the selftest.</summary>
    internal string? BatchPotentialLastLine { get; private set; }

    /// <summary>The last finished pass's numbers, for the selftest.</summary>
    internal BatchPotentialTally.Report? BatchPotentialLastReport { get; private set; }

    /// <summary>Called once per frame from <see cref="_Process"/> once the viewpoint is known.</summary>
    private void TickBatchPotential(Godot.Vector3 viewPos, Camera3D? camera, double sliceMs = BatchPotentialSliceMs)
    {
        if (!RenderConfig.BatchPotential) return;

        double now = NowSeconds();
        if (!_bpRunning)
        {
            if (now - _bpPassStartedAt < BatchPotentialIntervalSeconds) return;
            BeginBatchPotentialPass(viewPos, camera, now);
        }

        using var _phase = MainThreadPhase.Enter("batch-potential");
        long start = Stopwatch.GetTimestamp();
        long budget = (long)(sliceMs * Stopwatch.Frequency / 1000.0);
        int visited = 0;
        while (_bpCursor < _bpOrder.Count)
        {
            // Every visit: a visit is microseconds and the clock tens of nanoseconds, and the one
            // visit in a few hundred that runs a key check costs far more than the rest.
            if (visited > 0 && Stopwatch.GetTimestamp() - start > budget) break;
            var id = _bpOrder[_bpCursor++];
            visited++;
            if (_visuals.TryGetValue(id, out var state)) SampleBatchPotential(id, state, now);
        }

        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        _bpCostMs += ms;
        _bpSlices++;
        if (ms > _bpWorstSliceMs) _bpWorstSliceMs = ms;
        MainThreadWorkQueue.RecordExternal("diag.batch", ms);

        if (_bpCursor >= _bpOrder.Count) FinishBatchPotentialPass(now);
    }

    private void BeginBatchPotentialPass(Godot.Vector3 viewPos, Camera3D? camera, double now)
    {
        _bpRunning = true;
        _bpPassStartedAt = now;
        _bpOrder.Clear();
        _bpOrder.AddRange(_visuals.Keys);
        _bpCursor = 0;
        _bpTally.Clear();
        _bpObjects = _bpInstanced = _bpAnimesh = _bpUnloaded = _bpOther = 0;
        _bpCostMs = _bpWorstSliceMs = 0;
        _bpSlices = 0;
        _bpKeyFirst.Clear();
        _bpKeyRepeats = _bpKeyChecks = _bpKeyMismatches = 0;
        _bpKeyMismatch = null;

        // No camera (headless) = everything in view, measured from the viewpoint, Godot's default
        // depth range. Godot's depth layer is distance * 16 / (far - near), clamped to 0..15
        // (render_forward_clustered.cpp, _fill_render_list).
        if (camera != null && IsInstanceValid(camera))
        {
            var planes = camera.GetFrustum();
            _bpFrustum = new Plane[planes.Count];
            for (int i = 0; i < planes.Count; i++) _bpFrustum[i] = planes[i];
            _bpCamPos = camera.GlobalPosition;
            _bpLayerScale = 16f / Mathf.Max(camera.Far - camera.Near, 1f);
        }
        else
        {
            _bpFrustum = Array.Empty<Plane>();
            _bpCamPos = viewPos;
            _bpLayerScale = 16f / 4000f;
        }
    }

    private void FinishBatchPotentialPass(double now)
    {
        _bpRunning = false;
        _bpKeyFirst.Clear();   // drop the material references the key check held

        var r = _bpTally.Compute();
        BatchPotentialLastReport = r;

        static string Pass(in BatchPotentialTally.PassEstimate p) =>
            $"surf={p.Surfaces} now={p.Now} shared={p.Shared} cell32={p.Cell32} cell64={p.Cell64} " +
            $"both32={p.Both32} both64={p.Both64}";

        string keyCheck = _bpKeyChecks == 0
            ? "keyCheck=none"
            : $"keyCheck={_bpKeyChecks - _bpKeyMismatches}/{_bpKeyChecks} equal";
        double avg = _bpSlices > 0 ? _bpCostMs / _bpSlices : 0;

        string line =
            $"[BatchPotential] objs={_bpObjects} (instanced={_bpInstanced} animesh={_bpAnimesh} " +
            $"unloaded={_bpUnloaded} other={_bpOther}) | main: {Pass(r.Main)} | " +
            $"keys={r.Keys} private={r.Private} (anim={r.PrivateAnim} sel={r.PrivateSelected} " +
            $"mirror={r.PrivateMirror} media={r.PrivateMedia}) blend={r.Blend} depthPass={r.DepthPasses} " +
            $"hidden={r.Hidden} static={r.StaticEligible} | shadow(1 view): {Pass(r.Shadow)} | {keyCheck} | " +
            $"pass={now - _bpPassStartedAt:F1}s slices={_bpSlices} avgMs={avg:F3} worstMs={_bpWorstSliceMs:F3}";
        BatchPotentialLastLine = line;
        SLNG.App.UI.StatsOverlay.EmitPerfLine(line);

        // The first disagreement names the uniform the key missed -- the one thing a reader of the
        // line would need to fix the key.
        if (_bpKeyMismatch != null)
            SLNG.App.UI.StatsOverlay.EmitPerfLine($"[BatchPotential] key mismatch: {_bpKeyMismatch}");
    }

    /// <summary>Adds one visual's drawn surfaces to the tally.</summary>
    private void SampleBatchPotential(Guid id, VisualState state, double now)
    {
        if (!state.Shown || state.ResourcesReleased || !IsInstanceValid(state.MeshInstance)) return;
        _bpObjects++;

        // Drawn by a MultiMesh group (FEAT-PERF-06): already one draw per group, see [Instancing].
        if (_instanceGroups != null && _instanceGroups.IsInstanced(id)) { _bpInstanced++; return; }
        // Drawn by the control avatar's skeleton, not as a prim (FEAT-ANIMESH-01).
        if (state.ControlAvatarOwned) { _bpAnimesh++; return; }

        var mi = state.MeshInstance;
        if (mi.Mesh is not { } mesh) { _bpUnloaded++; return; }
        var prim = _world?.GetEntity(id)?.GetComponent<PrimitiveComponent>();
        if (prim == null) { _bpOther++; return; }

        int surfaces = mesh.GetSurfaceCount();
        if (surfaces == 0) { _bpUnloaded++; return; }

        // Bounding sphere against the pass's frustum. Godot culls the instance AABB; the sphere
        // around it is the cheap superset, and the radius is the managed copy where there is one.
        float radius = float.IsNaN(state.CachedBoundingRadius) ? EffectiveBoundingRadius(state) : state.CachedBoundingRadius;
        bool inView = true;
        foreach (var plane in _bpFrustum)
        {
            if (plane.DistanceTo(state.Pos) > radius) { inView = false; break; }
        }
        int layer = Math.Clamp((int)(state.Pos.DistanceTo(_bpCamPos) * _bpLayerScale), 0, 15);

        bool casts = !state.ShadowForcedOffByDistance
                     && mi.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off;
        bool isStatic = !prim.IsPhysical && now - state.LastMovedAt > BatchStaticAfterSeconds;

        int animFace = int.MinValue;   // no running texture animation
        if (_texAnims.TryGetValue(id, out var anim)) animFace = anim.Anim.Face;   // -1 = every face
        bool selected = _instanceSuppressed.Contains(id);
        bool foliage = PrimPCode.IsFoliage(prim.Shape.PCode);

        // The renderer's own default face (ApplyFaceMaterialsAsync): what a surface without its own
        // record, or a whole-node material, is built from.
        var defaultFace = new FaceTexture(prim.TextureId, prim.RenderMaterialId, prim.LegacyMaterialId, prim.ColorTint,
            prim.RepeatU, prim.RepeatV, prim.OffsetU, prim.OffsetV, prim.Rotation, prim.TexGen, prim.Fullbright);
        _meshFaceIndices.TryGetValue(state.LoadedMeshKey, out var faceIndices);
        var wholeNode = mi.MaterialOverride;

        for (int i = 0; i < surfaces; i++)
        {
            int faceIdx = faceIndices != null && i < faceIndices.Length ? faceIndices[i] : -1;
            var face = foliage ? default : FaceSurfaceMerge.Resolve(prim.Faces, defaultFace, faceIdx);

            // BUG-RENDER-16: a surface moved onto its own child for sorting leaves a Hidden
            // placeholder on the parent (a draw in both passes, nothing rasterised) and is drawn
            // by the child, which casts no shadow.
            MeshInstance3D? child = null;
            if (state.SplitChildren != null)
            {
                foreach (var (s, node) in state.SplitChildren)
                    if (s == i && IsInstanceValid(node)) { child = node; break; }
            }

            if (child != null)
            {
                AddBatchSample(state, i, face, prim, mi.GetSurfaceOverrideMaterial(i), Kind.Hidden, Reasons.None,
                               inView, casts, isStatic, layer, checkKey: false);
                var reasons = BatchPrivateReasons(state, i, faceIdx, face, prim, animFace, selected);
                AddBatchSample(state, i, face, prim, child.GetSurfaceOverrideMaterial(0), null, reasons,
                               inView, casts: false, isStatic, layer, checkKey: true);
                continue;
            }

            var material = wholeNode ?? mi.GetSurfaceOverrideMaterial(i);
            var why = BatchPrivateReasons(state, i, faceIdx, face, prim, animFace, selected);
            AddBatchSample(state, i, face, prim, material, null, why, inView, casts, isStatic, layer, checkKey: true);
        }
    }

    private Reasons BatchPrivateReasons(VisualState state, int surface, int faceIdx, in FaceTexture face,
                                        PrimitiveComponent prim, int animFace, bool selected)
    {
        var why = Reasons.None;
        if (animFace != int.MinValue && (animFace < 0 || animFace == faceIdx)) why |= Reasons.TextureAnimation;
        if (selected) why |= Reasons.Selected;
        if (state.MirrorSurfaces != null)
        {
            foreach (var (s, _) in state.MirrorSurfaces)
                if (s == surface) { why |= Reasons.Mirror; break; }
        }
        // A MOAP face: its albedo is replaced by the fetched image (MVP3-3), per object.
        if (face.HasMedia && prim.MediaFaces is { } media && faceIdx >= 0 && faceIdx < media.Length
            && media[faceIdx] != null)
            why |= Reasons.Media;
        return why;
    }

    private void AddBatchSample(VisualState state, int surface, in FaceTexture face, PrimitiveComponent prim,
                                Material? material, Kind? forced, Reasons why, bool inView, bool casts,
                                bool isStatic, int layer, bool checkKey)
    {
        var shaderMaterial = material as ShaderMaterial;
        var shader = shaderMaterial?.Shader;
        var kind = forced ?? BatchKindOf(shader);

        // The hero mirror's swapped shader is private however it got there.
        if (ReferenceEquals(shader, PrimShaderFamily.Mirror)) why |= Reasons.Mirror;

        bool depthPass = kind == Kind.Blend && shaderMaterial?.NextPass != null;
        ulong variant = BatchShaderId(shader) | (depthPass ? 1UL << 32 : 0UL);

        // No material yet: Godot draws the surface with its one default material, already shared.
        ulong instance = material != null ? material.GetInstanceId() : 0UL;
        var key = PrimMaterialKey.Create(face, prim.Scale, variant);

        _bpTally.Add(new BatchPotentialTally.Sample(
            state.LoadedMeshKey, surface, key, instance, kind, why, depthPass, inView, casts, isStatic, layer,
            new System.Numerics.Vector3(state.Pos.X, state.Pos.Y, state.Pos.Z)));

        if (checkKey && why == Reasons.None && shaderMaterial != null && !depthPass)
            CheckBatchKey(key, shaderMaterial);
    }

    /// <summary>The key claims that equal inputs give equal uniforms. A sample of equal-key pairs
    /// is compared by the real values (every uniform read back, FEAT-PERF-06's fingerprint), so the
    /// line says whether the claim holds in the scene it measured. Only for shaders whose uniform
    /// list the fingerprint already holds: reading it the first time is a RenderingServer round trip
    /// (BUG-PERF-15). Instancing fetches the opaque one as soon as a prim joins a group.</summary>
    private void CheckBatchKey(PrimMaterialKey key, ShaderMaterial material)
    {
        if (material.Shader is not { } shader || !MaterialFingerprint.IsReadyFor(shader)) return;
        if (!_bpKeyFirst.TryGetValue(key, out var first))
        {
            _bpKeyFirst[key] = (material, null);
            return;
        }
        if (ReferenceEquals(first.Material, material) || !IsInstanceValid(first.Material)) return;
        if (_bpKeyChecks >= BatchKeyChecksPerPass || (++_bpKeyRepeats % _bpKeyCheckEvery) != 0) return;

        string a = first.Fingerprint ?? MaterialFingerprint.Of(first.Material);
        if (first.Fingerprint == null) _bpKeyFirst[key] = (first.Material, a);
        string b = MaterialFingerprint.Of(material);
        _bpKeyChecks++;
        if (a == b) return;

        _bpKeyMismatches++;
        _bpKeyMismatch ??= FirstDifference(a, b);
    }

    private static string FirstDifference(string a, string b)
    {
        var pa = a.Split('|');
        var pb = b.Split('|');
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
            if (pa[i] != pb[i]) return $"{pa[i]} vs {pb[i]}";
        return $"{pa.Length} vs {pb.Length} uniforms";
    }

    private ulong BatchShaderId(Shader? shader)
    {
        if (shader == null) return 0;
        if (!_bpShaderIds.TryGetValue(shader, out var id))
        {
            id = (ulong)_bpShaderIds.Count + 1;
            _bpShaderIds[shader] = id;
        }
        return id;
    }

    /// <summary>The Godot list a surface lands in for its shader: alpha-blended kinds are sorted by
    /// depth and cast nothing; alpha-tested kinds stay in the opaque list (BUG-RENDER-17 is why they
    /// are told apart); everything else -- opaque, double-sided opaque, the mirror -- is opaque.</summary>
    private static Kind BatchKindOf(Shader? shader)
    {
        if (shader == null) return Kind.Opaque;
        if (ReferenceEquals(shader, PrimShaderFamily.Hidden)) return Kind.Hidden;
        var b = PrimShaderFamily.BaseVariant(shader);
        if (IsSortedTransparent(b)
            || ReferenceEquals(b, PrimShaderFamily.Select(PrimShaderFamily.Kind.Blend, PrimShaderFamily.Surface.WorldPrim, doubleSided: true)))
            return Kind.Blend;
        return HasAlphaDiscard(b) ? Kind.Masked : Kind.Opaque;
    }
}
