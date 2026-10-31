using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-27: decides which avatar is the <b>focus subject</b> -- the one the person is looking at, whose
/// appearance should finish loading before everyone else's. Two ways in, in this order:
/// <list type="number">
/// <item>An explicit focus (roster double-click, Alt+Click): the camera's focus point lies on the avatar.
/// Works from any distance, because the camera stays where it was.</item>
/// <item>Proximity: the camera is close to an avatar and looking roughly at it (wheel zoom, walking up).</item>
/// </list>
/// Stateful so the answer is stable: the current subject is kept against a marginally better candidate, and
/// is held for <see cref="LossGraceSeconds"/> after it stops qualifying so a swept camera does not make the
/// loader flap. The caller runs it a few times a second, never per frame.
/// </summary>
public sealed class FocusTracker
{
    /// <summary>A focus point this close to an avatar's position (its pelvis) counts as a focus on it:
    /// the point is on the skin, at most a metre and a half from the pelvis.</summary>
    public const float ExplicitRadiusMetres = 2.0f;

    /// <summary>Proximity focus: the camera within this distance of the avatar ...</summary>
    public const float ProximityMetres = 6.0f;

    /// <summary>... and the avatar within about 37 degrees of the view axis.</summary>
    public const float ProximityMinCosAngle = 0.8f;

    /// <summary>A candidate must beat the current subject's score by this much to replace it.</summary>
    public const float SwitchMargin = 0.75f;

    public const double LossGraceSeconds = 1.5;

    /// <summary>Added to a proximity score so any explicit focus (score 0..<see cref="ExplicitRadiusMetres"/>)
    /// outranks any proximity one.</summary>
    private const float ProximityBase = 10f;

    /// <param name="Id">The avatar.</param>
    /// <param name="DistanceToCamera">Metres from the camera to the avatar.</param>
    /// <param name="DistanceToFocusPoint">Metres from the camera's explicit focus point to the avatar, or
    /// <see cref="float.NaN"/> when the camera has no explicit avatar focus.</param>
    /// <param name="CosAngle">Cosine of the angle between the view axis and the direction to the avatar.</param>
    public readonly record struct Candidate(Guid Id, float DistanceToCamera, float DistanceToFocusPoint, float CosAngle);

    private Guid _current;
    private double _lostSince = double.NaN;

    /// <summary>The current subject, or <see cref="Guid.Empty"/>.</summary>
    public Guid Current => _current;

    /// <summary>True when the last <see cref="Update"/> changed the subject.</summary>
    public bool Changed { get; private set; }

    /// <summary>Lower is better; <see cref="float.PositiveInfinity"/> when the candidate does not qualify.</summary>
    internal static float Score(in Candidate c)
    {
        if (!float.IsNaN(c.DistanceToFocusPoint) && c.DistanceToFocusPoint <= ExplicitRadiusMetres)
            return c.DistanceToFocusPoint;
        if (c.DistanceToCamera <= ProximityMetres && c.CosAngle >= ProximityMinCosAngle)
            return ProximityBase + c.DistanceToCamera;
        return float.PositiveInfinity;
    }

    public Guid Update(double nowSeconds, IReadOnlyList<Candidate> candidates)
    {
        Guid previous = _current;

        Guid best = Guid.Empty;
        float bestScore = float.PositiveInfinity;
        float currentScore = float.PositiveInfinity;
        foreach (var c in candidates)
        {
            float s = Score(c);
            if (c.Id == _current) currentScore = s;
            if (s < bestScore) { bestScore = s; best = c.Id; }
        }

        if (!float.IsPositiveInfinity(currentScore))
        {
            _lostSince = double.NaN;
            if (best != _current && bestScore + SwitchMargin < currentScore) _current = best;
        }
        else if (best != Guid.Empty)
        {
            _current = best;
            _lostSince = double.NaN;
        }
        else if (_current != Guid.Empty)
        {
            if (double.IsNaN(_lostSince)) _lostSince = nowSeconds;
            if (nowSeconds - _lostSince > LossGraceSeconds)
            {
                _current = Guid.Empty;
                _lostSince = double.NaN;
            }
        }

        Changed = _current != previous;
        return _current;
    }
}
