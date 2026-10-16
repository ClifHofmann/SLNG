using System;
using System.Numerics;
using SLNG.Core.ECS;

namespace SLNG.Core.Components;

/// <summary>FEAT-RENDER-27: an object turning under target omega (<c>llTargetOmega</c>, the wire's
/// AngularVelocity), done on the client the way the viewer does it.
///
/// <para>The simulator does not stream the rotation of a non-physical spinner: it sends the omega
/// once and the viewer turns the object itself, every frame (<c>LLViewerObject::idleUpdate</c> ->
/// <c>applyAngularVelocity</c>, llviewerobject.cpp:2510 and :7305). What the viewer has turned so
/// far is kept apart from the server's rotation (<c>mAngularVelocityRot</c>) and laid on top of it
/// again after every update (:2370), so an update that leaves the rotation alone does not snap the
/// object back. That is how a swing works: its script only flips the sign of the omega, and the
/// object keeps going from wherever it is.</para>
///
/// <para>Frames: the omega axis is in the PARENT's frame (the region for a root, the root for a
/// child), because the viewer applies the step after the object's own rotation --
/// <c>getRotation() * dQ</c> in LL's order, whose product is the reverse of the Hamilton product
/// used here (llquaternion.cpp:534). So <see cref="Rotation"/> is applied as
/// <c>Rotation * serverRotation</c>, never the other way round.</para>
///
/// <para>Present only on an object that has turned at least once; every other object carries none
/// and costs nothing.</para></summary>
public sealed class SpinComponent : IComponent
{
    /// <summary>Squared rate (rad/s) at or below which the viewer leaves the object alone
    /// (applyAngularVelocity, llviewerobject.cpp:7312).</summary>
    public const float MinOmegaSquared = 0.00001f;

    /// <summary>One whole turn of a quaternion. The angle is wrapped at this so it never grows
    /// large enough to lose precision; a quaternion repeats every 4 pi, a rotation every 2 pi.</summary>
    private const float QuaternionPeriod = 4f * MathF.PI;

    /// <summary>The latest omega the simulator sent, in radians per second about the axis it points
    /// along, in the parent's frame.</summary>
    public Vector3 AngularVelocity { get; private set; }

    /// <summary>Everything turned on the client since the last reset, in the parent's frame:
    /// <c>mAngularVelocityRot</c>. Identity when nothing has been turned.</summary>
    public Quaternion Rotation { get; private set; } = Quaternion.Identity;

    /// <summary>The originating simulator's time dilation as of the last update. The viewer scales
    /// the step by its region's (llviewerobject.cpp:2506).</summary>
    public float TimeDilation { get; set; } = 1f;

    /// <summary>True while the object is actually turning.</summary>
    public bool IsTurning => AngularVelocity.LengthSquared() > MinOmegaSquared;

    // Rotation = AxisAngle(axis, _angle) * _base. Kept as an angle rather than multiplied up frame
    // by frame, so an object left spinning for hours does not drift off unit length.
    private Quaternion _base = Quaternion.Identity;
    private float _angle;

    private Quaternion _previousServerRotation;
    private bool _hasPreviousServerRotation;

    /// <summary>Takes one object update, with the viewer's rules for what it does to the rotation
    /// turned so far (processUpdateMessage, llviewerobject.cpp:2349-2370):
    /// <list type="bullet">
    /// <item>a server rotation different from the last one starts over (<c>resetRot</c>);</item>
    /// <item>a new omega with the same rotation keeps what was turned on a non-physical object
    /// (<c>resetRotTime</c>) and starts over on a physical one;</item>
    /// <item>and, before either, an uncompressed full update whose omega is exactly zero starts
    /// over (<paramref name="zeroOmegaResets"/>, :1416).</item>
    /// </list></summary>
    /// <param name="serverRotation">The rotation the update carries: the prim's own, relative to
    /// its parent for a child.</param>
    /// <param name="angularVelocity">The omega the update carries.</param>
    /// <param name="isPhysical">The object's physics flag (<c>flagUsePhysics</c>).</param>
    /// <param name="zeroOmegaResets">True for an uncompressed full ObjectUpdate; only those reset
    /// on a zero omega.</param>
    public void ApplyUpdate(Quaternion serverRotation, Vector3 angularVelocity, bool isPhysical, bool zeroOmegaResets)
    {
        if (zeroOmegaResets && angularVelocity == Vector3.Zero) Reset();

        bool omegaChanged = angularVelocity != AngularVelocity;
        if (!_hasPreviousServerRotation || serverRotation != _previousServerRotation)
        {
            Reset();
        }
        else if (omegaChanged)
        {
            if (isPhysical) Reset();
            else KeepTurned();
        }

        _previousServerRotation = serverRotation;
        _hasPreviousServerRotation = true;
        AngularVelocity = angularVelocity;
    }

    /// <summary>Turns the object on by <paramref name="seconds"/> of its omega (already scaled by
    /// time dilation). A no-op below <see cref="MinOmegaSquared"/>.</summary>
    public void Advance(float seconds)
    {
        float omegaSquared = AngularVelocity.LengthSquared();
        if (omegaSquared <= MinOmegaSquared || !(seconds > 0f)) return;

        float omega = MathF.Sqrt(omegaSquared);
        _angle += omega * seconds;
        if (_angle >= QuaternionPeriod) _angle %= QuaternionPeriod;
        // The step comes AFTER what was turned before (LL mAngularVelocityRot *= dQ, i.e. the
        // Hamilton dQ * accumulated), so a changed axis turns the already-turned object further.
        Rotation = Quaternion.CreateFromAxisAngle(AngularVelocity / omega, _angle) * _base;
    }

    /// <summary>The rotation to draw the prim with, given the one the simulator sent: LL's
    /// <c>new_rot * mAngularVelocityRot</c> (llviewerobject.cpp:2370).</summary>
    public Quaternion Apply(Quaternion serverRotation) => Rotation * serverRotation;

    /// <summary><c>resetRot</c>: drop everything turned so far.</summary>
    private void Reset()
    {
        _base = Quaternion.Identity;
        _angle = 0f;
        Rotation = Quaternion.Identity;
    }

    /// <summary><c>resetRotTime</c>: keep what was turned and turn on from there with the new omega.</summary>
    private void KeepTurned()
    {
        _base = Rotation;
        _angle = 0f;
    }
}
