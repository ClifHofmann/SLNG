using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-50: worn rigged alpha in the reference viewer's order, writing depth like the viewer's
/// rigged-alpha pass. Spec: docs/specs/BUG-RENDER-50-worn-alpha-draw-order.md.
///
/// <para><b>Order.</b> Every worn rigged instance of an avatar is a child of its skeleton with the same
/// box, so Godot's transparent sort sees one depth for all of them and decides by
/// <see cref="GeometryInstance3D.SortingOffset"/> alone. Each item gets a rank from
/// <see cref="WornAlphaOrder"/> (bake wearers, then the HIGHEST attachment point first, then the newer
/// linkset first -- llvoavatar.cpp:3046-3112, llspatialpartition.h:240-250) and owns that rank's slot:
/// its blended surfaces in face order, then its depth pass. Ranks are recomputed when an item is rigged
/// or taken off, or changes its point.</para>
///
/// <para><b>Depth.</b> The viewer draws rigged alpha in a first pass that writes depth
/// (lldrawpoolalpha.cpp:203-205, :240), so an item drawn later that lies behind an earlier one is hidden
/// there instead of painting over it. Here each blended surface gets a depth-only copy
/// (<see cref="PrimShaderFamily.DepthCoreAvatar"/>) at its item's <see cref="WornAlphaOrder.DepthOffset"/>:
/// after all of the item's own surfaces (an item never occludes its own layers -- BUG-RENDER-12's hair
/// failure) and before the next item. On an item's own instance that copy is the parent's surface the
/// blended face was moved off for sorting (BUG-RENDER-38), which drew nothing before -- so the pass costs
/// no extra instance there; every blended surface is now moved, also a single one. On a merged outfit
/// (FEAT-PERF-23) it is a child of the sorted instance sharing its mesh. Threshold:
/// <see cref="RenderConfig.WornAlphaDepth"/>.</para>
/// </summary>
public partial class AvatarRenderer
{
    /// <summary>One worn rigged item's place in its wearer's alpha order.</summary>
    private sealed class WornAlphaEntry
    {
        public bool WearsBake;

        /// <summary>The attachment point it was last ranked at.</summary>
        public byte Point;

        /// <summary>The start of its slot (<see cref="WornAlphaOrder.BaseOffset"/>); NaN until ranked.</summary>
        public float Base = float.NaN;

        /// <summary>The rank moved and its nodes have not been told yet.</summary>
        public bool Stale;

        /// <summary>Its own instance, when it has one (staged, held, or an animated-object part).</summary>
        public MeshInstance3D? Staged;
    }

    /// <summary>Avatars whose ranks changed since the last tick.</summary>
    private readonly HashSet<AvatarVisual> _wornAlphaDirty = new();

    /// <summary>The slot start an instance was given, on the instance (its split children are placed from it).</summary>
    private static readonly StringName WornAlphaBaseMeta = "slng_worn_alpha_base";

    /// <summary>The surface a split child (BUG-RENDER-38) was moved off.</summary>
    private static readonly StringName SortedSurfaceMeta = "slng_sorted_surface";

    /// <summary>The depth-pass child of a merged sorted instance.</summary>
    private const string WornDepthName = "WornAlphaDepth";

    /// <summary>The depth pass of each blended colour material, made once.</summary>
    private static readonly ConditionalWeakTable<Material, ShaderMaterial> _wornDepthMaterials = new();

    /// <summary>Whether a face of this mesh shows a Bakes-on-Mesh channel.</summary>
    private static bool WearsBake(FaceTexture[]? faces, FaceTexture defaultFace, int[] faceIndices)
    {
        foreach (int faceIndex in faceIndices)
            if (SLNG.Assets.BakedTextureIds.TryGetBakeIndex(ResolveFaceTexture(faces, defaultFace, faceIndex).TextureId, out _))
                return true;
        return false;
    }

    /// <summary>A worn rigged item's (or an animated object's part's) own instance was built or rebuilt:
    /// it joins its wearer's order, which is re-ranked; this item's nodes are placed now, the others' at the
    /// next tick.</summary>
    private void RegisterWornAlphaItem(AvatarVisual visual, Guid entityId, MeshInstance3D mi, bool wearsBake)
    {
        if (!visual.WornAlpha.TryGetValue(entityId, out var entry))
            visual.WornAlpha[entityId] = entry = new WornAlphaEntry();
        entry.WearsBake = wearsBake;
        entry.Staged = mi;
        if (RankWornAlpha(visual)) _wornAlphaDirty.Add(visual);
        WriteWornAlphaNodes(visual, entityId, entry);
    }

    /// <summary>The item was taken off (or the animated object lost the part): the rest close up.</summary>
    private void ForgetWornAlphaItem(AvatarVisual visual, Guid entityId)
    {
        if (visual.WornAlpha.Remove(entityId)) _wornAlphaDirty.Add(visual);
    }

    /// <summary>An update of a worn item: a new attachment point re-ranks its wearer.</summary>
    private void NoteWornAlphaPoint(AvatarVisual visual, Guid entityId, byte point)
    {
        if (visual.WornAlpha.TryGetValue(entityId, out var entry) && entry.Point != point) _wornAlphaDirty.Add(visual);
    }

