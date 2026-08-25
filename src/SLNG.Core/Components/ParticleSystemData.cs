using System.Numerics;
using System;

namespace SLNG.Core.Components;

/// <summary>
/// Engine-agnostic representation of a Second Life particle system.
/// Maps 1:1 to LibreMetaverse's Primitive.ParticleSystem but uses
/// System.Numerics and Core types so the renderer does not depend on LibreMetaverse.
/// </summary>
public record ParticleSystemData(
    uint Flags,
    byte Pattern,
    float MaxAge,
    float StartAge,
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
    Vector4 StartColor,
    Vector4 EndColor,
    float StartScaleX,
    float StartScaleY,
    float EndScaleX,
    float EndScaleY
);
