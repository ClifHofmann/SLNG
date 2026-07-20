using System.Numerics;
using LibreMetaverse;
using SLNG.Assets;
using Xunit;

namespace SLNG.Core.Tests;

public class AvatarShapeServiceTests
{
    // "Shoulders" (paramId 36) has DefaultValue -0.5 in a [-1.8, 1.4] range and distorts
    // mNeck/mChest. Verified against the real viewer source
    // (LLVisualParam::setWeight / LLPolySkeletalDistortion::apply,
    // indra/llcharacter/llvisualparam.cpp): mCurWeight is the RAW weight (clamped to
    // [min,max]), never DefaultValue-subtracted, and mLastWeight starts at 0.f — so a "never
    // touched" shape genuinely carries scaleDef * DefaultValue distortion. -0.5 is NOT zero,
    // so this param is a real-world case where "at this param's own default" != "zero weight".
    private const int ShouldersParamId = 36;

    [Fact]
    public void Raw_weight_zero_means_zero_distortion_for_that_param()
    {
        Assert.True(VisualParams.Params.TryGetValue(ShouldersParamId, out var shoulders));
        Assert.InRange(0f, shoulders.MinValue, shoulders.MaxValue); // sanity: 0 is reachable

        int[] group0 = VisualParams.Group0ParamIds!;
        var bytes = new byte[group0.Length];
        for (int i = 0; i < group0.Length; i++)
        {
            if (!VisualParams.Params.TryGetValue(group0[i], out var vp)) continue;
            float range = vp.MaxValue - vp.MinValue;
            // Encode weight=0 for every param reachable there, matching mLastWeight's real
            // starting point — the byte that reconstructs to valFloat==0.
            bytes[i] = range == 0 ? (byte)0 : (byte)System.Math.Round((0f - vp.MinValue) / range * 255f);
        }

        var distortions = AvatarShapeService.ComputeDistortions(bytes);

        if (distortions.BoneMods.TryGetValue("mNeck", out var neck))
        {
            // Byte quantization of a float range introduces sub-percent rounding error; allow a
            // small tolerance instead of requiring bit-exact zero.
            Assert.True(neck.Scale.Length() < 0.01f, $"expected ~zero mNeck distortion at raw weight 0 on every param, got {neck.Scale}");
        }
    }

    [Fact]
    public void Param_at_its_own_default_value_applies_that_default_as_distortion()
    {
        Assert.True(VisualParams.Params.TryGetValue(ShouldersParamId, out var shoulders));
        Assert.NotEqual(0f, shoulders.DefaultValue); // sanity: this param's default really isn't 0
        var neckDist = System.Array.Find(shoulders.SkeletalDistortions!, d => d.BoneName == "mNeck");
        var expectedNeckScale = new System.Numerics.Vector3(neckDist.ScaleDeformation.X, neckDist.ScaleDeformation.Y, neckDist.ScaleDeformation.Z) * shoulders.DefaultValue;

        int[] group0 = VisualParams.Group0ParamIds!;
        int shouldersIdx = System.Array.IndexOf(group0, ShouldersParamId);
        var bytes = new byte[group0.Length];
        for (int i = 0; i < group0.Length; i++)
        {
            if (!VisualParams.Params.TryGetValue(group0[i], out var vp)) continue;
            float range = vp.MaxValue - vp.MinValue;
            // Every OTHER param at raw weight 0 (see test above) so only Shoulders contributes.
            bytes[i] = range == 0 ? (byte)0 : (byte)System.Math.Round((0f - vp.MinValue) / range * 255f);
        }
        // Shoulders itself at its own DefaultValue.
        bytes[shouldersIdx] = (byte)System.Math.Round((shoulders.DefaultValue - shoulders.MinValue) / (shoulders.MaxValue - shoulders.MinValue) * 255f);

        var distortions = AvatarShapeService.ComputeDistortions(bytes);

        Assert.True(distortions.BoneMods.TryGetValue("mNeck", out var neck), "expected mNeck to have a distortion entry");
        Assert.True((neck.Scale - expectedNeckScale).Length() < 0.01f,
            $"expected mNeck scale distortion {expectedNeckScale} (Shoulders' own default applied directly, per the real viewer), got {neck.Scale}");
    }

