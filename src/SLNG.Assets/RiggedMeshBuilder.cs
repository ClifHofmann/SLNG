using System;
using System.Collections.Generic;
using System.Numerics;
using SLNG.Core;

namespace SLNG.Assets;

/// <summary>
/// The pure-data half of rigging a worn mesh to the avatar skeleton (BUG-PERF-06): every vertex
/// through the bind-shape matrix into Godot axes, every weight resolved to a skin slot, and
/// consecutive same-material submeshes merged into one surface.
/// </summary>
/// <remarks>
/// This used to run inside one main-thread queue item together with the engine calls, at up to
/// 122 ms a rig on a busy sim -- a hitch frame per worn mesh. Nothing in it needs the engine or
/// the scene, so it runs on a worker; what is left for the main thread is binding the Skin to the
/// skeleton and handing the arrays to the engine. Everything here is unchanged from the version
/// that lived in AvatarRenderer.BuildRiggedMeshInstance, down to the order of operations.
/// </remarks>
public static class RiggedMeshBuilder
{
    /// <summary>The skin slot each of a mesh's joints binds to: -1 for a joint that names no bone
    /// of ours, otherwise its rank among the joints that do. That is the order the renderer adds
    /// the binds in (a slot is the bind's position in the Skin), so the two always agree as long
    /// as both ask the same question of the same skeleton.</summary>
    public static int[] SlotsForJoints(IReadOnlyList<string> jointNames, Func<string, bool> isBone)
    {
        var slots = new int[jointNames.Count];
        int next = 0;
        for (int j = 0; j < slots.Length; j++)
            slots[j] = isBone(jointNames[j]) ? next++ : -1;
        return slots;
    }

    /// <summary>Number of slots <see cref="SlotsForJoints"/> handed out.</summary>
    public static int SlotCount(int[] slotForJoint)
    {
        int count = 0;
        foreach (int slot in slotForJoint) if (slot >= 0) count++;
        return count;
    }

    /// <summary>Builds the surfaces of a rigged mesh. Submeshes without indices or weights are
    /// skipped, as before. <paramref name="faceFor"/> resolves a submesh's SL face record; two
    /// consecutive submeshes whose records compare equal share one surface.</summary>
    public static RiggedMeshGeometry Build(MeshData mesh, int[] slotForJoint, Func<int, FaceTexture> faceFor)
    {
        int slotCount = SlotCount(slotForJoint);
        var geo = new RiggedMeshGeometry
        {
            SubmeshCount = mesh.Submeshes.Count,
            SlotWeightSum = new float[slotCount],
        };
        var skin = mesh.Skin;
        if (skin == null || slotCount == 0) return geo;

        int jointCount = skin.JointNames.Length;
        var bindShape = skin.BindShapeMatrix;

        // Normals need the inverse-transpose of the bind-shape's linear part, not the raw
        // matrix — verified against the viewer source (llface.cpp, getGeometryVolume):
        // positions are pre-multiplied by BindShapeMatrix directly, but normals/tangents by
        // `transpose(inverse(BindShapeMatrix))`. Using the raw matrix (as System.Numerics'
        // Vector3.TransformNormal does by default) only matches for uniform scale; several of
        // our real assets have highly non-uniform bind-shape scale (observed up to ~4:1 on one
        // axis vs another), which would skew normals — and therefore lighting — noticeably off
        // true. Computed once per mesh, not per vertex, since bindShape doesn't vary by vertex.
        var bindShapeNormalMatrix = bindShape;
        if (Matrix4x4.Invert(bindShape, out var bindShapeInv))
            bindShapeNormalMatrix = Matrix4x4.Transpose(bindShapeInv);

        // BUG-RENDER-12: consecutive submeshes that resolve to the SAME face record are committed
        // as ONE surface, in their authored order.
        //
        // This is the half of the reference viewer's rigged-alpha design that makes its depth
        // write safe. llvovolume.cpp:6332-6335, with Linden's own comment:
        //     if (rigged) {
        //         if (!distance_sort) // <--- alpha "sort" rigged faces by maintaining original draw order
        //             std::sort(faces, faces + face_count, CompareBatchBreakerRigged());
        //     }
        // alpha_sort is always true (:6146), so for RIGGED alpha that branch sorts nothing at all:
        // worn mesh faces are batched in the order the creator authored them and are never
        // distance-sorted, unlike unrigged alpha (which does sort, and even then only re-sorts once
        // the view angle has moved more than 0.64 -- llspatialpartition.cpp:667-674).
        //
        // Godot has no equivalent knob: every transparent SURFACE is re-sorted by AABB-centre
        // distance each frame. Splitting a mesh into one surface per SL face therefore hands Godot
        // six independently reorderable pieces of what the creator authored as one ordered stream.
        // Measured on the live hair: three rigged meshes of six faces each, all six carrying the
        // identical texture (`face ids: [b9af3b5f x 6]`) -- 18 co-located transparent draws whose
        // order reshuffled on the smallest camera move.
        //
        // Merging a run restores the authored order as a single draw call, because within one
        // surface Godot draws triangles in index order and sorts nothing. Only CONSECUTIVE runs
        // are merged, never scattered matches: merging non-adjacent faces would interleave
        // triangles the creator ordered deliberately, which is the very thing being preserved.
        // Faces are merged only when their whole FaceTexture record compares equal, so the
        // surviving surface's material is bit-identical to the ones it replaces.
        Run? run = null;

        void FlushRun()
        {
            // A run whose submeshes carried no vertices has nothing to draw. It is dropped
            // together with its face index, so the face list stays parallel to the surfaces.
            if (run != null && run.Positions.Count > 0)
                geo.Surfaces.Add(run.ToSurface());
            run = null;
        }

        // Reused across every vertex: AddInfluence writes only the slots it fills, so they are
        // cleared per vertex.
        var bones = new int[4];
        var wts = new float[4];
        int remapped = 0;
        var bpMin = new Vector3(float.MaxValue);
        var bpMax = new Vector3(float.MinValue);

        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Indices.Length == 0 || sub.Weights == null) continue;

