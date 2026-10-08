using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;

namespace SLNG.App;

// BUG-PERF-06: a static object's mesh, prepared on a worker before its main-thread apply.
//
// mesh.apply / primmesh.apply / sculpt.apply each ended in AssignSharedMesh, which on a cache miss
// built the ArrayMesh right there (SurfaceTool per run, MeshTangents) and, for an object near the
// avatar, the trimesh faces too (collision.urgent). Both are CPU work on plain arrays. The loaders
// now do it on a worker as soon as the asset has arrived, for the surface grouping the object's
// face records call for at that moment; AssignSharedMesh still decides everything on the main
// thread, exactly as before, and only swaps its own build for the prepared arrays when they were
// made for the same mesh, flip and grouping. Anything else -- the face records changed in between,
// a re-plan, a restore -- builds the old way. So the main thread can never get a different mesh
// than it would have built itself; it just usually does not have to build it.
public partial class ObjectRenderer
{
    /// <summary>What a worker made of a decoded mesh for one surface grouping.</summary>
    private sealed class PreparedStaticMesh
    {
        public required MeshData Data { get; init; }
        public required bool FlipV { get; init; }

        /// <summary>The grouping the surfaces were built for (<see cref="PlanSurfaceMerge"/>'s RunStart).</summary>
        public required bool[] RunStart { get; init; }

        /// <summary>One array set per surface, from <see cref="BuildSurfaceArrays"/>.</summary>
        public required Godot.Collections.Array[] Surfaces { get; init; }
        public required int[] FaceIndices { get; init; }

        /// <summary><see cref="BuildTrimeshFaces"/> for the same data: what the collision shape is made of.</summary>
        public required Godot.Vector3[] TrimeshFaces { get; init; }

        public double PrepareMs { get; init; }

        public bool IsFor(MeshData data, bool flipV, bool[] runStart) =>
            ReferenceEquals(Data, data) && FlipV == flipV && RunStart.AsSpan().SequenceEqual(runStart);
    }

    /// <summary>The face records a surface-merge plan reads, taken from the world on the main thread.
    /// <paramref name="HasPrim"/> false means there were none (the plan then merges nothing).</summary>
    private readonly record struct SurfacePlanInputs(bool HasPrim, FaceTexture[]? Faces, FaceTexture DefaultFace, int AnimatedFace);

    /// <summary>Bounds concurrent preparation, as <c>GpuCache._imagePrepGate</c> bounds image work: an
    /// unbounded Task.Run per object turns a region load into thread-pool queueing (BUG-NET-11).
    /// Half the cores, like the rig workers -- the main thread still needs one of its own.</summary>
    private static readonly System.Threading.SemaphoreSlim _meshPrepGate =
        new(Math.Max(2, System.Environment.ProcessorCount / 2));

    /// <summary>Preparations in flight, so a burst of objects sharing one asset (a field of identical
    /// rocks) prepares it once. An entry lives only while its task runs.</summary>
    private readonly ConcurrentDictionary<(MeshData Data, bool FlipV, ulong Pattern), Lazy<Task<PreparedStaticMesh?>>> _preparing = new();

    /// <summary>Main thread only: reads the world.</summary>
    private SurfacePlanInputs CapturePlanInputs(VisualState state)
    {
        var prim = _world?.GetEntity(state.EntityId)?.GetComponent<PrimitiveComponent>();
        if (prim == null) return new SurfacePlanInputs(false, null, default, FaceSurfaceMerge.NoAnimatedFace);

        var defaultFace = new FaceTexture(prim.TextureId, prim.RenderMaterialId, prim.LegacyMaterialId,
            prim.ColorTint, prim.RepeatU, prim.RepeatV, prim.OffsetU, prim.OffsetV, prim.Rotation,
            prim.TexGen, prim.Fullbright);
        return new SurfacePlanInputs(true, prim.Faces, defaultFace, AnimBarrierFace(prim));
    }

    /// <summary>BUG-RENDER-16's grouping for <paramref name="data"/> under <paramref name="inputs"/>:
    /// which committable submeshes start a new surface, and how many surfaces that makes. Pure, so
    /// the main thread and the workers plan with the same code.</summary>
    private static (bool[] RunStart, int Surfaces) PlanRunStart(MeshData data, in SurfacePlanInputs inputs)
    {
        var committable = new List<int>(data.Submeshes.Count);
        foreach (var sub in data.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;
            committable.Add(sub.FaceIndex);
        }

        var runStart = new bool[committable.Count];
        if (committable.Count == 0 || !inputs.HasPrim)
        {
            // Nothing to commit, or no face records to reason about: merge nothing, which
            // reproduces the pre-merge behaviour exactly (and keeps the cache key equal to the
            // geometry key).
            for (int i = 0; i < runStart.Length; i++) runStart[i] = true;
            return (runStart, runStart.Length);
        }

        var indices = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(committable);
        int surfaces = FaceSurfaceMerge.Plan(indices, inputs.Faces, inputs.DefaultFace, inputs.AnimatedFace, runStart);
        return (runStart, surfaces);
    }

