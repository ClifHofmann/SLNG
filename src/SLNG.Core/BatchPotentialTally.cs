using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Core;

/// <summary>FEAT-PERF-20 Phase 0: how many draws a scene's prims would cost under each batching
/// option, counted from one sample per drawn surface. Pure arithmetic over what the renderer
/// reports, so the counting rules are testable without Godot; the renderer only gathers samples.
///
/// <para><b>The rule being modelled</b> (Godot 4.7 Forward+, <c>render_forward_clustered.cpp</c>
/// <c>_fill_instance_data</c>): an element is drawn as an extra instance of the previous draw when
/// both sort keys match -- same mesh surface, same material, same shader, same depth layer
/// (<c>CLAMP(int(depth * 16 / z_range), 0, 15)</c>) -- and neither is a multimesh. The opaque list
/// is sorted by key, so all matching elements end up next to each other and one draw remains per
/// distinct key; the transparent list is sorted by depth, so its elements are counted one each.
/// The shadow pass sorts by the same keys.</para>
///
/// <para>Four counts per pass:</para>
/// <list type="bullet">
/// <item><c>Now</c>: today's materials -- one object per face, so practically one draw per
/// surface;</item>
/// <item><c>Shared</c> (option A): faces with equal <see cref="PrimMaterialKey"/>s share a
/// material, except those that must keep a private one (<see cref="PrivateReasons"/>);</item>
/// <item><c>Cell32/Cell64</c> (option B): static opaque/masked shareable surfaces are merged into
/// one mesh per (cell, material, shadow flag); everything else stays as now;</item>
/// <item><c>Both32/Both64</c> (option C): B for the static part, A for the rest.</item>
/// </list></summary>
public sealed class BatchPotentialTally
{
    /// <summary>Which of Godot's lists a surface is drawn in, and whether it reaches the shadow
    /// pass.</summary>
    public enum SurfaceKind : byte
    {
        /// <summary>Opaque list, casts shadows.</summary>
        Opaque,
        /// <summary>Alpha-tested (scissor, hash): opaque list, casts shadows, discards.</summary>
        Masked,
        /// <summary>Alpha-blended: the depth-sorted transparent list, no shadow.</summary>
        Blend,
        /// <summary>A parent's placeholder for a surface moved onto its own child for sorting
        /// (BUG-RENDER-16): opaque list, still a draw in both passes, draws nothing.</summary>
        Hidden,
    }

    /// <summary>Why a surface cannot share its material with an identical-looking face.</summary>
    [Flags]
    public enum PrivateReasons : byte
    {
        /// <summary>Shareable.</summary>
        None = 0,
        /// <summary>A running texture animation writes its UV uniforms every frame.</summary>
        TextureAnimation = 1,
        /// <summary>Selected / being edited: its state changes under the user's hands.</summary>
        Selected = 2,
        /// <summary>The hero mirror: its shader is swapped while it is the chosen one.</summary>
        Mirror = 4,
        /// <summary>A MOAP face: a fetched media image replaces its albedo texture.</summary>
        Media = 8,
    }

    /// <summary>One drawn surface.</summary>
    /// <param name="MeshKey">The shared mesh resource (Godot merges only draws of one mesh).</param>
    /// <param name="Surface">The surface index within that mesh.</param>
    /// <param name="Key">The surface's material key.</param>
    /// <param name="MaterialInstance">The material object drawn today (its identity, for
    /// <c>Now</c> and for a private material).</param>
    /// <param name="Kind">List and shadow behaviour.</param>
    /// <param name="Private">Why it needs its own material, if it does.</param>
    /// <param name="HasDepthPass">A chained depth-only pass (BUG-RENDER-16's DepthCore): one
    /// more transparent-list draw, no shadow.</param>
    /// <param name="InView">Inside the camera frustum (the main pass draws only these).</param>
    /// <param name="CastsShadow">The instance casts a shadow now.</param>
    /// <param name="IsStatic">Has not moved for a while and is not physical.</param>
    /// <param name="DepthLayer">Godot's 16-band depth layer, part of its merge key.</param>
    /// <param name="Position">World position, for the cell.</param>
    public readonly record struct Sample(
        Guid MeshKey,
        int Surface,
        PrimMaterialKey Key,
        ulong MaterialInstance,
        SurfaceKind Kind,
        PrivateReasons Private,
        bool HasDepthPass,
        bool InView,
        bool CastsShadow,
        bool IsStatic,
        int DepthLayer,
        Vector3 Position);

