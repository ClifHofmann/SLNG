using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace SLNG.App;

/// <summary>
/// FEAT-PERF-06: draw-call reduction by rendering groups of identical, repeated, static prims
/// through one <see cref="MultiMeshInstance3D"/> instead of one <see cref="MeshInstance3D"/>
/// each. Measured trigger: a fully-loaded OpenSim villa/garden region submitting ~12–14k draw
/// calls for ~4–4500 visible objects (×3 for the two directional-shadow splits) because every
/// prim is its own node and nothing batches.
///
/// <para>Scope of what this touches: ONLY the drawn geometry. Each prim keeps its own
/// <c>StaticBody3D</c>/<c>CollisionShape3D</c>, so collision, the object-selection raycast and
/// editing are unaffected. A prim is <b>evicted</b> back to its own <see cref="MeshInstance3D"/>
/// the instant it stops being "identical, repeated and static" (selected, edited, animated,
/// out of draw distance).</para>
///
/// <para>Godot <see cref="MultiMesh"/> gives one mesh + one material for every instance and one
/// <c>cast_shadow</c> for the whole node, so a group can only hold prims that resolve to the
/// exact same mesh resource, the exact same single material (compared by
/// <see cref="MaterialFingerprint"/>) and the same shadow flag.</para>
/// </summary>
internal sealed class ObjectInstanceGroups
{
    private readonly Node3D _parent;
    private readonly Func<Guid, MeshInstance3D?> _nodeFor;

    private readonly Dictionary<InstanceGroupKey, InstanceGroup> _groups = new();

    // The first prim seen for a key does NOT get a MultiMesh -- one instance is no cheaper than
    // one MeshInstance3D and it would churn a node per unique object. The group is realised when
    // the second member arrives. Until then the key's members render normally.
    private readonly Dictionary<InstanceGroupKey, List<Guid>> _pending = new();

    private readonly Dictionary<Guid, InstanceGroupKey> _memberKey = new();

    public ObjectInstanceGroups(Node3D parent, Func<Guid, MeshInstance3D?> nodeFor)
    {
        _parent = parent;
        _nodeFor = nodeFor;
    }

    /// <summary>True once the prim's geometry is actually being drawn by a MultiMesh (i.e. its own
    /// node's <c>Mesh</c> has been nulled). A pending first-of-key member is NOT instanced.</summary>
    public bool IsInstanced(Guid id)
        => _memberKey.TryGetValue(id, out var key) && _groups.ContainsKey(key);

    /// <summary>Diagnostic-only: the realised group a member currently belongs to, or null if it
    /// is not instanced (pending, or not tracked at all). Lets a click diagnostic show the ACTUAL
    /// bound material/mesh a member draws through, rather than only the per-object face data that
    /// went into the fingerprint -- the two can legitimately diverge if this member's own state
    /// changed after it joined (see <see cref="Join"/>: re-fingerprinting only happens on the next
    /// cull-sweep offer).</summary>
    public InstanceGroup? GroupFor(Guid id)
        => _memberKey.TryGetValue(id, out var key) && _groups.TryGetValue(key, out var g) ? g : null;

    /// <summary>True if this prim is either instanced or a pending first-of-key member. The cull
    /// sweep uses it to skip re-deriving a key (and re-fingerprinting a material) for a prim it
    /// has already placed — an explicit <see cref="Leave"/> from a material/mesh change is what
    /// puts it back in play.</summary>
    public bool IsTracked(Guid id) => _memberKey.ContainsKey(id);

    /// <summary>Offers a prim to its group. Safe to call every cull sweep: a no-op when the prim
    /// is already in the right group at an unchanged transform.</summary>
    public void Join(Guid id, in InstanceGroupKey key, Mesh sharedMesh, Material sharedMaterial, Transform3D xf)
    {
        if (_memberKey.TryGetValue(id, out var current))
        {
            if (current.Equals(key))
            {
                if (_groups.TryGetValue(key, out var g)) g.AddOrUpdate(id, xf);
                return; // pending members move via their own node
            }
            Leave(id); // key changed (re-textured, shadow flip, new mesh) -- rebuild membership
        }

        if (_groups.TryGetValue(key, out var group))
        {
            group.AddOrUpdate(id, xf);
            _memberKey[id] = key;
            var n = _nodeFor(id);
            if (n != null) n.Mesh = null;
            return;
        }

        if (!_pending.TryGetValue(key, out var list))
        {
            list = new List<Guid>(2);
            _pending[key] = list;
        }
        if (!list.Contains(id)) list.Add(id);
        _memberKey[id] = key;

        if (list.Count >= 2)
        {
            var realised = new InstanceGroup(_parent, key, sharedMesh, sharedMaterial);
            foreach (var member in list)
            {
                var mn = _nodeFor(member);
                if (mn == null) { _memberKey.Remove(member); continue; }
                realised.AddOrUpdate(member, member == id ? xf : mn.Transform);
                mn.Mesh = null;
            }
            _groups[key] = realised;
            _pending.Remove(key);
        }
    }