    /// <summary>Prepares a freshly arrived mesh on a worker, unless there is nothing to prepare or
    /// the GpuCache already holds what it would make. Null means the main thread does what it
    /// always did. <paramref name="geometryKey"/> is the key AssignSharedMesh will be given.</summary>
    private Task<PreparedStaticMesh?> PrepareArrivedMeshAsync(
        MeshData? data, bool flipV, SurfacePlanInputs inputs, Guid geometryKey, Guid entityId)
    {
        if (data == null || data.Submeshes.Count == 0) return Task.FromResult<PreparedStaticMesh?>(null);

        var (runStart, _) = PlanRunStart(data, inputs);
        ulong pattern = FaceSurfaceMerge.IsIdentity(runStart, runStart.Length) ? 0UL : FaceSurfaceMerge.Signature(runStart, runStart.Length);
        // A grouping that merges nothing is cached under the geometry key itself (MergedMeshKey),
        // so a shared asset that is already resident -- the common case for repeated scenery --
        // is not prepared again. A merged grouping's key lives in main-thread tables; those are
        // simply prepared.
        if (pattern == 0UL && _gpuCache?.Get(geometryKey) is ArrayMesh) return Task.FromResult<PreparedStaticMesh?>(null);

        return PrepareStaticMeshAsync(data, flipV, runStart, pattern, entityId);
    }

    /// <summary>Prepares <paramref name="data"/> on a worker for the grouping <paramref name="runStart"/>.
    /// Null when there is nothing to use: the client is quitting, or it failed (the main thread then
    /// builds it the old way, and says so if it fails again).</summary>
    private Task<PreparedStaticMesh?> PrepareStaticMeshAsync(MeshData data, bool flipV, bool[] runStart, ulong pattern, Guid entityId)
    {
        var key = (data, flipV, pattern);

        // Lazy, because GetOrAdd may run its factory twice under contention; only the Lazy that
        // won is ever started.
        var entry = _preparing.GetOrAdd(key, k => new Lazy<Task<PreparedStaticMesh?>>(() => Task.Run(async () =>
            {
                await _meshPrepGate.WaitAsync().ConfigureAwait(false);
                if (!EngineWorkerGate.TryEnter())
                {
                    _meshPrepGate.Release();
                    return null;
                }
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var surfaces = BuildSurfaceArrays(data, flipV, runStart, out var faceIndices,
                        () => $"prim entity={entityId:N} (prepared on a worker)");
                    var faces = BuildTrimeshFaces(data);
                    return new PreparedStaticMesh
                    {
                        Data = data,
                        FlipV = flipV,
                        RunStart = runStart,
                        Surfaces = surfaces,
                        FaceIndices = faceIndices,
                        TrimeshFaces = faces,
                        PrepareMs = clock.Elapsed.TotalMilliseconds,
                    };
                }
                catch (Exception ex)
                {
                    GD.PushWarning($"[ObjectRenderer] preparing a mesh for entity {entityId:N} failed: {ex.Message}");
                    return (PreparedStaticMesh?)null;
                }
                finally
                {
                    EngineWorkerGate.Exit();
                    _meshPrepGate.Release();
                }
            })));

