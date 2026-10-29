using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace SLNG.App;

/// <summary>
/// FEAT-PERF-24 P0-c: the synthetic prim bench, nodes against RenderingServer / PhysicsServer RIDs.
/// Opt-in: <c>godot --headless --path app --log-file user://logs/selftest/primbench.log -- --selftest --prim-bench</c>.
/// With the flag, <see cref="Run"/> runs this INSTEAD of the normal checks (the bench needs hundreds of
/// real frames, and the normal pass must stay fast), prints <c>[PrimBench]</c> lines and quits.
///
/// <para>Asset-free: no login, no network. 30,000 prims on a grid over 256 x 256 m share one 6-surface
/// box <see cref="ArrayMesh"/> and one <see cref="ConcavePolygonShape3D"/>; every prim has its own 6
/// <see cref="ShaderMaterial"/>s (the prim shader, as BuildFaceMaterialAsync makes them). Backend A
/// builds exactly what <c>ObjectRenderer.CreateVisual</c> does (MeshInstance3D > StaticBody3D >
/// CollisionShape3D); backend B builds a RenderingServer instance plus a PhysicsServer body.</para>
///
/// <para>Timing per frame: <c>scripts</c> is the scenario's own per-frame work (it runs from the
/// <c>process_frame</c> signal, before any node's <c>_process</c>); <c>flush</c> is from there to a
/// zero-length SceneTreeTimer (deferred calls and transform notifications have run); <c>tail</c> is from
/// the timer to <c>frame_pre_draw</c> (delete queue). The same cut points as <see cref="FrameTimeline"/>.</para>
/// </summary>
public static partial class SelfTest
{
    private const string PrimBenchFlag = "--prim-bench";

    /// <summary>True when <c>--prim-bench</c> was passed (after the <c>--selftest</c> separator or not).</summary>
    public static bool PrimBenchRequested =>
        System.Array.IndexOf(OS.GetCmdlineArgs(), PrimBenchFlag) >= 0
        || System.Array.IndexOf(OS.GetCmdlineUserArgs(), PrimBenchFlag) >= 0;

    private const int PbPrims = 30000;
    private const int PbSurfaces = 6;
    private const int PbFrames = 600;
    private const double PbBudgetMs = 2.0;
    private const int PbRays = 1000;

    private static async void RunPrimBench(SceneTree tree)
    {
        int exit = 1;
        try
        {
            exit = await PrimBenchMain(tree) ? 0 : 1;
        }
        catch (Exception ex)
        {
            GD.Print($"[PrimBench] FAIL exception: {ex}");
        }
        tree.Quit(exit);
    }

    // ---------------------------------------------------------------------------------------------
    // Data shared by both backends
    // ---------------------------------------------------------------------------------------------

    private sealed class PbData
    {
        public Vector3[] Pos = new Vector3[PbPrims];
        public Vector3[] Scl = new Vector3[PbPrims];
        public ShaderMaterial[] Mats = new ShaderMaterial[PbPrims * PbSurfaces];
        public ArrayMesh Mesh = null!;
        public ArrayMesh MeshAlt = null!;
        public ConcavePolygonShape3D Shape = null!;
        public Rid Scenario;
        public Rid Space;
        public Node3D Root = null!;
    }

