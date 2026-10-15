using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

// FEAT-PERF-14 -- the headless proof that a prim gets the engine visibility range the rule says, that the
// things which must never be culled by size are not, and that a rescale, a selection or a setting change
// reaches the number the engine reads. Kept out of ObjectRenderer.cs, which is only ever driven from here.

public partial class ObjectRenderer
{
    /// <summary>
    /// Drives real entities through the real event flow with no asset service: a decoded mesh is delivered
    /// by hand, through the method the asset callback uses. The camera does not exist in a headless run,
    /// so the viewport side of the rule (pixels per radian) is set directly, which is exactly the input
    /// <see cref="UpdateRangeEpoch"/> derives from it.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestVisibilityRanges(World world)
    {
        const ulong Region = 11437119954698752UL;
        const float Ppr = 1031f;                       // 1080p at a 60 degree vertical field of view
        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);

        float savedPx = RenderConfig.SmallObjectCullPixels;
        float savedLod = RenderConfig.VolumeLodFactor;
        float savedDraw = RenderConfig.DrawDistance;
        try
        {
            RenderConfig.SmallObjectCullPixels = 3f;
            RenderConfig.VolumeLodFactor = 1f;
            RenderConfig.DrawDistance = 96f;
            _rangePpr = Ppr;
            _rangeDraw = 96f; _rangePx = 3f; _rangeLod = 1f;
            _rangeEpoch++;

            // A triangle whose bounds are exactly 1 x 1 x 1, so the node's scale IS the object's size.
            var unit = new MeshData(new[]
            {
                new MeshSubmesh(
                    new[]
                    {
                        new System.Numerics.Vector3(-0.5f, -0.5f, -0.5f),
                        new System.Numerics.Vector3(0.5f, -0.5f, 0f),
                        new System.Numerics.Vector3(0f, 0.5f, 0.5f),
                    },
                    new[] { System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitZ },
                    new[] { System.Numerics.Vector2.Zero, System.Numerics.Vector2.UnitX, System.Numerics.Vector2.UnitY },
                    new[] { 0, 1, 2 }),
            });

            Entity AddPrim(uint localId, float size)
            {
                var e = world.GetOrCreateEntity(Region, localId);
                e.SetComponent(new TransformComponent(new System.Numerics.Vector3(128f, 128f, 25f), System.Numerics.Quaternion.Identity));
                var prim = new PrimitiveComponent(new System.Numerics.Vector3(size, size, size), 0, isMesh: true, meshId: Guid.NewGuid());
                e.SetComponent(prim);
                world.NotifyComponentUpdated(e, prim);
                Settle();
                var st = _visuals[e.Id];
                st.ResourcesReleased = false;
                st.LoadedMeshId = prim.MeshId;
                st.LoadedMeshDetailLevel = MeshDetailLevel.Highest;
                ApplyArrivedMesh(st, prim.MeshId, MeshDetailLevel.Highest, unit);
                Settle();
                return e;
            }

            void Rescale(Entity e, float size)
            {
                var prim = e.GetComponent<PrimitiveComponent>()!;
                prim.Scale = new System.Numerics.Vector3(size, size, size);
                world.NotifyComponentUpdated(e, prim);
                Settle();
            }

            // What the rule says for a cube of this edge, as the renderer will have computed it.
            float Expected(float size, float lod = 1f, float px = 3f)
            {
                float radius = size * (float)Math.Sqrt(3) * 0.5f;
                float end = SmallObjectCull.EndDistance(radius, Ppr, px, lod, RenderConfig.DrawDistance * 1.15f);
                return end > 0f ? Mathf.Round(end * 2f) * 0.5f : 0f;
            }

            bool Near(float a, float b) => Mathf.Abs(a - b) < 0.01f;

            // ---- a small prim gets the range the rule gives, margin included ----
            var small = AddPrim(1, 0.2f);
            var ss = _visuals[small.Id];
            float want = Expected(0.2f);
            Expect(want > SmallObjectCull.MinEndMetres, $"the test prim must be small enough to be ranged (rule says {want})");
            Expect(Near(ss.MeshInstance.VisibilityRangeEnd, want),
                $"a 20 cm prim must get VisibilityRangeEnd {want}, got {ss.MeshInstance.VisibilityRangeEnd}");
            Expect(Near(ss.MeshInstance.VisibilityRangeEndMargin, SmallObjectCull.Margin(want)),
                $"and the hysteresis margin {SmallObjectCull.Margin(want)}, got {ss.MeshInstance.VisibilityRangeEndMargin}");
            Expect(ss.MeshInstance.VisibilityRangeFadeMode == GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
                "fade must stay off (a dither pass is what this exists to avoid)");
            Expect(ss.ShadowEnd > 0f && ss.ShadowEnd < want || ss.ShadowEnd == SmallObjectCull.MinEndMetres,
                $"a small caster must stop casting sooner than it stops drawing (shadow {ss.ShadowEnd}, draw {want})");

            // ---- a big prim never gets one ----
            var big = AddPrim(2, 4f);
            Expect(_visuals[big.Id].MeshInstance.VisibilityRangeEnd == 0f,
                $"a 4 m prim must never be culled by size (got {_visuals[big.Id].MeshInstance.VisibilityRangeEnd})");

            // ---- a rescale reaches the range, in both directions ----
            Rescale(small, 0.1f);
            float wantSmaller = Expected(0.1f);
            Expect(Near(ss.MeshInstance.VisibilityRangeEnd, wantSmaller) && wantSmaller < want,
                $"shrinking a prim must shorten its range ({want} -> {wantSmaller}, node has {ss.MeshInstance.VisibilityRangeEnd})");
            Rescale(small, 3f);
            Expect(ss.MeshInstance.VisibilityRangeEnd == 0f, "growing it past the limit must remove the range");
            Rescale(small, 0.2f);
            Expect(Near(ss.MeshInstance.VisibilityRangeEnd, want), "and shrinking it again must bring it back");

            // ---- selected / edited objects are exempt, and get the range back when deselected ----
            SuppressInstancing(small.Id, true);
            Expect(ss.MeshInstance.VisibilityRangeEnd == 0f && ss.RangeExempt && ss.ShadowEnd == 0f,
                "a selected prim must not be culled by size");
            SuppressInstancing(small.Id, false);
            Expect(Near(ss.MeshInstance.VisibilityRangeEnd, want) && !ss.RangeExempt,
                $"deselecting must restore the range (got {ss.MeshInstance.VisibilityRangeEnd})");

            // ---- a light and an emitter are exempt ----
            var lamp = AddPrim(3, 0.2f);
            var lampPrim = lamp.GetComponent<PrimitiveComponent>()!;
            lampPrim.LightEnabled = true;
            lampPrim.LightRadius = 5f;
            lampPrim.LightIntensity = 1f;
            world.NotifyComponentUpdated(lamp, lampPrim);
            Settle();
            Expect(_visuals[lamp.Id].LightNode != null && _visuals[lamp.Id].MeshInstance.VisibilityRangeEnd == 0f,
                "a prim carrying a light must not be culled by size");
            lampPrim.LightEnabled = false;
            world.NotifyComponentUpdated(lamp, lampPrim);
            Settle();
            Expect(Near(_visuals[lamp.Id].MeshInstance.VisibilityRangeEnd, want),
                "and gets its range once the light goes out");

            // ---- a mirror is exempt ----
            var mirror = AddPrim(4, 0.2f);
            _flaggedMirrors.Add(mirror.Id);
            RefreshVisibilityRange(_visuals[mirror.Id], readMesh: false);
            Expect(_visuals[mirror.Id].MeshInstance.VisibilityRangeEnd == 0f, "a flagged mirror must not be culled by size");
            _flaggedMirrors.Remove(mirror.Id);

            // ---- the settings reach the number: threshold, Object Detail, draw distance ----
            // The epoch is what carries a setting to an object that is not otherwise touched.
            void Sweep(Entity e)
            {
                RenderConfig.SmallObjectCullPixels = _rangePx;
                var st = _visuals[e.Id];
                if (st.RangeEpoch != _rangeEpoch) RefreshVisibilityRange(st, readMesh: false);
            }

            _rangePx = 0f; RenderConfig.SmallObjectCullPixels = 0f; _rangeEpoch++;
            Sweep(small);
            Expect(ss.MeshInstance.VisibilityRangeEnd == 0f && ss.ShadowEnd == 0f, "threshold 0 must switch the cull off");

            _rangePx = 3f; RenderConfig.SmallObjectCullPixels = 3f; RenderConfig.VolumeLodFactor = 3f; _rangeLod = 3f; _rangeEpoch++;
            Sweep(small);
            float wantUltra = Expected(0.2f, lod: 3f);
            // At 3.0 the 20 cm prim reaches past the draw distance, which reads as "no range" (0): also further.
            Expect(Near(ss.MeshInstance.VisibilityRangeEnd, wantUltra) && (wantUltra == 0f || wantUltra > want),
                $"a higher Object Detail must reach further ({want} -> {wantUltra}, node has {ss.MeshInstance.VisibilityRangeEnd})");

            RenderConfig.VolumeLodFactor = 1f; _rangeLod = 1f;
            RenderConfig.DrawDistance = 30f; _rangeDraw = 30f; _rangeEpoch++;
            Sweep(small);
            Expect(ss.MeshInstance.VisibilityRangeEnd == 0f,
                $"a range beyond where the sweep hides the object anyway must read as none (got {ss.MeshInstance.VisibilityRangeEnd})");
            RenderConfig.DrawDistance = 96f; _rangeDraw = 96f; _rangeEpoch++;
            Sweep(small);
            Expect(Near(ss.MeshInstance.VisibilityRangeEnd, want), "and come back with the draw distance");

            // ---- no viewport yet: nothing is ranged, and the object stays due for a refresh ----
            float keepPpr = _rangePpr;
            var late = AddPrim(5, 0.2f);
            _rangePpr = 0f; _rangeEpoch++;
            var ls = _visuals[late.Id];
            ls.RangeEpoch = -1;
            RefreshVisibilityRange(ls, readMesh: false);
            Expect(ls.RangeEpoch == -1, "without a viewport the object must stay due for a refresh");
            _rangePpr = keepPpr;
        }
        finally
        {
            RenderConfig.SmallObjectCullPixels = savedPx;
            RenderConfig.VolumeLodFactor = savedLod;
            RenderConfig.DrawDistance = savedDraw;
        }

        return failures.Count == 0
            ? (true, "small prims ranged by size; big, selected, lit and mirror prims exempt; rescale and settings reach the node")
            : (false, string.Join("; ", failures));
    }
}
