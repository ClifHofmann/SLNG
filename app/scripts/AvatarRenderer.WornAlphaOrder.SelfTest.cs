using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// BUG-RENDER-50: "worn rigged alpha in the viewer's order, with depth". A remote avatar wears, through
    /// the real commit path and in this order, a bikini bottom at attachment point 23 (two blended faces), a
    /// mesh body wearing the bake (point 10), and a two-prim add-on at point 28 whose prims carry one and two
    /// blended faces -- the case it was found on, where the add-on's strips lie behind the bikini and the old
    /// LocalId tie-break drew them after it. Then:
    ///
    /// <list type="number">
    /// <item>Ranks: the bake wearer first, every add-on prim before the bikini.</item>
    /// <item>Every blended surface is on an instance of its own (also the single one) at its surface slot;
    /// every add-on surface sorts before every bikini surface, so the bikini draws over the strips.</item>
    /// <item>Depth: the parent's copy of each moved surface is the avatar depth pass (depth_draw_always,
    /// cull_disabled, the configured threshold, the colour's texture and placement) at its item's depth slot,
    /// after the item's own surfaces and before the next item's -- so what the add-on writes is in the
    /// buffer before the bikini is drawn, and anything drawn after it that lies behind it is rejected.</item>
    /// <item>Merged (FEAT-PERF-23): each sorted instance sits at the same offset with a depth pass that
    /// shares its mesh and skin at the item's depth slot.</item>
    /// <item>Taking an add-on prim off re-ranks: the bikini moves up, staged and merged nodes with it.</item>
    /// <item>A legacy material's alpha mode decides a worn face: NONE opaque, MASK at its cutoff, BLEND
    /// blended, a translucent face colour blends; with the pass off the parent's copies draw nothing.</item>
    /// </list>
    /// No pixels: the headless smoke test has no rasterizer, so occlusion itself is asserted through the
    /// order and the depth state that produce it.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestWornAlphaOrder()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        const ulong Region = 11437119954698752UL;
        var problems = new List<string>();
        var priorWorld = _world;
        var priorCache = _gpuCache;
        bool priorSetting = RenderConfig.OutfitMerge;
        bool priorApplied = _outfitMergeApplied;
        float priorDepth = RenderConfig.WornAlphaDepth;
        var priorLegacy = LegacyMaterialsForSelfTest;
        var world = new World();
        _world = world;
        _gpuCache = new GpuCache(64L << 20);
        AvatarVisual? visual = null;
        var worn = new List<Guid>();

        try
        {
            RenderConfig.OutfitMerge = true;
            _outfitMergeApplied = true;
            RenderConfig.WornAlphaDepth = 0.33f;

            var wearer = world.GetOrCreateEntity(Region, 1);
            wearer.SetComponent(new AvatarComponent(Guid.NewGuid(), "Alpha", "Order", false));
            var vis = NewSelfTestWearer(wearer.Id, wearer.GetComponent<AvatarComponent>()!.AgentId);
            visual = vis;

            Guid Worn(uint localId, uint parentLocalId, byte point)
            {
                var e = world.GetOrCreateEntity(Region, localId);
                e.SetComponent(new TransformComponent { ParentLocalId = parentLocalId });
                e.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0));
                e.SetComponent(new AttachmentComponent(wearer.Id, point));
                worn.Add(e.Id);
                return e.Id;
            }
            FaceTexture Face(Guid tex, float alpha) =>
                new(tex, Guid.Empty, Guid.Empty, new System.Numerics.Vector4(1f, 1f, 1f, alpha), 1f, 1f, 0f, 0f, 0f);
            var ibmTorso = System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -1.2f);
            MeshData Mesh(int faces, float x) =>
                TestMesh(new[] { "mPelvis", "mTorso" }, new[] { System.Numerics.Matrix4x4.Identity, ibmTorso }, faces, x);

            // The log's local ids: the old tie-break put the bikini at 0.0328 and the add-on at 0.0394+.
            const uint bikiniId = 256u * 4821 + 28, addonRootId = 256u * 4823 + 94, bodyId = 256u * 4700 + 200;
            var bikini = Worn(bikiniId, 1, 23);
            var body = Worn(bodyId, 1, 10);
            var addonRoot = Worn(addonRootId, 1, 28);
            var addonChild = Worn(addonRootId + 18, addonRootId, 28);
            var veil = Worn(addonRootId + 40, 1, 28); // every face hidden: draws nothing at all
            var lowerBake = new Guid("24daea5f-0539-cfcf-047f-fbc40b2786ba"); // IMG_USE_BAKED_LOWER

            CommitSelfTestRig(vis, bikini, Mesh(2, 0f), new[] { Face(Guid.NewGuid(), 0.5f), Face(Guid.NewGuid(), 0.6f) }, keepTangents: true);
            CommitSelfTestRig(vis, body, Mesh(1, -0.2f), new[] { Face(lowerBake, 1f) }, keepTangents: true);
            CommitSelfTestRig(vis, addonRoot, Mesh(2, 0.1f), new[] { Face(Guid.NewGuid(), 1f), Face(Guid.NewGuid(), 0.5f) }, keepTangents: true);
            CommitSelfTestRig(vis, addonChild, Mesh(2, 0.15f), new[] { Face(Guid.NewGuid(), 0.5f), Face(Guid.NewGuid(), 0.5f) }, keepTangents: true);
            CommitSelfTestRig(vis, veil, Mesh(2, 0.2f), new[] { Face(Guid.NewGuid(), 0f), Face(Guid.NewGuid(), 0f) }, keepTangents: true);
            DrainQueue();
            TickWornAlphaOrder();
            // v0.27.92: every surface this builds (1, 2 and all-hidden blended faces) is indexed triangles.
            CheckIndexedSurfaces(vis, problems, "staged");
            if (_riggedAttachments.TryGetValue(veil, out var veilMi) && IsInstanceValid(veilMi) && veilMi.Visible)
                problems.Add("the all-hidden item is drawn");

            // --- 1. ranks ------------------------------------------------------------------------------
            float Base(Guid id) => WornAlphaBaseOf(vis, id, float.NaN);
            if (Base(body) != 0f) problems.Add($"the bake wearer is not first (base {Base(body)})");
            foreach (var part in new[] { addonRoot, addonChild })
                if (!(Base(part) < Base(bikini))) problems.Add($"add-on prim {MergeShort(part)} (base {Base(part):0.0000}) does not rank before the bikini ({Base(bikini):0.0000})");

            // --- 2 and 3. staged: split children and the parent's depth copies --------------------------------
            var staged = new Dictionary<Guid, List<(int Surface, float Color, float Depth, Material? Pass, Material? ColorMaterial)>>();
            foreach (var id in new[] { bikini, addonRoot, addonChild })
            {
                if (!_riggedAttachments.TryGetValue(id, out var mi) || mi.Mesh is not ArrayMesh mesh)
                {
                    problems.Add($"{MergeShort(id)} has no instance of its own after its commit");
                    continue;
                }
                var list = staged[id] = new();
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    var split = mi.GetNodeOrNull<MeshInstance3D>(SortedSurfaceName(s));
                    var own = split?.GetSurfaceOverrideMaterial(0) ?? mi.GetSurfaceOverrideMaterial(s);
                    bool blended = own is ShaderMaterial sm && ReferenceEquals(sm.Shader, PrimShaderFamily.Select(PrimShaderFamily.Kind.Blend, PrimShaderFamily.Surface.Avatar));
                    if (!blended) continue;
                    if (split == null)
                    {
                        problems.Add($"blended surface {s} of {MergeShort(id)} was not moved onto an instance of its own");
                        continue;
                    }
                    list.Add((s, split.SortingOffset, mi.SortingOffset, mi.GetSurfaceOverrideMaterial(s), own));
                }
                if (list.Count == 0) problems.Add($"{MergeShort(id)} has no blended surface to check");
                if (mi.SortingOffset != WornAlphaOrder.DepthOffset(Base(id))) problems.Add($"{MergeShort(id)}'s instance is not at its depth slot");
            }
            float addonLast = staged.Where(kv => kv.Key != bikini).SelectMany(kv => kv.Value).Select(v => Math.Max(v.Color, v.Depth)).DefaultIfEmpty(float.NaN).Max();
            float bikiniFirst = staged.GetValueOrDefault(bikini)?.Select(v => v.Color).DefaultIfEmpty(float.NaN).Min() ?? float.NaN;
            if (!(addonLast < bikiniFirst))
                problems.Add($"the add-on's last surface or depth pass ({addonLast:0.00000}) does not come before the bikini's first surface ({bikiniFirst:0.00000})");
            foreach (var (id, list) in staged)
            {
                foreach (var (s, color, depth, pass, colorMaterial) in list)
                {
                    if (color != WornAlphaOrder.SurfaceOffset(Base(id), s)) problems.Add($"surface {s} of {MergeShort(id)} is not at its surface slot");
                    if (!(depth > color)) problems.Add($"{MergeShort(id)}'s depth pass does not come after its surface {s}");
                    CheckDepthPass(pass, colorMaterial, $"staged {MergeShort(id)} surface {s}");
                }
            }
            var depthCode = PrimShaderFamily.DepthCoreAvatar.Code;
            if (!depthCode.Contains("depth_draw_always", StringComparison.Ordinal) || !depthCode.Contains("cull_disabled", StringComparison.Ordinal))
                problems.Add("the avatar depth pass is not depth_draw_always + cull_disabled");

            // --- 4. merged ------------------------------------------------------------------------------
            var mergedIds = new[] { bikini, addonRoot, addonChild };
            if (!WaitMerged(vis, () => mergedIds.All(id => _mergeItems.TryGetValue(id, out var it) && it.InBucket)))
                problems.Add("the outfit did not merge within 10 s");
            var sortedNodes = CheckMergedOrder(vis, mergedIds, problems, "merged");
            CheckIndexedSurfaces(vis, problems, "merged");
            float addonMergedLast = sortedNodes.Where(n => n.Item != bikini).Select(n => n.Depth).DefaultIfEmpty(float.NaN).Max();
            float bikiniMergedFirst = sortedNodes.Where(n => n.Item == bikini).Select(n => n.Color).DefaultIfEmpty(float.NaN).Min();
            if (!(addonMergedLast < bikiniMergedFirst))
                problems.Add($"merged: the add-on's depth passes ({addonMergedLast:0.00000}) do not come before the bikini ({bikiniMergedFirst:0.00000})");

            // --- 5. an add-on prim taken off: the bikini moves up ---------------------------------------------
            float bikiniBefore = Base(bikini);
            RemoveVisual(addonChild);
            if (!WaitMerged(vis, () => vis.OutfitMerge?.Buckets.Values.All(b => !b.Members.Contains(addonChild)) ?? true))
                problems.Add("taking the add-on prim off did not re-merge within 10 s");
            TickWornAlphaOrder();
            if (!(Base(bikini) < bikiniBefore)) problems.Add($"the bikini did not move up after an add-on prim left ({bikiniBefore:0.0000} -> {Base(bikini):0.0000})");
            CheckMergedOrder(vis, new[] { bikini, addonRoot }, problems, "after the detach");

            // --- 6. legacy alpha modes and the switch ---------------------------------------------------------
            CheckLegacyAlphaModes(problems);
            RenderConfig.WornAlphaDepth = 0f;
            var anyBlend = new ShaderMaterial { Shader = PrimShaderFamily.Select(PrimShaderFamily.Kind.Blend, PrimShaderFamily.Surface.Avatar) };
            if (WornDepthMaterialFor(anyBlend) != null) problems.Add("--worn-alpha-depth=0 still makes a depth pass");
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.OutfitMerge = false;
            WaitRigWorkersIdle();
            DrainQueue();
            if (visual != null)
            {
                UnloadOutfitMerge(visual);
                ReleaseSelfTestWearer(visual, worn);
            }
            _mergeItems.Clear();
            _mergeHeld.Clear();
            _mergeTouched.Clear();
            _wornAlphaDirty.Clear();
            RenderConfig.OutfitMerge = priorSetting;
            _outfitMergeApplied = priorApplied;
            RenderConfig.WornAlphaDepth = priorDepth;
            LegacyMaterialsForSelfTest = priorLegacy;
            _world = priorWorld;
            _gpuCache = priorCache;
        }

        return problems.Count == 0
            ? (true, "bake wearer first, point 28 before point 23 (staged and merged), every blended surface on its own instance, " +
                     "a cull_disabled depth_draw_always pass per item between its surfaces and the next item, re-ranked on detach; " +
                     "legacy NONE/MASK/BLEND honoured on worn faces")
            : (false, string.Join("; ", problems.Distinct()));

        void CheckDepthPass(Material? pass, Material? color, string what)
        {
            if (pass is not ShaderMaterial depth || !ReferenceEquals(depth.Shader, PrimShaderFamily.DepthCoreAvatar))
            {
                problems.Add($"{what}: no avatar depth pass");
                return;
            }
            if (MathF.Abs((float)depth.GetShaderParameter(PrimShaderFamily.CoreAlphaThreshold) - RenderConfig.WornAlphaDepth) > 1e-6f)
                problems.Add($"{what}: depth pass threshold is not the configured {RenderConfig.WornAlphaDepth}");
            if (color is ShaderMaterial c)
            {
                foreach (var name in new[] { PrimShaderFamily.AlbedoColor, PrimShaderFamily.UvScale, PrimShaderFamily.UvOffset, PrimShaderFamily.UvRotation, PrimShaderFamily.AlbedoTexture })
                    if (!Equals(depth.GetShaderParameter(name).Obj, c.GetShaderParameter(name).Obj))
                        problems.Add($"{what}: depth pass {name} differs from the colour pass");
                if (depth.RenderPriority != c.RenderPriority) problems.Add($"{what}: depth pass render priority differs from the colour pass");
            }
        }
    }

    /// <summary>v0.27.92: every mesh drawn under the wearer's skeleton -- own instances, split children, depth
    /// passes, buckets and sorted instances -- has surfaces of indexed TRIANGLES: a non-empty index array whose
    /// length is a multiple of 3 over a non-empty vertex array. A surface with vertices and no indices is drawn
    /// non-indexed, and Godot refuses any whose vertex count is not a multiple of 3 ("Vertex amount (N) must be a
    /// multiple of ... (3)", rendering_device.cpp). Read from the ArrayMesh resource, so it works headless.</summary>
    private static void CheckIndexedSurfaces(AvatarVisual visual, List<string> problems, string when)
    {
        if (visual.Skeleton == null) return;
        int surfaces = 0;
        Walk(visual.Skeleton);
        if (surfaces == 0) problems.Add($"{when}: no surface to check");

        void Walk(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is MeshInstance3D mi && mi.Mesh is ArrayMesh mesh && !mi.IsQueuedForDeletion())
                {
                    for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    {
                        surfaces++;
                        int vertices = mesh.SurfaceGetArrayLen(s);
                        int indices = mesh.SurfaceGetArrayIndexLen(s);
                        if (mesh.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles || vertices <= 0 || indices <= 0 || indices % 3 != 0)
                            problems.Add($"{when}: {mi.Name} surface {s} is {vertices} vertices / {indices} indices ({mesh.SurfaceGetPrimitiveType(s)})");
                    }
                }
                Walk(child);
            }
        }
    }

    /// <summary>Every sorted instance of <paramref name="items"/> sits at its surface slot with a depth pass at its
    /// item's depth slot, sharing its mesh and skin; returns them as (item, colour offset, depth offset).</summary>
    private List<(Guid Item, float Color, float Depth)> CheckMergedOrder(AvatarVisual visual, Guid[] items, List<string> problems, string when)
    {
        var found = new List<(Guid, float, float)>();
        var state = visual.OutfitMerge;
        if (state == null)
        {
            problems.Add($"{when}: no merge state");
            return found;
        }
        foreach (var bucket in state.Buckets.Values)
        {
            foreach (var ((item, surface), (node, _)) in bucket.DrawnAt)
            {
                if (!items.Contains(item) || !bucket.Sorted.Contains(node)) continue;
                float b = WornAlphaBaseOf(visual, item, float.NaN);
                if (node.SortingOffset != WornAlphaOrder.SurfaceOffset(b, surface))
                    problems.Add($"{when}: sorted surface {surface} of {MergeShort(item)} sits at {node.SortingOffset:0.00000}, its slot is {WornAlphaOrder.SurfaceOffset(b, surface):0.00000}");
                var depth = node.GetNodeOrNull<MeshInstance3D>(WornDepthName);
                if (depth == null)
                {
                    problems.Add($"{when}: sorted surface {surface} of {MergeShort(item)} has no depth pass");
                    continue;
                }
                if (depth.SortingOffset != WornAlphaOrder.DepthOffset(b)) problems.Add($"{when}: {MergeShort(item)}'s merged depth pass is not at its depth slot");
                if (!ReferenceEquals(depth.Mesh, node.Mesh) || !ReferenceEquals(depth.Skin, node.Skin) || depth.Skeleton.IsEmpty)
                    problems.Add($"{when}: {MergeShort(item)}'s merged depth pass does not share its instance's mesh and skin");
                if (depth.GetSurfaceOverrideMaterial(0) is not ShaderMaterial dm || !ReferenceEquals(dm.Shader, PrimShaderFamily.DepthCoreAvatar))
                    problems.Add($"{when}: {MergeShort(item)}'s merged depth pass wears no depth material");
                if (depth.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) problems.Add($"{when}: a depth pass casts a shadow");
                found.Add((item, node.SortingOffset, depth.SortingOffset));
            }
        }
        foreach (var item in items)
            if (!found.Any(f => f.Item1 == item)) problems.Add($"{when}: {MergeShort(item)} has no sorted instance");
        return found;
    }

    /// <summary>A legacy material's diffuse alpha mode on a worn face (no asset service: the materials are
    /// served from <see cref="LegacyMaterialsForSelfTest"/>).</summary>
    private void CheckLegacyAlphaModes(List<string> problems)
    {
        LegacyMaterialData Legacy(LegacyDiffuseAlphaMode mode, byte cutoff) => new(Guid.NewGuid(), Guid.Empty,
            System.Numerics.Vector2.Zero, System.Numerics.Vector2.One, 0f, Guid.Empty, System.Numerics.Vector2.Zero,
            System.Numerics.Vector2.One, 0f, System.Numerics.Vector4.One, 51, 0, cutoff, mode);
        var none = Legacy(LegacyDiffuseAlphaMode.None, 0);
        var mask = Legacy(LegacyDiffuseAlphaMode.Mask, 128);
        var blend = Legacy(LegacyDiffuseAlphaMode.Blend, 0);
        var fallback = Legacy(LegacyDiffuseAlphaMode.Default, 0);
        LegacyMaterialsForSelfTest = new Dictionary<Guid, LegacyMaterialData>
        {
            [none.Id] = none, [mask.Id] = mask, [blend.Id] = blend, [fallback.Id] = fallback,
        };
        FaceTexture Face(Guid legacy, float alpha) =>
            new(Guid.NewGuid(), Guid.Empty, legacy, new System.Numerics.Vector4(1f, 1f, 1f, alpha), 1f, 1f, 0f, 0f, 0f);
        (PrimShaderFamily.Kind Kind, float Threshold) Built(FaceTexture face)
        {
            var material = BuildFaceMaterialAsync(face, null, Guid.NewGuid(), 0).GetAwaiter().GetResult();
            var facts = FactsOf(material);
            return (facts.Kind, facts.Threshold);
        }
        if (Built(Face(none.Id, 1f)).Kind != PrimShaderFamily.Kind.Opaque) problems.Add("legacy NONE is not opaque");
        var masked = Built(Face(mask.Id, 1f));
        if (masked.Kind != PrimShaderFamily.Kind.Scissor || MathF.Abs(masked.Threshold - 128f / 255f) > 1e-5f)
            problems.Add($"legacy MASK 128 is {masked.Kind} @ {masked.Threshold}, expected Scissor @ {128f / 255f}");
        if (Built(Face(blend.Id, 1f)).Kind != PrimShaderFamily.Kind.Blend) problems.Add("legacy BLEND is not blended");
        if (Built(Face(none.Id, 0.5f)).Kind != PrimShaderFamily.Kind.Blend) problems.Add("legacy NONE with a translucent face colour is not blended");
        if (Built(Face(fallback.Id, 1f)).Kind != PrimShaderFamily.Kind.Opaque) problems.Add("legacy DEFAULT (no texture) is not left as before");
    }
}
