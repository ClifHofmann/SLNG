using System.Collections.Generic;
using System.Numerics;
using LibreMetaverse;
using Vector3 = System.Numerics.Vector3;

namespace SLNG.Assets;

/// <summary>
/// Service that computes bone distortions (scale and position offsets) in SL coordinate space
/// from a raw visual parameters byte array, using LibreMetaverse's VisualParam database.
/// Completely engine-neutral: does not leak LibreMetaverse types out of its public interface.
/// </summary>
public static class AvatarShapeService
{
    public static Dictionary<string, (Vector3 Scale, Vector3 Position)> ComputeDistortions(byte[]? visualParams)
    {
        var distortions = new Dictionary<string, (Vector3 Scale, Vector3 Position)>();

        int[]? group0 = VisualParams.Group0ParamIds;
        if (group0 == null)
            return distortions;

        for (int i = 0; i < group0.Length; i++)
        {
            int paramId = group0[i];

            if (!VisualParams.Params.TryGetValue(paramId, out var param))
                continue;

            float valFloat;
            if (visualParams != null && i < visualParams.Length)
            {
                byte valByte = visualParams[i];
                valFloat = param.MinValue + (valByte / 255.0f) * (param.MaxValue - param.MinValue);
            }
            else
            {
                valFloat = param.DefaultValue;
            }

            if (param.SkeletalDistortions == null || param.SkeletalDistortions.Length == 0)
                continue;

            foreach (var dist in param.SkeletalDistortions)
            {
                string boneName = dist.BoneName;
                var scaleDef = new Vector3(dist.ScaleDeformation.X, dist.ScaleDeformation.Y, dist.ScaleDeformation.Z);
                var posDef = new Vector3(dist.PositionDeformation.X, dist.PositionDeformation.Y, dist.PositionDeformation.Z);

                if (!distortions.TryGetValue(boneName, out var current))
                    current = (Vector3.Zero, Vector3.Zero);

                current.Scale += scaleDef * valFloat;
                if (dist.HasPositionDeformation)
                    current.Position += posDef * valFloat;

                distortions[boneName] = current;
            }
        }

        return distortions;
    }
}
