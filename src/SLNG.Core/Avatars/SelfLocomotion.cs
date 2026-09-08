using System;
using System.Collections.Generic;

namespace SLNG.Core.Avatars;

/// <summary>The self avatar's movement state for one frame, as seen by the client's own input /
/// controller -- no network round-trip. Fed to <see cref="SelfLocomotion.Predict"/>.</summary>
public readonly record struct LocomotionState(
    bool Sitting,
    bool Flying,
    bool Grounded,
    /// <summary>A forward or backward walk key is held.</summary>
    bool MovingForward,
    /// <summary>A left turn key is held (and not walking forward/back).</summary>
    bool TurningLeft,
    /// <summary>A right turn key is held (and not walking forward/back).</summary>
    bool TurningRight,
    /// <summary>A crouch key is held, on the ground, not flying.</summary>
    bool Crouching,
    /// <summary>Horizontal speed in m/s (ground plane). Only used to tell walk from run and to
    /// catch motion the key state alone misses (e.g. being pushed, or momentum after a key
    /// release). Comes from the network-reported velocity, so it may lag -- that is fine here,
    /// the latency-critical signal is the key state.</summary>
    float SpeedHoriz,
    /// <summary>Vertical speed in m/s, + is up. Distinguishes hover-up / hover-down while flying.</summary>
    float SpeedVert);

/// <summary>
/// Which built-in locomotion animation the SELF avatar should be playing right now, decided from
/// the client's own movement state instead of waiting for the simulator to echo an
/// <c>AvatarAnimation</c> back (FEAT-ANIM-01: "die Lauf-Animation kommt zu spät wenn es laggt").
///
/// <para>The reference viewer plays these compiled-in motions locally and immediately from
/// <c>gAgent</c>'s movement state (<c>llagent.cpp</c> / <c>LLVOAvatar::idleUpdateWalkRun...</c>);
/// it never waits for its own echo. This is the engine- and protocol-agnostic half of that: a
/// pure state -> anim-id decision. The renderer substitutes the result for the sim's echo of
/// these same ids and lets an AO's higher-priority custom anims win per bone.</para>
///
/// <para>UUIDs are the <c>ANIM_AGENT_*</c> constants from <c>indra/llcommon/llanimationstates.cpp</c>
/// (identical to LibreMetaverse's <c>Animations.*</c>), kept here as <see cref="Guid"/> so this
/// stays out of both the engine and the protocol layer -- same rationale as
/// <c>BakeChannelNames</c>.</para>
/// </summary>
public static class SelfLocomotion
{
    public static readonly Guid Stand = new("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");
    public static readonly Guid Walk = new("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0");
    public static readonly Guid Run = new("05ddbff8-aaa9-92a1-2b74-8fe77a29b445");
    public static readonly Guid TurnLeft = new("56e0ba0d-4a9f-7f27-6117-32f2ebbf6135");
    public static readonly Guid TurnRight = new("2d6daa51-3192-6794-8e2e-a15f8338ec30");
    public static readonly Guid Fly = new("aec4610c-757f-bc4e-c092-c6e9caf18daf");
    public static readonly Guid FlySlow = new("2b5a38b2-5e00-3a97-a495-4c826bc443e6");
    public static readonly Guid Hover = new("4ae8016b-31b9-03bb-c401-b1ea941db41d");
    public static readonly Guid HoverUp = new("62c5de58-cb33-5743-3d07-9e4cd4352864");
    public static readonly Guid HoverDown = new("20f063ea-8306-2562-0b07-5c853b37b31e");
    public static readonly Guid FallDown = new("666307d9-a860-572d-6fd4-c3ab8865c094");
    public static readonly Guid PreJump = new("7a4e87fe-de39-6fcb-6223-024b00893244");
    public static readonly Guid Jump = new("2305bd75-1ca9-b03b-1faa-b176b8a8c49e");
    public static readonly Guid Land = new("7a17b059-12b2-41b1-570a-186368b6aa6f");
    public static readonly Guid MediumLand = new("f4f00d6e-b9fe-9292-f4cb-0ae06ea58d57");
    public static readonly Guid Crouch = new("201f3fdf-cb1f-dbec-201f-7333e328ae7c");
    public static readonly Guid CrouchWalk = new("47f5f6fb-22e5-ae44-f871-73aaaf4a6022");
    public static readonly Guid Standup = new("3da1d753-028a-5446-24f3-9c9b856d9422");

    /// <summary>The built-in ids the prediction can emit — the renderer strips exactly these out of
    /// the sim's echoed animation set (only while it has a non-null prediction to substitute) so
    /// the two don't fight. A custom AO animation is NOT in here — it stays in the set and wins per
    /// bone via its authored priority. <c>Sit</c>/<c>SitGround</c>/<c>Standup</c> are deliberately
    /// absent: the prediction never emits them (it returns null when sitting), and filtering the
    /// server's sit/stand-up animation would strip the pose with nothing to replace it.</summary>
    public static readonly IReadOnlySet<Guid> All = new HashSet<Guid>
    {
        Stand, Walk, Run, TurnLeft, TurnRight, Fly, FlySlow, Hover, HoverUp, HoverDown,
        FallDown, PreJump, Jump, Land, MediumLand, Crouch, CrouchWalk,
    };

    /// <summary>The set to warm the animation cache with at login so the first prediction is never
    /// a live asset fetch.</summary>
    public static readonly IReadOnlyList<Guid> Prefetch = new[]
    {
        Stand, Walk, Run, TurnLeft, TurnRight, Fly, FlySlow, Hover, HoverUp, HoverDown,
        FallDown, PreJump, Jump, Land, MediumLand, Crouch, CrouchWalk, Standup,
    };

    /// <summary>SL's default agent walk speed is ~3.2 m/s and run ~5.2 m/s; this separates them.</summary>
    public const float RunSpeedThreshold = 4.6f;

    /// <summary>Below this the avatar is treated as stationary (noise / residual drift).</summary>
    public const float MoveSpeedThreshold = 0.1f;

    /// <summary>Vertical speed past which a flying avatar is hovering up or down rather than level.</summary>
    public const float VerticalMoveThreshold = 0.5f;

    /// <summary>The locomotion animation the self avatar should play this frame, or
    /// <see langword="null"/> when it is sitting (the server drives the sit pose) — nothing to
    /// predict.</summary>
    public static Guid? Predict(in LocomotionState s)
    {
        if (s.Sitting) return null;

        bool moving = s.MovingForward || s.SpeedHoriz > MoveSpeedThreshold;

        if (s.Flying)
        {
            if (s.SpeedVert > VerticalMoveThreshold) return HoverUp;
            if (s.SpeedVert < -VerticalMoveThreshold) return HoverDown;
            return moving ? Fly : Hover;
        }

        if (!s.Grounded)
        {
            // Airborne and not flying: falling. (Jump/prejump need a jump-input edge the
            // controller doesn't expose yet — a follow-up; FallDown covers the common case.)
            return FallDown;
        }

        if (s.Crouching) return moving ? CrouchWalk : Crouch;

        if (moving) return s.SpeedHoriz > RunSpeedThreshold ? Run : Walk;

        if (s.TurningLeft) return TurnLeft;
        if (s.TurningRight) return TurnRight;

        return Stand;
    }
}
