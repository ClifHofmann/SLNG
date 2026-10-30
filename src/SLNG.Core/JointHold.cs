using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>What happened to a held joint, for the <c>--diag</c> log.</summary>
public enum JointHoldEvent
{
    /// <summary>An animation stopped driving the joint; it now keeps its last value.</summary>
    Held,
    /// <summary>An animation drives the joint again; the hold is dropped.</summary>
    Replaced,
    /// <summary>The player was stopped or its skeleton reset; the hold is dropped.</summary>
    Cleared,
}

/// <summary>
/// BUG-ANIM-09: the last value of every joint an animation drove and nothing drives any more, for an
/// animation player that puts the skeleton back to rest each frame.
/// </summary>
/// <remarks>
/// <para>The viewer does not put a joint back when its motion ends. <c>LLJointStateBlender::blendJointStates</c>
/// returns early when no joint state drives the joint -- "instead of resetting joint state to default, just
/// leave it unchanged from last frame" (llpose.cpp:242-246) -- a stopped motion only drops its weight to 0
/// (<c>LLMotion::deactivate</c>, llmotion.cpp:157-170), and the one thing that restores joints is
/// <c>LLVOAvatar::resetSkeleton</c> (llvoavatar.cpp:2256). A deformer that keys only a pair of collision
/// volumes for a quarter second therefore leaves them posed; SLNG snapped them back every frame.</para>
///
/// <para>Per frame: <see cref="BeginFrame"/>, <see cref="Driven"/> for every joint the animations give a
/// value, <see cref="EndFrame"/> -- a joint driven last frame and not this one is held at its last value.
/// Cost in a steady state is one array write per driven joint; the held joints (a handful) are written
/// back by the caller after its own reset. Single-threaded, main thread.</para>
///
/// <para>Which joints are <i>holdable</i> is the caller's call: the viewer's own default motions keep
/// driving some joints (<see cref="DefaultMotionJoints"/>) and SLNG has no such motions.</para>
/// </remarks>
public sealed class JointHold<T>
{
    private readonly bool[] _holdable;
    private readonly bool[] _held;
    private readonly int[] _drivenFrame;
    private readonly T[] _last;
    private readonly List<int> _heldJoints = new();
    private readonly Action<int, JointHoldEvent>? _log;
    private List<int> _previous = new();
    private List<int> _current = new();
    private int _frame;

    /// <param name="jointCount">Joints are numbered 0..jointCount-1; anything outside is ignored.</param>
    /// <param name="holdable">Whether a joint may be held at all.</param>
    /// <param name="log">Told about every hold, replacement and release (null: nobody is).</param>
    public JointHold(int jointCount, Func<int, bool> holdable, Action<int, JointHoldEvent>? log = null)
    {
        _holdable = new bool[jointCount];
        _held = new bool[jointCount];
        _drivenFrame = new int[jointCount];
        _last = new T[jointCount];
        for (int i = 0; i < jointCount; i++) _holdable[i] = holdable(i);
        _log = log;
    }

    /// <summary>How many joints are held.</summary>
    public int HeldCount => _heldJoints.Count;

    /// <summary>The <paramref name="index"/>th held joint.</summary>
    public int HeldJoint(int index) => _heldJoints[index];

    /// <summary>The value the <paramref name="index"/>th held joint keeps.</summary>
    public T HeldValue(int index) => _last[_heldJoints[index]];

    /// <summary>True when <paramref name="joint"/> is held.</summary>
    public bool IsHeld(int joint) => (uint)joint < (uint)_held.Length && _held[joint];

    /// <summary>Starts a frame's bookkeeping.</summary>
    public void BeginFrame()
    {
        _frame++;
        (_previous, _current) = (_current, _previous);
        _current.Clear();
    }

    /// <summary>Records that an animation gives <paramref name="joint"/> <paramref name="value"/> this
    /// frame. Drops the hold the joint was under.</summary>
    public void Driven(int joint, T value)
    {
        if ((uint)joint >= (uint)_holdable.Length || !_holdable[joint]) return;

        _last[joint] = value;
        if (_drivenFrame[joint] != _frame)
        {
            _drivenFrame[joint] = _frame;
            _current.Add(joint);
        }

        if (_held[joint])
        {
            _held[joint] = false;
            _heldJoints.Remove(joint);
            _log?.Invoke(joint, JointHoldEvent.Replaced);
        }
    }

    /// <summary>Ends the frame: every joint driven the frame before and not this one is held.</summary>
    public void EndFrame()
    {
        foreach (int joint in _previous)
        {
            if (_drivenFrame[joint] == _frame || _held[joint]) continue;

            _held[joint] = true;
            _heldJoints.Add(joint);
            _log?.Invoke(joint, JointHoldEvent.Held);
        }
    }

    /// <summary>Lets go of everything, and forgets what was being driven (a reset is not a release).</summary>
    public void Clear()
    {
        foreach (int joint in _heldJoints)
        {
            _held[joint] = false;
            _log?.Invoke(joint, JointHoldEvent.Cleared);
        }
        _heldJoints.Clear();
        _previous.Clear();
        _current.Clear();
    }
}