    /// <summary>Re-ranks the avatars that changed and moves the nodes of every item whose slot moved.</summary>
    private void TickWornAlphaOrder()
    {
        if (_wornAlphaDirty.Count == 0) return;
        foreach (var visual in _wornAlphaDirty)
        {
            if (!_visuals.TryGetValue(visual.EntityId, out var live) || !ReferenceEquals(live, visual)) continue;
            RankWornAlpha(visual);
            foreach (var (entityId, entry) in visual.WornAlpha)
                if (entry.Stale) WriteWornAlphaNodes(visual, entityId, entry);
        }
        _wornAlphaDirty.Clear();
    }

    /// <summary>Ranks the visual's items (<see cref="WornAlphaOrder.Rank"/>) and records each slot; true when
    /// any slot moved.</summary>
    private bool RankWornAlpha(AvatarVisual visual)
    {
        int n = visual.WornAlpha.Count;
        if (n == 0) return false;
        var ids = new Guid[n];
        var entries = new WornAlphaEntry[n];
        var items = new WornAlphaItem[n];
        int i = 0;
        foreach (var (entityId, entry) in visual.WornAlpha)
        {
            var entity = _world?.GetEntity(entityId);
            byte point = entity?.GetComponent<AttachmentComponent>()?.AttachmentPoint ?? 0;
            uint root = entity != null ? WornLinksetRoot(entity) : 0;
            entry.Point = point;
            ids[i] = entityId;
            entries[i] = entry;
            items[i] = new WornAlphaItem(entry.WearsBake, point, root, entity?.LocalId ?? 0);
            i++;
        }
        var ranks = WornAlphaOrder.Rank(items);
        bool moved = false;
        for (int k = 0; k < n; k++)
        {
            float b = WornAlphaOrder.BaseOffset(ranks[k]);
            if (b == entries[k].Base) continue;
            entries[k].Base = b;
            entries[k].Stale = true;
            moved = true;
        }
        return moved;
    }

    /// <summary>Places every node that draws the item: its own instance and split children, and the sorted
    /// instances (with their depth passes) of a bucket that draws it.</summary>
    private void WriteWornAlphaNodes(AvatarVisual visual, Guid entityId, WornAlphaEntry entry)
    {
        float b = entry.Base;
        if (float.IsNaN(b)) return;
        entry.Stale = false;
        if (entry.Staged is { } mi && IsInstanceValid(mi)) PlaceWornInstance(mi, b);
        if (!_mergeItems.TryGetValue(entityId, out var item) || !ReferenceEquals(item.Visual, visual)) return;
        item.SortingOffset = b;
        if (!item.InBucket || item.Merged == null || visual.OutfitMerge == null
            || !visual.OutfitMerge.Buckets.TryGetValue(item.Linkset, out var bucket))
            return;
        for (int s = 0; s < item.Merged.Rig.Surfaces.Length; s++)
        {
            if (bucket.DrawnAt.TryGetValue((entityId, s), out var at) && IsInstanceValid(at.Node) && bucket.Sorted.Contains(at.Node))
                PlaceSortedInstance(at.Node, b, s);
        }
    }

    /// <summary>An item's own instance: the instance at the depth slot (it draws the depth copies of the
    /// surfaces moved off it), each split child at its surface's slot.</summary>
    private static void PlaceWornInstance(MeshInstance3D mi, float baseOffset)
    {
        mi.SetMeta(WornAlphaBaseMeta, baseOffset);
        mi.SortingOffset = WornAlphaOrder.DepthOffset(baseOffset);
        foreach (var child in mi.GetChildren())
        {
            if (child is MeshInstance3D split && split.HasMeta(SortedSurfaceMeta))
                split.SortingOffset = WornAlphaOrder.SurfaceOffset(baseOffset, split.GetMeta(SortedSurfaceMeta).AsInt32());
        }
    }

    /// <summary>A merged item's sorted instance for <paramref name="surface"/>, and its depth pass.</summary>
    private static void PlaceSortedInstance(MeshInstance3D node, float baseOffset, int surface)
    {
        node.SetMeta(WornAlphaBaseMeta, baseOffset);
        node.SortingOffset = WornAlphaOrder.SurfaceOffset(baseOffset, surface);
        if (node.GetNodeOrNull<MeshInstance3D>(WornDepthName) is { } depth)
            depth.SortingOffset = WornAlphaOrder.DepthOffset(baseOffset);
    }

    /// <summary>The slot start of a worn item as its wearer ranks it now, or <paramref name="fallback"/> when
    /// it is not ranked.</summary>
    private static float WornAlphaBaseOf(AvatarVisual visual, Guid entityId, float fallback) =>
        visual.WornAlpha.TryGetValue(entityId, out var entry) && !float.IsNaN(entry.Base) ? entry.Base : fallback;

    /// <summary>The slot start recorded on an instance, or null for one that is not in an order (a rigid
    /// attachment).</summary>
    private static float? WornAlphaBaseOf(MeshInstance3D mi) =>
        mi.HasMeta(WornAlphaBaseMeta) ? mi.GetMeta(WornAlphaBaseMeta).AsSingle() : null;

