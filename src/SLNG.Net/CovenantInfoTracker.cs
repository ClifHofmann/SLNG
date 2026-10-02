using SLNG.Core;

namespace SLNG.Net;

/// <summary>Holds the last <see cref="CovenantInfo"/> that was raised and decides, per reply and per
/// text, whether a new event is due (FEAT-LAND-05). Pure state: no network, no LibreMetaverse type, so
/// the merge rules are tested without a session.
///
/// <para>Called from LibreMetaverse's network threads and from the text-fetch continuation, hence the
/// lock. It never touches <c>World</c>.</para></summary>
internal sealed class CovenantInfoTracker
{
    private readonly object _gate = new();
    private CovenantInfo? _current;

    // The covenant whose text is on its way, so a second reply does not start a second fetch.
    private (ulong Region, Guid Covenant)? _fetching;

    /// <summary>The record most recently handed out, or null.</summary>
    internal CovenantInfo? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Forgets the held record, e.g. on a new login.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _current = null;
            _fetching = null;
        }
    }

    /// <summary>An <c>EstateCovenantReply</c> arrived. Returns the record to raise (null when nothing
    /// changed) and whether the caller must now fetch the covenant text.
    ///
    /// A reply to our own request (<paramref name="solicited"/>) is always raised -- the window asked.
    /// An unsolicited one (the sim pushing a changed covenant) only when it differs from what is held.
    /// For the same covenant asset a text already <see cref="CovenantTextState.Loaded"/> is carried over:
    /// the notecard behind an asset id never changes, so there is nothing to fetch and the view does not
    /// flash "loading" again. A <see cref="CovenantTextState.Failed"/> text is not carried: a fresh request
    /// is the retry.</summary>
    internal (CovenantInfo? Raised, bool NeedsText) OnReply(CovenantInfo mapped, bool solicited)
    {
        lock (_gate)
        {
            if (_current is { } held
                && held.RegionHandle == mapped.RegionHandle
                && held.CovenantId != null && held.CovenantId == mapped.CovenantId
                && held.TextState == CovenantTextState.Loaded)
            {
                mapped = mapped with { TextState = CovenantTextState.Loaded, Text = held.Text };
            }

            if (!solicited && mapped == _current) return (null, false);

            _current = mapped;

            bool needsText = mapped.TextState == CovenantTextState.Loading
                && _fetching != (mapped.RegionHandle, mapped.CovenantId!.Value);
            if (needsText) _fetching = (mapped.RegionHandle, mapped.CovenantId!.Value);
            return (mapped, needsText);
        }
    }

    /// <summary>The covenant text arrived (<paramref name="text"/>) or could not be fetched or decoded
    /// (null). Returns the completed record to raise, or null when the held record is no longer the one
    /// this text belongs to (another region, or the estate changed its covenant meanwhile).</summary>
    internal CovenantInfo? OnText(ulong regionHandle, Guid covenantId, string? text)
    {
        lock (_gate)
        {
            if (_fetching == (regionHandle, covenantId)) _fetching = null;

            if (_current is not { } held
                || held.RegionHandle != regionHandle
                || held.CovenantId != covenantId
                || held.TextState != CovenantTextState.Loading)
                return null;

            var done = text == null
                ? held with { TextState = CovenantTextState.Failed, Text = null }
                : held with { TextState = CovenantTextState.Loaded, Text = text };
            _current = done;
            return done;
        }
    }
}
