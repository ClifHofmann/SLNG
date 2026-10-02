using SLNG.Core;

namespace SLNG.Net;

/// <summary>Holds the request for the owner list that is in flight and the list built from its replies
/// (FEAT-LAND-03). Pure state: no network, no LibreMetaverse type, so the coalescing, the first-reply-clears /
/// later-replies-append rule and the timeout race are tested without a session.
///
/// <para>Called from LibreMetaverse's network threads (UDP replies, EventQueue replies and the timeout continuation
/// arrive on different ones), hence the lock. It never touches <c>World</c>.</para></summary>
internal sealed class ParcelObjectOwnersTracker
{
    private readonly object _gate = new();

    // The last request made (region, local id) and its serial; null before the first and after Reset.
    private (ulong Region, int LocalId, int Serial)? _request;

    // True from the request until its first reply (or its timeout).
    private bool _pending;

    private int _serial;
    private ParcelObjectOwners? _current;

    /// <summary>The list built so far for the last request, or null when no reply has come yet.</summary>
    internal ParcelObjectOwners? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Forgets everything, e.g. on a new login.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _request = null;
            _pending = false;
            _current = null;
        }
    }

    /// <summary>The caller wants the list for this parcel. Returns whether a message must now be sent, and the
    /// serial of the request that the answer or the timeout belongs to.
    ///
    /// <para><b>The rate limit.</b> While a request for the SAME parcel is still waiting for its first reply, no
    /// second one is sent and the serial of the waiting one is returned: a Refresh button pressed twice quickly
    /// sends once, and the one answer serves both. Once it was answered (or timed out) the next call sends
    /// again. A request for another parcel replaces the waiting one (its timeout then does nothing).</para></summary>
    internal (bool Send, int Serial) Begin(ulong regionHandle, int localId)
    {
        lock (_gate)
        {
            if (_pending && _request is { } r && r.Region == regionHandle && r.LocalId == localId)
                return (false, r.Serial);

            int serial = ++_serial;
            _request = (regionHandle, localId, serial);
            _pending = true;
            _current = null; // the list is cleared when the request is sent (onClickRefresh, llfloaterland.cpp:1575)
            return (true, serial);
        }
    }

    /// <summary>A reply arrived from <paramref name="regionHandle"/>. Returns the cumulative record to raise, or
    /// null when there is no request for that region to attribute it to. The first reply after a request starts
    /// the list over; later ones are appended (<c>mFirstReply</c>, <c>llfloaterland.cpp</c>:1613). A reply that
    /// comes after the timeout also lands: it replaces the "no answer" state with the list, which is better than
    /// leaving the window on a failure that turned out not to be one.</summary>
    internal ParcelObjectOwners? OnReply(ulong regionHandle, IReadOnlyList<OwnerRow> rows)
    {
        lock (_gate)
        {
            if (_request is not { } r || r.Region != regionHandle) return null;

            bool first = _pending;
            _pending = false;
            _current = ParcelObjectOwnersMapper.Merge(_current, r.Region, r.LocalId, rows, replace: first);
            return _current;
        }
    }

    /// <summary>The wait for request <paramref name="serial"/> is over. Returns the (region, local id) to report as
    /// failed, or null when it was answered meanwhile or has been replaced by a newer request.</summary>
    internal (ulong Region, int LocalId)? OnTimeout(int serial)
    {
        lock (_gate)
        {
            if (!_pending || _request is not { } r || r.Serial != serial) return null;
            _pending = false;
            return (r.Region, r.LocalId);
        }
    }
}
