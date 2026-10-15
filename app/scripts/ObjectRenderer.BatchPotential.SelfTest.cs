using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class ObjectRenderer
{
    /// <summary>
    /// FEAT-PERF-20 Phase 0: one [BatchPotential] pass over a scene small enough to count by hand,
    /// through the real sampler -- the node's materials, the frustum, the shadow flag, the selection
    /// set -- so what the line reports in a crowded region is the same arithmetic.
    ///
    /// <para>Six prims share one two-surface mesh, every face the same texture record, all static,
    /// each surface with its own material object as the renderer builds them. P1-P3 plain (P3's
    /// materials carry a different tint than its record says -- a uniform the key cannot see, which
    /// the key check must catch), P4 selected, P5's second surface alpha-blended, P6 off to the side
    /// out of view and in the next 32 m cell but the same 64 m one.</para>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestBatchPotential(World world)
    {
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        bool savedFlag = RenderConfig.BatchPotential;
        int savedEvery = _bpKeyCheckEvery;
        var made = new List<Entity>();
        var camera = new Camera3D();
        var meshKey = Guid.NewGuid();
        string? line = null;
        try
        {
            RenderConfig.BatchPotential = true;
            _bpKeyCheckEvery = 1;

            // Two surfaces of one triangle each.
            var mesh = new ArrayMesh();
            for (int s = 0; s < 2; s++)
            {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(0, 0, 0), new(0, 1, 0), new(1, 0, 0) };
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
            _meshFaceIndices[meshKey] = new[] { 0, 1 };

            // Godot coordinates chosen inside one 32 m cell ([1024,1056) x [32,64) x [-1056,-1024));
            // P6 at x=1086 is the next 32 m cell but the same 64 m one.
            var b = new Vector3(1040, 48, -1040);
            AddChild(camera);
            camera.LookAtFromPosition(b + new Vector3(0, 0, 20), b, Vector3.Up);

            var face = new FaceTexture(new Guid("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Empty, Guid.Empty,
                System.Numerics.Vector4.One, 1, 1, 0, 0, 0);

            VisualState Add(uint localId, Vector3 at, Color tint, bool blendSecond)
            {
                var e = world.GetOrCreateEntity(SelfTestRegion, localId);
                e.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity));
                var prim = new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: Guid.NewGuid())
                {
                    Faces = new[] { face, face },
                };
                e.SetComponent(prim);
                world.NotifyComponentUpdated(e, prim);
                Settle();
                made.Add(e);

                var state = _visuals[e.Id];
                state.ResourcesReleased = false;
                state.Shown = true;
                state.MeshInstance.Visible = true;
                state.Pos = at;
                state.CachedBoundingRadius = 1f;
                state.MeshInstance.Mesh = mesh;
                state.LoadedMeshKey = meshKey;
                for (int s = 0; s < 2; s++)
                {
                    var m = new ShaderMaterial
                    {
                        Shader = blendSecond && s == 1 ? PrimShaderFamily.Blend : PrimShaderFamily.Opaque,
                    };
                    m.SetShaderParameter(PrimShaderFamily.AlbedoColor, tint);
                    state.MeshInstance.SetSurfaceOverrideMaterial(s, m);
                }
                return state;
            }

            Add(9101, b, Colors.White, false);
            Add(9102, b + new Vector3(1, 0, 0), Colors.White, false);
            Add(9103, b + new Vector3(0, 1, 0), Colors.Red, false);
            var p4 = Add(9104, b + new Vector3(0, 0, 1), Colors.White, false);
            _instanceSuppressed.Add(p4.EntityId);
            Add(9105, b + new Vector3(1, 1, 0), Colors.White, true);
            Add(9106, new Vector3(1086, 48, -1030), Colors.White, false);

            // The key check only uses shaders whose uniform list is already held (no RenderingServer
            // round trip from a diagnostic); in the client instancing fetches it, here it is primed.
            MaterialFingerprint.Of(new ShaderMaterial { Shader = PrimShaderFamily.Opaque });

            // An empty budget still moves -- one visit per frame -- and nothing is reported before
            // the pass is whole.
            _bpPassStartedAt = double.NegativeInfinity;
            BatchPotentialLastLine = null;
            TickBatchPotential(b, camera, sliceMs: 0);
            Expect(_bpRunning && _bpCursor == 1, $"an empty budget visited {_bpCursor} (expected 1, pass still running)");
            Expect(BatchPotentialLastLine == null, "a line was printed before the pass finished");
            for (int frame = 0; frame < 5; frame++) TickBatchPotential(b, camera, sliceMs: 0);
            Expect(!_bpRunning, "the pass did not finish in six frames of an empty budget");

            line = BatchPotentialLastLine;
            Expect(line != null && line.StartsWith("[BatchPotential]", StringComparison.Ordinal), "no [BatchPotential] line");

            if (BatchPotentialLastReport is { } r)
            {
                // Main pass (P1-P5 in view): 10 surfaces, each its own material today; shared, the
                // plain opaque surfaces of one mesh collapse to one draw per surface index (2), P4
                // keeps its two, the blended one stays one -> 5; per cell, the 7 static opaque
                // surfaces (both surfaces have the same key) are one mesh -> 1 + P4's 2 + 1 blend.
                Expect(r.Main.Surfaces == 10, $"main surfaces {r.Main.Surfaces} (expected 10 -- is P6 out of view and P1-P5 in?)");
                Expect(r.Main.Now == 10, $"main now {r.Main.Now} (expected 10)");
                Expect(r.Main.Shared == 5, $"main shared {r.Main.Shared} (expected 5)");
                Expect(r.Main.Cell32 == 4 && r.Main.Both32 == 4, $"main cell32/both32 {r.Main.Cell32}/{r.Main.Both32} (expected 4/4)");
                // Two shareable materials: the opaque face and the blended one (sharing does not
                // batch a sorted surface, but it still saves the material).
                Expect(r.Keys == 2 && r.Private == 2 && r.PrivateSelected == 2, $"keys={r.Keys} private={r.Private} sel={r.PrivateSelected} (expected 2, 2, 2)");
                Expect(r.Blend == 1 && r.StaticEligible == 7, $"blend={r.Blend} static={r.StaticEligible} (expected 1, 7)");

                // Shadow pass: every caster, on screen or not, minus the blended surface -> 11; P6
                // is its own 32 m cell but shares the 64 m one.
                Expect(r.Shadow.Surfaces == 11, $"shadow surfaces {r.Shadow.Surfaces} (expected 11)");
                Expect(r.Shadow.Shared == 4, $"shadow shared {r.Shadow.Shared} (expected 4)");
                Expect(r.Shadow.Cell32 == 4 && r.Shadow.Cell64 == 3, $"shadow cell32/cell64 {r.Shadow.Cell32}/{r.Shadow.Cell64} (expected 4/3)");
            }
            else
            {
                failures.Add("no report");
            }

            // Every repeat of the opaque key is compared with P1's first surface: P1's second, P2,
            // P3, P5's first and P6 (out of view, still sampled) -- P4 is private. P3's tint is a
            // uniform its face record does not carry, so its two must disagree and name it.
            Expect(_bpKeyChecks == 8 && _bpKeyMismatches == 2,
                   $"key check {_bpKeyChecks - _bpKeyMismatches}/{_bpKeyChecks} equal (expected 6/8)");
            Expect(_bpKeyMismatch != null && _bpKeyMismatch.Contains("albedo_color", StringComparison.Ordinal),
                   $"mismatch named '{_bpKeyMismatch}' (expected albedo_color)");
        }
        catch (Exception ex)
        {
            failures.Add($"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.BatchPotential = savedFlag;
            _bpKeyCheckEvery = savedEvery;
            foreach (var e in made)
            {
                _instanceSuppressed.Remove(e.Id);
                if (_visuals.TryGetValue(e.Id, out var state))
                {
                    state.LoadedMeshKey = Guid.Empty;
                    RemoveVisual(e.Id.ToString());
                }
            }
            _meshFaceIndices.Remove(meshKey);
            camera.QueueFree();
            Settle();
        }

        return failures.Count == 0
            ? (true, line ?? "")
            : (false, string.Join("; ", failures));
    }

    /// <summary>
    /// FEAT-PERF-20 Phase 0: what a pass costs in steady state. 1,500 six-surface prims (9,000
    /// surfaces, 48 distinct materials, every face its own material object as in the client) are
    /// walked once to warm up (JIT, the per-shader uniform list the key check reads once), then once
    /// at the real per-frame budget. Reports the cost per visual and the slices the pass took; fails
    /// only if a slice ran grossly over its budget, since a timing on a shared machine is a
    /// measurement, not a contract.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestBatchPotentialCost(World world)
    {
        const int Prims = 1500, Surfaces = 6, Tints = 8;
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        bool savedFlag = RenderConfig.BatchPotential;
        var made = new List<Entity>();
        var camera = new Camera3D();
        var meshKey = Guid.NewGuid();
        try
        {
            RenderConfig.BatchPotential = true;

            var mesh = new ArrayMesh();
            for (int s = 0; s < Surfaces; s++)
            {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(0, 0, 0), new(0, 1, 0), new(1, 0, 0) };
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
            _meshFaceIndices[meshKey] = new[] { 0, 1, 2, 3, 4, 5 };

            var centre = new Vector3(1100, 30, -1100);
            AddChild(camera);
            camera.LookAtFromPosition(centre + new Vector3(0, 2, 0), centre + new Vector3(0, 2, -10), Vector3.Up);

            var rng = new Random(20);
            for (int i = 0; i < Prims; i++)
            {
                var e = world.GetOrCreateEntity(SelfTestRegion, 20000 + (uint)i);
                e.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity));
                int tint = i % Tints;
                var faceSet = new FaceTexture[Surfaces];
                for (int f = 0; f < Surfaces; f++)
                {
                    faceSet[f] = new FaceTexture(new Guid(f + 1, 0, 0, new byte[8]), Guid.Empty, Guid.Empty,
                        new System.Numerics.Vector4(1f, 1f - tint * 0.1f, 1f, 1f), 1, 1, 0, 0, 0);
                }
                var prim = new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: Guid.NewGuid())
                {
                    Faces = faceSet,
                };
                e.SetComponent(prim);
                world.NotifyComponentUpdated(e, prim);
                made.Add(e);
            }
            Settle();

            foreach (var e in made)
            {
                var state = _visuals[e.Id];
                var faceSet = e.GetComponent<PrimitiveComponent>()!.Faces!;
                state.ResourcesReleased = false;
                state.Shown = true;
                state.Pos = centre + new Vector3(rng.Next(-100, 100), rng.Next(0, 20), rng.Next(-100, 100));
                state.CachedBoundingRadius = 1f;
                state.MeshInstance.Mesh = mesh;
                state.LoadedMeshKey = meshKey;
                for (int s = 0; s < Surfaces; s++)
                {
                    var m = new ShaderMaterial { Shader = PrimShaderFamily.Opaque };
                    var c = faceSet[s].Color;
                    m.SetShaderParameter(PrimShaderFamily.AlbedoColor, new Color(c.X, c.Y, c.Z, c.W));
                    state.MeshInstance.SetSurfaceOverrideMaterial(s, m);
                }
            }

            // Warm-up pass, unbudgeted; the fingerprint's uniform list primed as instancing would.
            MaterialFingerprint.Of(new ShaderMaterial { Shader = PrimShaderFamily.Opaque });
            _bpPassStartedAt = double.NegativeInfinity;
            TickBatchPotential(centre, camera, sliceMs: 1e6);

            // The measured pass, at the client's budget.
            _bpPassStartedAt = double.NegativeInfinity;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int frames = 0;
            do
            {
                TickBatchPotential(centre, camera);
                frames++;
            }
            while (_bpRunning && frames < 100000);
            double wallMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            double perVisualUs = _bpCostMs * 1000.0 / Prims;
            var r = BatchPotentialLastReport;
            string detail = $"{Prims} visuals x {Surfaces} surfaces: {perVisualUs:F1} us per visual " +
                            $"({perVisualUs / Surfaces:F2} us per surface), pass {_bpCostMs:F1} ms in {_bpSlices} slices " +
                            $"of {BatchPotentialSliceMs} ms budget (avg {_bpCostMs / Math.Max(_bpSlices, 1):F3}, worst {_bpWorstSliceMs:F3}), " +
                            $"{_bpKeyChecks} key checks {_bpKeyChecks - _bpKeyMismatches} equal; " +
                            $"main now={r?.Main.Now} shared={r?.Main.Shared} both32={r?.Main.Both32}; wall {wallMs:F0} ms";

            // Gross overrun only: one visit past the budget is by design.
            bool ok = !_bpRunning && _bpWorstSliceMs < 5.0 && _bpKeyMismatches == 0
                      && r is { } rep && rep.Main.Shared <= Surfaces * Tints;
            return (ok, detail);
        }
        catch (Exception ex)
        {
            return (false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.BatchPotential = savedFlag;
            foreach (var e in made)
            {
                if (_visuals.TryGetValue(e.Id, out var state))
                {
                    state.LoadedMeshKey = Guid.Empty;
                    RemoveVisual(e.Id.ToString());
                }
            }
            _meshFaceIndices.Remove(meshKey);
            camera.QueueFree();
            Settle();
        }
    }
}
