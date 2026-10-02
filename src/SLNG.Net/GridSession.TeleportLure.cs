using System.Globalization;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Interfaces;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, teleport lure part (BUG-NET-27).
//
// The receiving side of "Offer teleport" / "Request teleport": decoding the two instant messages,
// and answering them. The sending side is OfferTeleport in GridSession.Profiles.cs.
//
// Everything here was read off the reference viewer, not remembered:
//   dialog ids        llinstantmessage.h:125-129   IM_LURE_USER 22, IM_LURE_DECLINED 24,
//                                                  IM_GODLIKE_LURE_USER 25, IM_TELEPORT_REQUEST 26
//   the IM fields     llviewermessage.cpp:2145-2158  from_id = AgentData.AgentID, session_id =
//                                                  MessageBlock.ID, then llimprocessing.cpp:1206-1326
//   accept            llviewermessage.cpp:2020-2024  gAgent.teleportViaLure(lure_id, godlike)
//                     llagent.cpp:4383-4391          TeleportLureRequest { AgentID, SessionID,
//                                                  LureID, TeleportFlags }
//   decline (offer)   llviewermessage.cpp:2026-2033  send_simple_im(from, "", IM_LURE_DECLINED,
//                                                  lure_id)  -> :6147-6163, sent IM_ONLINE
//   answer a request  llviewermessage.cpp:6346-6364  "Yes" = send_lures() to the asker, "No" = a
//                                                  case that does nothing at all
//   the lure itself   llviewermessage.cpp:6216-6259  StartLure { LureType 0, Message }
// LibreMetaverse's names for the dialogs are inverted against the viewer's: its RequestTeleport (22)
// is an OFFER, its RequestLure (26) is the viewer's IM_TELEPORT_REQUEST.
public sealed partial class GridSession
{
    // TELEPORT_FLAGS_VIA_LURE / _VIA_GODLIKE_LURE / _DISABLE_CANCEL, llteleportflags.h:33-42.
    private const uint TeleportFlagViaLure = 1u << 2;
    private const uint TeleportFlagViaGodlikeLure = 1u << 8;
    private const uint TeleportFlagDisableCancel = 1u << 11;

    /// <summary>What the viewer puts in <c>TeleportLureRequest.TeleportFlags</c>
    /// (llagent.cpp:4366-4375). The field is legacy — the simulator derives the real flags — but
    /// the godlike bit is what tells it which kind of lure this answers.</summary>
    internal static uint LureTeleportFlags(bool godlike) =>
        godlike ? TeleportFlagViaGodlikeLure | TeleportFlagDisableCancel : TeleportFlagViaLure;

    /// <summary>Reads an instant message as a teleport offer or request, or returns null when it
    /// is neither. Pure, so the wire reading can be tested without a network.</summary>
    /// <param name="lureId">The message's <c>ID</c> field — LibreMetaverse's <c>IMSessionID</c>.</param>
    internal static TeleportOfferEvent? TryDecodeTeleportIm(
        InstantMessageDialog dialog, Guid lureId, Guid fromId, string? fromName, string? message, byte[]? bucket)
    {
        TeleportOfferKind kind;
        bool godlike = false;
        switch (dialog)
        {
            case InstantMessageDialog.RequestTeleport: // IM_LURE_USER
                kind = TeleportOfferKind.Offer;
                break;
            case InstantMessageDialog.GodLikeRequestTeleport: // IM_GODLIKE_LURE_USER
                kind = TeleportOfferKind.Offer;
                godlike = true;
                break;
            case InstantMessageDialog.RequestLure: // IM_TELEPORT_REQUEST
                kind = TeleportOfferKind.Request;
                break;
            default:
                return null;
        }

        return new TeleportOfferEvent(
            kind, lureId, fromId, fromName ?? string.Empty, message ?? string.Empty, godlike,
            // llimprocessing.cpp:1235 -- the (empty) bucket of a request is not parsed.
            kind == TeleportOfferKind.Offer ? ParseLureMaturity(bucket) : null);
    }

