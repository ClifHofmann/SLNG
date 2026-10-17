using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>FEAT-PERF-23 Phase 0: the measurement probe that gated the outfit merge. Develop menu only;
/// nothing here changes what is drawn. Spec: docs/specs/FEAT-PERF-23-outfit-merge.md, "Phase 0".
///
/// <list type="number">
/// <item><b><c>[OutfitProbe]</c></b> (<see cref="LogOutfitProbe"/>): per full avatar, what its worn rigged
/// meshes cost on their own instances and what a per-linkset or per-avatar merge would leave. Counts
/// only; items the Phase 1 merge already holds have no instance and are reported apart.</item>
/// <item>The upload microbenchmark is <see cref="BenchSkinnedUpload"/>, run by <c>--selftest</c>.</item>
/// <item>Probe 3, the main-thread naive merge toggle, was retired with Phase 1: the real merge
/// (<c>AvatarRenderer.OutfitMerge.cs</c>, Graphics setting and <c>--outfit-merge</c>) is the A/B now.</item>
/// </list>
///
/// <para><b>No read-backs.</b> Everything comes from managed data and from scene-side getters that do
/// not reach the RenderingServer: the arrays retained in <see cref="MeshSurfaceCache"/>, the face index
/// each surface was built for (<see cref="RememberRigSurfaceFaces"/>), <c>ArrayMesh.SurfaceGetArrayLen</c>
/// (kept by the mesh resource), the surface override materials and their shader, and the binds held by
/// each <see cref="Skin"/> resource. No <c>SurfaceGetArrays</c>, <c>GetImage</c> or synchronous RS getter.</para>
/// </summary>
public partial class AvatarRenderer
{
    // ---- What CommitRiggedMeshInstance knew: the SL face behind every surface ---------------------

    /// <summary>The face index of each surface of a committed rigged mesh, as <c>RiggedMeshGeometry.FaceIndices</c>
    /// gave it. Weak like <see cref="MeshSurfaceCache"/>: it goes when the mesh goes.</summary>
    private static readonly ConditionalWeakTable<ArrayMesh, int[]> _rigSurfaceFaces = new();

    private static void RememberRigSurfaceFaces(ArrayMesh mesh, int[] faceIndices) =>
        _rigSurfaceFaces.AddOrUpdate(mesh, faceIndices);

    // ---- One rigged item as both probes see it ----------------------------------------------------

    private enum ProbeKind { Pending, Opaque, Scissor, Hash, Blend, Hidden }

    private sealed class ProbeSurface
    {
        public int Index;
        public ProbeKind Kind;
        public Material? Material;
        public OutfitMaterialKey Key;
        public float SortingOffset;
        public bool Split;
        public int Vertices;
    }

    private sealed class ProbeItem
    {
        public Guid EntityId;
        public long Linkset;
        public MeshInstance3D Mi = null!;
        public ArrayMesh Mesh = null!;
        public Godot.Collections.Array[]? Arrays;
        public bool FacesKnown;
        public bool Drawn;
        public bool Pending;
        public int SplitChildren;
        public readonly List<ProbeSurface> Surfaces = new();

        /// <summary>Has everything a merge needs: drawn, every material in, arrays and faces retained.</summary>
        public bool Mergeable => Drawn && !Pending && FacesKnown && Arrays != null && Arrays.Length == Surfaces.Count;
    }

    private static bool IsOutfitProbeCandidate(AvatarVisual visual) =>
        !visual.IsReduced && !visual.IsParked && !visual.IsControlAvatar
        && visual.Skeleton != null && IsInstanceValid(visual.Skeleton);

    /// <summary>Every rigged item in <see cref="_riggedAttachments"/>, grouped by the avatar wearing it.</summary>
    private Dictionary<Guid, List<(Guid Entity, MeshInstance3D Mi)>> RiggedItemsByWearer()
    {
        var map = new Dictionary<Guid, List<(Guid, MeshInstance3D)>>();
        foreach (var (entityId, mi) in _riggedAttachments)
        {
            if (!_attachmentToAvatar.TryGetValue(entityId, out var wearer)) continue;
            if (!map.TryGetValue(wearer, out var list)) map[wearer] = list = new();
            list.Add((entityId, mi));
        }
        return map;
    }

