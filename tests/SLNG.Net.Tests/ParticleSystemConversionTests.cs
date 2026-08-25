using LibreMetaverse;
using SLNG.Core.Components;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// Conversion of LibreMetaverse's decoded particle block to the neutral
/// <see cref="ParticleSystemData"/> at the SLNG.Net boundary (AGENTS.md: no LibreMetaverse type
/// crosses a public boundary).
///
/// <para>The inputs go through <c>GetBytes()</c> and back so the test travels the real wire
/// encoding, quantisation included -- the block's fixed-point fields have as little as 5
/// fractional bits, so nothing survives it exactly.</para>
///
/// <para>What these tests are guarding is a pair of name collisions in LibreMetaverse's own API.
/// <c>MaxAge</c>/<c>PartMaxAge</c> and <c>PartFlags</c>/<c>PartDataFlags</c> each look like the
/// obvious field to read and each is the wrong one; picking the wrong one produces a working,
/// silent, wrong particle system rather than an error.</para>
/// </summary>
public class ParticleSystemConversionTests
{
    private const float Tol = 0.02f;

    /// <summary>
    /// The fountain from <c>scratch/TestParticles.lsl</c>, as the simulator sends it.
    /// </summary>
    private static Primitive.ParticleSystem WireFountain()
    {
        var sys = new Primitive.ParticleSystem
        {
            CRC = 1,
            // PSYS_SRC_* -- the emitter. MaxAge 0 is the LSL default: emit forever.
            PartFlags = (uint)Primitive.ParticleSystem.ParticleFlags.UseNewAngle,
            Pattern = Primitive.ParticleSystem.SourcePattern.Explode,
            MaxAge = 0f,
            StartAge = 0f,
            InnerAngle = 0f,
            OuterAngle = 0f,
            BurstRate = 0.1f,
            BurstRadius = 0.5f,
            BurstSpeedMin = 1f,
            BurstSpeedMax = 3f,
            BurstPartCount = 10,
            AngularVelocity = Vector3.Zero,
            PartAcceleration = new Vector3(0f, 0f, -1f),
            Texture = UUID.Zero,
            Target = UUID.Zero,
            // PSYS_PART_* -- the particles.
            PartDataFlags = Primitive.ParticleSystem.ParticleDataFlags.InterpColor
                | Primitive.ParticleSystem.ParticleDataFlags.InterpScale
                | Primitive.ParticleSystem.ParticleDataFlags.Emissive,
            PartMaxAge = 3f,
            PartStartColor = new Color4(1f, 0.5f, 0f, 1f),
            PartEndColor = new Color4(1f, 0f, 0f, 1f),
            PartStartScaleX = 0.1f,
            PartStartScaleY = 0.2f,
            PartEndScaleX = 1f,
            PartEndScaleY = 2f,
            // The block only stays legacy-compatible with the default blend function. Anything
            // else makes GetBytes() emit the extended block and set the DataBlend marker bit in
            // the same flag word -- an encoding detail, not something the script asked for.
            BlendFuncSource = (byte)Primitive.ParticleSystem.BlendFunc.SourceAlpha,
            BlendFuncDest = (byte)Primitive.ParticleSystem.BlendFunc.OneMinusSourceAlpha,
        };

        return RoundTrip(sys);
    }

    private static Primitive.ParticleSystem RoundTrip(Primitive.ParticleSystem sys) =>
        new(sys.GetBytes(), 0);

    /// <summary>
    /// <c>PSYS_PART_MAX_AGE</c> (how long a particle lives) must come from <c>PartMaxAge</c>, and
    /// <c>PSYS_SRC_MAX_AGE</c> (how long the emitter runs) from <c>MaxAge</c>. They are different
    /// fields in different sections of the block, and the common script sets the second to 0.
    /// Reading the source age as the particle age is what made a 3-second fountain render as
    /// nothing at all.
    /// </summary>
    [Fact]
    public void The_two_max_ages_do_not_swap()
    {
        ParticleSystemData? data = ParticleSystemConverter.FromWire(WireFountain());

        Assert.NotNull(data);
        Assert.Equal(3f, data!.PartMaxAge, Tol);
        Assert.Equal(0f, data.SourceMaxAge, Tol);
        Assert.False(data.IsInert);
        // ~300 alive in steady state (10 per 0.1 s, 3 s each). Not exactly 300: the burst rate
        // has 8 fractional bits, so 0.1 s arrives as 0.09765625 and the emitter runs slightly
        // hot. The exact arithmetic is pinned without the wire in ParticleSystemDataTests.
        Assert.InRange(data.SteadyStateParticleCount(cap: 4096), 290, 320);
    }

