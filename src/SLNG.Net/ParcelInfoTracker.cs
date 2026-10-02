using SLNG.Core;
using ParcelInfo = SLNG.Core.ParcelInfo; // LibreMetaverse has a ParcelInfo too

namespace SLNG.Net;

/// <summary>Holds the last <see cref="ParcelInfo"/> that was raised and decides, per reply, whether a
/// new event is due (FEAT-LAND-01). Pure state: no network, no LibreMetaverse type, so the merge and
/// de-duplication rules are tested without a session.
///
/// Called from LibreMetaverse's network threads (properties and dwell replies arrive on different
/// ones), hence the lock. It never touches <c>World</c>.</summary>
internal sealed class ParcelInfoTracker
{
    private readonly object _gate = new();
    private ParcelInfo? _current;

    /// <summary>The record most recently handed out, or null.</summary>
    internal ParcelInfo? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Forgets the held record, e.g. on a new login, so a stale dwell cannot be merged
    /// into a parcel of another session.</summary>
    internal void Reset()
    {
        lock (_gate) _current = null;
    }

    /// <summary>A parcel's properties arrived. Returns the record to raise, or null when nothing
    /// changed.
    ///
    /// A reply to our own request (<paramref name="solicited"/>) is always raised -- the window asked.
    /// An unsolicited one is raised only when it differs from what was last shown: the sim re-sends
    /// the agent's parcel on every crossing and on every lookup <c>GridSession</c> makes for the
    /// environment poll, and none of that should redraw the window. For the same parcel the known
    /// dwell and parcel id are carried over, so a refresh does not flash "Loading..." again.</summary>
    internal ParcelInfo? OnProperties(ParcelInfo mapped, bool solicited)
    {
        lock (_gate)
        {
            if (_current is { } held && held.RegionHandle == mapped.RegionHandle && held.LocalId == mapped.LocalId)
                mapped = mapped with { Dwell = held.Dwell, ParcelId = held.ParcelId };

            if (!solicited && mapped == _current) return null;

            _current = mapped;
            return mapped;
        }
    }

    /// <summary>A <c>ParcelDwellReply</c> arrived. Returns the merged record to raise, or null when it
    /// is for a parcel that is not the current one, or adds nothing (LibreMetaverse's own
    /// <c>AlwaysRequestDwell</c> asks as well, so the same answer can come twice). An empty parcel id
    /// does not overwrite a known one. A negative or NaN dwell is dropped: the viewer's own "not loaded
    /// yet" marker is -1 (<c>DWELL_NAN</c>, <c>llviewerparcelmgr.h</c>:46), so it can never be a count.</summary>
    internal ParcelInfo? OnDwell(ulong regionHandle, int localId, Guid parcelId, float dwell)
    {
        if (float.IsNaN(dwell) || dwell < 0f) return null;

        lock (_gate)
        {
            if (_current is not { } held || held.RegionHandle != regionHandle || held.LocalId != localId)
                return null;

            var merged = held with
            {
                Dwell = dwell,
                ParcelId = parcelId == Guid.Empty ? held.ParcelId : parcelId,
            };
            if (merged == held) return null;

            _current = merged;
            return merged;
        }
    }
}
