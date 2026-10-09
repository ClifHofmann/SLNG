using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

// BUG-PERF-08: a NON-rigged worn mesh (a prim, a sculpt, a static mesh hat), split into a worker half
// and a main-thread half -- the same split BUG-PERF-06 made for rigged meshes (RigWorker.cs) and HUD
// meshes (PrepareHudMesh).
//
// The `avatar.attach` queue item built every such mesh on the main thread: a SurfaceTool per same-
// material run fed vertex by vertex, Godot's MikkTSpace tangent pass, and for the own avatar a
// CreateTrimeshShape, which reads every surface back out of the RenderingServer. Measured in-world
// 2026-10-08 (Amrum, v0.26.116): 1549 items, 2.3 s in total, max 79 ms -- and
// MainThreadWorkQueue.Pump runs at least one item per lane per frame however long it takes, so the
// slow ones were hitch frames. All of it is CPU work on arrays except the last step of each
// surface: SurfaceTool.Commit is `AddSurfaceFromArrays(Triangles, CommitToArrays())`, and only that
// call touches the RenderingServer (scene/resources/surface_tool.cpp, 4.7-stable). The arrays are
// made here, on a worker; the main thread uploads them and puts the instance in the scene.
public partial class AvatarRenderer
{
    /// <summary>One request to build a non-rigged worn mesh. <c>WantPick</c> was read when the request
    /// was made: the own avatar's items get a click collider, so the worker lists its faces too.</summary>
    private sealed record PendingAttach(
        MeshData MeshData, Node3D AttachParent, AvatarVisual Visual, Guid MeshId,
        FaceTexture[]? Faces, FaceTexture DefaultFace, System.Numerics.Vector3 Scale,
        System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation,
        Guid EntityId, long Sequence, bool WantPick);

    /// <summary>The newest request number per worn entity. Workers finish in any order, while the main-
    /// thread queue used to apply requests in the order they were made; a prepared mesh is dropped when
    /// its number is no longer this one, so the end state is the one a FIFO queue would have left.
    /// Taking the entry away (the entity left) cancels everything still in flight for it.</summary>
    private readonly ConcurrentDictionary<Guid, long> _attachNewest = new();

    private long _attachSequence;

    /// <summary>Engine-ready surface arrays for one non-rigged worn mesh, and what it cost to make them.</summary>
    internal sealed class PreparedAttachMesh
    {
        /// <summary>One array set per surface (vertex, normal, tangent, UV, index), for
        /// <c>ArrayMesh.AddSurfaceFromArrays</c>.</summary>
        public required Godot.Collections.Array[] SurfaceArrays { get; init; }

        /// <summary>The SL face of each merged run, in run order (not necessarily one per surface:
        /// a run that has no vertices makes a face entry and no surface).</summary>
        public required int[] FaceIndices { get; init; }

        /// <summary>The click collider's faces, three points per triangle: exactly what
        /// <c>ArrayMesh.CreateTrimeshShape</c> would hand its <c>ConcavePolygonShape3D</c>. Null when
        /// the request did not ask for them.</summary>
        public Godot.Vector3[]? PickFaces { get; init; }

        /// <summary>Repairs made on the way in (BUG-RENDER-40); reported on the main thread, where the
        /// build used to run, so the log reads as before.</summary>
        public required MeshArrayGuard.VertexGuard Guard { get; init; }

        public double PrepareMs { get; init; }
    }

    /// <summary>Records the request as the newest for the entity and starts preparing it on a worker.
    /// Called from the mesh-load threads, which may well be the main thread.</summary>
    private void RequestAttachMesh(
        MeshData meshData, Node3D attachParent, AvatarVisual avatarVisual, Guid meshId,
        FaceTexture[]? faces, FaceTexture defaultFace, System.Numerics.Vector3 slScale,
        System.Numerics.Vector3 slPos, System.Numerics.Quaternion slRot, Guid entityId)
    {
        long sequence = Interlocked.Increment(ref _attachSequence);
        // The larger number wins when two callers race to record theirs.
        _attachNewest.AddOrUpdate(entityId, sequence, (_, old) => Math.Max(old, sequence));

        var request = new PendingAttach(meshData, attachParent, avatarVisual, meshId, faces, defaultFace,
                                        slScale, slPos, slRot, entityId, sequence, avatarVisual.IsSelf);
        // Unconditional hop: a cached mesh completes its task synchronously, on whatever thread
        // asked -- and the whole point is that this work never runs on the main thread.
        _ = Task.Run(() => PrepareAttachAsync(request));
    }