    /// <summary>The depth pass of a blended worn face: the colour material with the depth-only shader, the
    /// same textures (shared, so a sharpen reaches both) and placement. Null when the face is not blended,
    /// failed, hidden, or the pass is off.</summary>
    private static ShaderMaterial? WornDepthMaterialFor(Material? color)
    {
        if (RenderConfig.WornAlphaDepth <= 0f || color is not ShaderMaterial sm) return null;
        if (!ReferenceEquals(sm.Shader, PrimShaderFamily.Select(PrimShaderFamily.Kind.Blend, PrimShaderFamily.Surface.Avatar)))
            return null;
        var facts = FactsOf(sm);
        if (facts.Failed || facts.Hidden) return null;
        if (_wornDepthMaterials.TryGetValue(sm, out var made)) return made;
        // NOT Duplicate(): duplicating a ShaderMaterial walks its property list, and that asks the
        // RenderingServer for the shader's uniform list and every parameter's default -- a full sync with the
        // separate render thread, per call. v0.27.87-91 did that once per blended worn face and logged thousands
        // of "get_shader_parameter_list / shader_get_parameter_default causing RenderingServer synchronizations"
        // warnings in a crowded region. GetShaderParameter reads the material's own values and never syncs, so
        // the uniforms the pass reads (prim_depth_core_avatar.gdshader: the alpha and the placement in
        // slng_transform_uv) are copied by name; one the colour material never set stays at the same default.
        var depth = new ShaderMaterial
        {
            Shader = PrimShaderFamily.DepthCoreAvatar,
            RenderPriority = sm.RenderPriority,
        };
        foreach (var name in DepthPassUniforms)
        {
            var value = sm.GetShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil) depth.SetShaderParameter(name, value);
        }
        depth.SetShaderParameter(PrimShaderFamily.CoreAlphaThreshold, RenderConfig.WornAlphaDepth);
        _wornDepthMaterials.AddOrUpdate(sm, depth);
        return depth;
    }

    /// <summary>What <c>prim_depth_core_avatar.gdshader</c> reads of the colour material: the alpha (colour,
    /// texture) and the diffuse placement <c>slng_transform_uv</c> computes (TE repeats/offset/rotation, planar
    /// texgen, the glTF transform, a texture animation).</summary>
    private static readonly StringName[] DepthPassUniforms =
    {
        PrimShaderFamily.AlbedoColor, PrimShaderFamily.AlbedoTexture, PrimShaderFamily.HasAlbedoTexture,
        PrimShaderFamily.UvScale, PrimShaderFamily.UvOffset, PrimShaderFamily.UvRotation,
        PrimShaderFamily.UvTexGen, PrimShaderFamily.PrimScale,
        PrimShaderFamily.PbrUvA, PrimShaderFamily.PbrUvB, PrimShaderFamily.TexAnimA, PrimShaderFamily.TexAnimB,
    };

    /// <summary>What the parent instance draws for a surface moved onto its own child: the face's depth pass
    /// on a rigged mesh, nothing otherwise.</summary>
    private Material SplitParentMaterial(MeshInstance3D parent, Material? color) =>
        (parent.Skin != null ? WornDepthMaterialFor(color) : null) ?? HiddenSurfaceMaterial;

    /// <summary>Gives a merged sorted instance the depth pass of <paramref name="color"/> (or takes it away
    /// when the face no longer has one). The instance must be in the tree.</summary>
    private static void SetSortedDepthPass(MeshInstance3D node, Material? color)
    {
        var existing = node.GetNodeOrNull<MeshInstance3D>(WornDepthName);
        var depthMaterial = WornDepthMaterialFor(color);
        if (depthMaterial == null)
        {
            if (existing != null)
            {
                existing.Skeleton = new NodePath();
                node.RemoveChild(existing);
                existing.QueueFree();
            }
            return;
        }
        if (existing != null)
        {
            existing.SetSurfaceOverrideMaterial(0, depthMaterial);
            return;
        }
        float baseOffset = WornAlphaBaseOf(node) ?? node.SortingOffset;
        var depth = new MeshInstance3D
        {
            Name = WornDepthName,
            Mesh = node.Mesh,
            Skin = node.Skin,
            CustomAabb = node.CustomAabb,
            Layers = node.Layers,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            SortingOffset = WornAlphaOrder.DepthOffset(baseOffset),
        };
        depth.SetSurfaceOverrideMaterial(0, depthMaterial);
        node.AddChild(depth);
        if (!node.Skeleton.IsEmpty && node.GetNodeOrNull<Skeleton3D>(node.Skeleton) is { } skeleton)
            depth.Skeleton = depth.GetPathTo(skeleton);
        else if (node.HasMeta(DetachedSkeletonMeta))
            // A parked instance (DetachSkeleton): the pass waits detached too, and ReattachSkeleton(node)
            // restores both. The stored path is relative to the parent, one level up from here.
            depth.SetMeta(DetachedSkeletonMeta, new NodePath("../" + node.GetMeta(DetachedSkeletonMeta).AsNodePath()));
    }
}
