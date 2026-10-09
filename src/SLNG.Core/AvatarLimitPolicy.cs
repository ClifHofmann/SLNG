using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-08: Pure policy for limiting fully rendered avatars based on distance and hysteresis.
/// Matches the reference viewer's <c>RenderAvatarMaxNonImpostors</c> concept.
/// </summary>
public static class AvatarLimitPolicy
{
    public const float DefaultHysteresisFraction = 0.15f;

    /// <summary>
    /// Reference viewer preset values from <c>featuretable.txt</c>:
    /// Low 3 (:98), Mid 7 (:185), High 11 (:269), Ultra 16 (:351).
    /// </summary>
    public const int PresetLowCap = 3;
    public const int PresetMediumCap = 7;
    public const int PresetHighCap = 11;
    public const int PresetUltraCap = 16;

    /// <summary>
    /// An avatar candidate within visible range to be evaluated.
    /// </summary>
    /// <param name="Id">Unique avatar or entity ID.</param>
    /// <param name="Distance">Distance from camera / agent in metres.</param>
    /// <param name="IsCurrentlyFull">True if the avatar was fully rendered in the previous evaluation.</param>
    /// <param name="IsExempt">True if the avatar is exempt from the cap (e.g. self or user override).</param>
    public readonly record struct Candidate(
        Guid Id,
        float Distance,
        bool IsCurrentlyFull,
        bool IsExempt
    );

    /// <summary>
    /// Evaluates which avatar candidates should be fully rendered vs reduced.
    /// <para>
    /// Rules:
    /// 1. Exempt candidates (self, user override) are always fully rendered and do not consume cap slots.
    /// 2. If <paramref name="cap"/> &lt;= 0, all candidates are fully rendered (unlimited).
    /// 3. Non-exempt candidates are sorted by effective distance:
    ///    An incumbent full avatar receives a distance discount:
    ///    <c>effectiveDistance = distance * (1.0f - hysteresisFraction)</c>,
    ///    preventing rapid boundary flipping (thrashing).
    /// 4. The top <paramref name="cap"/> candidates are marked full (<c>true</c>); all remaining
    ///    candidates are marked reduced (<c>false</c>).
    /// </para>
    /// </summary>
    public static Dictionary<Guid, bool> Evaluate(
        IReadOnlyList<Candidate> candidates,
        int cap,
        float hysteresisFraction = DefaultHysteresisFraction)
    {
        var result = new Dictionary<Guid, bool>(candidates.Count);
        if (candidates.Count == 0) return result;

        if (cap <= 0)
        {
            foreach (var c in candidates)
                result[c.Id] = true;
            return result;
        }

        var nonExempt = new List<(Candidate Candidate, float EffectiveDistance)>(candidates.Count);

        foreach (var c in candidates)
        {
            if (c.IsExempt)
            {
                result[c.Id] = true;
            }
            else
            {
                float effDist = c.IsCurrentlyFull
                    ? Math.Max(0f, c.Distance * (1.0f - hysteresisFraction))
                    : Math.Max(0f, c.Distance);
                nonExempt.Add((c, effDist));
            }
        }

        // Sort by effective distance ascending (nearest first).
        // Deterministic tie-breaker by Guid if effective distances match.
        nonExempt.Sort((a, b) =>
        {
            int cmp = a.EffectiveDistance.CompareTo(b.EffectiveDistance);
            return cmp != 0 ? cmp : a.Candidate.Id.CompareTo(b.Candidate.Id);
        });

        for (int i = 0; i < nonExempt.Count; i++)
        {
            bool isFull = i < cap;
            result[nonExempt[i].Candidate.Id] = isFull;
        }

        return result;
    }
}
