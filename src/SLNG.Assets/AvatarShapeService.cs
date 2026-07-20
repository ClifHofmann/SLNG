using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml.Linq;
using LibreMetaverse;
using Vector3 = System.Numerics.Vector3;

namespace SLNG.Assets;

public record AvatarShapeData(
    Dictionary<string, (Vector3 Scale, Vector3 Position)> BoneMods,
    Dictionary<string, float> Morphs
);

/// <summary>
/// Service that computes bone distortions (scale and position offsets) in SL coordinate space
/// from a raw visual parameters byte array, using LibreMetaverse's VisualParam database.
/// Completely engine-neutral: does not leak LibreMetaverse types out of its public interface.
/// </summary>
public static class AvatarShapeService
{
    // SL's "male" toggle (0 = female, 1 = male) — id is stable across viewer versions (it's the
    // one dummy/edit_group="dummy" boolean param avatar_lad.xml uses to gate every sex-specific
    // morph). Confirmed via the bundled avatar_lad.xml (name="male", value_min=0, value_max=1).
    private const int MaleParamId = 80;

    // paramId -> "male"/"female", populated from avatar_lad.xml's own `sex` attribute. LibreMetaverse's
    // generated VisualParam struct does NOT carry this (verified against its source generator),
    // so it has to be read from the XML directly. A param with no entry here is unisex ("both" in
    // the real viewer), matching avatar_lad.xml's convention of only tagging sex-specific params.
    // Only ever holds a SUCCESSFULLY parsed map (i.e. charDir was non-null and the file existed).
    // A call with charDir == null (or a bad path) must NOT poison this for later callers that DO
    // have a real charDir — it returns a fresh empty map instead of caching one.
    private static Dictionary<int, string>? _paramSex;

    private static Dictionary<int, string> GetParamSexMap(string? charDir)
    {
        if (_paramSex != null) return _paramSex;
        if (charDir == null) return new Dictionary<int, string>();

        var map = new Dictionary<int, string>();
        try
        {
            string path = Path.Combine(charDir, "avatar_lad.xml");
            if (File.Exists(path))
            {
                var doc = XDocument.Load(path);
                foreach (var p in doc.Descendants("param"))
                {
                    var idAttr = p.Attribute("id");
                    var sexAttr = p.Attribute("sex");
                    if (idAttr != null && sexAttr != null && int.TryParse(idAttr.Value, out int id))
                        map[id] = sexAttr.Value;
                }
                _paramSex = map; // cache only a successful parse
            }
        }
        catch
        {
            // Fall through and return an unparsed (empty) map without caching — every param
            // falls back to unisex, i.e. the pre-fix behavior, rather than a hard failure. Not
            // cached so a later call (e.g. once the file is actually present) can retry.
        }
        return map;
    }

    private static float ReadRawValue(int paramId, byte[]? visualParams, int[] group0)
    {
        int idx = Array.IndexOf(group0, paramId);
        if (idx < 0 || !VisualParams.Params.TryGetValue(paramId, out var vp)) return 0f;
        if (visualParams != null && idx < visualParams.Length)
            return vp.MinValue + (visualParams[idx] / 255.0f) * (vp.MaxValue - vp.MinValue);
        return vp.DefaultValue;
    }

    /// <summary>Resolves one param's EFFECTIVE weight — the value the viewer actually applies —
    /// from its raw/derived weight, after the real viewer's sex gate and range clamp:
    /// `effective_weight = (getSex() &amp; avatar_sex) ? weight : getDefaultWeight()`, then
    /// clamped to [min, max] (LLPolySkeletalDistortion::apply / LLPolyMorphTarget::apply share
    /// this exact gate). Both the skeletal-distortion path and the vertex-morph path consume the
    /// result, so it lives in one place.</summary>
    private static float EffectiveWeight(
        VisualParam param, float rawWeight, bool avatarIsMale, Dictionary<int, string> paramSex)
    {
        bool sexMatches = !paramSex.TryGetValue(param.ParamID, out var sexTag) ||
                           (sexTag == "male") == avatarIsMale;
        float weightSource = sexMatches ? rawWeight : param.DefaultValue;
        return Math.Clamp(weightSource, param.MinValue, param.MaxValue);
    }