    /// <summary>The destination's rating out of an offer's binary bucket, or null.
    /// <c>parse_lure_bucket</c> (llimprocessing.cpp:352-404) wants eight pipe-separated integers
    /// (<c>gx|gy|rx|ry|rz|lx|ly|lz</c>) and then an optional access token trimmed of whitespace:
    /// "A", "M" or "PG". Anything else — a short bucket, a non-number, a missing or unknown token —
    /// leaves the viewer at <c>SIM_ACCESS_MIN</c> and the prompt is shown all the same, so this
    /// returns null rather than rejecting the offer. The string stops at the first NUL
    /// (<c>ll_safe_string</c>, :1227).</summary>
    internal static MaturityLevel? ParseLureMaturity(byte[]? bucket)
    {
        if (bucket == null || bucket.Length == 0) return null;

        int length = Array.IndexOf(bucket, (byte)0);
        string text = Encoding.UTF8.GetString(bucket, 0, length < 0 ? bucket.Length : length);

        var tokens = text.Split('|');
        const int PositionTokens = 8;
        if (tokens.Length <= PositionTokens) return null;
        for (int i = 0; i < PositionTokens; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                return null;
        }

        return tokens[PositionTokens].Trim() switch
        {
            "A" => MaturityLevel.Adult,
            "M" => MaturityLevel.Moderate,
            "PG" => MaturityLevel.General,
            _ => null,
        };
    }

    /// <summary>Accepts a teleport offer: sends <c>TeleportLureRequest</c> and follows the teleport
    /// through the same <see cref="TeleportProgress"/> events as any other, so the loading overlay
    /// shows it with no second path. Returns whether the request was sent — false when not
    /// connected, for an empty id, or while another teleport is running.</summary>
    /// <remarks>
    /// Fire-and-forget like <see cref="TeleportToGlobalPosition"/>: the outcome arrives as
    /// <see cref="TeleportProgress"/>. A lure that has expired or been withdrawn is the case this has
    /// to get right, because LibreMetaverse cannot report it: its TeleportFailed handler returns early
    /// while its own teleport status is <c>None</c> (AgentManager.TeleportHandler), which is the state
    /// a lure accept is in — it never started the teleport itself. So the failure is picked up here,
    /// from the UDP packet and from the EventQueue message, and the wait is bounded by LibreMetaverse's
    /// own teleport timeout. The viewer sets its own teleport state before sending
    /// (llagent.cpp:4357-4358), which is why it has no such gap.
    /// </remarks>
    public bool AcceptTeleportOffer(Guid lureId, bool godlike)
    {
        if (lureId == Guid.Empty || !_client.Network.Connected) return false;
        // See _teleportInProgress's doc comment.
        if (System.Threading.Interlocked.CompareExchange(ref _teleportInProgress, 1, 0) != 0) return false;

        _ = AcceptTeleportOfferCoreAsync(lureId, godlike);
        return true;
    }