    /// <summary>The draws one pass would cost under each option.</summary>
    public readonly record struct PassEstimate(
        int Surfaces, int Now, int Shared, int Cell32, int Cell64, int Both32, int Both64);

    /// <summary>What a whole tally adds up to.</summary>
    /// <param name="Main">The camera pass (in-view samples).</param>
    /// <param name="Shadow">The shadow pass, for ONE view: every shadow-casting sample, whether
    /// on screen or not. A directional light draws a caster once per cascade it falls into.</param>
    /// <param name="Keys">Distinct shareable material keys on screen.</param>
    /// <param name="Private">On-screen surfaces that keep a private material (any reason).</param>
    /// <param name="PrivateAnim">... of which texture-animated.</param>
    /// <param name="PrivateSelected">... of which selected.</param>
    /// <param name="PrivateMirror">... of which the hero mirror.</param>
    /// <param name="PrivateMedia">... of which MOAP faces.</param>
    /// <param name="Blend">On-screen alpha-blended surfaces (never batched by any option).</param>
    /// <param name="DepthPasses">On-screen chained depth passes.</param>
    /// <param name="Hidden">On-screen hidden placeholder surfaces.</param>
    /// <param name="StaticEligible">On-screen surfaces a per-cell merge would take.</param>
    public readonly record struct Report(
        PassEstimate Main,
        PassEstimate Shadow,
        int Keys,
        int Private,
        int PrivateAnim,
        int PrivateSelected,
        int PrivateMirror,
        int PrivateMedia,
        int Blend,
        int DepthPasses,
        int Hidden,
        int StaticEligible);

    /// <summary>The two cell edges the spec compares, in metres.</summary>
    public const float SmallCell = 32f;

    /// <inheritdoc cref="SmallCell"/>
    public const float LargeCell = 64f;


    private readonly PassCounter _main = new();
    private readonly PassCounter _shadow = new();
    private readonly HashSet<PrimMaterialKey> _keys = new();
    private int _count, _private, _anim, _selected, _mirror, _media, _blend, _depth, _hidden, _eligible;

    /// <summary>Samples added since the last <see cref="Clear"/>.</summary>
    public int Count => _count;

    /// <summary>Adds one drawn surface. The counting happens here, so a caller that spreads its
    /// samples over many frames spreads the cost with them and <see cref="Compute"/> stays O(1).</summary>
    public void Add(in Sample s)
    {
        _count++;

        // Blended surfaces carry no FLAG_PASS_SHADOW; neither do the depth passes.
        if (s.CastsShadow && s.Kind != SurfaceKind.Blend) _shadow.Add(s, layer: 0, depthPass: false);
        if (!s.InView) return;

        // The shadow views have their own depth ranges; the layer is a main-pass term only.
        _main.Add(s, s.DepthLayer, s.HasDepthPass);

        if (s.Kind == SurfaceKind.Blend) _blend++;
        if (s.Kind == SurfaceKind.Hidden) _hidden++;
        if (s.HasDepthPass) _depth++;
        if (IsCellEligible(s)) _eligible++;
        if (s.Private != PrivateReasons.None)
        {
            _private++;
            if ((s.Private & PrivateReasons.TextureAnimation) != 0) _anim++;
            if ((s.Private & PrivateReasons.Selected) != 0) _selected++;
            if ((s.Private & PrivateReasons.Mirror) != 0) _mirror++;
            if ((s.Private & PrivateReasons.Media) != 0) _media++;
        }
        else if (s.Kind != SurfaceKind.Hidden)
        {
            _keys.Add(s.Key);
        }
    }