    /// <summary>Computes one driven param's weight from its driver's raw weight, exactly per
    /// LLDriverParam::getDrivenWeight (indra/llappearance/lldriverparam.cpp) — a piecewise-linear
    /// "trapezoid" over the driver's own value range: ramps driven from its own min to its own
    /// max between [min1,max1], plateaus at driven-max between [max1,max2], ramps back down to
    /// driven-min between [max2,min2], and holds driven-min beyond that (or driven-max if max2 is
    /// already at/beyond the driver's own max — a pure "ramp up and stay" shape).</summary>
    private static float GetDrivenWeight(VisualParam driverParam, DrivenParamInfo dpi, float driverWeight, VisualParam drivenParam)
    {
        float driverMin = driverParam.MinValue, driverMax = driverParam.MaxValue;

        // LibreMetaverse's source generator emits (0,0,0,0) for a <driven> element that omits
        // min1/max1/max2/min2 (HasRange=false) — but the real viewer
        // (LLDriverParamInfo::parseXml, lldriverparam.cpp) defaults them to the DRIVER's own
        // min/max/max/max instead (a plain ramp with no plateau), NOT to zero. Verified by
        // reading the actual source rather than trusting the generated default.
        float min1 = driverMin, max1 = driverMax, max2 = driverMax, min2 = driverMax;
        if (dpi.HasRange)
        {
            min1 = dpi.Min1;
            max1 = dpi.Max1;
            max2 = dpi.Max2;
            min2 = dpi.Min2;
        }

        float drivenMin = drivenParam.MinValue, drivenMax = drivenParam.MaxValue;

        if (driverWeight <= min1)
            return (min1 == max1 && min1 <= driverMin) ? drivenMax : drivenMin;
        if (driverWeight <= max1)
        {
            float t = (driverWeight - min1) / (max1 - min1);
            return drivenMin + t * (drivenMax - drivenMin);
        }
        if (driverWeight <= max2)
            return drivenMax;
        if (driverWeight <= min2)
        {
            float t = (driverWeight - max2) / (min2 - max2);
            return drivenMax + t * (drivenMin - drivenMax);
        }
        return max2 >= driverMax ? drivenMax : drivenMin;
    }

