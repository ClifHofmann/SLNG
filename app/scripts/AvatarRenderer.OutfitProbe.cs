using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>FEAT-PERF-23 Phase 0: the measurement probe that gates the outfit merge. Develop menu only;
/// nothing here runs unless the person asks for it, and nothing that is drawn changes while the merge
/// toggle is off. Spec: docs/specs/FEAT-PERF-23-outfit-merge.md, "Phase 0".
///
/// <list type="number">
/// <item><b><c>[OutfitProbe]</c></b> (<see cref="LogOutfitProbe"/>): per full avatar, what its worn rigged
/// meshes cost today and what a per-linkset or per-avatar merge would leave. Counts only.</item>
/// <item>The upload microbenchmark is <see cref="BenchSkinnedUpload"/>, run by <c>--selftest</c>.</item>
/// <item><b>Outfit merge (probe)</b> (<see cref="ToggleOutfitMergeProbe"/>): merges every full non-self
/// avatar's rigged items for real, so <c>[Perf]</c> can be read with and without. It measures engine
/// cost, not looks: pending bakes, late materials, outfit and shape changes are not followed while it
/// is on.</item>
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

    // ---- State of the merge toggle --------------------------------------------------------------

    /// <summary>How the probe merges: off, one bucket per avatar, or one bucket per worn linkset (Phase 1's shape).</summary>
    internal enum OutfitMergeProbeMode { Off, PerAvatar, PerLinkset }

    private OutfitMergeProbeMode _outfitMergeProbe = OutfitMergeProbeMode.Off;

    /// <summary>The merge the toggle currently has applied.</summary>
    internal OutfitMergeProbeMode OutfitMergeProbe => _outfitMergeProbe;

    /// <summary>One avatar's applied merge: the originals it hid and the instances it built.</summary>
    private sealed class OutfitProbeMerge
    {
        public readonly List<MeshInstance3D> Originals = new();
        public readonly List<MeshInstance3D> Built = new();
    }

    private readonly Dictionary<AvatarVisual, OutfitProbeMerge> _outfitProbeMerges = new();

    /// <summary>Originals the merge hid, so the count probe still reads them as drawn.</summary>
    private readonly HashSet<MeshInstance3D> _outfitProbeHidden = new();

    private const string OutfitProbeMergedName = "OutfitProbeMerged";
    private const string OutfitProbeSortedName = "OutfitProbeSorted";

    // ---- One rigged item as both probes see it ----------------------------------------------------

    private enum ProbeKind { Pending, Opaque, Scissor, Hash, Blend, Hidden }

    /// <summary>The spec's material key: two faces with equal keys get identical materials.
    /// <see cref="FaceTexture"/> equality already means "identical material inputs" (BUG-RENDER-12); the
    /// resolved texture (the bake, for BoM), the Kind and the scissor threshold cover what is derived from
    /// the texture.</summary>
    private readonly record struct OutfitMaterialKey(FaceTexture Face, Guid Texture, PrimShaderFamily.Kind Kind, float Threshold);

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
                Drawn = mi.Visible || _outfitProbeHidden.Contains(mi),
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
        string total = $"[OutfitProbe] total others avatars={others.Avatars} merge={OutfitMergeModeName(_outfitMergeProbe)} {others.Describe()} " +
                       $"ms={clock.Elapsed.TotalMilliseconds:F1}";
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

    // ---- Probe 3: Outfit merge (probe) ------------------------------------------------------------

    internal static string OutfitMergeModeName(OutfitMergeProbeMode mode) => mode switch
    {
        OutfitMergeProbeMode.PerAvatar => "perAvatar",
        OutfitMergeProbeMode.PerLinkset => "perLinkset",
        _ => "off",
    };

    /// <summary>Develop menu "Outfit merge (probe)": turns <paramref name="mode"/> on, or off when it is
    /// the one already applied. Switching modes restores first. Returns the mode now applied and the
    /// line it logged.</summary>
    internal (OutfitMergeProbeMode Mode, string Line) ToggleOutfitMergeProbe(OutfitMergeProbeMode mode)
    {
        string? offLine = null;
        if (_outfitMergeProbe != OutfitMergeProbeMode.Off)
        {
            offLine = RestoreAllOutfitMergeProbe();
            if (mode == _outfitMergeProbe || mode == OutfitMergeProbeMode.Off)
            {
                _outfitMergeProbe = OutfitMergeProbeMode.Off;
                return (OutfitMergeProbeMode.Off, offLine);
            }
        }
        if (mode == OutfitMergeProbeMode.Off) return (OutfitMergeProbeMode.Off, offLine ?? "[OutfitMergeProbe] already off");
        _outfitMergeProbe = mode;
        return (mode, ApplyOutfitMergeProbe(mode));
    }

    /// <summary>Called where an avatar stops being full (reduce/park) or leaves: its originals come back
    /// before that path handles them, and the merged instances go.</summary>
    private void ReleaseOutfitMergeProbe(AvatarVisual visual)
    {
        if (_outfitProbeMerges.Count == 0 || !_outfitProbeMerges.Remove(visual, out var merge)) return;
        RestoreOutfitMerge(merge);
    }

    private string RestoreAllOutfitMergeProbe()
    {
        var clock = Stopwatch.StartNew();
        int avatars = 0, restored = 0, gone = 0, reshown = 0;
        foreach (var merge in _outfitProbeMerges.Values)
        {
            avatars++;
            var (r, g, s) = RestoreOutfitMerge(merge);
            restored += r; gone += g; reshown += s;
        }
        _outfitProbeMerges.Clear();
        _outfitProbeHidden.Clear();
        string line = $"[OutfitMergeProbe] off avatars={avatars} restored={restored} gone={gone} reshown={reshown} " +
                      $"ms={clock.Elapsed.TotalMilliseconds:F1}";
        EmitOutfitProbeLine(line);
        return line;
    }

    /// <summary>Frees the built instances and puts the originals back the way the unpark path does
    /// (skeleton path back, shown, then the regular all-hidden rule). <c>gone</c>: originals freed while
    /// merged (an outfit change). <c>reshown</c>: originals the regular path had already shown again
    /// (a late material), i.e. drawn twice for part of the window.</summary>
    private (int Restored, int Gone, int Reshown) RestoreOutfitMerge(OutfitProbeMerge merge)
    {
        foreach (var built in merge.Built)
        {
            if (!IsInstanceValid(built)) continue;
            // Out of the tree now, not at the end of the frame: its skin stops being written at once,
            // [AvatarCost] does not count it, and a re-merge in the same frame reuses its name.
            built.Skeleton = new NodePath();
            built.GetParent()?.RemoveChild(built);
            built.QueueFree();
        }
        int restored = 0, gone = 0, reshown = 0;
        foreach (var mi in merge.Originals)
        {
            _outfitProbeHidden.Remove(mi);
            if (!IsInstanceValid(mi) || mi.IsQueuedForDeletion())
            {
                gone++;
                continue;
            }
            if (mi.Visible) reshown++;
            ReattachSkeleton(mi);
            mi.Visible = true;
            UpdateWornMeshVisibility(mi);
            restored++;
        }
        return (restored, gone, reshown);
    }

    private sealed class MergeGroup
    {
        public long Bucket;
        public Material Material = null!;
        public readonly List<(Godot.Collections.Array Arrays, int[] Remap)> Parts = new();
    }

    private string ApplyOutfitMergeProbe(OutfitMergeProbeMode mode)
    {
        var clock = Stopwatch.StartNew();
        var byWearer = RiggedItemsByWearer();
        int avatars = 0, items = 0, instBefore = 0, instAfter = 0, surfBefore = 0, surfAfter = 0, uploads = 0;
        int uncached = 0, pending = 0, hiddenItems = 0, maxUploadVerts = 0;
        long bindsBefore = 0, slotsAfter = 0;
        double uploadMs = 0, maxUploadMs = 0, worstAvatarMs = 0;

        foreach (var (avatarId, visual) in _visuals)
        {
            if (visual.IsSelf || !IsOutfitProbeCandidate(visual)) continue;
            var avatarClock = Stopwatch.StartNew();
            byWearer.TryGetValue(avatarId, out var rigged);
            var eligible = new List<ProbeItem>();
            foreach (var item in CollectProbeItems(visual, rigged))
            {
                if (!item.Drawn) { hiddenItems++; continue; }
                if (item.Pending) { pending++; continue; }
                if (!item.Mergeable) { uncached++; continue; }
                eligible.Add(item);
            }
            if (eligible.Count == 0) continue;

            var skeleton = visual.Skeleton!;
            var outfitSkin = new Skin();
            var slotOf = new Dictionary<(int Bone, Transform3D Pose), int>();
            var remapOfSkin = new Dictionary<ulong, int[]>();
            var groups = new Dictionary<(long Bucket, OutfitMaterialKey Key), MergeGroup>();
            var groupOrder = new List<MergeGroup>();
            var sorted = new List<(ProbeSurface Surface, Godot.Collections.Array Arrays, int[] Remap)>();

            foreach (var item in eligible)
            {
                var skin = item.Mi.Skin!;
                bindsBefore += skin.GetBindCount();
                if (!remapOfSkin.TryGetValue(skin.GetInstanceId(), out var remap))
                {
                    remap = new int[skin.GetBindCount()];
                    for (int b = 0; b < remap.Length; b++)
                    {
                        var key = (skin.GetBindBone(b), skin.GetBindPose(b));
                        if (!slotOf.TryGetValue(key, out int slot))
                        {
                            slot = outfitSkin.GetBindCount();
                            outfitSkin.AddBind(key.Item1, key.Item2);
                            slotOf[key] = slot;
                        }
                        remap[b] = slot;
                    }
                    remapOfSkin[skin.GetInstanceId()] = remap;
                }

                instBefore += 1 + item.SplitChildren;
                long bucket = mode == OutfitMergeProbeMode.PerLinkset ? item.Linkset : 0;
                foreach (var surface in item.Surfaces)
                {
                    surfBefore += surface.Split ? 2 : 1;
                    if (surface.Kind == ProbeKind.Hidden) continue;
                    var arrays = item.Arrays![surface.Index];
                    if (surface.Kind == ProbeKind.Blend)
                    {
                        sorted.Add((surface, arrays, remap));
                        continue;
                    }
                    if (!groups.TryGetValue((bucket, surface.Key), out var group))
                    {
                        group = new MergeGroup { Bucket = bucket, Material = surface.Material! };
                        groups[(bucket, surface.Key)] = group;
                        groupOrder.Add(group);
                    }
                    group.Parts.Add((arrays, remap));
                }
            }

            var merge = new OutfitProbeMerge();
            var template = eligible[0].Mi;
            var box = template.CustomAabb;

            // Buckets: one per avatar, or one per linkset, each with one surface per material key.
            var buckets = new Dictionary<long, (ArrayMesh Mesh, List<Material> Materials)>();
            var bucketOrder = new List<long>();
            foreach (var group in groupOrder)
            {
                if (!buckets.TryGetValue(group.Bucket, out var b))
                {
                    b = (new ArrayMesh(), new List<Material>());
                    buckets[group.Bucket] = b;
                    bucketOrder.Add(group.Bucket);
                }
                var arrays = ConcatSkinnedSurfaces(group.Parts, out int verts);
                var t0 = Stopwatch.GetTimestamp();
                b.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                uploads++; uploadMs += ms;
                if (ms > maxUploadMs) { maxUploadMs = ms; maxUploadVerts = verts; }
                b.Materials.Add(group.Material);
                if (_outfitProbeKeepArrays) _outfitProbeArrays.AddOrUpdate(b.Mesh, Append(_outfitProbeArrays, b.Mesh, arrays));
            }
            int bucketIndex = 0;
            foreach (long key in bucketOrder)
            {
                var (mesh, materials) = buckets[key];
                var mi = new MeshInstance3D
                {
                    Name = mode == OutfitMergeProbeMode.PerLinkset ? $"{OutfitProbeMergedName}{bucketIndex}" : OutfitProbeMergedName,
                    Mesh = mesh,
                    Skin = outfitSkin,
                    Layers = template.Layers,
                    CastShadow = template.CastShadow,
                    CustomAabb = box,
                };
                for (int s = 0; s < materials.Count; s++) mi.SetSurfaceOverrideMaterial(s, materials[s]);
                skeleton.AddChild(mi);
                mi.Skeleton = mi.GetPathTo(skeleton);
                merge.Built.Add(mi);
                bucketIndex++;
                instAfter++;
                surfAfter += materials.Count;
            }

            // Blend surfaces: one sorted instance each, on the outfit skin, at today's exact offset.
            for (int i = 0; i < sorted.Count; i++)
            {
                var (surface, arrays, remap) = sorted[i];
                var one = ConcatSkinnedSurfaces(new List<(Godot.Collections.Array, int[])> { (arrays, remap) }, out int verts);
                var mesh = new ArrayMesh();
                var t0 = Stopwatch.GetTimestamp();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, one);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                uploads++; uploadMs += ms;
                if (ms > maxUploadMs) { maxUploadMs = ms; maxUploadVerts = verts; }
                if (_outfitProbeKeepArrays) _outfitProbeArrays.AddOrUpdate(mesh, new[] { one });
                var mi = new MeshInstance3D
                {
                    Name = $"{OutfitProbeSortedName}{i}",
                    Mesh = mesh,
                    Skin = outfitSkin,
                    Layers = template.Layers,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    SortingOffset = surface.SortingOffset,
                    CustomAabb = box,
                };
                mi.SetSurfaceOverrideMaterial(0, surface.Material);
                skeleton.AddChild(mi);
                mi.Skeleton = mi.GetPathTo(skeleton);
                merge.Built.Add(mi);
                instAfter++;
                surfAfter++;
            }

            // Only now: the originals go, in the same frame the merge comes in.
            foreach (var item in eligible)
            {
                DetachSkeleton(item.Mi);
                item.Mi.Visible = false;
                merge.Originals.Add(item.Mi);
                _outfitProbeHidden.Add(item.Mi);
            }
            _outfitProbeMerges[visual] = merge;
            avatars++;
            items += eligible.Count;
            slotsAfter += outfitSkin.GetBindCount();
            worstAvatarMs = Math.Max(worstAvatarMs, avatarClock.Elapsed.TotalMilliseconds);
        }

        string line = $"[OutfitMergeProbe] on mode={OutfitMergeModeName(mode)} avatars={avatars} items={items} " +
                      $"instances {instBefore} -> {instAfter} surfaces {surfBefore} -> {surfAfter} " +
                      $"binds {bindsBefore} -> slots {slotsAfter} " +
                      $"skipped: pending={pending} uncached={uncached} hiddenItems={hiddenItems} | " +
                      $"upload: surfaces={uploads} totalMs={uploadMs:F1} maxMs={maxUploadMs:F2} ({maxUploadVerts} verts) | " +
                      $"ms={clock.Elapsed.TotalMilliseconds:F1} worstAvatarMs={worstAvatarMs:F1}";
        EmitOutfitProbeLine(line);
        return line;
    }

    /// <summary>Self test only: keep the arrays each built mesh was made from, so the test can check the
    /// bone remap without reading the mesh back. Off in the client, where nothing reads a probe bucket
    /// back and the copy would only double its memory.</summary>
    private static bool _outfitProbeKeepArrays;

    private static readonly ConditionalWeakTable<ArrayMesh, Godot.Collections.Array[]> _outfitProbeArrays = new();

    private static Godot.Collections.Array[] Append(ConditionalWeakTable<ArrayMesh, Godot.Collections.Array[]> table,
        ArrayMesh mesh, Godot.Collections.Array arrays)
    {
        if (!table.TryGetValue(mesh, out var existing)) return new[] { arrays };
        var grown = new Godot.Collections.Array[existing.Length + 1];
        existing.CopyTo(grown, 0);
        grown[^1] = arrays;
        return grown;
    }

    /// <summary>Concatenates skinned surfaces into one: positions, normals, tangents, UVs and weights
    /// as they are, bone slots through each part's item-to-outfit table, indices offset. A stream a
    /// part lacks is filled with the neutral value, so the result is always consistent.</summary>
    private static Godot.Collections.Array ConcatSkinnedSurfaces(List<(Godot.Collections.Array Arrays, int[] Remap)> parts, out int vertexCount)
    {
        var p = new Godot.Vector3[parts.Count][];
        var n = new Godot.Vector3[parts.Count][];
        var tg = new float[parts.Count][];
        var uv = new Godot.Vector2[parts.Count][];
        var bo = new int[parts.Count][];
        var we = new float[parts.Count][];
        var ix = new int[parts.Count][];
        int verts = 0, indices = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var a = parts[i].Arrays;
            p[i] = Read(a, Mesh.ArrayType.Vertex).AsVector3Array();
            n[i] = Read(a, Mesh.ArrayType.Normal).AsVector3Array();
            tg[i] = Read(a, Mesh.ArrayType.Tangent).AsFloat32Array();
            uv[i] = Read(a, Mesh.ArrayType.TexUV).AsVector2Array();
            bo[i] = Read(a, Mesh.ArrayType.Bones).AsInt32Array();
            we[i] = Read(a, Mesh.ArrayType.Weights).AsFloat32Array();
            var index = Read(a, Mesh.ArrayType.Index);
            ix[i] = index.VariantType == Variant.Type.Nil ? Sequence(p[i].Length) : index.AsInt32Array();
            verts += p[i].Length;
            indices += ix[i].Length;
        }

        var pos = new Godot.Vector3[verts];
        var nrm = new Godot.Vector3[verts];
        var tan = new float[verts * 4];
        var tex = new Godot.Vector2[verts];
        var bones = new int[verts * 4];
        var weights = new float[verts * 4];
        var idx = new int[indices];
        int vOff = 0, iOff = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            int count = p[i].Length;
            var remap = parts[i].Remap;
            int fallback = remap.Length > 0 ? remap[0] : 0;
            p[i].CopyTo(pos, vOff);
            if (n[i].Length == count) n[i].CopyTo(nrm, vOff);
            else for (int v = 0; v < count; v++) nrm[vOff + v] = Godot.Vector3.Up;
            if (tg[i].Length == count * 4) tg[i].CopyTo(tan, vOff * 4);
            else for (int v = 0; v < count; v++) { tan[(vOff + v) * 4] = 1f; tan[(vOff + v) * 4 + 3] = 1f; }
            if (uv[i].Length == count) uv[i].CopyTo(tex, vOff);
            if (bo[i].Length == count * 4 && we[i].Length == count * 4)
            {
                for (int k = 0; k < count * 4; k++)
                {
                    int slot = bo[i][k];
                    bones[vOff * 4 + k] = slot >= 0 && slot < remap.Length ? remap[slot] : fallback;
                    weights[vOff * 4 + k] = we[i][k];
                }
            }
            else
            {
                for (int v = 0; v < count; v++)
                {
                    bones[(vOff + v) * 4] = fallback;
                    weights[(vOff + v) * 4] = 1f;
                }
            }
            for (int k = 0; k < ix[i].Length; k++) idx[iOff + k] = ix[i][k] + vOff;
            vOff += count;
            iOff += ix[i].Length;
        }

        var result = new Godot.Collections.Array();
        result.Resize((int)Mesh.ArrayType.Max);
        result[(int)Mesh.ArrayType.Vertex] = Variant.CreateFrom(pos);
        result[(int)Mesh.ArrayType.Normal] = Variant.CreateFrom(nrm);
        result[(int)Mesh.ArrayType.Tangent] = Variant.CreateFrom(tan);
        result[(int)Mesh.ArrayType.TexUV] = Variant.CreateFrom(tex);
        result[(int)Mesh.ArrayType.Bones] = Variant.CreateFrom(bones);
        result[(int)Mesh.ArrayType.Weights] = Variant.CreateFrom(weights);
        result[(int)Mesh.ArrayType.Index] = Variant.CreateFrom(idx);
        vertexCount = verts;
        return result;

        static Variant Read(Godot.Collections.Array a, Mesh.ArrayType type) =>
            (int)type < a.Count ? a[(int)type] : default;

        static int[] Sequence(int count)
        {
            var s = new int[count];
            for (int k = 0; k < count; k++) s[k] = k;
            return s;
        }
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
