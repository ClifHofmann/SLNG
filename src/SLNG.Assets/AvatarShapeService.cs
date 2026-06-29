using System;
using System.Collections.Generic;
using System.Numerics;
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
    public static AvatarShapeData ComputeDistortions(byte[]? visualParams)
    {
        var boneMods = new Dictionary<string, (Vector3 Scale, Vector3 Position)>();
        var morphs = new Dictionary<string, float>();

        int[]? group0 = VisualParams.Group0ParamIds;
        if (group0 == null)
            return new AvatarShapeData(boneMods, morphs);

        for (int i = 0; i < group0.Length; i++)
        {
            int paramId = group0[i];

            float valFloat;
            if (visualParams != null && i < visualParams.Length)
            {
                byte valByte = visualParams[i];
                if (VisualParams.Params.TryGetValue(paramId, out var vp))
                {
                    valFloat = vp.MinValue + (valByte / 255.0f) * (vp.MaxValue - vp.MinValue);
                }
                else
                {
                    continue;
                }
            }
            else
            {
                if (VisualParams.Params.TryGetValue(paramId, out var vp))
                {
                    valFloat = vp.DefaultValue;
                }
                else
                {
                    continue;
                }
            }

            if (VisualParams.Params.TryGetValue(paramId, out var param))
            {
                // Morphs
                // SL maps the parameter's Name directly to the Morph Target name.
                // We add the weight computed above. Some parameters might map to the same morph target
                // so we accumulate.
                if (!morphs.TryGetValue(param.Name, out var mWeight))
                {
                    mWeight = 0f;
                }
                morphs[param.Name] = mWeight + valFloat;

                // Skeletal Distortions
                if (param.SkeletalDistortions == null || param.SkeletalDistortions.Length == 0)
                    continue;

                foreach (var dist in param.SkeletalDistortions)
                {
                    string boneName = dist.BoneName;
                    var scaleDef = new Vector3(dist.ScaleDeformation.X, dist.ScaleDeformation.Y, dist.ScaleDeformation.Z);
                    var posDef = new Vector3(dist.PositionDeformation.X, dist.PositionDeformation.Y, dist.PositionDeformation.Z);

                    if (!boneMods.TryGetValue(boneName, out var current))
                    {
                        current = (Vector3.Zero, Vector3.Zero);
                    }

                    current.Scale += scaleDef * valFloat;
                    if (dist.HasPositionDeformation)
                    {
                        current.Position += posDef * valFloat;
                    }

                    boneMods[boneName] = current;
                }
            }
        }

        return new AvatarShapeData(boneMods, morphs);
    }
}
