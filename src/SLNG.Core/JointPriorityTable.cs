using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// BUG-ANIM-07: the per-joint, highest-priority-wins table an animation player fills once per
/// frame, one table per channel (rotation, position). Engine-neutral so the rule can be tested
/// without a skeleton: <typeparamref name="T"/> is whatever the player blends (a Godot quaternion,
/// a keyed position).
/// </summary>
/// <remarks>
/// <para>The reference viewer resolves every joint on its own: of the motions that key a joint,
/// the one with the highest priority drives it (<c>LLJointStateBlender::addJointState</c>,
/// llpose.cpp:200-230). Rotation and position are separate channels with separate winners, so a
/// rotation-only gesture does not take away the position key of an underlying pose.</para>
///
/// <para>Equal priority: the CANDIDATE wins, i.e. the animation offered last. The player offers
/// its animations in a fixed order, so the later one of two equal-priority animations decides, the
/// same rule the rotation channel has always used here.</para>
///
/// <para>One instance is reused every frame (<see cref="Clear"/>) -- it was ~7 KB of garbage per
/// avatar per frame as a fresh pair of dictionaries. Single-threaded: filled and read on the main
/// thread inside one method.</para>
/// </remarks>
public sealed class JointPriorityTable<T>
{
    private readonly Dictionary<int, (int Priority, T Value)> _entries = new();

    /// <summary>How many joints have an entry.</summary>
    public int Count => _entries.Count;

    /// <summary>Forgets every entry, keeping the allocation.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>True when something of <paramref name="priority"/> would take <paramref name="joint"/>:
    /// nothing holds it yet, or what holds it is of the same or a lower priority. Lets the caller
    /// skip evaluating a key that would lose.</summary>
    public bool Wins(int joint, int priority)
        => !_entries.TryGetValue(joint, out var existing) || priority >= existing.Priority;

    /// <summary>Gives <paramref name="joint"/> to <paramref name="value"/> unconditionally. Pair with
    /// <see cref="Wins"/>, or use <see cref="Offer"/>.</summary>
    public void Set(int joint, int priority, T value) => _entries[joint] = (priority, value);

    /// <summary>Offers <paramref name="value"/> for <paramref name="joint"/>; true when it took it.</summary>
    public bool Offer(int joint, int priority, T value)
    {
        if (!Wins(joint, priority)) return false;
        _entries[joint] = (priority, value);
        return true;
    }

    /// <summary>The value currently holding <paramref name="joint"/>.</summary>
    public bool TryGet(int joint, out T value)
    {
        if (_entries.TryGetValue(joint, out var existing))
        {
            value = existing.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>The priority and value currently holding <paramref name="joint"/>.</summary>
    public bool TryGet(int joint, out int priority, out T value)
    {
        if (_entries.TryGetValue(joint, out var existing))
        {
            priority = existing.Priority;
            value = existing.Value;
            return true;
        }
        priority = 0;
        value = default!;
        return false;
    }

    /// <summary>Allocation-free <c>foreach (var (joint, entry) in table)</c>.</summary>
    public Dictionary<int, (int Priority, T Value)>.Enumerator GetEnumerator() => _entries.GetEnumerator();

    /// <summary>The priority an animation's key on one joint is offered at: the locomotion boost
    /// wins over everything while it lasts; otherwise the joint's own priority when the animation
    /// gives one (<c>&gt;= 0</c>), else the animation's.</summary>
    public static int EffectivePriority(bool boosted, int boostPriority, int jointPriority, int animationPriority)
        => boosted ? boostPriority : (jointPriority >= 0 ? jointPriority : animationPriority);
}
