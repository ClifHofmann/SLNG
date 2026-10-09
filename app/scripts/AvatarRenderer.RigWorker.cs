using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

// BUG-PERF-06: rigging a worn mesh, split into a worker half and a main-thread half.
//
// A rig was one main-thread queue item that cost up to 193 ms on a busy sim (avatar.rig.verts up to
// 122 ms, avatar.rig.tangents up to 67 ms), and MainThreadWorkQueue.Pump runs at least one item per
// lane per frame however long it takes -- so every worn mesh was a hitch frame. Almost all of that is
// CPU work on arrays: the vertex loop (SLNG.Assets.RiggedMeshBuilder) and Godot's MikkTSpace tangent
// pass, which SurfaceTool runs on its own vertex list without touching the RenderingServer (only
// SurfaceTool.Commit does, through ArrayMesh.AddSurfaceFromArrays -- scene/resources/surface_tool.cpp,
// 4.7-stable). Both now run here, on a worker; the main thread binds the Skin to the skeleton, adds
// the finished arrays to an ArrayMesh and puts the instance in the scene (CommitPreparedRig).
// The same split GpuCache.PrepareImageAsync made for textures.
public partial class AvatarRenderer
{
    /// <summary>A worker's finished geometry for one request, waiting for its main-thread commit.</summary>
    private sealed record PreparedRig(PendingRig Request, PreparedRiggedMesh Mesh, double PrepareMs);

    /// <summary>The newest prepared geometry per worn entity. Written by the rig workers, taken by
    /// <see cref="CommitPreparedRig"/>.</summary>
    private readonly ConcurrentDictionary<Guid, PreparedRig> _preparedRigs = new();

    /// <summary>Entities with a prepare running. At most one per entity: a request that arrives
    /// while one is running is picked up by that worker when it finishes, so five repeats of the
    /// same mesh cost two prepares at most, never five.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _rigsPreparing = new();

    /// <summary>Bounds concurrent rig preparation, as <c>GpuCache._imagePrepGate</c> bounds image
    /// work. Half the cores: a region load fetches, decodes textures and prepares rigs all at the
    /// same time, and the main thread still needs a core of its own.</summary>
    private static readonly SemaphoreSlim _rigPrepGate = new(Math.Max(2, System.Environment.ProcessorCount / 2));

    /// <summary>Engine-ready surface arrays for one rigged mesh, and what it cost to make them.</summary>
    internal sealed class PreparedRiggedMesh
    {
        public required RiggedMeshGeometry Geometry { get; init; }

        /// <summary>One <c>Mesh.ArrayType.Max</c>-sized array set per surface, for
        /// <c>ArrayMesh.AddSurfaceFromArrays</c>.</summary>
        public required Godot.Collections.Array[] SurfaceArrays { get; init; }

        /// <summary>The joint-to-slot map the weights were written with. The Skin bound on the main
        /// thread has to agree with it.</summary>
        public required int[] SlotForJoint { get; init; }

        /// <summary>The click colliders' per-bone chunks (QueueRiggedPickBodies), when they were asked
        /// for; cut from the same arrays, so no read-back from the RenderingServer is needed.</summary>
        public PickChunk[]? PickChunks { get; init; }

        public double VertsMs { get; init; }
        public double TangentsMs { get; init; }
        public double PickMs { get; init; }
    }

    // BUG-PERF-12 counters for [AvatarCost]:
    private static long _rigsPreparedCount;
    private static long _rigsWithNormalCount;
    private static long _submeshesTotalCount;
    private static long _submeshesHiddenCount;

    private static readonly int MaxRigWorkers = Math.Max(2, System.Environment.ProcessorCount / 2);
    private int _activeRigWorkers;

    /// <summary>Records the newest rig request for a worn entity and makes sure workers are on it.
    /// Called from the mesh-load threads.</summary>
    private void RequestRig(Guid entityId, PendingRig request)
    {
        _pendingRigs[entityId] = request;
        EnsureRigWorkers();
    }

    private void EnsureRigWorkers()
    {
        while (_activeRigWorkers < MaxRigWorkers && !_pendingRigs.IsEmpty)
        {
            if (Interlocked.Increment(ref _activeRigWorkers) <= MaxRigWorkers)
            {
                _ = Task.Run(RunRigWorkerLoopAsync);
            }
            else
            {
                Interlocked.Decrement(ref _activeRigWorkers);
                break;
            }
        }
    }

