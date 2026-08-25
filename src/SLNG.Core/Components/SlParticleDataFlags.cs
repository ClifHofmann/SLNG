namespace SLNG.Core.Components;

/// <summary>
/// The per-particle flags a script sets with <c>PSYS_PART_FLAGS</c> -- the 32-bit field in the
/// ObjectUpdate particle block's part-data section (LibreMetaverse:
/// <c>Primitive.ParticleSystem.PartDataFlags</c>, viewer: <c>LLPartData::mFlags</c>).
///
/// <para>Not to be confused with <see cref="SlParticleSourceFlags"/> -- see the remarks there.</para>
/// </summary>
[Flags]
public enum SlParticleDataFlags : uint
{
    None = 0,

    /// <summary>Interpolate colour from start to end colour over the particle's life.</summary>
    InterpColor = 0x001,

    /// <summary>Interpolate scale from start to end scale over the particle's life.</summary>
    InterpScale = 0x002,

    /// <summary>Bounce off the object's z-height. Not implemented by SLNG.</summary>
    Bounce = 0x004,

    /// <summary>Particles are affected by regional wind. Not implemented by SLNG.</summary>
    Wind = 0x008,

    /// <summary>Particles move with the emitter instead of staying where they were emitted.</summary>
    FollowSrc = 0x010,

    /// <summary>The particle billboard's up axis follows its velocity. Not implemented by SLNG.</summary>
    FollowVelocity = 0x020,

    /// <summary>Particles are pulled toward the target object. Not implemented by SLNG.</summary>
    TargetPos = 0x040,

    /// <summary>Particles travel linearly to the target. Not implemented by SLNG.</summary>
    TargetLinear = 0x080,

    /// <summary>Fullbright: the particle is not lit by the scene (viewer: <c>LLFace::FULLBRIGHT</c>,
    /// llvopartgroup.cpp:346). NOT additive blending -- that is what the separate blend-func
    /// extension is for.</summary>
    Emissive = 0x100,

    /// <summary>Beam to target. Not implemented by SLNG.</summary>
    Beam = 0x200,

    /// <summary>Ribbon (particles connected into a strip). Not implemented by SLNG.</summary>
    Ribbon = 0x400,

    /// <summary>The block carries the glow extension. Not implemented by SLNG.</summary>
    DataGlow = 0x10000,

    /// <summary>The block carries the blend-function extension. Not implemented by SLNG.</summary>
    DataBlend = 0x20000,
}