    /// <summary>
    /// The <c>PSYS_PART_FLAGS</c> bits live in <c>PartDataFlags</c>. <c>PartFlags</c> is the
    /// source flag word and defines only ObjectRelative (0x01) and UseNewAngle (0x02) -- the two
    /// words overlap on exactly the bits that decide colour and scale interpolation, so reading
    /// the wrong one is invisible rather than fatal.
    /// </summary>
    [Fact]
    public void The_two_flag_words_do_not_swap()
    {
        ParticleSystemData? data = ParticleSystemConverter.FromWire(WireFountain());

        Assert.NotNull(data);
        Assert.Equal(
            SlParticleDataFlags.InterpColor | SlParticleDataFlags.InterpScale | SlParticleDataFlags.Emissive,
            data!.PartDataFlags);
        Assert.Equal(SlParticleSourceFlags.UseNewAngle, data.SourceFlags);
    }

    [Fact]
    public void Source_and_particle_parameters_survive_the_wire()
    {
        ParticleSystemData? data = ParticleSystemConverter.FromWire(WireFountain());

        Assert.NotNull(data);
        Assert.Equal(SlParticlePattern.Explode, data!.Pattern);
        Assert.Equal(0.1f, data.BurstRate, Tol);
        Assert.Equal(0.5f, data.BurstRadius, Tol);
        Assert.Equal(1f, data.BurstSpeedMin, Tol);
        Assert.Equal(3f, data.BurstSpeedMax, Tol);
        Assert.Equal(10, data.BurstPartCount);
        Assert.Equal(-1f, data.PartAcceleration.Z, Tol);
        Assert.Equal(1f, data.PartStartColor.X, Tol);
        Assert.Equal(0.5f, data.PartStartColor.Y, Tol);
        Assert.Equal(0f, data.PartEndColor.Y, Tol);
    }

    /// <summary>
    /// SL particles are rectangles: X and Y are independent, and a converter that carries only X
    /// silently squares every one of them.
    /// </summary>
    [Fact]
    public void The_two_scale_axes_stay_independent()
    {
        ParticleSystemData? data = ParticleSystemConverter.FromWire(WireFountain());

        Assert.NotNull(data);
        Assert.Equal(0.1f, data!.PartStartScaleX, Tol);
        Assert.Equal(0.2f, data.PartStartScaleY, Tol);
        Assert.Equal(1f, data.PartEndScaleX, Tol);
        Assert.Equal(2f, data.PartEndScaleY, Tol);
    }

    /// <summary>
    /// A CRC of 0 is the wire's "this object has no particle system" -- what a simulator sends
    /// for <c>llParticleSystem([])</c>, and what LibreMetaverse leaves behind for an object that
    /// never had one.
    /// </summary>
    [Fact]
    public void No_particle_system_converts_to_null()
    {
        Assert.Null(ParticleSystemConverter.FromWire(new Primitive.ParticleSystem()));
    }