    /// <summary>BUG-PERF-12: worker loop that continuously claims the closest pending rig to the
    /// camera/avatar (self first), prepares it off the main thread, and enqueues its commit.</summary>
    private async Task RunRigWorkerLoopAsync()
    {
        try
        {
            while (TryClaimNextRig(out var entityId, out var request))
            {
                try
                {
                    var ready = await PrepareOnWorkerAsync(request).ConfigureAwait(false);
                    if (ready != null)
                    {
                        _preparedRigs[entityId] = ready;
                        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => CommitPreparedRig(entityId),
                            coalesceKey: $"avatar.rig:{entityId}", label: "avatar.rig");
                    }
                    else
                    {
                        _pendingRigs.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, PendingRig>(entityId, request));
                    }
                }
                finally
                {
                    _rigsPreparing.TryRemove(entityId, out _);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeRigWorkers);
        }
    }

    /// <summary>Picks the highest priority pending rig: self avatar always 0, then distance from
    /// camera squared, with culled/far avatars pushed to the end.</summary>
    private bool TryClaimNextRig(out Guid bestEntityId, out PendingRig bestRequest)
    {
        bestEntityId = Guid.Empty;
        bestRequest = null!;

        float bestDistSq = float.MaxValue;
        Guid candidateId = Guid.Empty;
        PendingRig? candidateReq = null;
        var camPos = _lastCamPos;

        foreach (var (id, req) in _pendingRigs)
        {
            if (_rigsPreparing.ContainsKey(id)) continue;
            if (_preparedRigs.TryGetValue(id, out var ready) && ready.Request.Equals(req)) continue;

            float distSq;
            if (req.Visual.IsSelf)
            {
                distSq = 0f;
            }
            else
            {
                distSq = 1f + req.Visual.GodotPos.DistanceSquaredTo(camPos);
                if (!req.Visual.Shown) distSq += 1_000_000f;
            }

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                candidateId = id;
                candidateReq = req;
                if (distSq == 0f) break;
            }
        }

        if (candidateId == Guid.Empty || candidateReq == null)
            return false;

        if (_rigsPreparing.TryAdd(candidateId, 0))
        {
            if (_pendingRigs.TryGetValue(candidateId, out var newest))
            {
                if (_preparedRigs.TryGetValue(candidateId, out var already) && already.Request.Equals(newest))
                {
                    _rigsPreparing.TryRemove(candidateId, out _);
                    return false;
                }
                bestEntityId = candidateId;
                bestRequest = newest;
                return true;
            }
            _rigsPreparing.TryRemove(candidateId, out _);
        }

        return false;
    }

    private static async Task<PreparedRig?> PrepareOnWorkerAsync(PendingRig request)
    {
        await _rigPrepGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!EngineWorkerGate.TryEnter()) return null;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var definition = request.Definition;
                // The node is SkeletonBuilder.Build(definition): one bone per definition bone, so
                // "does FindBone find it" is "does the definition have it".
                Func<string, bool> isBone = definition == null
                    ? _ => false
                    : name => definition.GetBone(definition.ResolveBoneName(name)) != null;
                var slots = RiggedMeshBuilder.SlotsForJoints(request.MeshData.Skin!.JointNames, isBone);
                var mesh = PrepareRiggedMesh(request.MeshData, slots, request.Faces, request.DefaultFace, request.WantPick);
                return new PreparedRig(request, mesh, clock.Elapsed.TotalMilliseconds);
            }
            finally
            {
                EngineWorkerGate.Exit();
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[RiggedMesh] preparing mesh {request.MeshId:N} failed: {ex.Message}");
            return null;
        }
        finally
        {
            _rigPrepGate.Release();
        }
    }

    /// <summary>Self test (BUG-PERF-08): a worn rig's click colliders are built one chunk per queue
    /// item, and not at all for an item that was taken off, replaced or re-rigged meanwhile. Needs no
    /// grid: a world with one entity, a bare skeleton and a skin that binds a bone per slot.</summary>
    internal (bool Passed, string Detail) SelfTestRiggedPickBodies()
    {
        var problems = new System.Collections.Generic.List<string>();
        var world = new SLNG.Core.ECS.World();
        var previousWorld = _world;
        _world = world;
        var visual = new AvatarVisual { IsSelf = true };
        AddChild(visual.Root);
        var skeleton = new Skeleton3D();
        for (int bone = 0; bone < 4; bone++) skeleton.AddBone($"mTestBone{bone}");
        visual.Root.AddChild(skeleton);
        var id = world.GetOrCreateEntity(1UL, 7001).Id;

        MeshInstance3D NewItem()
        {
            var skin = new Skin();
            for (int slot = 0; slot < 4; slot++) skin.AddBind(slot, Transform3D.Identity);
            var mi = new MeshInstance3D { Skin = skin };
            skeleton.AddChild(mi);
            return mi;
        }

        var chunks = new PickChunk[4];
        for (int slot = 0; slot < chunks.Length; slot++)
            chunks[slot] = new PickChunk(slot, new[] { Godot.Vector3.Zero, Godot.Vector3.Right, Godot.Vector3.Up });

        int Built() => _riggedPickBodies.TryGetValue(id, out var list) ? list.Count(IsInstanceValid) : 0;

        // Pumps one item at a time (a budget of nothing still runs one per lane) and reports the
        // most colliders any single pump added.
        int Drain(out int pumps)
        {
            int most = 0;
            pumps = 0;
            while (MainThreadWorkQueue.Depth > 0 && pumps < 500)
            {
                int before = Built();
                MainThreadWorkQueue.Pump(0);
                most = Math.Max(most, Built() - before);
                pumps++;
            }
            return most;
        }

        try
        {
            var item = NewItem();
            _riggedAttachments[id] = item;
            QueueRiggedPickBodies(item, visual, skeleton, id, chunks);
            if (Built() != 0) problems.Add("a collider was built inside the rig item");
            int most = Drain(out int pumps);
            if (Built() != chunks.Length) problems.Add($"{Built()} colliders built, expected {chunks.Length}");
            if (most != 1) problems.Add($"{most} colliders in one queue item, expected 1");
            if (_pendingRiggedPicks.ContainsKey(id)) problems.Add("the debt was kept after the last chunk");

            // Taken off (RemoveVisual / UpdateAttachment): the colliders go and nothing more is built.
            var removed = NewItem();
            _riggedAttachments[id] = removed;
            QueueRiggedPickBodies(removed, visual, skeleton, id, chunks);
            ClearRiggedPickBodies(id);
            Drain(out _);
            if (Built() != 0) problems.Add($"{Built()} colliders built for an item that was taken off");

            // Replaced by a newer rig before its turn: only the newer one's chunks are built.
            var stale = NewItem();
            _riggedAttachments[id] = stale;
            QueueRiggedPickBodies(stale, visual, skeleton, id, chunks);
            var fresh = NewItem();
            _riggedAttachments[id] = fresh;
            QueueRiggedPickBodies(fresh, visual, skeleton, id, chunks.AsSpan(0, 2).ToArray());
            Drain(out _);
            if (Built() != 2) problems.Add($"{Built()} colliders after a re-rig, expected the new rig's 2");

            // Replaced without the debt being cleared (the item the entity points at moved on): dropped.
            var moved = NewItem();
            _riggedAttachments[id] = moved;
            QueueRiggedPickBodies(moved, visual, skeleton, id, chunks);
            ClearRiggedPickBodies(id);
            QueueRiggedPickBodies(moved, visual, skeleton, id, chunks);
            _riggedAttachments[id] = NewItem();
            Drain(out _);
            if (Built() != 0) problems.Add($"{Built()} colliders built for an item that is no longer the rigged one");

            return problems.Count == 0
                ? (true, $"{chunks.Length} chunks took {pumps} queue items of at most one collider each; a removed, re-rigged or replaced item builds nothing stale")
                : (false, string.Join("; ", problems));
        }
        finally
        {
            ClearRiggedPickBodies(id);
            _riggedAttachments.Remove(id);
            _world = previousWorld;
            if (IsInstanceValid(visual.Root)) visual.Root.QueueFree();
        }
    }

    /// <summary>The CPU half of a rig: geometry, weights, surface merging and tangents. Safe on a
    /// worker; also run inline by <see cref="BuildRiggedMeshInstance"/> for the callers that build
    /// synchronously.</summary>
    internal static PreparedRiggedMesh PrepareRiggedMesh(
        MeshData meshData, int[] slotForJoint, FaceTexture[]? faces, FaceTexture defaultFace, bool wantPickChunks = false)
    {
        Interlocked.Increment(ref _rigsPreparedCount);
        bool hasNormal = false;
        if (faces != null)
        {
            for (int f = 0; f < faces.Length; f++)
            {
                if (faces[f].RenderMaterialId != Guid.Empty || faces[f].LegacyMaterialId != Guid.Empty)
                {
                    hasNormal = true;
                    break;
                }
            }
        }
        if (!hasNormal && (defaultFace.RenderMaterialId != Guid.Empty || defaultFace.LegacyMaterialId != Guid.Empty))
            hasNormal = true;
        if (hasNormal)
            Interlocked.Increment(ref _rigsWithNormalCount);

        for (int i = 0; i < meshData.Submeshes.Count; i++)
        {
            var sub = meshData.Submeshes[i];
            if (sub.Indices.Length == 0 || sub.Weights == null) continue;
            Interlocked.Increment(ref _submeshesTotalCount);
            var subFace = ResolveFaceTexture(faces, defaultFace, sub.FaceIndex);
            if (subFace.IsInvisible)
                Interlocked.Increment(ref _submeshesHiddenCount);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var geometry = RiggedMeshBuilder.Build(meshData, slotForJoint, fi => ResolveFaceTexture(faces, defaultFace, fi));
        double vertsMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        var arrays = new Godot.Collections.Array[geometry.Surfaces.Count];
        for (int i = 0; i < arrays.Length; i++)
            arrays[i] = RiggedSurfaceArrays(geometry.Surfaces[i]);
        double tangentsMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        PickChunk[]? pickChunks = null;
        if (wantPickChunks)
        {
            var surfaces = new System.Collections.Generic.List<(Godot.Vector3[], int[], float[], int[])>(geometry.Surfaces.Count);
            foreach (var surface in geometry.Surfaces)
            {
                surfaces.Add((MemoryMarshal.Cast<System.Numerics.Vector3, Godot.Vector3>(surface.Positions.AsSpan()).ToArray(),
                              surface.Bones, surface.Weights, surface.Indices));
            }
            pickChunks = ChunkByDominantSlot(surfaces, RiggedMeshBuilder.SlotCount(slotForJoint));
        }

        return new PreparedRiggedMesh
        {
            Geometry = geometry,
            SurfaceArrays = arrays,
            SlotForJoint = slotForJoint,
            PickChunks = pickChunks,
            VertsMs = vertsMs,
            TangentsMs = tangentsMs,
            PickMs = clock.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>One surface's engine arrays, with tangents from Godot's own MikkTSpace pass.</summary>
    /// <remarks>
    /// The tangents are exactly what the per-vertex SurfaceTool build used to produce: MikkTSpace
    /// reads only positions, normals, UVs and indices, and <c>SurfaceTool.CreateFromArrays</c> puts
    /// those into the same vertex list <c>AddVertex</c> did. The skin streams do not take part, so
    /// they are added afterwards instead of being copied through the tool. The self test
    /// (<c>rigged mesh prepared off the main thread</c>) compares the two builds array by array.
    /// </remarks>
    internal static Godot.Collections.Array RiggedSurfaceArrays(RiggedSurface surface)
    {
        var input = new Godot.Collections.Array();
        input.Resize((int)Mesh.ArrayType.Max);
        input[(int)Mesh.ArrayType.Vertex] = Variant.CreateFrom(MemoryMarshal.Cast<System.Numerics.Vector3, Godot.Vector3>(surface.Positions.AsSpan()));
        input[(int)Mesh.ArrayType.Normal] = Variant.CreateFrom(MemoryMarshal.Cast<System.Numerics.Vector3, Godot.Vector3>(surface.Normals.AsSpan()));
        input[(int)Mesh.ArrayType.TexUV] = Variant.CreateFrom(MemoryMarshal.Cast<System.Numerics.Vector2, Godot.Vector2>(surface.UVs.AsSpan()));
        input[(int)Mesh.ArrayType.Index] = surface.Indices;

        using var tool = new SurfaceTool();
        tool.CreateFromArrays(input, Mesh.PrimitiveType.Triangles);
        tool.GenerateTangents();
        var arrays = tool.CommitToArrays();
        arrays[(int)Mesh.ArrayType.Bones] = surface.Bones;
        arrays[(int)Mesh.ArrayType.Weights] = surface.Weights;
        return arrays;
    }
}
