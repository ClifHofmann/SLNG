using System.Numerics;
using SLNG.Core.Components;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The two derived quantities on <see cref="ParticleSystemData"/>. Both exist because a
/// fixed-pool particle system (Godot's, and every other engine's) has to be told a size, while
/// SL describes emission as "N particles every T seconds" and never states one.
/// </summary>
public class ParticleSystemDataTests
{
    /// <summary>
    /// The fountain from <c>scratch/TestParticles.lsl</c>: 10 particles per 0.1 s burst, each
    /// living 3 s, and -- this is the part that broke the renderer -- <c>PSYS_SRC_MAX_AGE 0.0</c>,
    /// the default "emit forever".
    /// </summary>
    private static ParticleSystemData Fountain(
        float sourceMaxAge = 0f,
        float partMaxAge = 3f,
        float burstRate = 0.1f,
        byte burstPartCount = 10) => new(
            SourceFlags: SlParticleSourceFlags.UseNewAngle,
            Pattern: SlParticlePattern.Explode,
            SourceMaxAge: sourceMaxAge,
            SourceStartAge: 0f,
            InnerAngle: 0f,
            OuterAngle: 0f,
            BurstRate: burstRate,
            BurstRadius: 0.5f,
            BurstSpeedMin: 1f,
            BurstSpeedMax: 3f,
            BurstPartCount: burstPartCount,
            AngularVelocity: Vector3.Zero,
            PartAcceleration: new Vector3(0f, 0f, -1f),
            TextureId: Guid.Empty,
            TargetId: Guid.Empty,
            PartDataFlags: SlParticleDataFlags.InterpColor | SlParticleDataFlags.InterpScale
                | SlParticleDataFlags.Emissive,
            PartMaxAge: partMaxAge,
            PartStartColor: new Vector4(1f, 0.5f, 0f, 1f),
            PartEndColor: new Vector4(1f, 0f, 0f, 1f),
            PartStartScaleX: 0.1f,
            PartStartScaleY: 0.1f,
            PartEndScaleX: 1f,
            PartEndScaleY: 1f);

    // 10 particles / 0.1 s = 100 per second, each alive for 3 s.
    [Fact]
    public void SteadyStateParticleCount_is_the_emission_rate_times_the_particle_lifetime()
    {
        Assert.Equal(300, Fountain().SteadyStateParticleCount(cap: 4096));
    }

    // The regression this whole class exists for: the renderer used to be handed the SOURCE max
    // age (0 = emit forever) where the PARTICLE max age belongs. Through this formula a 0 turned
    // the fountain into a pool of exactly one particle, which is why nothing was visible.
    [Fact]
    public void An_immortal_emitter_does_not_collapse_the_pool()
    {
        ParticleSystemData immortal = Fountain(sourceMaxAge: 0f);

        Assert.Equal(0f, immortal.SourceMaxAge);
        Assert.Equal(300, immortal.SteadyStateParticleCount(cap: 4096));
    }

    [Fact]
    public void SteadyStateParticleCount_respects_the_cap()
    {
        // SL permits a burst of 255 every 10 ms -- 25500 alive at a 1 s lifetime.
        ParticleSystemData firehose = Fountain(partMaxAge: 1f, burstRate: 0.01f, burstPartCount: 255);

        Assert.Equal(2048, firehose.SteadyStateParticleCount(cap: 2048));
    }

    // A particle whose max age is 0 is already past it on its first simulation step, so the
    // viewer kills it before it is ever drawn (llviewerpartsim.cpp:400). 0 is "nothing renders",
    // not "lives forever".
    [Fact]
    public void A_zero_particle_lifetime_is_inert()
    {
        ParticleSystemData dead = Fountain(partMaxAge: 0f);

        Assert.True(dead.IsInert);
        Assert.Equal(0, dead.SteadyStateParticleCount(cap: 4096));
    }

    [Fact]
    public void A_burst_of_nothing_is_inert()
    {
        Assert.True(Fountain(burstPartCount: 0).IsInert);
    }

    [Fact]
    public void A_running_emitter_is_not_inert()
    {
        Assert.False(Fountain().IsInert);
    }

    // Value equality is what the renderer uses to tell "the script changed the particle system"
    // from "yet another position update arrived", so it has to survive a round trip through the
    // record's own copy semantics.
    [Fact]
    public void Identical_systems_compare_equal()
    {
        Assert.Equal(Fountain(), Fountain());
        Assert.NotEqual(Fountain(), Fountain(partMaxAge: 2.5f));
    }
}