    private List<ProbeItem> CollectProbeItems(AvatarVisual visual, List<(Guid Entity, MeshInstance3D Mi)>? rigged)
    {
        var items = new List<ProbeItem>();
        if (rigged == null || visual.Skeleton == null) return items;
        foreach (var (entityId, mi) in rigged)
        {
            if (!IsInstanceValid(mi) || mi.IsQueuedForDeletion() || mi.Skin == null || mi.Mesh is not ArrayMesh mesh) continue;
            if (mi.GetParent() != visual.Skeleton) continue;

            var entity = _world?.GetEntity(entityId);
            var item = new ProbeItem
            {
                EntityId = entityId,
                Mi = mi,
                Mesh = mesh,
                // Without an entity (it left the world a moment ago) the item is a linkset of its own.
                Linkset = entity != null ? WornLinksetRoot(entity) : -1 - items.Count,
                Drawn = mi.Visible,
            };
            if (MeshSurfaceCache.TryGetSurfaces(mesh, out var arrays)) item.Arrays = arrays;
            item.FacesKnown = _rigSurfaceFaces.TryGetValue(mesh, out var faceIndices);
            bool haveFaces = _attachmentMeshIds.TryGetValue(entityId, out var meshInfo);

            int count = mesh.GetSurfaceCount();
            for (int s = 0; s < count; s++)
            {
                var split = mi.GetNodeOrNull<MeshInstance3D>(SortedSurfaceName(s));
                var material = split != null ? split.GetSurfaceOverrideMaterial(0) : mi.GetSurfaceOverrideMaterial(s);
                var kind = ProbeKindOf(material);
                if (kind == ProbeKind.Pending) item.Pending = true;
                if (split != null) item.SplitChildren++;

                int faceIndex = faceIndices != null && s < faceIndices.Length ? faceIndices[s] : -1;
                var face = haveFaces ? ResolveFaceTexture(meshInfo.Faces, meshInfo.DefaultFace, faceIndex) : default;
                float threshold = kind == ProbeKind.Scissor && material is ShaderMaterial sm
                    ? sm.GetShaderParameter(PrimShaderFamily.AlphaScissorThreshold).AsSingle()
                    : 0f;
                item.Surfaces.Add(new ProbeSurface
                {
                    Index = s,
                    Kind = kind,
                    Material = material,
                    Key = new OutfitMaterialKey(face, ResolvedFaceTexture(visual, face), ShaderKindOf(kind), threshold),
                    SortingOffset = split?.SortingOffset ?? mi.SortingOffset,
                    Split = split != null,
                    Vertices = mesh.SurfaceGetArrayLen(s),
                });
            }
            items.Add(item);
        }
        return items;
    }

    /// <summary>What a face samples: the bake for a Bakes-on-Mesh magic id, the id itself otherwise.</summary>
    private static Guid ResolvedFaceTexture(AvatarVisual visual, FaceTexture face)
    {
        if (SLNG.Assets.BakedTextureIds.TryGetBakeIndex(face.TextureId, out int bake))
            return visual.LoadedTextures.TryGetValue(bake, out var baked) ? baked : Guid.Empty;
        return face.TextureId;
    }

    private static ProbeKind ProbeKindOf(Material? material)
    {
        if (material == null) return ProbeKind.Pending;
        if (material is not ShaderMaterial sm || sm.Shader == null) return ProbeKind.Opaque;
        if (ReferenceEquals(sm.Shader, PrimShaderFamily.Hidden)) return ProbeKind.Hidden;
        var shader = PrimShaderFamily.BaseVariant(sm.Shader);
        const PrimShaderFamily.Surface Avatar = PrimShaderFamily.Surface.Avatar;
        if (ReferenceEquals(shader, PrimShaderFamily.Select(PrimShaderFamily.Kind.Blend, Avatar))) return ProbeKind.Blend;
        if (ReferenceEquals(shader, PrimShaderFamily.Select(PrimShaderFamily.Kind.Scissor, Avatar))) return ProbeKind.Scissor;
        if (ReferenceEquals(shader, PrimShaderFamily.Select(PrimShaderFamily.Kind.Hash, Avatar))) return ProbeKind.Hash;
        return ProbeKind.Opaque;
    }

    private static PrimShaderFamily.Kind ShaderKindOf(ProbeKind kind) => kind switch
    {
        ProbeKind.Scissor => PrimShaderFamily.Kind.Scissor,
        ProbeKind.Hash => PrimShaderFamily.Kind.Hash,
        ProbeKind.Blend => PrimShaderFamily.Kind.Blend,
        _ => PrimShaderFamily.Kind.Opaque,
    };

    // ---- Probe 1: [OutfitProbe] -------------------------------------------------------------------

