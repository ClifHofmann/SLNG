using System;
using System.Collections.Generic;

namespace SLNG.Core;

public static class GltfOverrideStamp
{
    /// <summary>Returns <paramref name="faces"/> with each face's PbrOverride set to the entry of
    /// <paramref name="overrides"/> for its index (null where there is none). Returns the SAME array when nothing
    /// would change, so a caller can tell by reference whether to notify. When an override names a face beyond the
    /// array (or the array is null), the array grows; the new faces are copies of <paramref name="defaultFace"/>,
    /// which is what the renderer builds a surface without its own record from anyway.</summary>
    public static FaceTexture[]? Apply(FaceTexture[]? faces, in FaceTexture defaultFace,
                                       IReadOnlyDictionary<int, GltfMaterialOverride>? overrides)
    {
        // Plain loops, no LINQ: this runs on every object update of a prim that has (or had) overrides.
        if (overrides is not { Count: > 0 })
        {
            if (faces == null) return faces;
            bool any = false;
            for (int i = 0; i < faces.Length; i++)
            {
                if (faces[i].PbrOverride != null)
                {
                    any = true;
                    break;
                }
            }
            if (!any) return faces;
            var cleared = (FaceTexture[])faces.Clone();
            for (int i = 0; i < cleared.Length; i++) cleared[i] = cleared[i] with { PbrOverride = null };
            return cleared;
        }

        int max = 0;
        foreach (var k in overrides.Keys)
        {
            if (k > max) max = k;
        }
        int length = Math.Max(faces?.Length ?? 0, max + 1);
        var result = new FaceTexture[length];
        bool changed = faces == null || faces.Length != length;
        for (int i = 0; i < length; i++)
        {
            var f = faces != null && i < faces.Length ? faces[i] : defaultFace;
            overrides.TryGetValue(i, out var o);
            if (!Equals(f.PbrOverride, o))
            {
                f = f with { PbrOverride = o };
                changed = true;
            }
            result[i] = f;
        }
        return changed ? result : faces;
    }
}
