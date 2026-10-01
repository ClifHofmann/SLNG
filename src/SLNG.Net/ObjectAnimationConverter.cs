using LibreMetaverse;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>
/// Turns LibreMetaverse's <c>ObjectManager.ObjectAnimation</c> payload into the neutral
/// <see cref="ObjectAnimationEvent"/>, and describes it for the diagnostic log. A pure static so
/// it can be pinned without a live <c>Simulator</c>.
///
/// <para>The library raises that event for every <c>ObjectAnimation</c> packet: <c>ObjectID</c> is
/// the packet's <c>Sender.ID</c> (the PRIM's object UUID) and <c>Animations</c> a
/// <c>List&lt;Animation&gt;</c> with one element per <c>AnimationList</c> block, the signed sequence
/// id in <c>AnimationSequence</c>. An empty list is raised as an empty list -- which is how the sim
/// says "stop everything", so it must not be dropped here. Checked against the pinned 3.1.6
/// assembly, not the stale vendored source.</para>
/// </summary>
internal static class ObjectAnimationConverter
{
    /// <summary>How many animation ids the diagnostic line spells out. The count is always exact;
    /// the ids are only there to tell "does the sim send them at all" from "it sent something else".</summary>
    private const int DescribedIds = 3;

    internal static ObjectAnimationEvent FromWire(
        ulong regionHandle, UUID objectId, IReadOnlyList<Animation>? animations)
    {
        // Always a fresh array: the library hands the same list to its event and to
        // Primitive.SignaledAnimations, and the neutral event outlives the callback in a queue.
        var converted = new SignaledAnimation[animations?.Count ?? 0];
        for (int i = 0; i < converted.Length; i++)
        {
            var a = animations![i];
            converted[i] = new SignaledAnimation(a.AnimationID.Guid, a.AnimationSequence);
        }

        return new ObjectAnimationEvent(regionHandle, objectId.Guid, converted);
    }

    /// <summary>One line per received message: region, object (8 chars), the true count and the
    /// first few animation ids (8 chars each).</summary>
    internal static string Describe(ObjectAnimationEvent e)
    {
        string line = $"[ObjectAnimation] region={e.RegionHandle} object={Short(e.ObjectId)} count={e.Animations.Count}";
        if (e.Animations.Count == 0) return line;

        var first = e.Animations.Take(DescribedIds).Select(a => Short(a.AnimationId));
        return $"{line} first={string.Join(",", first)}";
    }

    private static string Short(Guid id) => id.ToString("N")[..8];
}