            var subFace = faceFor(sub.FaceIndex);
            if (subFace.IsInvisible)
            {
                FlushRun();
                continue;
            }

            if (run == null || !subFace.Equals(run.Face))
            {
                FlushRun();
                run = new Run(sub.FaceIndex, subFace);
            }

            // Indices are submesh-local, so a submesh appended to a run in progress has to shift
            // them past everything already in the run.
            int indexBase = run.Positions.Count;

            for (int i = 0; i < sub.Positions.Length; i++)
            {
                // Mesh-local → bind pose (SL coords) via the bind-shape matrix, then SL→Godot.
                var pSL = Vector3.Transform(sub.Positions[i], bindShape);
                var nSL = Vector3.TransformNormal(sub.Normals[i], bindShapeNormalMatrix);
                if (nSL.LengthSquared() > 1e-8f) nSL = Vector3.Normalize(nSL);
                var uv = sub.UVs[i];
                var w = sub.Weights[i];

                bpMin = Vector3.Min(bpMin, pSL);
                bpMax = Vector3.Max(bpMax, pSL);

                Array.Clear(bones, 0, 4);
                Array.Clear(wts, 0, 4);
                int c = 0; float sum = 0f;
                AddInfluence(w.Joint0, w.Weight0, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                AddInfluence(w.Joint1, w.Weight1, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                AddInfluence(w.Joint2, w.Weight2, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                AddInfluence(w.Joint3, w.Weight3, slotForJoint, jointCount, bones, wts, ref c, ref sum, ref remapped);
                geo.TotalVertices++;
                if (sum > 1e-5f) { for (int k = 0; k < 4; k++) wts[k] /= sum; }
                else { bones[0] = 0; wts[0] = 1f; geo.OrphanedVertices++; } // orphaned vertex — pin to first bound bone
                for (int k = 0; k < 4; k++) if (wts[k] > 0f) geo.SlotWeightSum[bones[k]] += wts[k];

                // BUG-RENDER-40: non-finite values are repaired here rather than handed to the
                // engine, which would normalise them and log an anonymous warning per vertex.
                // The vertex number is the running count, as it always was in the log line.
                int vertexNumber = geo.TotalVertices;
                var normal = new Vector3(nSL.X, nSL.Z, -nSL.Y);
                if (!IsFinite(normal))
                {
                    if (geo.BadNormals++ == 0) geo.FirstBadNormal = vertexNumber;
                    normal = Vector3.UnitY;
                }
                // Same SL→Godot V-flip as the system body parts (see BuildPartResources):
                // SL UVs are authored bottom-left origin; Godot samples top-left.
                var uvG = new Vector2(uv.X, 1.0f - uv.Y);
                if (!float.IsFinite(uvG.X) || !float.IsFinite(uvG.Y))
                {
                    if (geo.BadUvs++ == 0) geo.FirstBadUv = vertexNumber;
                    uvG = Vector2.Zero;
                }
                var position = new Vector3(pSL.X, pSL.Z, -pSL.Y);
                if (!IsFinite(position))
                {
                    if (geo.BadPositions++ == 0) geo.FirstBadPosition = vertexNumber;
                    position = Vector3.Zero;
                }

                run.Positions.Add(position);
                run.Normals.Add(normal);
                run.UVs.Add(uvG);
                run.Bones.AddRange(bones);
                run.Weights.AddRange(wts);
            }

            // SL/OpenGL authors triangles CCW-front; Godot/Vulkan expects CW-front — left
            // uncorrected, every SL-sourced triangle rasterizes as a backface (masked by
            // CullMode.Disabled, needed just to make anything render), and Godot's double-sided
            // handling flips the normal for perceived backfaces, inverting diffuse lighting while
            // leaving shadows (depth-only) unaffected. Fix: submit each triangle's 3 vertices in
            // reversed order — every per-vertex step above is order-independent across the mesh,
            // so only the ORDER the 3 indices of each triangle are visited changes.
            for (int t = 0; t + 2 < sub.Indices.Length; t += 3)
            {
                run.Indices.Add(indexBase + sub.Indices[t]);
                run.Indices.Add(indexBase + sub.Indices[t + 2]);
                run.Indices.Add(indexBase + sub.Indices[t + 1]);
            }
        }
        FlushRun();

        geo.RemappedInfluences = remapped;
        if (geo.Surfaces.Count > 0)
        {
            geo.BindPoseMin = bpMin;
            geo.BindPoseMax = bpMax;
        }
        else
        {
            geo.BindPoseMin = Vector3.Zero;
            geo.BindPoseMax = Vector3.Zero;
        }
        return geo;
    }

    /// <summary>Adds one of a vertex's up-to-4 bone influences, matching the real viewer's
    /// handling of malformed joint references (verified against
    /// scratch/slviewer/indra/newview/llskinningutil.cpp).
    ///
    /// Viewer parity, and the bug this used to have: an influence whose joint index is out of
    /// range is CLAMPED into range and KEPT — never dropped. `getPerVertexSkinMatrix` (:250) does
    /// `idx[k] = llclamp((S32) floorf(w), 0, max_joints-1)`, and `scrubSkinWeights` (:209-222)
    /// pre-clamps the stored weights the same way; likewise `scrubInvalidJoints` (:112-125)
    /// rewrites a joint NAME the avatar doesn't have to "mPelvis" and `initJointNums` (:314-315)
    /// falls back to joint num 0 — again remapping, never discarding. This method previously
    /// `return`ed early in both cases, silently discarding that influence. Because the caller then
    /// renormalizes the surviving weights (`wts[k] /= sum`), a vertex that should have been, say,
    /// 60% neck / 40% head became 100% head — snapping it to an unrelated bone while its
    /// neighbours stayed put. Whole triangles get stretched between the two, which reads as the
    /// mesh tearing into scattered flat shards even though its bind pose, textures, UVs and
    /// position are all correct. It also leaves the `orphanedVerts` counter at 0 (at least one
    /// influence survives per vertex), so the existing diagnostic could not see it.</summary>
    internal static void AddInfluence(int joint, float weight, int[] slotForJoint, int jointCount,
        int[] bones, float[] wts, ref int count, ref float sum, ref int remapped)
    {
        // !IsFinite first: NaN fails `weight <= 0f` and would be accumulated into the sum and the
        // weights handed to the engine; +Inf would turn the renormalising divide into inf/inf.
        if (count >= 4 || !float.IsFinite(weight) || weight <= 0f || jointCount <= 0) return;

        int j = joint;
        if (j < 0 || j >= jointCount)
        {
            j = Math.Clamp(j, 0, jointCount - 1);
            remapped++;
        }

        int slot = slotForJoint[j];
        if (slot < 0)
        {
            // The joint name resolved to no bone in OUR skeleton. Viewer: remap to mPelvis /
            // joint 0 rather than dropping. Slot 0 is this mesh's first successfully bound joint
            // — the nearest available analog to that fallback.
            slot = 0;
            remapped++;
        }

        bones[count] = slot;
        wts[count] = weight;
        sum += weight;
        count++;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>One surface in progress.</summary>
    private sealed class Run
    {
        public Run(int faceIndex, FaceTexture face)
        {
            FaceIndex = faceIndex;
            Face = face;
        }

        public int FaceIndex { get; }
        public FaceTexture Face { get; }
        public List<Vector3> Positions { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> UVs { get; } = new();
        public List<int> Bones { get; } = new();
        public List<float> Weights { get; } = new();
        public List<int> Indices { get; } = new();

        public RiggedSurface ToSurface() => new(
            FaceIndex, Positions.ToArray(), Normals.ToArray(), UVs.ToArray(),
            Bones.ToArray(), Weights.ToArray(), Indices.ToArray());
    }
}