    /// <summary>Forgets every sample (start of a new pass).</summary>
    public void Clear()
    {
        _main.Clear();
        _shadow.Clear();
        _keys.Clear();
        _count = _private = _anim = _selected = _mirror = _media = _blend = _depth = _hidden = _eligible = 0;
    }

    /// <summary>True when a per-cell merge would take the surface: static, depth-writing, and
    /// free to share its material.</summary>
    public static bool IsCellEligible(in Sample s) =>
        s.IsStatic && s.Private == PrivateReasons.None
        && (s.Kind == SurfaceKind.Opaque || s.Kind == SurfaceKind.Masked);

    /// <summary>The integer cell of <paramref name="position"/> for an edge of
    /// <paramref name="size"/> metres.</summary>
    public static (int X, int Y, int Z) Cell(Vector3 position, float size) =>
        ((int)MathF.Floor(position.X / size), (int)MathF.Floor(position.Y / size), (int)MathF.Floor(position.Z / size));

    /// <summary>What the samples so far add up to, for both passes.</summary>
    public Report Compute() => new(
        _main.Estimate(), _shadow.Estimate(), _keys.Count, _private, _anim, _selected, _mirror, _media,
        _blend, _depth, _hidden, _eligible);

    // What one sorted-and-merged opaque element is identified by. Material is either a shared
    // key or, for a surface that keeps its own material, the object it keeps.
    private readonly record struct MergeId(Guid Mesh, int Surface, PrimMaterialKey? Key, ulong Instance, int Layer);

    private readonly record struct CellId((int X, int Y, int Z) Cell, PrimMaterialKey Key, bool CastsShadow);

    /// <summary>The distinct-element sets of one pass.</summary>
    private sealed class PassCounter
    {
        private int _surfaces;
        private int _sorted;   // transparent-list elements: one draw each under every option
        private readonly HashSet<MergeId> _now = new();
        private readonly HashSet<MergeId> _shared = new();
        private readonly HashSet<MergeId> _restNow = new();
        private readonly HashSet<MergeId> _restShared = new();
        private readonly HashSet<CellId> _cell32 = new();
        private readonly HashSet<CellId> _cell64 = new();

        public void Add(in Sample s, int layer, bool depthPass)
        {
            _surfaces++;
            if (depthPass)
            {
                _surfaces++;
                _sorted++;
            }

            if (s.Kind == SurfaceKind.Blend)
            {
                _sorted++;
                return;
            }

            var asNow = new MergeId(s.MeshKey, s.Surface, null, s.MaterialInstance, layer);
            bool shareable = s.Private == PrivateReasons.None && s.Kind != SurfaceKind.Hidden;
            var asShared = shareable ? new MergeId(s.MeshKey, s.Surface, s.Key, 0, layer) : asNow;

            _now.Add(asNow);
            _shared.Add(asShared);

            if (IsCellEligible(s))
            {
                // A merged mesh has one shadow flag, so casters and non-casters of one material
                // in one cell are two meshes.
                _cell32.Add(new CellId(Cell(s.Position, SmallCell), s.Key, s.CastsShadow));
                _cell64.Add(new CellId(Cell(s.Position, LargeCell), s.Key, s.CastsShadow));
            }
            else
            {
                _restNow.Add(asNow);
                _restShared.Add(asShared);
            }
        }

        public PassEstimate Estimate() => new(
            Surfaces: _surfaces,
            Now: _now.Count + _sorted,
            Shared: _shared.Count + _sorted,
            Cell32: _cell32.Count + _restNow.Count + _sorted,
            Cell64: _cell64.Count + _restNow.Count + _sorted,
            Both32: _cell32.Count + _restShared.Count + _sorted,
            Both64: _cell64.Count + _restShared.Count + _sorted);

        public void Clear()
        {
            _surfaces = _sorted = 0;
            _now.Clear();
            _shared.Clear();
            _restNow.Clear();
            _restShared.Clear();
            _cell32.Clear();
            _cell64.Clear();
        }
    }
}