    private bool IsNewestAttachRequest(PendingAttach request) =>
        _attachNewest.TryGetValue(request.EntityId, out var newest) && newest == request.Sequence;

    private async Task PrepareAttachAsync(PendingAttach request)
    {
        PreparedAttachMesh? prepared = null;
        // Reuses the rig gate: the same pool of cores does this work, and a region load runs both at once.
        await _rigPrepGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A request that was replaced while it waited for a core is not worth building.
            if (!IsNewestAttachRequest(request) || !EngineWorkerGate.TryEnter()) return;
            try
            {
                prepared = PrepareAttachmentMesh(request.MeshData, request.Faces, request.DefaultFace,
                                                 request.Scale, request.WantPick);
            }
            finally
            {
                EngineWorkerGate.Exit();
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[Attachment] preparing mesh {request.MeshId:N} failed: {ex.Message}");
            return;
        }
        finally
        {
            _rigPrepGate.Release();
        }

        if (prepared == null) return;
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => CommitPreparedAttach(request, prepared),
            label: "avatar.attach");
    }

    /// <summary>The main-thread half: uploads the worker's arrays and puts the instance in the scene.
    /// Runs as the <c>avatar.attach</c> queue item.</summary>
    private void CommitPreparedAttach(PendingAttach request, PreparedAttachMesh prepared)
    {
        if (!IsInstanceValid(request.AttachParent)) return;
        if (!IsNewestAttachRequest(request)) return;

        var entityId = request.EntityId;
        var avatarVisual = request.Visual;
        MainThreadWorkQueue.RecordExternal("avatar.attach.prepare", prepared.PrepareMs);

        var arrayMesh = new ArrayMesh();
        foreach (var arrays in prepared.SurfaceArrays)
            arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        prepared.Guard.Report(() => $"worn attachment entity={entityId:N} mesh={request.MeshId:N} avatar={avatarVisual.AgentId:N}");

        // Where the item sits NOW: a duplicate update may have moved it while it was being prepared
        // (ApplyAttachmentPose has no node to move yet and only records the pose).
        var slPos = request.Position;
        var slRot = request.Rotation;
        if (_attachmentLocalPoses.TryGetValue(entityId, out var latest))
        {
            slPos = latest.Position;
            slRot = latest.Rotation;
        }
        var mi = new MeshInstance3D { Name = "AttachMesh", Mesh = arrayMesh };
        mi.Position = new Godot.Vector3(slPos.X, slPos.Z, -slPos.Y);
        mi.Quaternion = new Godot.Quaternion(slRot.X, slRot.Z, -slRot.Y, slRot.W);
        request.AttachParent.AddChild(mi);
        AddAttachmentPickBody(mi, arrayMesh, avatarVisual, entityId, prepared.PickFaces);
        RestoreWornHighlight(entityId);
        RegisterBomAndUpdateVisibility(avatarVisual, mi, prepared.FaceIndices, request.Faces, request.DefaultFace, request.MeshId);
        _ = ApplyFaceMaterialsAsync(mi, prepared.FaceIndices, request.Faces, request.DefaultFace, avatarVisual, request.MeshId);
    }

    /// <summary>Self test (BUG-PERF-08): of two requests for one worn entity only the newer reaches the
    /// scene, and a request whose entity left in the meantime reaches nothing. Needs no grid and no
    /// world: each request builds its mesh on a worker, and the queue is pumped by hand.</summary>
    internal (bool Passed, string Detail) SelfTestAttachSupersede()
    {
        var visual = new AvatarVisual();
        AddChild(visual.Root);
        try
        {
            var older = new Node3D { Name = "OlderPointOffset" };
            var newer = new Node3D { Name = "NewerPointOffset" };
            var gone = new Node3D { Name = "GonePointOffset" };
            visual.Root.AddChild(older);
            visual.Root.AddChild(newer);
            visual.Root.AddChild(gone);

            var triangle = new MeshSubmesh(
                new[] { System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY },
                new[] { System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ },
                new[] { System.Numerics.Vector2.Zero, System.Numerics.Vector2.UnitX, System.Numerics.Vector2.UnitY },
                new[] { 0, 1, 2 }, 0);
            var mesh = new MeshData(new[] { triangle });
            var one = System.Numerics.Vector3.One;
            var upright = System.Numerics.Quaternion.Identity;

            var entity = Guid.NewGuid();
            RequestAttachMesh(mesh, older, visual, Guid.Empty, null, default, one, new System.Numerics.Vector3(1f, 0f, 0f), upright, entity);
            RequestAttachMesh(mesh, newer, visual, Guid.Empty, null, default, one, new System.Numerics.Vector3(0f, 2f, 0f), upright, entity);

            // The entity leaves (RemoveVisual) after its request, before the commit.
            var leaving = Guid.NewGuid();
            RequestAttachMesh(mesh, gone, visual, Guid.Empty, null, default, one, System.Numerics.Vector3.Zero, upright, leaving);
            _attachNewest.TryRemove(leaving, out _);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000 && newer.GetChildCount() == 0)
            {
                Thread.Sleep(2);
                MainThreadWorkQueue.Pump(50);
            }
            // The two that must not commit get every chance to.
            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(4);
                MainThreadWorkQueue.Pump(50);
            }

            var problems = new List<string>();
            if (newer.GetChildCount() != 1) problems.Add($"the newer request made {newer.GetChildCount()} meshes, expected 1");
            else if (newer.GetChild(0) is not MeshInstance3D placed
                     || placed.Position.DistanceTo(new Godot.Vector3(0f, 0f, -2f)) > 1e-5f)
                problems.Add("the newer request's mesh is not where it asked to be");
            if (older.GetChildCount() != 0) problems.Add("the older request was committed after a newer one had been made");
            if (gone.GetChildCount() != 0) problems.Add("a request was committed for an entity that had left");

            return problems.Count == 0
                ? (true, "the newest request wins; a request for an entity that left is dropped")
                : (false, string.Join("; ", problems));
        }
        finally
        {
            if (IsInstanceValid(visual.Root)) visual.Root.QueueFree();
        }
    }

    /// <summary>The CPU half of a non-rigged worn mesh: vertices, same-material run merging, tangents,
    /// and (when asked) the faces of the click collider. Safe on a worker.</summary>
    /// <remarks>
    /// Builds exactly what the main-thread item built: the loop below is the old one, vertex for
    /// vertex, with <c>CommitToArrays</c> where <c>Commit</c> was. <c>Commit</c> adds nothing for a
    /// tool that has no vertices, so a run whose arrays carry no vertex stream is skipped here as
    /// well. The self test <c>worn attachment mesh prepared off the main thread</c> keeps the old
    /// build as an oracle and compares every array, the surface formats and the collider's faces.
    /// </remarks>
    internal static PreparedAttachMesh PrepareAttachmentMesh(
        MeshData meshData, FaceTexture[]? faces, FaceTexture defaultFace,
        System.Numerics.Vector3 slScale, bool wantPickFaces)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var surfaces = new List<Godot.Collections.Array>();
        var faceIndices = new List<int>();

        // BUG-RENDER-12: same authored-order surface merging as the rigged path -- see
        // RiggedMeshBuilder.Build for the viewer citations and the reasoning.
        // A non-rigged worn attachment (sculpt/prim hair, a mesh hat) reaches Godot's
        // transparent queue exactly the same way, so it has the same reorder problem.
        SurfaceTool? st = null;
        var runFace = default(FaceTexture);
        int runVertexBase = 0;
        // BUG-RENDER-40: non-finite vertex values are repaired on their way into the SurfaceTool
        // (GenerateTangents below would otherwise hand them to the engine) and named once.
        var guard = new MeshArrayGuard.VertexGuard();

        void FlushRun()
        {
            if (st == null) return;
            st.GenerateTangents();
            var arrays = st.CommitToArrays();
            if (arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil) surfaces.Add(arrays);
            st.Dispose();
            st = null;
        }

        foreach (var sub in meshData.Submeshes)
        {
            if (sub.Indices.Length == 0) continue;

            var subFace = ResolveFaceTexture(faces, defaultFace, sub.FaceIndex);
            if (st == null || !subFace.Equals(runFace))
            {
                FlushRun();
                st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                runFace = subFace;
                runVertexBase = 0;
                faceIndices.Add(sub.FaceIndex);
            }

            int indexBase = runVertexBase;
            // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front — left
            // uncorrected, every SL-sourced triangle rasterizes as a backface (masked by
            // CullMode.Disabled, needed just to make anything render), and Godot's
            // double-sided handling flips the normal for perceived backfaces, inverting
            // diffuse lighting while leaving shadows (depth-only) unaffected. Confirmed this
            // session via a T-pose + debug shader + a gizmo pointing at the real light
            // direction. Fix: submit each triangle's 3 vertices in reversed order.
            for (int i = 0; i < sub.Positions.Length; i++)
            {
                var p = sub.Positions[i];
                var n = sub.Normals[i];
                var uv = sub.UVs[i];

                st.SetNormal(guard.Normal(new Godot.Vector3(n.X, n.Z, -n.Y), i));
                st.SetUV(guard.Uv(new Godot.Vector2(uv.X, 1.0f - uv.Y), i));
                st.AddVertex(guard.Position(new Godot.Vector3(p.X * slScale.X, p.Z * slScale.Z, -p.Y * slScale.Y), i));
            }

            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                st.AddIndex(indexBase + sub.Indices[t]);
                st.AddIndex(indexBase + sub.Indices[t + 2]);
                st.AddIndex(indexBase + sub.Indices[t + 1]);
            }

            runVertexBase += sub.Positions.Length;
        }
        FlushRun();

        return new PreparedAttachMesh
        {
            SurfaceArrays = surfaces.ToArray(),
            FaceIndices = faceIndices.ToArray(),
            PickFaces = wantPickFaces ? TrimeshFaces(surfaces) : null,
            Guard = guard,
            PrepareMs = clock.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>What <c>ArrayMesh.CreateTrimeshShape().Data</c> would return for these surfaces, without
    /// reading them back out of the RenderingServer: every index in order, or every vertex of a
    /// non-indexed surface, each point snapped the way <c>TriangleMesh.create</c> snaps it
    /// (<see cref="SnapForTriangleMesh"/>). Surfaces are taken in order, as the engine's
    /// <c>Mesh.generate_triangle_mesh</c> does.</summary>
    internal static Godot.Vector3[] TrimeshFaces(IReadOnlyList<Godot.Collections.Array> surfaces)
    {
        var faces = new List<Godot.Vector3>();
        foreach (var arrays in surfaces)
        {
            var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indexStream = arrays[(int)Mesh.ArrayType.Index];
            var indices = indexStream.VariantType == Variant.Type.Nil ? Array.Empty<int>() : indexStream.AsInt32Array();
            if (indices.Length > 0)
            {
                // A stray index would make the engine refuse this surface at upload; it must not
                // take the rest of the mesh down here (an exception drops the whole request).
                if (indices.Any(index => (uint)index >= (uint)positions.Length)) continue;
                foreach (int index in indices) faces.Add(SnapForTriangleMesh(positions[index]));
            }
            else if (positions.Length % 3 == 0)
            {
                foreach (var position in positions) faces.Add(SnapForTriangleMesh(position));
            }
        }
        return faces.ToArray();
    }
}
