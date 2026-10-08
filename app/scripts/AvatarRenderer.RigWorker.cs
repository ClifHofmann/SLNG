using System;
using System.Collections.Concurrent;
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

        public double VertsMs { get; init; }
        public double TangentsMs { get; init; }
    }

    /// <summary>Records the newest rig request for a worn entity and makes sure a worker is on it.
    /// Called from the mesh-load threads.</summary>
    private void RequestRig(Guid entityId, PendingRig request)
    {
        _pendingRigs[entityId] = request;
        // Unconditional hop: the caller may well be the main thread (a cached mesh completes its
        // task synchronously), and the whole point is that this work never runs there.
        if (_rigsPreparing.TryAdd(entityId, 0))
            _ = Task.Run(() => PrepareRigsAsync(entityId));
    }

    /// <summary>Prepares the newest request for <paramref name="entityId"/>, queues its commit, and
    /// repeats while a newer request arrived in the meantime.</summary>
    private async Task PrepareRigsAsync(Guid entityId)
    {
        while (true)
        {
            PendingRig? prepared = null;
            if (_pendingRigs.TryGetValue(entityId, out var request))
            {
                prepared = request;
                var ready = await PrepareOnWorkerAsync(request).ConfigureAwait(false);
                if (ready != null)
                {
                    // Stored BEFORE the enqueue, so a commit item that is already waiting for this
                    // entity -- the queue then drops this enqueue as a repeat -- finds it.
                    _preparedRigs[entityId] = ready;
                    MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => CommitPreparedRig(entityId),
                        coalesceKey: $"avatar.rig:{entityId}", label: "avatar.rig");
                }
                else
                {
                    // Nothing to commit (it threw, or the client is quitting): let the request go
                    // unless a newer one has replaced it.
                    _pendingRigs.TryRemove(new System.Collections.Generic.KeyValuePair<Guid, PendingRig>(entityId, request));
                }
            }

            _rigsPreparing.TryRemove(entityId, out _);
            // A request that came in while this one was being prepared found the flag taken and
            // left the work to this worker. Released first and checked second, so a request in
            // between either sees the flag gone and starts its own worker, or is seen here.
            if (!_pendingRigs.TryGetValue(entityId, out var newest) || newest.Equals(prepared)) return;
            if (!_rigsPreparing.TryAdd(entityId, 0)) return;
        }
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
                var mesh = PrepareRiggedMesh(request.MeshData, slots, request.Faces, request.DefaultFace);
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

    /// <summary>The CPU half of a rig: geometry, weights, surface merging and tangents. Safe on a
    /// worker; also run inline by <see cref="BuildRiggedMeshInstance"/> for the callers that build
    /// synchronously.</summary>
    internal static PreparedRiggedMesh PrepareRiggedMesh(
        MeshData meshData, int[] slotForJoint, FaceTexture[]? faces, FaceTexture defaultFace)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var geometry = RiggedMeshBuilder.Build(meshData, slotForJoint, fi => ResolveFaceTexture(faces, defaultFace, fi));
        double vertsMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        var arrays = new Godot.Collections.Array[geometry.Surfaces.Count];
        for (int i = 0; i < arrays.Length; i++)
            arrays[i] = RiggedSurfaceArrays(geometry.Surfaces[i]);

        return new PreparedRiggedMesh
        {
            Geometry = geometry,
            SurfaceArrays = arrays,
            SlotForJoint = slotForJoint,
            VertsMs = vertsMs,
            TangentsMs = clock.Elapsed.TotalMilliseconds,
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