    /// <summary>One avatar's (or the total's) counts.</summary>
    internal sealed class OutfitProbeTally
    {
        public int Avatars, Items, HiddenItems, Linksets, SplitChildren;
        public long Vertices;
        public int Opaque, Scissor, Blend, Hidden, StandIns, Pending;
        public int KeysPerLinkset, KeysPerAvatar, TexOnlyKeys, Buckets, MergedBlend;
        public int StagedItems, StagedInstances, StagedSurfaces, Uncached;
        public long Binds, SkinWrites, DistinctBinds, DistinctBindsApprox;

        public int InstancesNow => Items + SplitChildren;
        public int SurfacesNow => Opaque + Scissor + Blend + Hidden + StandIns + Pending;
        public int InstancesPerLinkset => Buckets + MergedBlend + StagedInstances;
        public int SurfacesPerLinkset => KeysPerLinkset + MergedBlend + StagedSurfaces;
        public int InstancesPerAvatar => (KeysPerAvatar > 0 ? 1 : 0) + MergedBlend + StagedInstances;
        public int SurfacesPerAvatar => KeysPerAvatar + MergedBlend + StagedSurfaces;

        public void Add(OutfitProbeTally t)
        {
            Avatars += t.Avatars; Items += t.Items; HiddenItems += t.HiddenItems; Linksets += t.Linksets;
            SplitChildren += t.SplitChildren; Vertices += t.Vertices;
            Opaque += t.Opaque; Scissor += t.Scissor; Blend += t.Blend; Hidden += t.Hidden;
            StandIns += t.StandIns; Pending += t.Pending;
            KeysPerLinkset += t.KeysPerLinkset; KeysPerAvatar += t.KeysPerAvatar; TexOnlyKeys += t.TexOnlyKeys;
            Buckets += t.Buckets; MergedBlend += t.MergedBlend;
            StagedItems += t.StagedItems; StagedInstances += t.StagedInstances; StagedSurfaces += t.StagedSurfaces;
            Uncached += t.Uncached;
            Binds += t.Binds; SkinWrites += t.SkinWrites; DistinctBinds += t.DistinctBinds; DistinctBindsApprox += t.DistinctBindsApprox;
        }

        public string Describe() =>
            $"items={Items} hiddenItems={HiddenItems} linksets={Linksets} verts={Vertices} | " +
            $"now: instances={InstancesNow} surfaces={SurfacesNow} opaque={Opaque} scissor={Scissor} blend={Blend} " +
            $"hidden={Hidden} standIn={StandIns} pending={Pending} | " +
            $"keys: perLinkset={KeysPerLinkset} perAvatar={KeysPerAvatar} texOnly={TexOnlyKeys} | " +
            $"binds: sum={Binds} skinWrites={SkinWrites} distinct={DistinctBinds} distinct1e-5={DistinctBindsApprox} | " +
            $"perLinkset: instances={InstancesPerLinkset} surfaces={SurfacesPerLinkset} | " +
            $"perAvatar: instances={InstancesPerAvatar} surfaces={SurfacesPerAvatar} | " +
            $"staged={StagedItems} (uncached={Uncached})";
    }

    /// <summary>Counts every full avatar's worn rigged items: the per-avatar tallies and the total over
    /// the non-self avatars (the merge's Phase 1 scope).</summary>
    private (List<(AvatarVisual Visual, OutfitProbeTally Tally)> PerAvatar, OutfitProbeTally Others) RunOutfitProbe()
    {
        var byWearer = RiggedItemsByWearer();
        var perAvatar = new List<(AvatarVisual, OutfitProbeTally)>();
        var others = new OutfitProbeTally();
        foreach (var (avatarId, visual) in _visuals)
        {
            if (!IsOutfitProbeCandidate(visual)) continue;
            byWearer.TryGetValue(avatarId, out var rigged);
            var tally = TallyOutfit(CollectProbeItems(visual, rigged));
            tally.Avatars = 1;
            perAvatar.Add((visual, tally));
            if (!visual.IsSelf) others.Add(tally);
        }
        return (perAvatar, others);
    }