    private async Task AcceptTeleportOfferCoreAsync(Guid lureId, bool godlike)
    {
        var done = new TaskCompletionSource<(bool Ok, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int terminalRelayed = 0;

        void OnProgress(object? sender, TeleportEventArgs e)
        {
            OnLmvTeleportProgress(sender, e); // FEAT-UI-18: relay to the neutral TeleportProgress event
            switch (e.Status)
            {
                case TeleportStatus.Finished:
                    System.Threading.Interlocked.Exchange(ref terminalRelayed, 1);
                    done.TrySetResult((true, string.Empty));
                    break;
                case TeleportStatus.Failed:
                case TeleportStatus.Cancelled:
                    System.Threading.Interlocked.Exchange(ref terminalRelayed, 1);
                    done.TrySetResult((false, e.Message ?? string.Empty));
                    break;
            }
        }

        // The two routes the grid reports a refusal by, see the remarks on AcceptTeleportOffer.
        void OnFailedPacket(object? sender, PacketReceivedEventArgs e) =>
            done.TrySetResult((false, Utils.BytesToString(((TeleportFailedPacket)e.Packet).Info.Reason)));
        void OnFailedEvent(string capsKey, IMessage message, Simulator simulator)
        {
            if (message is TeleportFailedMessage failed) done.TrySetResult((false, failed.Reason ?? string.Empty));
        }

        _client.Self.TeleportProgress += OnProgress;
        _client.Network.RegisterCallback(PacketType.TeleportFailed, OnFailedPacket);
        _client.Network.RegisterEventCallback("TeleportFailed", OnFailedEvent);
        RaiseTeleportStage(TeleportStage.Started, string.Empty);
        try
        {
            _client.Network.SendPacket(new TeleportLureRequestPacket
            {
                Info =
                {
                    AgentID = _client.Self.AgentID,
                    SessionID = _client.Self.SessionID,
                    LureID = new UUID(lureId),
                    TeleportFlags = LureTeleportFlags(godlike),
                },
            });

            int timeoutMs = _client.Settings.Timing.TeleportTimeout;
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            var (ok, message) = finished == done.Task
                ? done.Task.Result
                : (false, "Teleport timed out.");

            if (ok) SyncLocalAgentPositionAfterTeleport();

            // A refusal we caught ourselves, or a timeout, was never relayed as a LibreMetaverse
            // event, so the overlay has not been told it is over.
            if (System.Threading.Volatile.Read(ref terminalRelayed) == 0)
                RaiseTeleportStage(ok ? TeleportStage.Finished : TeleportStage.Failed, ok ? string.Empty : message);
        }
        catch (Exception ex)
        {
            RaiseTeleportStage(TeleportStage.Failed, ex.Message);
        }
        finally
        {
            _client.Network.UnregisterEventCallback("TeleportFailed", OnFailedEvent);
            _client.Network.UnregisterCallback(PacketType.TeleportFailed, OnFailedPacket);
            _client.Self.TeleportProgress -= OnProgress;
            System.Threading.Interlocked.Exchange(ref _teleportInProgress, 0);
        }
    }

    /// <summary>Declines a teleport offer: an <c>IM_LURE_DECLINED</c> back to the sender carrying the
    /// lure id (llviewermessage.cpp:2029-2032). Returns whether it was sent.</summary>
    public bool DeclineTeleportOffer(Guid fromId, Guid lureId)
    {
        if (fromId == Guid.Empty || lureId == Guid.Empty || !_client.Network.Connected) return false;

        _client.Self.InstantMessage(
            _client.Self.Name,
            new UUID(fromId),
            string.Empty,
            new UUID(lureId),
            InstantMessageDialog.DenyTeleport,
            // send_simple_im -> IM_ONLINE (llviewermessage.cpp:6157).
            InstantMessageOnline.Online,
            _client.Self.SimPosition,
            UUID.Zero,
            Array.Empty<byte>());
        return true;
    }

    /// <summary>Answers a teleport request. <paramref name="accept"/> offers the asker a teleport to
    /// where we are (the viewer's <c>send_lures</c> to them, llviewermessage.cpp:6349-6357), the
    /// same lure as <see cref="OfferTeleport"/>; the asker's own client then shows it as an offer.
    /// Declining sends nothing — the viewer's "No" is a case that only breaks out
    /// (llviewermessage.cpp:6361-6364) — and is therefore complete at once.</summary>
    /// <returns>False only when an offer had to go out and could not: not connected, or an empty
    /// id. A decline always returns true.</returns>
    public bool AnswerTeleportRequest(Guid fromId, bool accept)
    {
        if (!accept) return true;
        if (fromId == Guid.Empty || !_client.Network.Connected) return false;

        OfferTeleport(fromId);
        return true;
    }
}