    /// <summary>Updates just the instance transform of an already-instanced prim (the common
    /// per-<c>ObjectUpdate</c> path). Cheap; no allocation.</summary>
    public void UpdateTransform(Guid id, Transform3D xf)
    {
        if (_memberKey.TryGetValue(id, out var key) && _groups.TryGetValue(key, out var g))
            g.AddOrUpdate(id, xf);
    }

    /// <summary>Removes a prim from any group and restores its own node's mesh so it draws
    /// itself again. Idempotent. Dissolves a group that would drop to a single member.</summary>
    public void Leave(Guid id)
    {
        if (!_memberKey.TryGetValue(id, out var key)) return;
        _memberKey.Remove(id);

        if (_pending.TryGetValue(key, out var pend))
        {
            pend.Remove(id);
            if (pend.Count == 0) _pending.Remove(key);
            return; // a pending member never had its mesh nulled
        }

        if (!_groups.TryGetValue(key, out var group)) return;

        group.Remove(id);
        RestoreMesh(id, group.SharedMesh, group.SharedMaterial);

        if (group.Count <= 1)
        {
            foreach (var remaining in group.Members)
            {
                RestoreMesh(remaining, group.SharedMesh, group.SharedMaterial);
                _memberKey.Remove(remaining);
            }
            group.Dissolve();
            _groups.Remove(key);
        }
    }

    // BUG-RENDER-17: restoring ONLY `Mesh` left an evicted member with no material at all --
    // Godot's per-surface override array is bound to the mesh's surface count, so nulling `Mesh`
    // on Join (line ~78/98 above) discards whatever override this node had, and reassigning the
    // shared mesh here got back an empty override array, not the one that used to be there. The
    // node then fell back to Godot's built-in default material: dim, generic-lit, and identical
    // regardless of windlight or shadow/SSAO settings -- reported live as "sieht aus wie ein
    // übertriebener Schatten", reproduced on demand by simply right-clicking (selecting) a
    // previously-correct instanced object, since selection is exactly this Leave() path
    // (SuppressInstancing -> ApplyHighlightBox needs a live node). Every member of a group shares
    // one material by construction (InstanceGroupKey's whole point), so the group's own
    // SharedMaterial is always the right value to put back -- no per-member state to track.
    private void RestoreMesh(Guid id, Mesh shared, Material material)
    {
        var n = _nodeFor(id);
        if (n == null || n.Mesh != null) return;
        n.Mesh = shared;
        n.SetSurfaceOverrideMaterial(0, material);
    }

    /// <summary>One <c>[Instancing]</c> perf line: how much the batching is actually buying.</summary>
    public string StatsLine()
    {
        int instances = 0, biggest = 0;
        foreach (var g in _groups.Values)
        {
            instances += g.Count;
            if (g.Count > biggest) biggest = g.Count;
        }
        int drawnAsGroups = _groups.Count;                 // one MultiMesh draw each (× shadow passes)
        int saved = instances - drawnAsGroups;             // MeshInstance3D draws removed
        return $"[Instancing] groups={_groups.Count} instances={instances} biggest={biggest} " +
               $"pending={_pending.Count} drawCallsSaved≈{Math.Max(0, saved)}";
    }
}