    /// <summary>Develop menu "Outfit probe (log counts)": one <c>[OutfitProbe]</c> line per full avatar and
    /// a total over the others, to godot.log and slng-perf.log. Returns how many avatars it logged.</summary>
    internal int LogOutfitProbe()
    {
        var clock = Stopwatch.StartNew();
        var (perAvatar, others) = RunOutfitProbe();
        foreach (var (visual, tally) in perAvatar)
            EmitOutfitProbeLine($"[OutfitProbe] avatar={visual.EntityId.ToString("N")[..8]} self={(visual.IsSelf ? "yes" : "no")} {tally.Describe()}");
        int merged = 0;
        foreach (var item in _mergeItems.Values) if (item.InBucket) merged++;
        // Merged items (FEAT-PERF-23 Phase 1) have no instance of their own, so they are not counted above.
        string total = $"[OutfitProbe] total others avatars={others.Avatars} outfitMerge={(RenderConfig.OutfitMerge ? "on" : "off")} " +
                       $"mergedItems={merged} (not counted) {others.Describe()} ms={clock.Elapsed.TotalMilliseconds:F1}";
        EmitOutfitProbeLine(total);
        return perAvatar.Count;
    }

    private static void EmitOutfitProbeLine(string line)
    {
        PerfSidecar.Write(line);
        GD.Print(line);
    }

    private static OutfitProbeTally TallyOutfit(List<ProbeItem> items)
    {
        var t = new OutfitProbeTally();
        var linksets = new HashSet<long>();
        var keysPerLinkset = new Dictionary<long, HashSet<OutfitMaterialKey>>();
        var keysPerAvatar = new HashSet<OutfitMaterialKey>();
        var texOnly = new HashSet<(Guid, PrimShaderFamily.Kind, float)>();
        var skinsSeen = new HashSet<ulong>();
        var exact = new HashSet<(int, Transform3D)>();
        var approx = new HashSet<(int, ulong)>();

        foreach (var item in items)
        {
            if (!item.Drawn)
            {
                t.HiddenItems++;
                continue;
            }
            t.Items++;
            linksets.Add(item.Linkset);
            t.SplitChildren += item.SplitChildren;

            int itemSurfaces = 0;
            foreach (var surface in item.Surfaces)
            {
                t.Vertices += surface.Vertices;
                itemSurfaces++;
                if (surface.Split)
                {
                    // The parent still draws the moved surface, with the Hidden shader.
                    t.StandIns++;
                    itemSurfaces++;
                }
                switch (surface.Kind)
                {
                    case ProbeKind.Opaque: t.Opaque++; break;
                    case ProbeKind.Scissor:
                    case ProbeKind.Hash: t.Scissor++; break;
                    case ProbeKind.Blend: t.Blend++; break;
                    case ProbeKind.Hidden: t.Hidden++; break;
                    default: t.Pending++; break;
                }
            }

            if (!item.Mergeable)
            {
                // Staging, as Phase 1 would keep it: its instances and surfaces stay as they are.
                t.StagedItems++;
                t.StagedInstances += 1 + item.SplitChildren;
                t.StagedSurfaces += itemSurfaces;
                if (item.Arrays == null || item.Arrays.Length != item.Surfaces.Count || !item.FacesKnown) t.Uncached++;
            }
            else
            {
                foreach (var surface in item.Surfaces)
                {
                    if (surface.Kind == ProbeKind.Hidden) continue; // draws nothing, merges away
                    if (surface.Kind == ProbeKind.Blend)
                    {
                        t.MergedBlend++;
                        continue;
                    }
                    if (!keysPerLinkset.TryGetValue(item.Linkset, out var set)) keysPerLinkset[item.Linkset] = set = new();
                    set.Add(surface.Key);
                    keysPerAvatar.Add(surface.Key);
                    texOnly.Add((surface.Key.Texture, surface.Key.Kind, surface.Key.Threshold));
                }
            }

            // Binds: what each item carries, what the skeleton writes today (distinct Skin objects since
            // FEAT-PERF-24), and the distinct (bone, bind) pairs an outfit skin would hold. The bind is
            // C^-1 * IBM * C with this avatar's own scale (BindRiggedSkin), a function of (bone, IBM) alone,
            // so equal pairs here are the spec's (bone, IBM) keys.
            var skin = item.Mi.Skin!;
            int n = skin.GetBindCount();
            t.Binds += n;
            if (!skinsSeen.Add(skin.GetInstanceId())) continue;
            t.SkinWrites += n;
            for (int b = 0; b < n; b++)
            {
                int bone = skin.GetBindBone(b);
                var pose = skin.GetBindPose(b);
                exact.Add((bone, pose));
                approx.Add((bone, QuantizedBindHash(pose, 1e-5f)));
            }
        }

        t.Linksets = linksets.Count;
        foreach (var set in keysPerLinkset.Values) t.KeysPerLinkset += set.Count;
        t.Buckets = keysPerLinkset.Count;
        t.KeysPerAvatar = keysPerAvatar.Count;
        t.TexOnlyKeys = texOnly.Count;
        t.DistinctBinds = exact.Count;
        t.DistinctBindsApprox = approx.Count;
        return t;
    }

