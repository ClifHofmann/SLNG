using Godot;
using System;
using System.Collections.Generic;

namespace SLNG.App;

public partial class AvatarRenderer
{
    // BUG-AVATAR-11: worn items that arrived before the avatar wearing them has a visual.
    //
    // Attachments are only ever processed from OnComponentUpdated -> UpdateAttachment, and
    // UpdateAttachment used to return early, recording nothing, while the wearer had no visual (or no
    // skeleton). Nothing ever asked again: CreateVisual does not look for earlier arrivals, and since
    // FEAT-PERF-08 an avatar starts reduced and SetAvatarFull rebuilds only what is in
    // AvatarVisual.WornAttachmentEntities. An item that came first was therefore never built, and its
    // wearer stayed a bare system body for good.
    //
    // The order "attachment before avatar" is the NORMAL one after a return to a region: the object cache
    // replays the region at once, attachment prims included (they sit in it under their wearer's local
    // id), and the avatars only follow from the simulator. The confirm step afterwards does not re-fire a
    // component update for an object that did not change.
    //
    // Main thread only, like every other table in this renderer: written from UpdateAttachment and
    // RemoveVisual (both deferred / drained on the main thread), read from OnEntityRemoved's pre-filter on
    // the thread that raises world events -- the same arrangement as _attachmentToAvatar.

    /// <summary>wearer avatar entity id -> the worn items seen for it while it had no visual.</summary>
    private readonly Dictionary<Guid, HashSet<Guid>> _pendingWornByWearer = new();

    /// <summary>worn item entity id -> the wearer it is waiting for (the reverse of
    /// <see cref="_pendingWornByWearer"/>, so an item that leaves, or is re-parented to another avatar,
    /// is found without walking every wearer).</summary>
    private readonly Dictionary<Guid, Guid> _pendingWornWearerOf = new();

    /// <summary>Cumulative count of worn items handed back to <c>UpdateAttachment</c> by
    /// <see cref="FlushPendingWornItems"/>. A seam for the self-test, which cannot drain
    /// <c>CallDeferred</c>; also what a log line reports.</summary>
    internal int PendingWornFlushed { get; private set; }

    /// <summary>Remembers that <paramref name="itemId"/> is worn by <paramref name="wearerId"/>, whose
    /// visual does not exist yet. Idempotent; moves the record when the item now names another wearer.</summary>
    private void NotePendingWornItem(Guid itemId, Guid wearerId)
    {
        // The wearer is often not in the world YET -- after a cache replay the attachment prims come first
        // and the avatars follow from the simulator -- so its absence is no reason to drop the record. The
        // record cannot outlive the item: OnEntityRemoved's pre-filter lets the item's removal through to
        // RemoveVisual, which forgets it.
        if (wearerId == Guid.Empty)
        {
            ForgetPendingWornItem(itemId);
            return;
        }

        if (_pendingWornWearerOf.TryGetValue(itemId, out var previous))
        {
            if (previous == wearerId) return;
            ForgetPendingWornItem(itemId);
        }

        if (!_pendingWornByWearer.TryGetValue(wearerId, out var items))
            _pendingWornByWearer[wearerId] = items = new HashSet<Guid>();
        items.Add(itemId);
        _pendingWornWearerOf[itemId] = wearerId;
    }

    /// <summary>The item is built, gone, detached or worn by someone else: it no longer waits.</summary>
    private void ForgetPendingWornItem(Guid itemId)
    {
        if (!_pendingWornWearerOf.Remove(itemId, out var wearerId)) return;
        if (_pendingWornByWearer.TryGetValue(wearerId, out var items) && items.Remove(itemId) && items.Count == 0)
            _pendingWornByWearer.Remove(wearerId);
    }

    /// <summary>The wearer itself left before it ever had a visual (or has just lost it): whatever waited
    /// for it can never be flushed.</summary>
    private void ForgetPendingWearer(Guid wearerId)
    {
        if (!_pendingWornByWearer.Remove(wearerId, out var items)) return;
        foreach (var itemId in items) _pendingWornWearerOf.Remove(itemId);
    }

    /// <summary>The wearer has a visual now (end of <c>CreateVisual</c>): ask <c>UpdateAttachment</c> again
    /// for everything that was worn before that. Deferred, as the world's own component updates are, so a
    /// full avatar's outfit is not built inside the frame that created its body. A reduced avatar's
    /// UpdateAttachment only records the items in <see cref="AvatarVisual.WornAttachmentEntities"/>;
    /// promotion builds them.</summary>
    private void FlushPendingWornItems(Guid wearerId)
    {
        if (!_pendingWornByWearer.Remove(wearerId, out var items)) return;

        foreach (var itemId in items)
        {
            _pendingWornWearerOf.Remove(itemId);
            PendingWornFlushed++;
            CallDeferred(nameof(UpdateAttachment), itemId.ToString());
        }

        if (Diagnostics.Enabled)
            GD.Print($"[Attachment] {items.Count} worn item(s) had arrived before avatar {wearerId:N}; asking for them again");
    }
}
