using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// BUG-ANIM-08: the per-joint table an animation player fills once per frame, one table per channel
/// (rotation, position). Replaces the highest-priority-takes-all table (<c>JointPriorityTable</c>,
/// BUG-ANIM-07): a motion that is fading in or out only takes a share of the joint, and the layers
/// below it fill the rest -- <c>LLJointStateBlender::blendJointStates</c>, llpose.cpp:238-400.
/// Engine-neutral: <typeparamref name="T"/> is whatever the player blends.
/// </summary>
/// <remarks>
/// <para>The viewer keeps up to <see cref="MaxLayers"/> joint states per joint ordered by priority
/// (llpose.cpp:196-233). The first is copied; each next one is blended UNDER what is accumulated,
/// <c>t = sum / min(1, weight + sum)</c>, so once the weights reach 1 the rest change nothing
/// (llpose.cpp:317-369). A weight of 0 is skipped (:279). A lone state is used whole whatever its
/// weight -- the weight only decides how it shares a joint with others.</para>
///
/// <para><b>Offer NEWEST first.</b> The viewer lists its motions newest first and a state of equal
/// priority stays behind the one already in the slot (llpose.cpp:213, 217), so of two equal
/// priorities the newer one is on top. Offering newest first reproduces that, and lets
/// <see cref="Saturated"/> tell the caller everything ahead of a candidate is already complete, so
/// it can skip evaluating a key that cannot show -- in a steady state (the winner at full weight)
/// that is every loser, and the cost is what it was before blending.</para>
///
/// <para>One instance is reused every frame: <see cref="Clear"/> is O(1) (a generation stamp) and a
/// joint's layer arrays are allocated once. Single-threaded: filled and read on the main thread
/// inside one method.</para>
/// </remarks>
public sealed class JointBlendTable<T>
{
    /// <summary>The viewer's <c>JSB_NUM_JOINT_STATES</c> (llpose.h:81).</summary>
    public const int MaxLayers = 6;

    private sealed class Layers
    {
        public int Stamp;
        public int Count;
        public readonly int[] Priority = new int[MaxLayers];
        public readonly float[] Weight = new float[MaxLayers];
        public readonly T[] Value = new T[MaxLayers];
    }

    private readonly Dictionary<int, Layers> _joints = new();
    private readonly List<int> _touched = new();
    private int _stamp = 1;

    /// <summary>How many joints have at least one layer.</summary>
    public int TouchedCount => _touched.Count;

    /// <summary>The <paramref name="index"/>th joint that has a layer, in the order first offered.</summary>
    public int TouchedJoint(int index) => _touched[index];

    /// <summary>Forgets every layer, keeping the allocations.</summary>
    public void Clear()
    {
        _stamp++;
        _touched.Clear();
    }

    /// <summary>True when what is already offered for <paramref name="joint"/> at
    /// <paramref name="priority"/> or above adds up to a full weight, so a candidate of that priority
    /// (which would go behind it) would change nothing.</summary>
    public bool Saturated(int joint, int priority)
    {
        if (!_joints.TryGetValue(joint, out var layers) || layers.Stamp != _stamp) return false;

        float sum = 0f;
        for (int i = 0; i < layers.Count && layers.Priority[i] >= priority; i++)
        {
            sum += layers.Weight[i];
            if (sum >= 1f - 1e-6f) return true;
        }
        return false;
    }

    /// <summary>Adds a layer for <paramref name="joint"/>. Of equal priority it goes behind what is
    /// there; a zero weight is not worth a slot.</summary>
    public void Offer(int joint, int priority, float weight, T value)
    {
        if (!(weight > 0f)) return;

        if (!_joints.TryGetValue(joint, out var layers))
        {
            layers = new Layers();
            _joints[joint] = layers;
        }

        if (layers.Stamp != _stamp)
        {
            layers.Stamp = _stamp;
            layers.Count = 0;
            _touched.Add(joint);
        }

        int at = 0;
        while (at < layers.Count && layers.Priority[at] >= priority) at++;
        if (at >= MaxLayers) return; // below every one of a full table

        int last = Math.Min(layers.Count, MaxLayers - 1); // the slot that takes what is shifted down
        for (int j = last; j > at; j--)
        {
            layers.Priority[j] = layers.Priority[j - 1];
            layers.Weight[j] = layers.Weight[j - 1];
            layers.Value[j] = layers.Value[j - 1];
        }

        layers.Priority[at] = priority;
        layers.Weight[at] = weight;
        layers.Value[at] = value;
        if (layers.Count < MaxLayers) layers.Count++;
    }

    /// <summary>The blended value of <paramref name="joint"/>. <paramref name="blend"/> is
    /// <c>blend(t, a, b) = a * (1 - t) + b * t</c> -- <c>nlerp</c> / <c>lerp</c>'s argument order. False
    /// when nothing was offered.</summary>
    public bool TryResolve(int joint, Func<float, T, T, T> blend, out T value)
    {
        if (!_joints.TryGetValue(joint, out var layers) || layers.Stamp != _stamp || layers.Count == 0)
        {
            value = default!;
            return false;
        }

        value = layers.Value[0];
        float sum = layers.Weight[0];
        for (int i = 1; i < layers.Count && sum < 1f; i++)
        {
            float newSum = Math.Min(1f, layers.Weight[i] + sum);
            value = blend(sum / newSum, layers.Value[i], value);
            sum = newSum;
        }
        return true;
    }
}