/// <summary>Group identity. Two prims may share a <see cref="MultiMesh"/> only if all three match:
/// the shared-mesh cache key (same <see cref="ArrayMesh"/> resource), the material fingerprint
/// (renders identically), and the shadow-casting flag (one <c>cast_shadow</c> per node).</summary>
internal readonly struct InstanceGroupKey : IEquatable<InstanceGroupKey>
{
    public readonly Guid MeshKey;
    public readonly string MaterialFingerprint;
    public readonly bool CastShadow;

    public InstanceGroupKey(Guid meshKey, string materialFingerprint, bool castShadow)
    {
        MeshKey = meshKey;
        MaterialFingerprint = materialFingerprint;
        CastShadow = castShadow;
    }

    public bool Equals(InstanceGroupKey other)
        => MeshKey == other.MeshKey
        && CastShadow == other.CastShadow
        && string.Equals(MaterialFingerprint, other.MaterialFingerprint, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is InstanceGroupKey o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(MeshKey, MaterialFingerprint, CastShadow);
}

/// <summary>One realised MultiMesh: the node, its buffer, and the dense entity↔slot mapping.</summary>
internal sealed class InstanceGroup
{
    private const int MinCapacity = 8;

    private readonly MultiMeshInstance3D _node;
    private readonly MultiMesh _mm;
    private readonly InstanceSlotMap _slots = new();
    private readonly List<Transform3D> _xf = new();

    public Mesh SharedMesh { get; }
    public Material SharedMaterial { get; }
    public int Count => _slots.Count;
    public IReadOnlyList<Guid> Members => _slots.Order;

    public InstanceGroup(Node3D parent, in InstanceGroupKey key, Mesh sharedMesh, Material sharedMaterial)
    {
        SharedMesh = sharedMesh;
        SharedMaterial = sharedMaterial;
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = sharedMesh,
            InstanceCount = 0,
        };
        _node = new MultiMeshInstance3D
        {
            Name = "Instances_" + key.MeshKey.ToString("N")[..8] + "_" + (uint)key.GetHashCode(),
            Multimesh = _mm,
            MaterialOverride = sharedMaterial,
            CastShadow = key.CastShadow
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(_node);
    }

    public void AddOrUpdate(Guid id, Transform3D xf)
    {
        if (_slots.TryIndex(id, out int i))
        {
            _xf[i] = xf;
            _mm.SetInstanceTransform(i, xf);
            return;
        }

        i = _slots.Add(id);
        _xf.Add(xf);
        EnsureCapacity(_xf.Count);
        _mm.SetInstanceTransform(i, xf);
        _mm.VisibleInstanceCount = _xf.Count;
    }

    public void Remove(Guid id)
    {
        int slot = _slots.Remove(id, out Guid moved);
        if (slot < 0) return;

        int last = _xf.Count - 1;
        if (slot != last) _xf[slot] = _xf[last];
        _xf.RemoveAt(last);

        // The swap-removed survivor now lives in `slot`; rewrite that one buffer entry.
        if (moved != Guid.Empty && slot < _xf.Count) _mm.SetInstanceTransform(slot, _xf[slot]);
        _mm.VisibleInstanceCount = _xf.Count;
    }

    public void Dissolve() => _node.QueueFree();

    /// <summary>Grows the buffer to at least <paramref name="need"/> instances. Setting
    /// <see cref="MultiMesh.InstanceCount"/> may drop existing data, so every live transform is
    /// rewritten afterwards -- O(n) on a grow only, never on a steady-state update.</summary>
    private void EnsureCapacity(int need)
    {
        if (_mm.InstanceCount >= need) return;

        int cap = Math.Max(MinCapacity, _mm.InstanceCount);
        while (cap < need) cap *= 2;

        _mm.InstanceCount = cap;
        for (int k = 0; k < _xf.Count; k++) _mm.SetInstanceTransform(k, _xf[k]);
        _mm.VisibleInstanceCount = _xf.Count;
    }
}

/// <summary>
/// Pure dense entity↔index bookkeeping with swap-remove, factored out of
/// <see cref="InstanceGroup"/> so the fiddly part (an index staying valid after a middle removal)
/// is testable without a Godot runtime. Exercised by <c>SelfTest.CheckInstanceSlotMap</c>.
/// </summary>
internal sealed class InstanceSlotMap
{
    private readonly List<Guid> _order = new();
    private readonly Dictionary<Guid, int> _index = new();

    public int Count => _order.Count;
    public IReadOnlyList<Guid> Order => _order;

    public bool TryIndex(Guid id, out int index) => _index.TryGetValue(id, out index);

    /// <summary>Appends <paramref name="id"/> (or returns its existing slot). Never reorders
    /// existing entries.</summary>
    public int Add(Guid id)
    {
        if (_index.TryGetValue(id, out int existing)) return existing;
        int i = _order.Count;
        _order.Add(id);
        _index[id] = i;
        return i;
    }

    /// <summary>Swap-removes <paramref name="id"/>: the last entry is moved into the freed slot so
    /// the index array stays dense. Returns the freed slot index (or -1 if absent);
    /// <paramref name="moved"/> is the entity relocated into that slot, or <see cref="Guid.Empty"/>
    /// when the removed entry was already last.</summary>
    public int Remove(Guid id, out Guid moved)
    {
        moved = Guid.Empty;
        if (!_index.TryGetValue(id, out int slot)) return -1;

        int last = _order.Count - 1;
        if (slot != last)
        {
            moved = _order[last];
            _order[slot] = moved;
            _index[moved] = slot;
        }
        _order.RemoveAt(last);
        _index.Remove(id);
        return slot;
    }
}

/// <summary>
/// A string that is equal for two <see cref="ShaderMaterial"/>s exactly when they will render
/// identically: same shader, same value for every shader uniform. Textures compare by resource
/// RID, which is stable across the GpuCache's in-place re-uploads (a sharpen/shrink calls
/// <c>SetImage</c> on the same <see cref="Texture2D"/>), so a resolution upgrade does not evict
/// a prim from its group. A uniform left at its shader default reads back as a null Variant on
/// both sides, so it compares equal without needing to know the default.
///
/// <para>Conservative by construction: any difference this cannot prove irrelevant produces a
/// different fingerprint, so at worst two mergeable prims stay separate. It never groups prims
/// that differ.</para>
/// </summary>
internal static class MaterialFingerprint
{
    public static string Of(ShaderMaterial mat)
    {
        var sb = new StringBuilder(160);
        sb.Append("sh:").Append(mat.Shader?.GetInstanceId() ?? 0);
        sb.Append("|rp:").Append(mat.RenderPriority);
        sb.Append("|np:").Append(mat.NextPass?.GetInstanceId() ?? 0);

        if (mat.Shader is { } shader)
        {
            // `prim_scale` is set on EVERY prim material from the object's world size, but only the
            // PLANAR projection samples it -- for a default-texgen face (`uv_texgen != 1`) it is
            // inert. Two otherwise-identical props at slightly different scales are extremely
            // common in SL builds, and folding a dead uniform into the fingerprint split each such
            // pair into its own group (measured: pending=1190, biggest=35). Neutralise it when it
            // cannot affect the pixels; a genuinely planar face keeps it and still groups only
            // with its exact-scale twins.
            Variant texGen = mat.GetShaderParameter(UvTexGen);
            bool planar = texGen.VariantType == Variant.Type.Int && texGen.AsInt64() == 1L;

            // getGroups:false -> real uniforms only, no group/subgroup headers. Order is the
            // shader's declaration order, identical for two materials sharing a shader, so the
            // string is directly comparable.
            GDArray uniforms = shader.GetShaderUniformList();
            foreach (Variant u in uniforms)
            {
                if (u.Obj is not GDDict d || !d.TryGetValue("name", out Variant nameV)) continue;
                string name = nameV.AsString();
                string value = !planar && name == PrimScaleUniform
                    ? "inert"
                    : VariantKey(mat.GetShaderParameter(name));
                sb.Append('|').Append(name).Append('=').Append(value);
            }
        }
        return sb.ToString();
    }

    private static readonly StringName UvTexGen = "uv_texgen";
    private const string PrimScaleUniform = "prim_scale";

    private static string VariantKey(Variant v)
    {
        switch (v.VariantType)
        {
            case Variant.Type.Nil:
                return "_";
            case Variant.Type.Object:
                return v.Obj is Resource r ? "rid:" + r.GetRid() : "obj";
            case Variant.Type.Bool:
                return v.AsBool() ? "1" : "0";
            case Variant.Type.Int:
                return "i" + v.AsInt64();
            case Variant.Type.Float:
                return "f" + v.AsDouble().ToString("R");
            case Variant.Type.Color:
                return "c" + v.AsColor();
            case Variant.Type.Vector2:
                return "v2" + v.AsVector2();
            case Variant.Type.Vector3:
                return "v3" + v.AsVector3();
            case Variant.Type.Vector4:
                return "v4" + v.AsVector4();
            default:
                return v.ToString();
        }
    }
}