    /// <summary>Resolves the EFFECTIVE weight of every VisualParam reachable from a transmitted
    /// appearance: each directly-transmitted Group0 param (from its raw byte, or its DefaultValue
    /// when the array is short/null) plus every param DRIVEN by one (weight derived through
    /// <see cref="GetDrivenWeight"/>). Both are sex-gated and clamped via
    /// <see cref="EffectiveWeight"/>. The result — <c>paramId → weight</c> — is the single source
    /// of truth shared by the skeletal-distortion path (<see cref="ComputeDistortions"/>) and the
    /// vertex-morph path, so the two can never disagree on how strongly a slider is applied.</summary>
    /// <param name="visualParams">Raw per-avatar VisualParams byte array (Group0, in
    /// Group0ParamIds order).</param>
    /// <param name="charDir">Directory containing avatar_lad.xml, used to read each param's sex
    /// tag. Pass null to skip sex-gating entirely (every param treated as unisex).</param>
    public static Dictionary<int, float> ComputeEffectiveWeights(byte[]? visualParams, string? charDir = null)
    {
        var weights = new Dictionary<int, float>();

        int[]? group0 = VisualParams.Group0ParamIds;
        if (group0 == null)
            return weights;

        var paramSex = GetParamSexMap(charDir);
        // "male" (id 80) is a Group0 param like any other — its own DefaultValue there is female,
        // matching the real viewer using getDefaultWeight() as the fallback for a non-matching sex.
        bool avatarIsMale = ReadRawValue(MaleParamId, visualParams, group0) >= 0.5f;

        for (int i = 0; i < group0.Length; i++)
        {
            int paramId = group0[i];
            if (!VisualParams.Params.TryGetValue(paramId, out var param)) continue;

            // Verified against the real viewer source (LLVisualParam::setWeight, indra/llcharacter/
            // llvisualparam.cpp): mCurWeight is the RAW weight clamped to [min,max] — NOT
            // DefaultWeight-subtracted — and mLastWeight starts at 0.f, so a "never touched" shape
            // genuinely carries whatever its params' own defaults produce (e.g. "Shoulders"
            // defaults to -0.5). Sex-gating + clamping happen in EffectiveWeight.
            float rawWeight;
            if (visualParams != null && i < visualParams.Length)
                rawWeight = param.MinValue + (visualParams[i] / 255.0f) * (param.MaxValue - param.MinValue);
            else
                rawWeight = param.DefaultValue;

            weights[paramId] = EffectiveWeight(param, rawWeight, avatarIsMale, paramSex);

            // Params in OTHER groups (e.g. group="1") are never independently transmitted — their
            // value is DERIVED from this transmitted "driver" param via a piecewise-linear mapping
            // (LLDriverParam). Head-shape and many body-shape sliders (incl. vertex morphs) only
            // reach the avatar through this driven path. The driven param's weight is keyed by its
            // OWN id; if two drivers ever drove the same id, last-write-wins matches the viewer
            // (setDrivenWeight overwrites, then the driven param applies once with its final value).
            if (param.Drivers != null && param.DrivenParams != null)
            {
                foreach (var dpi in param.DrivenParams)
                {
                    if (!VisualParams.Params.TryGetValue(dpi.ParamID, out var drivenParam)) continue;
                    float drivenWeight = GetDrivenWeight(param, dpi, rawWeight, drivenParam);
                    weights[dpi.ParamID] = EffectiveWeight(drivenParam, drivenWeight, avatarIsMale, paramSex);
                }
            }
        }

        return weights;
    }

    /// <summary>Computes per-bone skeletal distortions (scale + position offsets, SL space) from a
    /// transmitted appearance, by applying every reachable param's SkeletalDistortions scaled by
    /// its effective weight (see <see cref="ComputeEffectiveWeights"/>). Additive across params
    /// (order-independent), matching LLPolySkeletalDistortion::apply accumulating onto joint
    /// scale/position.</summary>
    /// <param name="visualParams">Raw per-avatar VisualParams byte array (Group0, in
    /// Group0ParamIds order).</param>
    /// <param name="charDir">Directory containing avatar_lad.xml for sex tags; null to skip.</param>
    public static AvatarShapeData ComputeDistortions(byte[]? visualParams, string? charDir = null)
    {
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>();
        var morphs = new Dictionary<string, float>();
        var weights = ComputeEffectiveWeights(visualParams, charDir);

        foreach (var (paramId, effectiveWeight) in weights)
        {
            if (!VisualParams.Params.TryGetValue(paramId, out var param)) continue;

            if (!morphs.TryGetValue(param.Name, out var mWeight))
            {
                mWeight = 0f;
            }
            morphs[param.Name] = mWeight + effectiveWeight;

            if (param.SkeletalDistortions == null || param.SkeletalDistortions.Length == 0) continue;

            foreach (var dist in param.SkeletalDistortions)
            {
                string boneName = dist.BoneName;
                var scaleDef = new Vector3(dist.ScaleDeformation.X, dist.ScaleDeformation.Y, dist.ScaleDeformation.Z);
                var posDef = new Vector3(dist.PositionDeformation.X, dist.PositionDeformation.Y, dist.PositionDeformation.Z);

                if (!distortions.TryGetValue(boneName, out var current))
                    current = (Vector3.Zero, Vector3.Zero);

                current.Scale += scaleDef * effectiveWeight;
                if (dist.HasPositionDeformation)
                    current.Position += posDef * effectiveWeight;

                distortions[boneName] = current;
            }
        }

        return new AvatarShapeData(distortions, morphs);
    }
}