    /// <summary>A 6-surface box: one quad per surface, so SetSurfaceOverrideMaterial has 6 slots to fill.</summary>
    private static ArrayMesh PbBuildBox(float size, out Vector3[] triangles)
    {
        (Vector3 N, Vector3 U, Vector3 V)[] faces =
        {
            (Vector3.Right, Vector3.Up, Vector3.Back),
            (Vector3.Left, Vector3.Back, Vector3.Up),
            (Vector3.Up, Vector3.Back, Vector3.Right),
            (Vector3.Down, Vector3.Right, Vector3.Back),
            (Vector3.Back, Vector3.Right, Vector3.Up),
            (Vector3.Forward, Vector3.Up, Vector3.Right),
        };
        var mesh = new ArrayMesh();
        var tris = new List<Vector3>();
        float h = size * 0.5f;
        foreach (var (n, u, v) in faces)
        {
            var c = new[]
            {
                n * h - u * h - v * h, n * h + u * h - v * h,
                n * h + u * h + v * h, n * h - u * h + v * h,
            };
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = c;
            arrays[(int)Mesh.ArrayType.Normal] = new[] { n, n, n, n };
            arrays[(int)Mesh.ArrayType.TexUV] = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };
            // Clockwise seen from outside: Godot's front face.
            arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            tris.AddRange(new[] { c[0], c[2], c[1], c[0], c[3], c[2] });
        }
        triangles = tris.ToArray();
        return mesh;
    }

    // ---------------------------------------------------------------------------------------------
    // Backends
    // ---------------------------------------------------------------------------------------------

    private abstract class PbBackend
    {
        protected readonly PbData D;
        protected PbBackend(PbData d) => D = d;
        public abstract string Name { get; }
        public abstract void Create(int i);
        public abstract void Remove(int i);
        public abstract void Move(int i, Vector3 position);
        public abstract void SetVisible(int i, bool visible);
        public abstract void SwapMesh(int i, bool alt);
        public abstract Rid BodyRid(int i);

        protected Transform3D Xf(int i, Vector3 pos) => new(Basis.FromScale(D.Scl[i]), pos);
    }

    /// <summary>Backend A: exactly CreateVisual's three nodes (ObjectRenderer.cs, "new VisualState").</summary>
    private sealed class PbNodes : PbBackend
    {
        private readonly MeshInstance3D?[] _mi = new MeshInstance3D?[PbPrims];
        private readonly StaticBody3D?[] _sb = new StaticBody3D?[PbPrims];

        public PbNodes(PbData d) : base(d) { }
        public override string Name => "A";

        public override void Create(int i)
        {
            var id = Guid.NewGuid();
            var mi = new MeshInstance3D { Name = "Obj_" + id.ToString("N"), Visible = false };
            var sb = new StaticBody3D { Name = "StaticBody" };
            var cs = new CollisionShape3D { Name = "Collision" };
            sb.SetMeta("EntityId", id.ToString());
            sb.SetMeta("LocalId", i.ToString());
            sb.AddChild(cs);
            mi.AddChild(sb);
            D.Root.AddChild(mi);
            // What UpdateVisual / AssignSharedMesh / BuildFaceMaterialAsync fill in afterwards.
            mi.Mesh = D.Mesh;
            for (int s = 0; s < PbSurfaces; s++) mi.SetSurfaceOverrideMaterial(s, D.Mats[i * PbSurfaces + s]);
            cs.Shape = D.Shape;
            mi.Scale = D.Scl[i];
            mi.Position = D.Pos[i];
            mi.Visible = true;
            _mi[i] = mi;
            _sb[i] = sb;
        }

        public override void Remove(int i)
        {
            _mi[i]!.QueueFree();   // RemoveVisual: state.MeshInstance.QueueFree()
            _mi[i] = null;
            _sb[i] = null;
        }

        public override void Move(int i, Vector3 position) => _mi[i]!.Position = position;
        public override void SetVisible(int i, bool visible) => _mi[i]!.Visible = visible;

        public override void SwapMesh(int i, bool alt)
        {
            var mi = _mi[i]!;
            mi.Mesh = alt ? D.MeshAlt : D.Mesh;
            for (int s = 0; s < PbSurfaces; s++) mi.SetSurfaceOverrideMaterial(s, D.Mats[i * PbSurfaces + s]);
        }

        public override Rid BodyRid(int i) => _sb[i]!.GetRid();
    }

    /// <summary>Backend B: a RenderingServer instance with 6 overrides plus a PhysicsServer static body.</summary>
    private sealed class PbRids : PbBackend
    {
        private readonly Rid[] _inst = new Rid[PbPrims];
        private readonly Rid[] _body = new Rid[PbPrims];

        public PbRids(PbData d) : base(d) { }
        public override string Name => "B";

        public override void Create(int i)
        {
            var xf = Xf(i, D.Pos[i]);
            var inst = RenderingServer.InstanceCreate2(D.Mesh.GetRid(), D.Scenario);
            for (int s = 0; s < PbSurfaces; s++)
                RenderingServer.InstanceSetSurfaceOverrideMaterial(inst, s, D.Mats[i * PbSurfaces + s].GetRid());
            RenderingServer.InstanceSetTransform(inst, xf);

            var body = PhysicsServer3D.BodyCreate();
            PhysicsServer3D.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
            PhysicsServer3D.BodyAddShape(body, D.Shape.GetRid(), Transform3D.Identity);
            PhysicsServer3D.BodySetSpace(body, D.Space);
            PhysicsServer3D.BodySetState(body, PhysicsServer3D.BodyState.Transform, xf);
            _inst[i] = inst;
            _body[i] = body;
        }

        public override void Remove(int i)
        {
            PhysicsServer3D.FreeRid(_body[i]);
            RenderingServer.FreeRid(_inst[i]);
            _body[i] = default;
            _inst[i] = default;
        }

        public override void Move(int i, Vector3 position)
        {
            var xf = Xf(i, position);
            RenderingServer.InstanceSetTransform(_inst[i], xf);
            PhysicsServer3D.BodySetState(_body[i], PhysicsServer3D.BodyState.Transform, xf);
        }

        public override void SetVisible(int i, bool visible) => RenderingServer.InstanceSetVisible(_inst[i], visible);

        public override void SwapMesh(int i, bool alt)
        {
            RenderingServer.InstanceSetBase(_inst[i], (alt ? D.MeshAlt : D.Mesh).GetRid());
            // RS clears the overrides on a base change (renderer_scene_cull.cpp), so every swap re-sends them.
            for (int s = 0; s < PbSurfaces; s++)
                RenderingServer.InstanceSetSurfaceOverrideMaterial(_inst[i], s, D.Mats[i * PbSurfaces + s].GetRid());
        }

        public override Rid BodyRid(int i) => _body[i];
    }

    // ---------------------------------------------------------------------------------------------
    // Frame sampler
    // ---------------------------------------------------------------------------------------------

    private sealed class PbRun
    {
        public readonly List<double> Scripts = new(), Flush = new(), Tail = new(), Frame = new();
        public double TotalMs;
        public int Frames;
        public double GcSeconds;
        public int Gen0, Gen1;
        public double StaticMb;
        public long Nodes;
    }

    private static double PbMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static long _pbScriptsEnd, _pbFlushMark, _pbLastPreDraw;
    private static bool _pbSampling;
    private static PbRun? _pbCurrent;

    private static bool _pbPreDrawSeen;

    /// <summary>frame_pre_draw. Not emitted by the dummy renderer (headless), in which case frames are
    /// closed by the next process_frame instead and tail/frame include the next physics step.</summary>
    private static void PbOnPreDraw()
    {
        _pbPreDrawSeen = true;
        PbCloseFrame(Stopwatch.GetTimestamp());
    }

    private static void PbCloseFrame(long now)
    {
        var run = _pbCurrent;
        if (run != null && _pbSampling)
        {
            if (_pbLastPreDraw != 0) run.Frame.Add(PbMs(now - _pbLastPreDraw));
            if (_pbScriptsEnd != 0 && _pbFlushMark >= _pbScriptsEnd && _pbFlushMark <= now)
            {
                run.Flush.Add(PbMs(_pbFlushMark - _pbScriptsEnd));
                run.Tail.Add(PbMs(now - _pbFlushMark));
            }
        }
        _pbLastPreDraw = now;
        _pbScriptsEnd = 0;
        _pbFlushMark = 0;
    }

    /// <summary>Runs <paramref name="step"/> once per frame, from the process_frame signal, until it returns
    /// true. The frame the step returns true in is still measured.</summary>
    private static async Task<PbRun> PbRunFrames(SceneTree tree, Func<int, bool> step)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var run = new PbRun();
        _pbCurrent = run;
        _pbSampling = true;
        _pbScriptsEnd = _pbFlushMark = 0;
        _pbLastPreDraw = 0;
        var onFlushed = Callable.From(() => _pbFlushMark = Stopwatch.GetTimestamp());

        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1);
        long startTs = 0;
        bool done = false;
        for (int f = 0; !done; f++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            long t0 = Stopwatch.GetTimestamp();
            if (!_pbPreDrawSeen) PbCloseFrame(t0);
            if (f == 0) startTs = t0;
            done = step(f);
            tree.CreateTimer(0.0, processAlways: true).Connect(SceneTreeTimer.SignalName.Timeout, onFlushed);
            long t1 = Stopwatch.GetTimestamp();
            run.Scripts.Add(PbMs(t1 - t0));
            _pbScriptsEnd = t1;
            run.Frames++;
            run.TotalMs = PbMs(t1 - startTs);
        }
        // Let the last frame's flush/tail be recorded by its pre_draw.
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (!_pbPreDrawSeen) PbCloseFrame(Stopwatch.GetTimestamp());
        _pbSampling = false;

        run.GcSeconds = Math.Max(1e-3, run.TotalMs / 1000.0);
        run.Gen0 = GC.CollectionCount(0) - gen0;
        run.Gen1 = GC.CollectionCount(1) - gen1;
        run.StaticMb = OS.GetStaticMemoryUsage() / 1048576.0;
        run.Nodes = (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        return run;
    }

    // ---------------------------------------------------------------------------------------------
    // The bench
    // ---------------------------------------------------------------------------------------------

    private static async Task<bool> PrimBenchMain(SceneTree tree)
    {
        var lines = new List<(int Order, string Line)>();
        var d = new PbData();
        var world = tree.Root.FindWorld3D();
        d.Scenario = world.Scenario;
        d.Space = world.Space;

        GD.Print($"[PrimBench] start: display={DisplayServer.GetName()} separateRenderThread={RenderThread.IsSeparate} " +
                 $"physics={ProjectSettings.GetSetting("physics/3d/physics_engine", "DEFAULT")} " +
                 $"prims={PbPrims} frames={PbFrames} budget={PbBudgetMs}ms");

        d.Mesh = PbBuildBox(1f, out var tris);
        d.MeshAlt = PbBuildBox(0.9f, out _);
        d.Shape = new ConcavePolygonShape3D { Data = tris, BackfaceCollision = true };
        d.Root = new Node3D { Name = "PrimBenchRoot" };
        tree.Root.AddChild(d.Root);

        // Grid over 256 x 256 m, deterministic non-uniform scale (so the ray test covers scaled prims).
        var rng = new Random(24);
        int nx = (int)Math.Ceiling(Math.Sqrt(PbPrims));
        float step = 256f / nx;
        for (int i = 0; i < PbPrims; i++)
        {
            d.Pos[i] = new Vector3((i % nx) * step, 1f + (float)rng.NextDouble(), (i / nx) * step);
            d.Scl[i] = new Vector3(0.4f + (float)rng.NextDouble() * 0.8f, 0.4f + (float)rng.NextDouble() * 0.8f,
                0.4f + (float)rng.NextDouble() * 0.8f);
        }

        long ts = Stopwatch.GetTimestamp();
        var shader = PrimShaderFamily.Opaque;
        for (int i = 0; i < d.Mats.Length; i++) d.Mats[i] = new ShaderMaterial { Shader = shader };
        GD.Print($"[PrimBench] setup: {d.Mats.Length} ShaderMaterials built in {PbMs(Stopwatch.GetTimestamp() - ts):F0} ms " +
                 $"(not part of any scenario); baseline nodes={(long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} " +
                 $"static={OS.GetStaticMemoryUsage() / 1048576.0:F0}MB");

        RenderingServer.FramePreDraw += PbOnPreDraw;
        var rays = PbMakeRays(d);
        var rayResults = new Dictionary<string, PbRayHit[]>();

        // Warm up a few frames so the first measured frame is not boot work.
        await PbRunFrames(tree, f => f >= 10);

        foreach (var backend in new PbBackend[] { new PbNodes(d), new PbRids(d) })
        {
            string b = backend.Name;

            // ---- S4: create 30k under a 2 ms/frame budget -----------------------------------------
            int created = 0;
            var s4 = await PbRunFrames(tree, _ =>
            {
                long start = Stopwatch.GetTimestamp();
                do { backend.Create(created++); }
                while (created < PbPrims && PbMs(Stopwatch.GetTimestamp() - start) < PbBudgetMs);
                return created >= PbPrims;
            });
            lines.Add((4, PbFormatTotal("S4", b, "create 30k @2ms", s4)));

            // Two physics frames so the bodies are in the broadphase before the rays.
            await tree.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            rayResults[b] = PbCastRays(world, backend, rays);

            // ---- S0: idle -------------------------------------------------------------------------
            lines.Add((0, PbFormatSteady("S0", b, "idle", await PbRunFrames(tree, f => f >= PbFrames - 1))));

            // ---- S1: move 300 prims per frame -----------------------------------------------------
            var s1 = await PbRunFrames(tree, f =>
            {
                int first = f * 300 % PbPrims;
                float dy = (f & 1) == 0 ? 0.25f : 0f;
                for (int k = 0; k < 300; k++)
                {
                    int i = (first + k) % PbPrims;
                    backend.Move(i, d.Pos[i] + new Vector3(0, dy, 0));
                }
                return f >= PbFrames - 1;
            });
            lines.Add((1, PbFormatSteady("S1", b, "move 300/frame", s1)));

            // ---- S2: toggle Visible on 2000 per frame ---------------------------------------------
            var vis = new bool[PbPrims];
            System.Array.Fill(vis, true);
            var s2 = await PbRunFrames(tree, f =>
            {
                int first = f * 2000 % PbPrims;
                for (int k = 0; k < 2000; k++)
                {
                    int i = (first + k) % PbPrims;
                    vis[i] = !vis[i];
                    backend.SetVisible(i, vis[i]);
                }
                return f >= PbFrames - 1;
            });
            for (int i = 0; i < PbPrims; i++) if (!vis[i]) backend.SetVisible(i, true);
            lines.Add((2, PbFormatSteady("S2", b, "toggle Visible 2000/frame", s2)));

            // ---- S3: swap mesh + re-send 6 overrides on 200 per frame -----------------------------
            var alt = new bool[PbPrims];
            var s3 = await PbRunFrames(tree, f =>
            {
                int first = f * 200 % PbPrims;
                for (int k = 0; k < 200; k++)
                {
                    int i = (first + k) % PbPrims;
                    alt[i] = !alt[i];
                    backend.SwapMesh(i, alt[i]);
                }
                return f >= PbFrames - 1;
            });
            lines.Add((3, PbFormatSteady("S3", b, "swap mesh+6 overrides 200/frame", s3)));

            // ---- S5: remove 30k under the same budget, delete queue drained -----------------------
            int removed = 0;
            long nodesBefore = (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
            long expectAfter = nodesBefore - (backend is PbNodes ? PbPrims * 3L : 0);
            var s5 = await PbRunFrames(tree, _ =>
            {
                if (removed < PbPrims)
                {
                    long start = Stopwatch.GetTimestamp();
                    do { backend.Remove(removed++); }
                    while (removed < PbPrims && PbMs(Stopwatch.GetTimestamp() - start) < PbBudgetMs);
                    if (removed < PbPrims) return false;
                }
                // Drained = the delete queue has freed every node (nodes only; RIDs go at once).
                return d.Root.GetChildCount() == 0;
            });
            lines.Add((5, PbFormatTotal("S5", b, "remove 30k @2ms +delete queue", s5)
                + $" nodesAfter={s5.Nodes} (expected~{expectAfter})"));
        }

        RenderingServer.FramePreDraw -= PbOnPreDraw;

        // ---- Ray parity ---------------------------------------------------------------------------
        var ra = rayResults["A"];
        var rb = rayResults["B"];
        int hitsA = ra.Count(h => h.Hit), hitsB = rb.Count(h => h.Hit), mismatches = 0;
        string firstMismatch = "";
        for (int k = 0; k < ra.Length; k++)
        {
            var x = ra[k];
            var y = rb[k];
            bool same = x.Hit == y.Hit
                && (!x.Hit || (x.Prim == y.Prim && x.Pos.DistanceTo(y.Pos) < 1e-3f && x.Normal.DistanceTo(y.Normal) < 1e-3f));
            if (!same)
            {
                mismatches++;
                if (firstMismatch.Length == 0)
                    firstMismatch = $" first: ray {k} A=({x.Hit},{x.Prim},{x.Pos}) B=({y.Hit},{y.Prim},{y.Pos})";
            }
        }
        bool parity = mismatches == 0 && hitsA >= PbRays * 9 / 10;
        string parityLine = $"[PrimBench] ray parity: rays={ra.Length} hitsA={hitsA} hitsB={hitsB} mismatches={mismatches}" +
                            $"{firstMismatch} -> {(parity ? "PASS" : "FAIL")}";

        // ---- Table --------------------------------------------------------------------------------
        GD.Print("[PrimBench] ---- results (ms; mean/p99 per frame; A = nodes, B = RIDs) ----");
        GD.Print(_pbPreDrawSeen
            ? "[PrimBench] frames closed by frame_pre_draw: scripts/flush/tail as in FrameTimeline"
            : "[PrimBench] frame_pre_draw not emitted (dummy renderer): frames closed by the next process_frame, " +
              "so tail and frame include the delete queue AND the next physics step; flush is exact");
        foreach (var (_, line) in lines.OrderBy(l => l.Order).ThenBy(l => l.Line, StringComparer.Ordinal))
            GD.Print(line);
        GD.Print(parityLine);
        GD.Print($"[PrimBench] {(parity ? "PASS" : "FAIL")}");

        d.Root.QueueFree();
        return parity;
    }

    // ---------------------------------------------------------------------------------------------
    // Rays
    // ---------------------------------------------------------------------------------------------

    private readonly record struct PbRay(Vector3 From, Vector3 To);
    private readonly record struct PbRayHit(bool Hit, int Prim, Vector3 Pos, Vector3 Normal);

    /// <summary>1,000 rays aimed at scaled prims: half straight down onto a jittered point of the prim, half
    /// slanted in from the side so they cross neighbours first.</summary>
    private static PbRay[] PbMakeRays(PbData d)
    {
        var rng = new Random(77);
        var rays = new PbRay[PbRays];
        for (int k = 0; k < PbRays; k++)
        {
            int i = rng.Next(PbPrims);
            var c = d.Pos[i];
            var s = d.Scl[i];
            var aim = c + new Vector3(
                ((float)rng.NextDouble() - 0.5f) * s.X * 0.8f, 0f, ((float)rng.NextDouble() - 0.5f) * s.Z * 0.8f);
            rays[k] = (k & 1) == 0
                ? new PbRay(aim + new Vector3(0, 10, 0), aim - new Vector3(0, 10, 0))
                : new PbRay(aim + new Vector3(-6, 4, -3), aim);
        }
        return rays;
    }

    private static PbRayHit[] PbCastRays(World3D world, PbBackend backend, PbRay[] rays)
    {
        // Rid -> prim index for the prims whose bodies exist.
        var byRid = new Dictionary<Rid, int>(PbPrims);
        for (int i = 0; i < PbPrims; i++) byRid[backend.BodyRid(i)] = i;

        var space = world.DirectSpaceState;
        var results = new PbRayHit[rays.Length];
        for (int k = 0; k < rays.Length; k++)
        {
            var q = PhysicsRayQueryParameters3D.Create(rays[k].From, rays[k].To);
            var r = space.IntersectRay(q);
            if (r.Count == 0) { results[k] = new PbRayHit(false, -1, default, default); continue; }
            int prim = byRid.TryGetValue(r["rid"].AsRid(), out var p) ? p : -2;
            results[k] = new PbRayHit(true, prim, r["position"].AsVector3(), r["normal"].AsVector3());
        }
        return results;
    }

    // ---------------------------------------------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------------------------------------------

    private static double PbMean(List<double> v) => v.Count == 0 ? double.NaN : v.Average();

    private static double PbP99(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(s.Count * 0.99) - 1)];
    }

    private static string PbPair(string name, List<double> v) =>
        v.Count == 0 ? $"{name} n/a" : $"{name} {PbMean(v):F2}/{PbP99(v):F2}";

    private static string PbCommon(PbRun r) =>
        $"nodes {r.Nodes} gc0/s {r.Gen0 / r.GcSeconds:F2} gc1/s {r.Gen1 / r.GcSeconds:F2} static {r.StaticMb:F0}MB";

    private static string PbFormatSteady(string id, string backend, string what, PbRun r)
    {
        var sb = new StringBuilder();
        sb.Append($"[PrimBench] {id} {backend} {what,-33} frames {r.Frames,4} | ")
          .Append(PbPair("scripts", r.Scripts)).Append(" | ")
          .Append(PbPair("flush", r.Flush)).Append(" | ")
          .Append(PbPair("tail", r.Tail)).Append(" | ")
          .Append(PbPair("frame", r.Frame)).Append(" | ")
          .Append(PbCommon(r));
        return sb.ToString();
    }

    private static string PbFormatTotal(string id, string backend, string what, PbRun r) =>
        $"[PrimBench] {id} {backend} {what,-33} total {r.TotalMs:F0} ms in {r.Frames} frames | " +
        $"sum scripts {r.Scripts.Sum():F0} flush {r.Flush.Sum():F0} tail {r.Tail.Sum():F0} ms | " +
        $"{PbPair("scripts", r.Scripts)} | {PbPair("flush", r.Flush)} | {PbPair("tail", r.Tail)} | {PbPair("frame", r.Frame)} | " +
        PbCommon(r);
}