    [Fact]
    public void Sex_gating_reads_avatar_lad_xml_without_changing_unrelated_bones()
    {
        // avatar_lad.xml (bundled with LibreMetaverse, copied to this test project's own output —
        // "linden/character/avatar_lad.xml") carries the per-param `sex` attribute LibreMetaverse's
        // generated VisualParam struct drops entirely. None of the Group0 params reachable from
        // ComputeDistortions happen to be sex-tagged themselves (sex-specific morphs live in other
        // groups, e.g. group="1" "Muscular_Torso") — so passing a real charDir must parse cleanly
        // and produce IDENTICAL results to the no-charDir case for our existing test data,
        // regardless of the "male" toggle (paramId 80).
        string charDir = System.IO.Path.Combine(AppContext.BaseDirectory, "linden", "character");
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(charDir, "avatar_lad.xml")),
            "expected avatar_lad.xml to be deployed alongside the test binary (via LibreMetaverse's content items)");

        var withoutCharDir = AvatarShapeService.ComputeDistortions(null);
        var withCharDir = AvatarShapeService.ComputeDistortions(null, charDir);

        Assert.Equal(withoutCharDir.BoneMods.Count, withCharDir.BoneMods.Count);
        foreach (var (bone, dist) in withoutCharDir.BoneMods)
        {
            Assert.True(withCharDir.BoneMods.TryGetValue(bone, out var dist2));
            Assert.True((dist.Scale - dist2.Scale).Length() < 0.0001f, $"bone {bone} scale differs between charDir=null and real charDir");
        }
    }

    // "Head Size" (driver paramId 682, Min=0/Max=1/Default=0.5) drives a SEPARATE "Head Size"
    // param (id 655, Min=-0.25/Max=0.1) that scales mFaceRoot/mHead/mSkull and nearly every
    // mFace* bone — confirmed via LibreMetaverse's own VisualParams.Params data. id 655 is NOT
    // in Group0ParamIds (it's only reachable by walking driver 682's DrivenParams), so before
    // driven-param support this fired for zero avatars regardless of their actual Head Size
    // slider — a real, previously-silent gap directly relevant to head-shape correctness.
    private const int HeadSizeDriverParamId = 682;
    private const int HeadSizeDrivenParamId = 655;

    [Fact]
    public void Driven_param_head_size_reaches_mFaceRoot()
    {
        Assert.True(VisualParams.Params.TryGetValue(HeadSizeDriverParamId, out var driver));
        Assert.True(VisualParams.Params.TryGetValue(HeadSizeDrivenParamId, out var driven));
        var dpi = System.Array.Find(driver.DrivenParams!, d => d.ParamID == HeadSizeDrivenParamId);
        Assert.False(dpi.HasRange); // confirmed via inspection; the expected-delta math below assumes this
        var faceRootDist = System.Array.Find(driven.SkeletalDistortions!, d => d.BoneName == "mFaceRoot");

        // Other drivers (e.g. "Egg_Head") ALSO reach mFaceRoot and are non-zero even at their own
        // raw weight 0 (their trapezoid doesn't cross zero there — legitimate SL design, not a
        // bug), so comparing against an absolute expected value would be fragile. Compare the
        // DELTA between Head Size at its own min vs its own max instead — isolates exactly Head
        // Size's contribution regardless of what every other driven param independently adds.
        int[] group0 = VisualParams.Group0ParamIds!;
        int driverIdx = System.Array.IndexOf(group0, HeadSizeDriverParamId);
        var bytesAtMin = new byte[group0.Length];
        for (int i = 0; i < group0.Length; i++)
        {
            if (!VisualParams.Params.TryGetValue(group0[i], out var vp)) continue;
            float range = vp.MaxValue - vp.MinValue;
            bytesAtMin[i] = range == 0 ? (byte)0 : (byte)System.Math.Round((0f - vp.MinValue) / range * 255f);
        }
        var bytesAtMax = (byte[])bytesAtMin.Clone();
        bytesAtMin[driverIdx] = 0;   // Head Size slider at its own minimum (raw weight = MinValue = 0)
        bytesAtMax[driverIdx] = 255; // Head Size slider at its own maximum (raw weight = MaxValue = 1)

        var distortionsAtMin = AvatarShapeService.ComputeDistortions(bytesAtMin);
        var distortionsAtMax = AvatarShapeService.ComputeDistortions(bytesAtMax);

        Assert.True(distortionsAtMax.BoneMods.TryGetValue("mFaceRoot", out var faceRootAtMax), "expected mFaceRoot to have a distortion entry from the driven \"Head Size\" param");
        distortionsAtMin.BoneMods.TryGetValue("mFaceRoot", out var faceRootAtMin); // may be absent (zero) if nothing else touches mFaceRoot here

        // Per LLDriverParam::getDrivenWeight with HasRange false (min1/max1/max2/min2 default to
        // driverMin/driverMax/driverMax/driverMax): driverWeight==driverMin gives driven.MinValue,
        // driverWeight==driverMax gives driven.MaxValue — so the delta isolates exactly
        // (driven.MaxValue - driven.MinValue) * scaleDef, independent of every other param.
        var scaleDef = new System.Numerics.Vector3(faceRootDist.ScaleDeformation.X, faceRootDist.ScaleDeformation.Y, faceRootDist.ScaleDeformation.Z);
        var expectedDelta = scaleDef * (driven.MaxValue - driven.MinValue);
        var actualDelta = faceRootAtMax.Scale - faceRootAtMin.Scale;

        Assert.True((actualDelta - expectedDelta).Length() < 0.01f,
            $"expected mFaceRoot scale delta {expectedDelta} between Head Size min and max, got {actualDelta}");
    }

    // Encodes every Group0 param at raw weight 0, so ComputeEffectiveWeights results below are
    // isolated to whatever a single param under test is set to.
    private static byte[] AllZeroWeightBytes()
    {
        int[] group0 = VisualParams.Group0ParamIds!;
        var bytes = new byte[group0.Length];
        for (int i = 0; i < group0.Length; i++)
        {
            if (!VisualParams.Params.TryGetValue(group0[i], out var vp)) continue;
            float range = vp.MaxValue - vp.MinValue;
            bytes[i] = range == 0 ? (byte)0 : (byte)System.Math.Round((0f - vp.MinValue) / range * 255f);
        }
        return bytes;
    }

    [Fact]
    public void Effective_weights_expose_a_group0_param_at_its_raw_value()
    {
        // Shoulders (36) is a directly-transmitted Group0 param. Its effective weight must equal
        // the raw slider value it was encoded at (unisex, within range → no gating/clamping).
        Assert.True(VisualParams.Params.TryGetValue(ShouldersParamId, out var shoulders));
        int[] group0 = VisualParams.Group0ParamIds!;
        int idx = System.Array.IndexOf(group0, ShouldersParamId);

        var bytes = AllZeroWeightBytes();
        // Encode Shoulders at raw weight 0.25 (arbitrary in-range value).
        const float target = 0.25f;
        bytes[idx] = (byte)System.Math.Round((target - shoulders.MinValue) / (shoulders.MaxValue - shoulders.MinValue) * 255f);

        var weights = AvatarShapeService.ComputeEffectiveWeights(bytes);

        Assert.True(weights.TryGetValue(ShouldersParamId, out float w), "expected Shoulders to appear in the effective-weight map");
        // Byte quantization introduces sub-percent error; allow a small tolerance.
        Assert.True(System.Math.Abs(w - target) < 0.02f, $"expected Shoulders effective weight ~{target}, got {w}");
    }

    [Fact]
    public void Effective_weights_derive_a_driven_param_across_its_full_range()
    {
        // Head Size driver (682, range [0,1], HasRange=false) ramps its driven param (655,
        // range [-0.25, 0.1]) linearly across the driver's full range. Verify the PUBLIC weight
        // map yields driven.MinValue at driver=0 and driven.MaxValue at driver=1 — the exact
        // LLDriverParam::getDrivenWeight endpoints — so the morph path can trust these weights.
        Assert.True(VisualParams.Params.TryGetValue(HeadSizeDrivenParamId, out var driven));
        int[] group0 = VisualParams.Group0ParamIds!;
        int driverIdx = System.Array.IndexOf(group0, HeadSizeDriverParamId);

        var bytesAtMin = AllZeroWeightBytes();
        var bytesAtMax = (byte[])bytesAtMin.Clone();
        bytesAtMin[driverIdx] = 0;
        bytesAtMax[driverIdx] = 255;

        var weightsAtMin = AvatarShapeService.ComputeEffectiveWeights(bytesAtMin);
        var weightsAtMax = AvatarShapeService.ComputeEffectiveWeights(bytesAtMax);

        Assert.True(weightsAtMin.TryGetValue(HeadSizeDrivenParamId, out float wMin));
        Assert.True(weightsAtMax.TryGetValue(HeadSizeDrivenParamId, out float wMax));
        Assert.True(System.Math.Abs(wMin - driven.MinValue) < 0.001f, $"expected driven weight {driven.MinValue} at driver min, got {wMin}");
        Assert.True(System.Math.Abs(wMax - driven.MaxValue) < 0.001f, $"expected driven weight {driven.MaxValue} at driver max, got {wMax}");
    }
}
