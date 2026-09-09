using System.Numerics;

namespace SLNG.Core.Components;

/// <summary>
/// Engine-agnostic representation of a Second Life particle system -- the decoded ObjectUpdate
/// particle block, in <c>System.Numerics</c> and Core types so the renderer never sees a
/// LibreMetaverse type (AGENTS.md's layering rule).
///
/// <para>The block has two sections and each one carries its own flags field and its own
/// "max age", with different meanings. Mixing them up is silent, not fatal, so the names here
/// keep the section explicit:</para>
/// <list type="table">
///   <item><term><see cref="SourceFlags"/> / <see cref="PartDataFlags"/></term>
///     <description>emitter flags (2 bits) vs. per-particle flags (<c>PSYS_PART_FLAGS</c>).</description></item>
///   <item><term><see cref="SourceMaxAge"/> / <see cref="PartMaxAge"/></term>
///     <description>how long the EMITTER runs (<c>PSYS_SRC_MAX_AGE</c>, 0 = forever) vs. how long
///     one PARTICLE lives (<c>PSYS_PART_MAX_AGE</c>, 0 = dies immediately).</description></item>
/// </list>
///
/// <para>Values are already sanitized and clamped to the ranges the real viewer enforces --
/// see <c>SLNG.Net</c>'s particle converter. In particular no field is NaN, which also means
/// the record's value equality is usable as a "did anything change?" test.</para>
/// </summary>
/// <param name="SourceFlags">Emitter flags. See <see cref="SlParticleSourceFlags"/>.</param>
/// <param name="Pattern">Emission pattern (<c>PSYS_SRC_PATTERN</c>).</param>
/// <param name="SourceMaxAge">Seconds the emitter runs before it stops, or 0 for forever
/// (<c>PSYS_SRC_MAX_AGE</c>). Counted together with <paramref name="SourceStartAge"/>.</param>
/// <param name="SourceStartAge">Age the emitter is considered to already have when it starts
/// (<c>PSYS_SRC_MAX_AGE</c>'s partner in the viewer's death test, llviewerpartsource.cpp:192).</param>
/// <param name="InnerAngle">Inner cone/fan half-angle in radians (<c>PSYS_SRC_ANGLE_BEGIN</c>).</param>
/// <param name="OuterAngle">Outer cone/fan half-angle in radians (<c>PSYS_SRC_ANGLE_END</c>).</param>
/// <param name="BurstRate">Seconds between bursts (<c>PSYS_SRC_BURST_RATE</c>).</param>
/// <param name="BurstRadius">Metres from the source at which particles appear
/// (<c>PSYS_SRC_BURST_RADIUS</c>), along their own emission direction.</param>
/// <param name="BurstSpeedMin">Minimum initial speed in m/s (<c>PSYS_SRC_BURST_SPEED_MIN</c>).</param>
/// <param name="BurstSpeedMax">Maximum initial speed in m/s (<c>PSYS_SRC_BURST_SPEED_MAX</c>).</param>
/// <param name="BurstPartCount">Particles emitted per burst (<c>PSYS_SRC_BURST_PART_COUNT</c>).</param>
/// <param name="AngularVelocity">Rotation applied to the emitter's own frame each second
/// (<c>PSYS_SRC_OMEGA</c>), in the source's local space.</param>
/// <param name="PartAcceleration">Constant acceleration applied to every live particle
/// (<c>PSYS_SRC_ACCEL</c>), in SL region axes (X east, Y north, Z up), m/s^2.</param>
/// <param name="TextureId">Particle texture, or <see cref="Guid.Empty"/> for the viewer default.</param>
/// <param name="TargetId">Target object for the target/beam flags. Not implemented by SLNG.</param>
/// <param name="PartDataFlags">Per-particle flags. See <see cref="SlParticleDataFlags"/>.</param>
/// <param name="PartMaxAge">Seconds one particle lives (<c>PSYS_PART_MAX_AGE</c>). 0 means the
/// particle is dead on its first simulation step -- the viewer's death test is
/// <c>age &gt; maxAge</c> (llviewerpartsim.cpp:400), so 0 is "nothing renders", not "forever".</param>
/// <param name="PartStartColor">RGBA at birth (<c>PSYS_PART_START_COLOR</c>/<c>_ALPHA</c>).</param>
/// <param name="PartEndColor">RGBA at death (<c>PSYS_PART_END_COLOR</c>/<c>_ALPHA</c>).</param>
/// <param name="PartStartScaleX">Billboard width in metres at birth. This is the FULL width --
/// the viewer builds the quad as centre +/- 0.5 * scale (llvopartgroup.cpp:549-550).</param>
/// <param name="PartStartScaleY">Billboard height in metres at birth.</param>
/// <param name="PartEndScaleX">Billboard width in metres at death.</param>
/// <param name="PartEndScaleY">Billboard height in metres at death.</param>
public record ParticleSystemData(
    SlParticleSourceFlags SourceFlags,
    SlParticlePattern Pattern,
    float SourceMaxAge,
    float SourceStartAge,
    float InnerAngle,
    float OuterAngle,
    float BurstRate,
    float BurstRadius,
    float BurstSpeedMin,
    float BurstSpeedMax,
    byte BurstPartCount,
    Vector3 AngularVelocity,
    Vector3 PartAcceleration,
    Guid TextureId,
    Guid TargetId,
    SlParticleDataFlags PartDataFlags,
    float PartMaxAge,
    Vector4 PartStartColor,
    Vector4 PartEndColor,
    float PartStartScaleX,
    float PartStartScaleY,
    float PartEndScaleX,
    float PartEndScaleY,
    // Defaulted so the existing positional construction keeps compiling, and defaulted to the
    // pair SL itself uses when a script sets neither -- ordinary alpha blending.
    SlParticleBlendFunc BlendFuncSource = SlParticleBlendFunc.SourceAlpha,
    SlParticleBlendFunc BlendFuncDest = SlParticleBlendFunc.OneMinusSourceAlpha
)
{
    /// <summary>True when this emitter asks for ADDITIVE blending — a destination factor of
    /// <see cref="SlParticleBlendFunc.One"/>, which is how flames, glows and light shafts are
    /// written: overlapping quads accumulate towards white instead of each occluding the last.
    ///
    /// <para>The source factor is deliberately not part of the test. Both common additive
    /// spellings (<c>SourceAlpha, One</c> and <c>One, One</c>) map to the same thing in a renderer
    /// that only offers Mix/Add, and it is the destination factor that decides whether the result
    /// accumulates.</para></summary>
    public bool IsAdditive => BlendFuncDest == SlParticleBlendFunc.One;

    /// <summary>True when the pair is neither ordinary alpha blending nor additive — something a
    /// Mix/Add-only renderer cannot express (multiply, subtract, dest-colour tricks). Callers use
    /// it to say so once rather than silently drawing the wrong thing.</summary>
    public bool HasUnsupportedBlendFunc =>
        !IsAdditive
        && !(BlendFuncSource == SlParticleBlendFunc.SourceAlpha
             && BlendFuncDest == SlParticleBlendFunc.OneMinusSourceAlpha);

    /// <summary>True if the emitter can never produce a visible particle, whatever the renderer
    /// does with it: a particle that is dead on its first step, or a burst of nothing.</summary>
    public bool IsInert => PartMaxAge <= 0f || BurstPartCount == 0;

    /// <summary>Particles alive at once once the emitter reaches steady state: the emission rate
    /// (<see cref="BurstPartCount"/> per <see cref="BurstRate"/> seconds) times how long each one
    /// lives. This is the pool size a fixed-size particle system has to allocate to reproduce
    /// SL's burst emission as a continuous stream.</summary>
    public int SteadyStateParticleCount(int cap)
    {
        if (IsInert)
        {
            return 0;
        }

        double perSecond = BurstPartCount / (double)BurstRate;
        double alive = Math.Ceiling(perSecond * PartMaxAge);
        return (int)Math.Clamp(alive, 1, cap);
    }
}
