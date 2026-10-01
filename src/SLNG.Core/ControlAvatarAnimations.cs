using System;
using System.Collections.Generic;
using System.Linq;

namespace SLNG.Core;

/// <summary>
/// What a control avatar should be playing (the union of the animations its object's prims signal)
/// and what has to change to get there from what it is playing now. FEAT-ANIMESH-02.
/// </summary>
/// <remarks>
/// <b>Union</b> -- <c>LLControlAvatar::updateAnimations</c> (llcontrolavatar.cpp:559-607): the
/// signalled animations of the linkset root and EVERY child prim are merged into one map; when the
/// same animation id is signalled by more than one prim the LARGER sequence id is kept
/// (<c>llmax</c> on two <c>S32</c>, so the comparison is signed). Which prims count is not limited
/// to rigged ones: a script in a plain child prim may be the one that starts the animation.
///
/// <para>Inside ONE prim's list a repeated id is not a max but a last-wins overwrite, because
/// <c>process_object_animation</c> (llviewermessage.cpp:4108) fills a <c>std::map</c> with
/// <c>map[id] = seq</c> and that map is what <c>updateAnimations</c> reads.</para>
///
/// <para><b>Diff</b> -- the loop in <c>LLVOAvatar::processAnimationStateChanges</c>
/// (llvoavatar.cpp:6071-6104): playing but no longer signalled stops; signalled and not playing
/// starts; signalled and playing with a DIFFERENT sequence id (<c>!=</c>, not "greater") is started
/// again; anything else is left alone.</para>
///
/// <para>Both put their result in animation-id order. The viewer's maps are ordered by id as well,
/// and a deterministic order is what lets a caller compare two results.</para>
/// </remarks>
public static class ControlAvatarAnimations
{
    /// <summary>The effective set over every prim's list, ordered by animation id.</summary>
    public static SignaledAnimation[] Union(IEnumerable<IReadOnlyList<SignaledAnimation>> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);

        var merged = new Dictionary<Guid, int>();
        var perPrim = new Dictionary<Guid, int>();

        foreach (var list in lists)
        {
            if (list == null || list.Count == 0) continue;

            // One prim: the last entry for an id wins.
            perPrim.Clear();
            for (int i = 0; i < list.Count; i++) perPrim[list[i].AnimationId] = list[i].SequenceId;

            // Between prims: the larger sequence id wins.
            foreach (var (id, sequence) in perPrim)
            {
                merged[id] = merged.TryGetValue(id, out int seen) ? Math.Max(seen, sequence) : sequence;
            }
        }

        return Ordered(merged);
    }

    /// <summary>What it takes to go from <paramref name="current"/> (what is playing) to
    /// <paramref name="wanted"/> (what is signalled). Both are sets by animation id; a repeated id
    /// in either keeps its last entry.</summary>
    public static AnimationSetChange Diff(IReadOnlyList<SignaledAnimation> current, IReadOnlyList<SignaledAnimation> wanted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(wanted);

        var playing = new Dictionary<Guid, int>();
        foreach (var a in current) playing[a.AnimationId] = a.SequenceId;
        var signalled = new Dictionary<Guid, int>();
        foreach (var a in wanted) signalled[a.AnimationId] = a.SequenceId;

        var start = new Dictionary<Guid, int>();
        var restart = new Dictionary<Guid, int>();
        foreach (var (id, sequence) in signalled)
        {
            if (!playing.TryGetValue(id, out int playingSequence)) start[id] = sequence;
            else if (playingSequence != sequence) restart[id] = sequence;
        }

        var stop = playing.Keys.Where(id => !signalled.ContainsKey(id)).OrderBy(id => id).ToArray();
        return new AnimationSetChange(Ordered(start), stop, Ordered(restart));
    }

    /// <summary>True when the two hold the same animations with the same sequence ids, whatever
    /// their order.</summary>
    public static bool SameSet(IReadOnlyList<SignaledAnimation> a, IReadOnlyList<SignaledAnimation> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count) return false;
        return Diff(a, b).IsEmpty && Diff(b, a).IsEmpty;
    }

    private static SignaledAnimation[] Ordered(Dictionary<Guid, int> map)
    {
        var result = new SignaledAnimation[map.Count];
        int n = 0;
        foreach (var (id, sequence) in map) result[n++] = new SignaledAnimation(id, sequence);
        Array.Sort(result, (x, y) => x.AnimationId.CompareTo(y.AnimationId));
        return result;
    }
}

/// <summary>The difference between a control avatar's playing set and its signalled set.</summary>
/// <param name="Start">Signalled and not playing.</param>
/// <param name="Stop">Playing and no longer signalled.</param>
/// <param name="Restart">Playing, signalled with a different sequence id -- carrying the NEW one.</param>
public readonly record struct AnimationSetChange(
    IReadOnlyList<SignaledAnimation> Start,
    IReadOnlyList<Guid> Stop,
    IReadOnlyList<SignaledAnimation> Restart)
{
    public bool IsEmpty => Start.Count == 0 && Stop.Count == 0 && Restart.Count == 0;
}
