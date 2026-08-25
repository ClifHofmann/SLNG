using LibreMetaverse;
using SLNG.Core.Components;

namespace SLNG.Net;

/// <summary>
/// Converts LibreMetaverse's decoded ObjectUpdate particle block into the neutral
/// <see cref="ParticleSystemData"/>. This is the boundary AGENTS.md's layering rule draws:
/// no LibreMetaverse type may reach the renderer.
///
/// <para>Two of LibreMetaverse's field names invite exactly the wrong reading, and both did
/// reach the renderer before this converter existed:</para>
/// <list type="bullet">
///   <item><description><c>ParticleSystem.MaxAge</c> is the SOURCE lifetime
///   (<c>PSYS_SRC_MAX_AGE</c>, "max_age" on the wire, 0 = emit forever). The particle lifetime
///   is the separate <c>PartMaxAge</c> ("part_max_age"). Using the former as the latter turns
///   the common "emitter runs forever" case into "particles live 0 seconds".</description></item>
///   <item><description><c>ParticleSystem.PartFlags</c> is the SOURCE flag word, which only
///   defines ObjectRelative (0x01) and UseNewAngle (0x02). The <c>PSYS_PART_FLAGS</c> bits
///   (InterpColor, InterpScale, FollowSrc, Emissive, ...) are the separate
///   <c>PartDataFlags</c>. They overlap numerically, so reading the wrong one produces
///   plausible garbage instead of an error.</description></item>
/// </list>
///
/// <para>Values are sanitized here rather than at the point of use. LibreMetaverse's
/// fixed-point unpack can yield NaN (see commit 5603670), and a NaN anywhere in the record
/// would also break its value equality -- which the renderer relies on to tell "the script
/// changed the particle system" from "another position update arrived".</para>
/// </summary>
internal static class ParticleSystemConverter
{
    /// <summary>The viewer's own floor on the burst interval, applied while unpacking the block
    /// (llpartdata.cpp:253). Everything downstream divides by this, so it is not optional.</summary>
    internal const float MinBurstRate = 0.01f;

    /// <summary>Viewer limits: llpartdata.cpp:52 (MAX_PART_SCALE), llpartdata.cpp:162
    /// (max age), llpartdata.h:213-215 (radius and speed), llpartdata.cpp:416-418 (accel).</summary>
    internal const float MaxPartScale = 4f;
    internal const float MaxPartMaxAge = 30f;
    internal const float MaxBurstRadius = 50f;
    internal const float MaxBurstSpeed = 100f;
    internal const float MaxPartAcceleration = 100f;

    /// <summary>
    /// Converts a decoded particle block, or returns null if the object has no particle system.
    /// </summary>
    internal static ParticleSystemData? FromWire(Primitive.ParticleSystem sys)
    {
        if (!CarriesSystem(sys))
        {
            return null;
        }

        return new ParticleSystemData(
            SourceFlags: (SlParticleSourceFlags)sys.PartFlags,
            Pattern: (SlParticlePattern)(byte)sys.Pattern,
            SourceMaxAge: Clamp(sys.MaxAge, 0f, float.MaxValue),
            SourceStartAge: Clamp(sys.StartAge, 0f, float.MaxValue),
            InnerAngle: Finite(sys.InnerAngle),
            OuterAngle: Finite(sys.OuterAngle),
            BurstRate: Math.Max(MinBurstRate, Finite(sys.BurstRate, MinBurstRate)),
            BurstRadius: Clamp(sys.BurstRadius, 0f, MaxBurstRadius),
            BurstSpeedMin: Clamp(sys.BurstSpeedMin, -MaxBurstSpeed, MaxBurstSpeed),
            BurstSpeedMax: Clamp(sys.BurstSpeedMax, -MaxBurstSpeed, MaxBurstSpeed),
            BurstPartCount: sys.BurstPartCount,
            AngularVelocity: Vector(sys.AngularVelocity, MaxBurstSpeed),
            PartAcceleration: Vector(sys.PartAcceleration, MaxPartAcceleration),
            TextureId: sys.Texture.Guid,
            TargetId: sys.Target.Guid,
            PartDataFlags: (SlParticleDataFlags)sys.PartDataFlags,
            PartMaxAge: Clamp(sys.PartMaxAge, 0f, MaxPartMaxAge),
            PartStartColor: Color(sys.PartStartColor),
            PartEndColor: Color(sys.PartEndColor),
            PartStartScaleX: Clamp(sys.PartStartScaleX, 0f, MaxPartScale),
            PartStartScaleY: Clamp(sys.PartStartScaleY, 0f, MaxPartScale),
            PartEndScaleX: Clamp(sys.PartEndScaleX, 0f, MaxPartScale),
            PartEndScaleY: Clamp(sys.PartEndScaleY, 0f, MaxPartScale));
    }

    /// <summary>
    /// Whether the ObjectUpdate carried a particle block at all.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately NOT a CRC test. The real viewer's only presence check is the size of
    /// the PSBlock (llpartdata.cpp:371-393) -- it reads <c>mCRC</c> off the wire and then never
    /// looks at it again anywhere in the source. Gating on the CRC is therefore our invention,
    /// and it throws away the entire system of any object whose block arrives with a zero there,
    /// which renders as "this object has no particles" and nothing else.</para>
    ///
    /// <para>LibreMetaverse hands over a struct rather than the block, so the size is gone by the
    /// time we see it; the equivalent test is whether anything survived the unpack. An absent
    /// block leaves every field at the constructor's defaults, which is also exactly what the
    /// simulator sends for <c>llParticleSystem([])</c>. A system that IS present but degenerate
    /// -- all zeroes -- is indistinguishable from that, and correctly so: it emits nothing in the
    /// viewer either (see <see cref="ParticleSystemData.IsInert"/>).</para>
    /// </remarks>
    private static bool CarriesSystem(Primitive.ParticleSystem sys) =>
        sys.CRC != 0
        || sys.Pattern != Primitive.ParticleSystem.SourcePattern.None
        || sys.PartDataFlags != Primitive.ParticleSystem.ParticleDataFlags.None
        || sys.BurstPartCount > 0
        || sys.PartMaxAge > 0f
        || sys.Texture != UUID.Zero;

    private static float Finite(float v, float fallback = 0f) => float.IsFinite(v) ? v : fallback;

    private static float Clamp(float v, float min, float max) => Math.Clamp(Finite(v), min, max);

    private static System.Numerics.Vector3 Vector(Vector3 v, float limit) =>
        new(Clamp(v.X, -limit, limit), Clamp(v.Y, -limit, limit), Clamp(v.Z, -limit, limit));

    private static System.Numerics.Vector4 Color(Color4 c) =>
        new(Clamp(c.R, 0f, 1f), Clamp(c.G, 0f, 1f), Clamp(c.B, 0f, 1f), Clamp(c.A, 0f, 1f));
}