    /// <summary>A bind's twelve values rounded to <paramref name="step"/>, hashed (FNV-1a): the 1e-5
    /// tolerance count. Values straddling a rounding boundary count as different, so it is a slight
    /// upper bound on what a tolerance dedupe would leave.</summary>
    private static ulong QuantizedBindHash(Transform3D t, float step)
    {
        Span<float> v = stackalloc float[12]
        {
            t.Basis.Row0.X, t.Basis.Row0.Y, t.Basis.Row0.Z,
            t.Basis.Row1.X, t.Basis.Row1.Y, t.Basis.Row1.Z,
            t.Basis.Row2.X, t.Basis.Row2.Y, t.Basis.Row2.Z,
            t.Origin.X, t.Origin.Y, t.Origin.Z,
        };
        ulong h = 14695981039346656037UL;
        foreach (float f in v)
        {
            long q = (long)Math.Round(f / step);
            h = (h ^ (ulong)q) * 1099511628211UL;
        }
        return h;
    }

    // ---- Probe 2: upload microbenchmark (--selftest) ---------------------------------------------

    /// <summary>Main-thread time to pack and to hand one skinned surface of <paramref name="vertices"/>
    /// vertices to the engine (<c>AddSurfaceFromArrays</c>), median of <paramref name="repeats"/> runs
    /// after a warm-up. Headless this is the CPU part only (<c>mesh_create_surface_data_from_arrays</c>
    /// runs on the calling thread with any renderer); the GPU buffer creation of a real device is not in
    /// it. About two triangles per vertex, four influences, like a dense garment.</summary>
    internal static (double PackMs, double UploadMs) BenchSkinnedUpload(int vertices, int repeats = 5)
    {
        int w = 256;
        int h = Math.Max(2, (vertices + w - 1) / w);
        int verts = w * h;
        var pos = new Godot.Vector3[verts];
        var nrm = new Godot.Vector3[verts];
        var tan = new float[verts * 4];
        var uv = new Godot.Vector2[verts];
        var bones = new int[verts * 4];
        var weights = new float[verts * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = y * w + x;
                pos[v] = new Godot.Vector3(x * 0.01f, y * 0.01f, Mathf.Sin(x * 0.1f) * 0.02f);
                nrm[v] = Godot.Vector3.Back;
                tan[v * 4] = 1f; tan[v * 4 + 3] = 1f;
                uv[v] = new Godot.Vector2(x / (float)(w - 1), y / (float)(h - 1));
                for (int k = 0; k < 4; k++)
                {
                    bones[v * 4 + k] = (x / 8 + k * 7 + y / 16) % 60;
                    weights[v * 4 + k] = k == 0 ? 0.55f : 0.15f;
                }
            }
        }
        var idx = new int[(w - 1) * (h - 1) * 6];
        int i = 0;
        for (int y = 0; y < h - 1; y++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                int a = y * w + x, b = a + 1, c = a + w, d = c + 1;
                idx[i++] = a; idx[i++] = c; idx[i++] = b;
                idx[i++] = b; idx[i++] = c; idx[i++] = d;
            }
        }

        var packs = new double[repeats];
        var uploadsMs = new double[repeats];
        for (int r = -1; r < repeats; r++)
        {
            var t0 = Stopwatch.GetTimestamp();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = Variant.CreateFrom(pos);
            arrays[(int)Mesh.ArrayType.Normal] = Variant.CreateFrom(nrm);
            arrays[(int)Mesh.ArrayType.Tangent] = Variant.CreateFrom(tan);
            arrays[(int)Mesh.ArrayType.TexUV] = Variant.CreateFrom(uv);
            arrays[(int)Mesh.ArrayType.Bones] = Variant.CreateFrom(bones);
            arrays[(int)Mesh.ArrayType.Weights] = Variant.CreateFrom(weights);
            arrays[(int)Mesh.ArrayType.Index] = Variant.CreateFrom(idx);
            double pack = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            var mesh = new ArrayMesh();
            var t1 = Stopwatch.GetTimestamp();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            double upload = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
            mesh.Dispose();
            if (r < 0) continue; // warm-up
            packs[r] = pack;
            uploadsMs[r] = upload;
        }
        Array.Sort(packs);
        Array.Sort(uploadsMs);
        return (packs[repeats / 2], uploadsMs[repeats / 2]);
    }
}