    /// <summary>
    /// LibreMetaverse's fixed-point unpack can produce NaN. Beyond the crashes that motivated
    /// commit 5603670, a NaN anywhere in the record also breaks its value equality (NaN equals
    /// nothing, including itself), which would restart the particle system on every single
    /// ObjectUpdate -- killing every particle before it could be seen.
    /// </summary>
    [Fact]
    public void Nan_never_reaches_the_renderer()
    {
        var broken = new Primitive.ParticleSystem
        {
            CRC = 1,
            MaxAge = float.NaN,
            PartMaxAge = float.NaN,
            BurstRate = float.NaN,
            BurstRadius = float.NaN,
            BurstSpeedMin = float.NaN,
            BurstSpeedMax = float.PositiveInfinity,
            InnerAngle = float.NaN,
            OuterAngle = float.NaN,
            PartAcceleration = new Vector3(float.NaN, float.NaN, float.NegativeInfinity),
            AngularVelocity = new Vector3(float.NaN, 0f, 0f),
            PartStartColor = new Color4(float.NaN, 0f, 0f, float.NaN),
            PartEndColor = new Color4(0f, float.NaN, 0f, 0f),
            PartStartScaleX = float.NaN,
            PartStartScaleY = float.NaN,
            PartEndScaleX = float.NaN,
            PartEndScaleY = float.NaN,
            BurstPartCount = 4,
        };

        ParticleSystemData? data = ParticleSystemConverter.FromWire(broken);

        Assert.NotNull(data);
        foreach (float value in new[]
                 {
                     data!.SourceMaxAge, data.SourceStartAge, data.InnerAngle, data.OuterAngle,
                     data.BurstRate, data.BurstRadius, data.BurstSpeedMin, data.BurstSpeedMax,
                     data.PartMaxAge, data.PartStartScaleX, data.PartStartScaleY,
                     data.PartEndScaleX, data.PartEndScaleY,
                     data.PartAcceleration.X, data.PartAcceleration.Y, data.PartAcceleration.Z,
                     data.AngularVelocity.X, data.AngularVelocity.Y, data.AngularVelocity.Z,
                     data.PartStartColor.X, data.PartStartColor.W, data.PartEndColor.Y,
                 })
        {
            Assert.True(float.IsFinite(value), "every field must be finite");
        }

        // Equality has to work, which is the whole point of sanitizing here rather than at the
        // point of use.
        Assert.Equal(ParticleSystemConverter.FromWire(broken), data);
    }

    /// <summary>
    /// The viewer's own limits, applied where the viewer applies them: a burst rate floor while
    /// unpacking (llpartdata.cpp:253) and the setter clamps everything else goes through
    /// (llpartdata.cpp:52, 162, 416; llpartdata.h:213-215).
    /// </summary>
    [Fact]
    public void Out_of_range_values_are_clamped_to_the_viewer_limits()
    {
        var extreme = new Primitive.ParticleSystem
        {
            CRC = 1,
            BurstRate = 0f,
            BurstRadius = 9999f,
            BurstSpeedMin = -9999f,
            BurstSpeedMax = 9999f,
            PartMaxAge = 9999f,
            PartAcceleration = new Vector3(9999f, -9999f, 0f),
            // Colours are not in this list: LibreMetaverse's own Color4 constructor already
            // refuses anything outside 0..1, so the converter's clamp there is only a NaN guard.
            PartStartScaleX = 99f,
            PartEndScaleY = 99f,
            BurstPartCount = 1,
        };

        ParticleSystemData? data = ParticleSystemConverter.FromWire(extreme);

        Assert.NotNull(data);
        // Everything downstream divides by the burst rate, so a 0 must never get through.
        Assert.Equal(ParticleSystemConverter.MinBurstRate, data!.BurstRate);
        Assert.Equal(ParticleSystemConverter.MaxBurstRadius, data.BurstRadius);
        Assert.Equal(-ParticleSystemConverter.MaxBurstSpeed, data.BurstSpeedMin);
        Assert.Equal(ParticleSystemConverter.MaxBurstSpeed, data.BurstSpeedMax);
        Assert.Equal(ParticleSystemConverter.MaxPartMaxAge, data.PartMaxAge);
        Assert.Equal(ParticleSystemConverter.MaxPartAcceleration, data.PartAcceleration.X);
        Assert.Equal(-ParticleSystemConverter.MaxPartAcceleration, data.PartAcceleration.Y);
        Assert.Equal(ParticleSystemConverter.MaxPartScale, data.PartStartScaleX);
        Assert.Equal(ParticleSystemConverter.MaxPartScale, data.PartEndScaleY);
    }
}