        var task = entry.Value;
        // Only while it runs: a later request for the same asset is served by the GpuCache.
        task.ContinueWith(t => _preparing.TryRemove(new KeyValuePair<(MeshData, bool, ulong), Lazy<Task<PreparedStaticMesh?>>>(key, entry)),
            TaskScheduler.Default);
        return task;
    }

    /// <summary>The engine arrays of a static mesh, one set per surface: everything
    /// <see cref="BuildArrayMesh"/> does short of handing them to the engine. SurfaceTool is a CPU
    /// helper -- only its Commit reaches the RenderingServer, and Commit is exactly
    /// <c>AddSurfaceFromArrays(CommitToArrays())</c> (scene/resources/surface_tool.cpp, 4.7-stable)
    /// -- so this is safe on a worker.</summary>
    /// <remarks><paramref name="runStart"/> (BUG-RENDER-16) is indexed by COMMITTABLE submesh —
    /// the same submeshes this method commits, empty ones already filtered out by
    /// <see cref="PlanSurfaceMerge"/> — and marks where a new Godot surface begins. Consecutive
    /// submeshes inside one run are appended into a single <see cref="SurfaceTool"/> with their
    /// indices shifted past the vertices already in it, so the run becomes one surface whose
    /// triangles are drawn in authored index order and are never sorted against each other. With
    /// every entry true this emits exactly one surface per submesh, i.e. the pre-merge
    /// behaviour.</remarks>
    private static Godot.Collections.Array[] BuildSurfaceArrays(MeshData mesh, bool flipV, bool[] runStart,
        out int[] faceIndices, Func<string>? label = null)
    {
        var surfaces = new List<Godot.Collections.Array>();
        var indices = new List<int>(mesh.Submeshes.Count);
        // BUG-RENDER-40: a non-finite vertex value is repaired on its way into the SurfaceTool and the
        // mesh is named once, instead of Godot printing anonymous normalize warnings later.
        var guard = new MeshArrayGuard.VertexGuard();

        SurfaceTool? st = null;
        int runVertexBase = 0;
        int committable = -1;

        void FlushRun()
        {
            if (st == null) return;
            var arrays = st.CommitToArrays();
            // Commit adds no surface for a run without a vertex, and then neither may this.
            if (arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil) surfaces.Add(arrays);
            st = null;
        }

        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;

            committable++;
            // Defensive: a plan shorter than the committable submeshes would silently merge the
            // tail into whatever run preceded it, so treat a missing entry as "starts a surface".
            // Written as one condition rather than via a bool so the compiler's null analysis can
            // still see that st is non-null below.
            if (st == null || committable >= runStart.Length || runStart[committable])
            {
                FlushRun();
                st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                runVertexBase = 0;
                indices.Add(sub.FaceIndex);
            }

            // Tangents, computed in GODOT space and from the FINAL UVs -- both matter. The
            // positions below are swizzled from SL's Z-up and the V is conditionally flipped, and
            // a tangent basis derived from the pre-swizzle values would be rotated relative to the
            // vertices it is attached to.
            //
            // Godot cannot apply a normal map without these. SurfaceTool.GenerateTangents() used
            // to do the job and had to be turned off: it produced NaNs on the degenerate triangles
            // SL content is full of, and those reached the Vulkan driver. SLNG.Assets.MeshTangents
            // guarantees finite output instead of dividing by a zero-area UV triangle -- see its
            // tests for the exact family of inputs that crashed.
            var tangentPositions = new System.Numerics.Vector3[sub.Positions.Length];
            var tangentNormals = new System.Numerics.Vector3[sub.Positions.Length];
            var tangentUvs = new System.Numerics.Vector2[sub.Positions.Length];
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var sp = sub.Positions[i];
                var sn = sub.Normals[i];
                var suv = sub.UVs[i];
                tangentPositions[i] = new System.Numerics.Vector3(sp.X, sp.Z, -sp.Y);
                tangentNormals[i] = new System.Numerics.Vector3(sn.X, sn.Z, -sn.Y);
                tangentUvs[i] = new System.Numerics.Vector2(suv.X, flipV ? 1.0f - suv.Y : suv.Y);
            }
            // The winding is reversed below, so the tangent maths gets the same order the GPU
            // will see rather than the source order.
            var tangentIndices = new int[sub.Indices.Length];
            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                tangentIndices[t] = sub.Indices[t];
                tangentIndices[t + 1] = sub.Indices[t + 2];
                tangentIndices[t + 2] = sub.Indices[t + 1];
            }
            var tangents = MeshTangents.Compute(tangentPositions, tangentNormals, tangentUvs, tangentIndices);

            // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front.
            // Reverse each triangle's winding by swapping its last two indices.
            // We supply the vertices once, then supply the reversed indices.
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var p = sub.Positions[i];
                var n = sub.Normals[i];
                var uv = sub.UVs[i];

                st.SetNormal(guard.Normal(new Godot.Vector3(n.X, n.Z, -n.Y), i));
                var tg = tangents[i];
                st.SetTangent(new Godot.Plane(tg.X, tg.Y, tg.Z, tg.W));
                st.SetUV(guard.Uv(new Godot.Vector2(uv.X, flipV ? 1.0f - uv.Y : uv.Y), i));
                st.AddVertex(guard.Position(new Godot.Vector3(p.X, p.Z, -p.Y), i));
            }

            // Shifted past whatever this run already holds. Zero unless a previous submesh was
            // merged into this same surface, so the un-merged path is unchanged arithmetic.
            int indexBase = runVertexBase;
            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                st.AddIndex(indexBase + sub.Indices[t]);
                st.AddIndex(indexBase + sub.Indices[t + 2]);
                st.AddIndex(indexBase + sub.Indices[t + 1]);
            }

            runVertexBase += sub.Positions.Length;
        }
        FlushRun();

        guard.Report(label ?? (() => "prim mesh (unlabelled)"));
        faceIndices = indices.ToArray();
        return surfaces.ToArray();
    }

    /// <summary>The engine half: one <c>AddSurfaceFromArrays</c> per prepared surface.</summary>
    private static ArrayMesh CommitSurfaceArrays(Godot.Collections.Array[] surfaces)
    {
        var arrayMesh = new ArrayMesh();
        foreach (var arrays in surfaces)
            arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return arrayMesh;
    }
}
