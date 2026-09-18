using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using LibreMetaverse;
using LibreMetaverse.Imaging;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using SLNG.Core;

namespace SLNG.Net;

// GridSession, Avatar part.
//
// Avatar updates and seating, appearance, visual parameters, wearables and the bake
// pipeline.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    private void OnAvatarUpdate(object? sender, AvatarUpdateEventArgs e)
    {
        // BUG-NET-03: since MultipleSims connects neighbor circuits, avatar updates now also arrive
        // from neighbor sims -- including our OWN avatar as a child agent there, with a different
        // LocalId. Feeding those to WorldSimulation flipped the local agent entity's RegionHandle/
        // LocalId back and forth, tearing down and rebuilding the Bento skeleton on every packet
        // and leaving stale BoneAttachment3D nodes behind (ObjectDisposedException spam from
        // AvatarRenderer.UpdateAttachment). We don't render neighbor-region avatars yet, so the
        // current sim stays the sole authority for avatars, exactly as before MultipleSims.
        if (e.Simulator != _client.Network.CurrentSim) return;

        bool isLocalAgent = e.Avatar.ID == _client.Self.AgentID;
        if (!ResolveSeatedTransform(e.Simulator, e.Avatar.Position, e.Avatar.Rotation, e.Avatar.ParentID,
            out var worldPos, out var worldRot))
        {
            RequestUnresolvedSeat(e.Simulator, e.Avatar.ParentID);
        }

        // An avatar in view carries its LEGACY name in the ObjectUpdate's NameValues, so it never
        // goes through OnUUIDNameReply and nothing would ever ask for its Display Name. Deduped
        // inside RequestDisplayName, so calling it on every update is free after the first.
        RequestDisplayName(e.Avatar.ID.Guid);

        AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
            e.Simulator.Handle,
            e.Avatar.LocalID,
            e.Avatar.ID.Guid,
            new System.Numerics.Vector3(worldPos.X, worldPos.Y, worldPos.Z),
            new System.Numerics.Quaternion(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
            e.Avatar.FirstName,
            e.Avatar.LastName,
            isLocalAgent,
            e.Avatar.Scale.Z,
            new System.Numerics.Vector3(e.Avatar.Velocity.X, e.Avatar.Velocity.Y, e.Avatar.Velocity.Z),
            e.TimeDilation / 65535.0f,
            e.Avatar.ParentID,
            ToSupportPlane(e.Avatar.CollisionPlane),
            GroupTitle: e.Avatar.GroupName));
    }

    /// <summary>SL's collision plane, converted at the boundary — no LibreMetaverse type may cross
    /// a public boundary of SLNG.Net (AGENTS.md).
    ///
    /// This is the simulator's own answer to what an avatar is standing on, produced by its Havok
    /// physics and shipped in the avatar's update. It is present only in the 140- and 76-byte
    /// ObjectData layouts; LibreMetaverse leaves it at default otherwise, so an all-zero plane is
    /// reported as "not sent" rather than as a degenerate plane through the origin. Both readings
    /// are wrong to clamp against, but only one of them is honest about why.
    ///
    /// The viewer keeps the same value as <c>LLVOAvatar::mFootPlane</c> and tests it with
    /// <c>isExactlyClear()</c> for exactly this reason (llworld.cpp:570).</summary>
    private static System.Numerics.Vector4? ToSupportPlane(LibreMetaverse.Vector4 plane)
    {
        if (plane.X == 0f && plane.Y == 0f && plane.Z == 0f && plane.W == 0f) return null;
        return new System.Numerics.Vector4(plane.X, plane.Y, plane.Z, plane.W);
    }

    /// <summary>MVP2-1: once an avatar sits, its wire Position/Rotation become relative to the
    /// seat prim (0 if standing) -- mirrors LibreMetaverse's own AgentManager.SimPosition/
    /// SimRotation walk (AgentManager.cs, verified against the vendored source), generalized here
    /// to ANY avatar (not just the local agent, which is all LMV itself resolves) since GridSession
    /// is the one seam where every avatar's transform gets converted to world space regardless of
    /// who it belongs to -- nothing downstream (WorldSimulation, the renderer, the camera) needs to
    /// know or special-case a seated avatar's transform at all. A no-op (returns the input
    /// unchanged, and true) when parentLocalId is 0.
    ///
    /// <b>Returns false when the seat prim itself is not yet in <c>sim.ObjectsPrimitives</c></b> --
    /// a real race, not a hypothetical one: an avatar's own (Terse)ObjectUpdate can arrive before
    /// the chair/vehicle it is sitting on has ever been seen, especially right after region entry
    /// when many objects stream in in an arbitrary order. <paramref name="worldPos"/>/
    /// <paramref name="worldRot"/> are still filled with the (wrong) relative values in that case
    /// -- the caller decides what to do about a still-unresolved seat, this method only reports it
    /// rather than silently handing back a value that LOOKS like a world position but is actually
    /// a small offset from an unknown origin (reported live 2026-09-17: a seated avatar rezzing far
    /// from her chair and only snapping to the right spot "after a while").</summary>
    private static bool ResolveSeatedTransform(
        LibreMetaverse.Simulator sim, LibreMetaverse.Vector3 relPos, LibreMetaverse.Quaternion relRot,
        uint parentLocalId, out LibreMetaverse.Vector3 worldPos, out LibreMetaverse.Quaternion worldRot)
    {
        worldPos = relPos;
        worldRot = relRot;
        if (parentLocalId == 0) return true;

        if (!sim.ObjectsPrimitives.TryGetValue(parentLocalId, out var seat) || seat == null) return false;

        worldPos = seat.Position + relPos * seat.Rotation;
        worldRot = relRot * seat.Rotation;

        // Walk up a linked-seat's own parent chain (e.g. sitting on a child prim of a vehicle) --
        // same loop LMV's SimPosition runs, position-only (LMV's own algorithm does not further
        // rotate by each ancestor, so this deliberately doesn't either).
        var p = seat;
        while (p != null && p.ParentID != 0)
        {
            if (sim.ObjectsAvatars.TryGetValue(p.ParentID, out var av) && av != null)
            {
                p = av;
                worldPos += p.Position;
            }
            else if (sim.ObjectsPrimitives.TryGetValue(p.ParentID, out p) && p != null)
            {
                worldPos += p.Position;
            }
            else
            {
                // An ancestor in the chain (e.g. a vehicle's root, for a seat on one of its child
                // parts) is unresolved too -- same race as the seat itself, one level up. worldPos
                // is short by whatever offset that ancestor would have added, so it's just as
                // untrustworthy as the seat-not-found case above.
                return false;
            }
        }
        return true;
    }

    /// <summary>Per-LocalID cooldown so a burst of terse updates for a seated avatar whose seat is
    /// still unresolved (they can arrive several times a second) sends ONE
    /// <c>RequestMultipleObjects</c> packet, not one per packet, while still re-asking if the
    /// first request was dropped rather than waiting indefinitely.</summary>
    private readonly ConcurrentDictionary<uint, DateTime> _lastSeatRequestByLocalId = new();

    private static readonly TimeSpan SeatRequestCooldown = TimeSpan.FromSeconds(2);

    /// <summary>Actively asks the sim to (re)send a seat prim <see cref="ResolveSeatedTransform"/>
    /// could not find, instead of passively waiting for it to show up on its own -- the object
    /// might already be streaming in, but there is no reason to just hope it arrives before the
    /// NEXT avatar update happens to need it again. Mirrors the real viewer's own behaviour of
    /// requesting an unresolved parent (LLViewerObject's orphan-child handling serves the same
    /// purpose from the other direction). This does not make the CURRENT packet's position
    /// correct -- only shortens how long the wrong one persists before a follow-up update, now
    /// far more likely to have the seat available, corrects it.</summary>
    private void RequestUnresolvedSeat(LibreMetaverse.Simulator sim, uint parentLocalId)
    {
        var now = DateTime.UtcNow;
        if (_lastSeatRequestByLocalId.TryGetValue(parentLocalId, out var last) && now - last < SeatRequestCooldown)
            return;
        _lastSeatRequestByLocalId[parentLocalId] = now;
        Console.Error.WriteLine(
            $"[Seat] parent {parentLocalId} unresolved for a seated avatar -- requesting it explicitly");
        _client.Objects.RequestObject(sim, parentLocalId);
    }

    /// <summary>BUG-NET-16: runs on EVERY full ObjectUpdate, checking whether any avatar
    /// LibreMetaverse already knows about is sitting on the prim that JUST updated (it might be a
    /// seat <see cref="RequestUnresolvedSeat"/> asked for, or just an ordinary edit/move of an
    /// already-known seat). A seated, otherwise-motionless avatar sends no packets of her own once
    /// resolution has already gone wrong once -- her <c>AvatarComponent</c>/<c>TransformComponent</c>
    /// would otherwise carry that one bad reading for the rest of the session, since nothing else
    /// would ever ask again. Re-resolving and re-publishing here reuses the exact same
    /// <see cref="ResolveSeatedTransform"/>/<see cref="AvatarUpdateEvent"/> path an ordinary packet
    /// would take, so WorldSimulation's existing large-jump-snaps/small-jump-eases logic (see
    /// ApplyAvatarUpdate) handles the correction the same way it would handle any other update.</summary>
    private void ReapplySeatedAvatarsOn(LibreMetaverse.Simulator sim, uint seatLocalId)
    {
        foreach (var av in sim.ObjectsAvatars.Values)
        {
            if (av == null || av.ParentID != seatLocalId) continue;
            if (!ResolveSeatedTransform(sim, av.Position, av.Rotation, av.ParentID, out var worldPos, out var worldRot))
                continue; // still unresolved for some other reason -- nothing new to publish

            // Only log when this local id was previously flagged unresolved -- an ordinary
            // ObjectUpdate for an already-fine seat (a chair being edited, a vehicle just moving)
            // takes this same path constantly and must not spam the console.
            if (_lastSeatRequestByLocalId.TryRemove(seatLocalId, out _))
            {
                Console.Error.WriteLine(
                    $"[Seat] parent {seatLocalId} resolved -- correcting avatar {av.LocalID}'s position");
            }

            bool isLocalAgent = av.ID == _client.Self.AgentID;
            AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
                sim.Handle,
                av.LocalID,
                av.ID.Guid,
                new System.Numerics.Vector3(worldPos.X, worldPos.Y, worldPos.Z),
                new System.Numerics.Quaternion(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
                av.FirstName,
                av.LastName,
                isLocalAgent,
                SittingOnLocalId: seatLocalId,
                GroupTitle: av.GroupName));
        }
    }

    private void OnAvatarAppearance(object? sender, AvatarAppearanceEventArgs e)
    {
        // Report the local agent's parameter set from an INCOMING appearance. This check first
        // hung off AppearanceSet, which only fires when LibreMetaverse runs its own bake -- i.e.
        // exactly the path SendAppearance=false disables -- so it could never fire in the
        // configuration it was written to diagnose. This event arrives regardless.
        if (e.AvatarID == _client.Self.AgentID)
        {
            if (!_visualParamsLogged)
            {
                _visualParamsLogged = true;
                LogVisualParamHealth();
            }

            // FEAT-AVATAR-01: remember the simulator's own relay of our shape. This array is in
            // Group0ParamIds (wire/decoder) order, which is what AvatarShapeService reads it as --
            // see _lastSelfRelayVisualParams. It is the ONLY trustworthy shape source for the self
            // avatar; LMV's MyVisualParameters is built in a DIFFERENT order (see OnAppearanceSet).
            if (VisualParamsHealthy(e.VisualParams?.ToArray()))
                _lastSelfRelayVisualParams = e.VisualParams!.ToArray();

            // The appearance workflow is off, so LMV's MyVisualParameters stays empty -- seed it
            // so a diagnostic read shows something truthful.
            TrySeedVisualParams(e.VisualParams?.ToArray(), "the self AvatarAppearance relay");
        }

        // FEAT-UI-16: a self appearance relay can change the worn wearable set.
        if (e.AvatarID == _client.Self.AgentID)
            WornItemsChanged?.Invoke(this, EventArgs.Empty);

        var textures = new Dictionary<int, Guid>();
        if (e.FaceTextures != null)
        {
            for (int i = 0; i < e.FaceTextures.Length; i++)
            {
                var face = e.FaceTextures[i];
                if (face != null && face.TextureID != LibreMetaverse.UUID.Zero)
                {
                    textures[i] = face.TextureID.Guid;
                }
            }
        }

        // FEAT-AVATAR-01: the simulator's own view of our baked textures -- the last set known to
        // actually work, since some other viewer produced them. Kept as the reference the bake
        // diagnostic compares LibreMetaverse's (possibly empty) Textures[] against, and the
        // fallback any future send must use rather than writing empty bake ids.
        if (e.AvatarID == _client.Self.AgentID && textures.Count > 0)
        {
            _lastSelfRelayBakes = new Dictionary<int, Guid>(textures);
            _selfAppearanceWithBakesSeen = true;
        }

        // AvatarAppearanceEventArgs doesn't expose the packet's AppearanceHover field (see
        // AvatarAppearanceEvent's doc comment for why it matters), but LibreMetaverse's own
        // internal AvatarAppearanceHandler already parsed it into the cached Avatar object's
        // HoverHeight before raising this event -- same ObjectsAvatars cache, same linear-scan-
        // by-AgentID pattern already used for the AgentId-resolution fix (see the ObjectUpdate/
        // TerseObjectUpdate handlers above), just keyed by AvatarID here since that's all this
        // event carries (no LocalID).
        float hoverOffsetZ = 0f;
        foreach (var kv in e.Simulator.ObjectsAvatars)
        {
            if (kv.Value != null && kv.Value.ID == e.AvatarID)
            {
                hoverOffsetZ = kv.Value.HoverHeight.Z;
                break;
            }
        }

        // Remembered for the local appearance refresh after our own bake, which has no incoming
        // event to read a hover height from and must not silently reset it to zero.
        if (e.AvatarID == _client.Self.AgentID) _lastSelfHoverOffsetZ = hoverOffsetZ;

        // Persist a healthy self appearance so a later login that receives none can still render
        // the real shape (ArmSelfAppearanceRestore).
        if (e.AvatarID == _client.Self.AgentID)
        {
            MaybeSaveSelfAppearanceCache();
            NoteSelfAppearanceRelayArrived();
        }

        AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
            e.Simulator.Handle,
            e.AvatarID.Guid,
            e.VisualParams?.ToArray() ?? Array.Empty<byte>(),
            textures,
            hoverOffsetZ
        ));
    }

    /// <summary>Fired when LibreMetaverse's appearance workflow (server-side bake POST on SL, or
    /// client-side bake + AgentSetAppearance on OpenSim) completes for the LOCAL agent. The self
    /// avatar's baked textures are produced by OUR viewer, not pushed unprompted by the sim, so
    /// this is the authoritative moment the real bakes exist.
    ///
    /// On server-side-baking regions LibreMetaverse also re-raises Avatars.AvatarAppearance for us
    /// with the composited ids (AppearanceManager.cs line 2323), so <see cref="OnAvatarAppearance"/>
    /// would already cover that case. But the OpenSim client-side path only populates
    /// <c>Appearance.MyTextures</c> and does NOT re-raise AvatarAppearance for self -- whether we
    /// then see a self AvatarAppearance depends on the sim echoing one, which is grid-dependent.
    /// Reading MyTextures here and emitting it through the same neutral event closes that gap so
    /// the renderer picks up the real bakes regardless of grid. Redundant-but-identical on SSB.</summary>
    /// <summary>Reports the visual-parameter set LibreMetaverse holds for the local agent.
    /// Read-only: it sends nothing and changes nothing. See its call site for why it exists.</summary>
    private bool _visualParamsLogged;

    private void LogVisualParamHealth()
    {
        try
        {
            var vp = _client.Appearance.MyVisualParameters;
            if (vp == null || vp.Length == 0)
            {
                if (Diag.Verbose) Console.Error.WriteLine("[VisualParams] LibreMetaverse holds NO visual parameters — " +
                    "an appearance send would have replaced the stored shape with defaults");
                return;
            }

            int zero = 0, mid = 0;
            foreach (var b in vp)
            {
                if (b == 0) zero++;
                else if (b == 128) mid++;
            }

            // 218 is the modern parameter count; a much shorter array means an incomplete set.
            // All-zero or all-128 is the tell-tale of a never-populated (default) array rather
            // than a real shape.
            if (Diag.Verbose) Console.Error.WriteLine($"[VisualParams] {vp.Length} params, {zero} zero, {mid} at 128 " +
                $"(mid), first 12: {string.Join(",", vp.Take(12))}" +
                ((zero + mid == vp.Length) ? "  <-- ALL DEFAULT: sending this would flatten the avatar" : ""));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VisualParams] could not be inspected: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The modern SL VisualParams set is 218 bytes; a materially shorter relay is
    /// incomplete. FEAT-AVATAR-01.</summary>
    internal const int MinHealthyVisualParams = 200;

    private bool _visualParamsSeeded;

    /// <summary>True if <paramref name="vp"/> reads as a genuine shape: long enough, and not the
    /// all-zero / all-128 array of a never-populated default set.</summary>
    internal static bool VisualParamsHealthy(byte[]? vp)
    {
        if (vp == null || vp.Length < MinHealthyVisualParams) return false;
        int defaulted = 0;
        foreach (var b in vp)
            if (b == 0 || b == 128) defaulted++;
        return defaulted < vp.Length;
    }

    /// <summary>Copies the simulator's self-appearance VisualParams into
    /// <c>AppearanceManager.MyVisualParameters</c> once, when LibreMetaverse holds nothing better,
    /// so <c>LogVisualParamHealth()</c> reports a real value while LMV's own workflow is disabled.
    ///
    /// NOTE: this does NOT make a wearable-edit rebake safe. <c>AppearanceManager.MakeAppearancePacket</c>
    /// rebuilds every param from the decoded <c>wearable.Asset</c> (falling back to
    /// <c>DefaultValue</c> when the asset was never downloaded) and then OVERWRITES
    /// <c>MyVisualParameters</c> — it never reads the seeded value. FEAT-AVATAR-01 Phase 1's
    /// wearable send is reverted for exactly this reason.</summary>
    private void TrySeedVisualParams(byte[]? arr, string source)
    {
        if (_visualParamsSeeded) return;
        if (!VisualParamsHealthy(arr)) return;
        if (VisualParamsHealthy(_client.Appearance.MyVisualParameters)
            && _client.Appearance.MyVisualParameters.Length >= arr!.Length)
        {
            _visualParamsSeeded = true; // LMV already has an equal-or-better set
            return;
        }

        _client.Appearance.MyVisualParameters = arr!;
        _visualParamsSeeded = true;
        if (Diag.Verbose) Console.Error.WriteLine($"[VisualParams] seeded {arr!.Length} params from {source} (diagnostic only)");
    }

    /// <summary>Whether a "Wear" / "Detach" on an inventory item targets a system wearable
    /// (Clothing/Bodypart layer — Alpha, Skin, Shape, Tattoo, Universal, …) rather than an
    /// attachment. FEAT-AVATAR-01.</summary>
    internal enum WearableKind { Attachment, Wearable }

    /// <summary>Classifies a resolved inventory item. Pure function of the two facts that decide it,
    /// so it unit-tests without a live client: LibreMetaverse types it as an
    /// <c>InventoryWearable</c>, or its asset type is Clothing (5) or Bodypart (13).</summary>
    internal static WearableKind ClassifyItem(bool isInventoryWearable, int assetType)
        => isInventoryWearable
           || assetType == (int)LibreMetaverse.AssetType.Clothing
           || assetType == (int)LibreMetaverse.AssetType.Bodypart
            ? WearableKind.Wearable
            : WearableKind.Attachment;

    /// <summary>FEAT-AVATAR-01: true only when the current region does the full SL server-side-baking
    /// handshake — advertises the <c>AgentAppearanceService</c> protocol AND registers the
    /// <c>UpdateAvatarAppearance</c> capability. Here a wearable edit needs no local preparation:
    /// <c>RemoveFromOutfit</c>/<c>AddToOutfit</c> reach <c>RequestSetAppearanceAsync</c>'s SSB branch,
    /// whose only outgoing request is a cap POST of <c>{ cof_version }</c> — no visual params, no
    /// shape — and the server composites. This is an SL path; OpenSim's own server-side appearance
    /// (XBakes) does NOT set these (<c>RequestSetAppearanceAsync</c> ~3264 and the comment at
    /// <c>MakeAppearancePacket</c> ~2952 both say "always false on OpenSim"), so on OpenSim this is
    /// false and the wearable edit takes the client-side path — safe only after
    /// <see cref="EnsureWornWearablesDecodedAsync"/>.</summary>
    public bool RegionHasServerSideBaking()
        => _client.Network.Connected
           && _client.Appearance.ServerBakingRegion()
           && _client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAvatarAppearance") != null;

    private bool _appearanceReadinessLogged;

    /// <summary>Logs, once per region, that system-wearable edits are disabled and why — so "the
    /// alpha layer won't come off" has an answer in the log. FEAT-AVATAR-01.</summary>
    private void LogAppearanceEditReadiness()
    {
        if (_appearanceReadinessLogged) return;
        _appearanceReadinessLogged = true;
        string region = _client.Network.CurrentSim?.Name ?? "?";
        Console.Error.WriteLine($"[Appearance] {region}: system-wearable edits enabled — every bake is " +
            "followed by an AgentSetAppearance built by AgentAppearanceParams: all 253 transmitted " +
            "visual params, in wire order (FEAT-AVATAR-01)");
    }

    /// <summary>FEAT-AVATAR-01: raised when the corrected <c>AgentSetAppearance</c> failed its
    /// pre-send verification and was therefore NOT sent — meaning LibreMetaverse's scrambled packet
    /// is what the grid now holds and the avatar needs repairing in another viewer. Payload is the
    /// verification failure, for a user-facing notice. Should never fire; if it does, that is the
    /// signal to stop editing wearables.</summary>
    public event EventHandler<string>? WearableEditUnavailable;

    /// <summary>Raised when a wardrobe edit is refused because Second Life does not allow it — a
    /// body part being taken off, say. Separate from <see cref="WearableEditUnavailable"/>, which
    /// means "this should work and did not": this one carries a finished explanation for the user,
    /// not a failure.</summary>
    public event EventHandler<string>? WearableEditRefused;

    // FEAT-AVATAR-01 — system-wearable remove/add. Blocked on an upstream bug, MEASURED not guessed.
    //
    // AppearanceManager.MakeAppearancePacket builds the outgoing AgentSetAppearance by iterating
    // VisualParams.Params (ALL 672 params) and taking the first 218, but the wire order is
    // VisualParams.Group0ParamIds (the 253 TRANSMITTED ids). They agree for 23 slots and diverge
    // from index 23 on -- 195 of 218 values land on the WRONG parameter, and the sim persists that.
    // Pinned by SLNG.Assets.Tests.VisualParamOrderTests.
    //
    // Every route into AppearanceManager hits it: AddToOutfit / RemoveFromOutfit /
    // ReplaceOutfitAsync / RequestSetAppearance all reach MakeAppearancePacket. That is the single
    // explanation for all three live incidents (2026-08-02 deformed, 2026-08-29 flat, 2026-08-31
    // torn rigged head), and no amount of preparing the wearables first can fix it -- the three
    // attempts that tried are in the spec.
    //
    // The way out, implemented here, needs no fork: let LibreMetaverse do the half it gets right
    // (compositing and uploading the bake textures), then immediately send a CORRECTED
    // AgentSetAppearance over the top -- MakeAppearancePacket() is public, so its fresh TextureEntry
    // is reusable while VisualParam[] and AgentData.Size are rebuilt by AgentAppearanceParams.
    // OpenSim's LLClientView.HandlerAgentSetAppearance never reads AgentData.SerialNum and never
    // rejects a repeat, so last write wins and the corrected packet is what gets stored. See
    // SendCorrectedAppearance below and the spec for the trade-off this accepts.

    /// <summary>Sends the legacy <c>AgentWearablesRequest</c> and waits for the simulator's
    /// <c>AgentWearablesUpdate</c>, which is what populates <c>AppearanceManager.Wearables</c>.
    ///
    /// <para>LibreMetaverse would send this itself at login, but only under
    /// <c>Settings.Agent.SendAppearance</c> — which stays off (see the ctor) — so with the flag off
    /// nobody ever asks and the worn set is empty for the whole session. That empty set is why the
    /// "Angezogen" tab showed every Clothing/Bodypart layer as "(nicht aktiv)": <c>GetWornItems</c>
    /// marks an item live from <c>GetWearables()</c>, and could only ever see the COF link instead.
    /// It is also what a wearable edit needs to build an appearance from.</para>
    ///
    /// <para>Mirrors LibreMetaverse's own private <c>GatherAgentWearablesViaLLUDPAsync</c>. Prefer
    /// this over the COF route (<c>RequestAgentWornAsync</c>): measured 2026-08-31, that one returns
    /// empty on OSGrid, while OpenSim answers this packet reliably. Best-effort — returns on the
    /// reply or after a 10 s timeout, and never throws into the caller.</para></summary>
    private async Task RequestWornWearablesViaLludpAsync(CancellationToken ct)
    {
        if (!_client.Network.Connected) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? s, LibreMetaverse.AgentWearablesReplyEventArgs e) => tcs.TrySetResult(true);

        _client.Appearance.AgentWearablesReply += OnReply;
        try
        {
            var request = new LibreMetaverse.Packets.AgentWearablesRequestPacket
            {
                AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID }
            };
            _client.Network.SendPacket(request);

            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);

            int count = _client.Appearance.GetWearables().Count();
            if (Diag.Verbose)
            {
                Console.Error.WriteLine(count > 0
                    ? $"[Appearance] worn wearables resolved: {count}"
                    : "[Appearance] no worn wearables returned -- the Worn tab will show layers as inactive");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] AgentWearablesRequest failed: {ex.Message}");
        }
        finally
        {
            _client.Appearance.AgentWearablesReply -= OnReply;
        }
    }

    /// <summary>Sends an <c>AgentSetAppearance</c> whose visual parameters are in the order the
    /// simulator actually reads, replacing the scrambled one LibreMetaverse just sent.
    ///
    /// <para>Called from <see cref="OnAppearanceSet"/>, i.e. after LibreMetaverse has finished a
    /// bake: at that point it has composited and uploaded the baked textures (its baker is correct)
    /// and has already sent its own packet with <b>195 of 218 params on the wrong parameter</b>.
    /// <c>MakeAppearancePacket()</c> is public, so the good half — the freshly baked
    /// <c>TextureEntry</c> and the wearable cache blocks — can be reused verbatim while
    /// <c>VisualParam[]</c> and <c>AgentData.Size</c> are rebuilt correctly by
    /// <see cref="AgentAppearanceParams"/>.</para>
    ///
    /// <para>This works because OpenSim's <c>LLClientView.HandlerAgentSetAppearance</c> never looks
    /// at <c>AgentData.SerialNum</c> and never rejects a repeat — last write wins. There is
    /// therefore a brief window (until this lands) in which the scrambled appearance is the stored
    /// one; that is the accepted trade-off of not forking LibreMetaverse.</para>
    ///
    /// <para>Refuses to send if the built array does not verify
    /// (<see cref="AgentAppearanceParams.VerifyRoundTrip"/>) — leaving LibreMetaverse's bad packet
    /// standing is worse than nothing, but sending a second unverified one is worse still.</para></summary>
    private void SendCorrectedAppearance()
    {
        if (!_client.Network.Connected) return;

        // Belt and braces. With SendAppearance off and no direct RequestSetAppearance call left,
        // LibreMetaverse never bakes, so OnAppearanceSet never fires and this is unreachable --
        // but "unreachable" is exactly what was assumed about the login send that broke the avatar.
        // The one thing this method must never do is transmit while writing is disabled.
        if (!_client.Settings.Agent.SendAppearance)
        {
            Console.Error.WriteLine("[Appearance] correction suppressed: appearance writing is disabled");
            return;
        }

        try
        {
            // The bake decoded these; Asset.Params is the wearable's own paramId -> weight map.
            var wearableParams = _client.Appearance.GetWearables()
                .Where(w => w.Asset != null)
                .Select(w => (IReadOnlyDictionary<int, float>)w.Asset!.Params)
                .ToList();

            var packet = _client.Appearance.MakeAppearancePacket();
            // Deliberately NOT packet.VisualParam.Length: LibreMetaverse allocates 218 (251 with a
            // Physics layer) while the wire actually carries 253 -- the simulator's own relay was
            // measured at 253, matching Group0ParamIds. Following LMV's length would truncate 35
            // parameters on top of the ordering bug. OpenSim reads the length dynamically.
            int length = AgentAppearanceParams.DefaultLength;

            byte[] wire;
            if (wearableParams.Count > 0)
            {
                wire = AgentAppearanceParams.BuildWireArray(wearableParams, length);
                if (!AgentAppearanceParams.VerifyRoundTrip(wire, wearableParams, out var failure))
                {
                    // Fall through to the relay below rather than returning: LibreMetaverse has
                    // ALREADY sent its scrambled packet by the time we get here, so staying silent
                    // leaves that as the account's stored shape. Overwriting with the simulator's
                    // own last relay is always at least as good as what it already had.
                    Console.Error.WriteLine($"[Appearance] built params rejected ({failure}) -- falling back to the simulator's relay");
                    wire = Array.Empty<byte>();
                }
            }
            else
            {
                wire = Array.Empty<byte>();
            }

            if (wire.Length == 0)
            {
                // No usable wearable-derived shape -- happens on the LOGIN bake, where
                // LibreMetaverse can complete without any wearable asset decoded. Send back the
                // shape the simulator itself last told us, which is already in wire order and is by
                // definition what the account holds. Measured 2026-08-31: an early return here left
                // LibreMetaverse's scrambled login packet standing and broke the avatar, which is
                // why this path must still SEND rather than skip.
                if (_lastSelfRelayVisualParams.Length == 0)
                {
                    Console.Error.WriteLine("[Appearance] correction NOT sent: no decoded wearables AND no simulator relay yet " +
                        "-- LibreMetaverse's packet stands, appearance may be wrong until a rebake");
                    WearableEditUnavailable?.Invoke(this, "no shape available to correct with");
                    return;
                }

                wire = _lastSelfRelayVisualParams.Length > length
                    ? _lastSelfRelayVisualParams.Take(length).ToArray()
                    : _lastSelfRelayVisualParams;
                Console.Error.WriteLine($"[Appearance] no decoded wearables -- restoring the simulator's own last shape ({wire.Length} params)");
            }

            // Bake textures. LibreMetaverse only composites when SendAppearance is on, and it is
            // not -- so its Textures[] can be all-zero, and a packet built from it says "I have no
            // baked textures". The simulator persists that and the avatar renders untextured for
            // everyone until something re-bakes it: measured live 2026-08-31, the head lost its
            // texture on the grid and only came back after a Firestorm login. Fill any empty slot
            // from the simulator's own last relay, and refuse outright if that still leaves a hole.
            var currentBakes = new Dictionary<int, Guid>();
            var teBytes = packet.ObjectData.TextureEntry;
            var entry = teBytes is { Length: > 1 }
                ? new Primitive.TextureEntry(teBytes, 0, teBytes.Length) : null;
            foreach (int slot in AgentAppearanceParams.EssentialBakeSlots)
            {
                var face = entry?.FaceTextures is { } fs && slot < fs.Length ? fs[slot] : null;
                var id = face?.TextureID ?? LibreMetaverse.UUID.Zero;
                currentBakes[slot] = id == AppearanceManager.DEFAULT_AVATAR_TEXTURE ? Guid.Empty : id.Guid;
            }

            var merged = AgentAppearanceParams.MergeBakeSlots(currentBakes, _lastSelfRelayBakes, out bool bakesComplete);
            if (!bakesComplete)
            {
                Console.Error.WriteLine("[Appearance] correction NOT sent -- incomplete bake set " +
                    $"({string.Join(" ", merged.OrderBy(k => k.Key).Select(k => $"{k.Key}={(k.Value == Guid.Empty ? "EMPTY" : "ok")}"))}); " +
                    "sending it would strip the avatar's textures");
                WearableEditUnavailable?.Invoke(this, "incomplete bake set");
                return;
            }

            if (entry != null)
            {
                foreach (var kv in merged) entry.CreateFace((uint)kv.Key).TextureID = new LibreMetaverse.UUID(kv.Value);
                packet.ObjectData.TextureEntry = entry.GetBytes();
            }

            var blocks = new LibreMetaverse.Packets.AgentSetAppearancePacket.VisualParamBlock[wire.Length];
            for (int i = 0; i < wire.Length; i++)
                blocks[i] = new LibreMetaverse.Packets.AgentSetAppearancePacket.VisualParamBlock { ParamValue = wire[i] };
            packet.VisualParam = blocks;

            // Height is derived from params 33/198/503/682/692/842, which LibreMetaverse reads off
            // whatever its mis-ordered loop landed on -- so its Size is wrong for the same reason.
            packet.AgentData.Size = new Vector3(0.45f, 0.6f,
                AgentAppearanceParams.ComputeAgentHeight(wearableParams));

            _client.Network.SendPacket(packet);

            // Keep LibreMetaverse's own copy consistent with what the grid now holds, so anything
            // else reading it is not looking at the scrambled array.
            _client.Appearance.MyVisualParameters = wire;

            int nonDefault = wire.Count(b => b != 0);
            int preserved = merged.Count(kv => currentBakes[kv.Key] == Guid.Empty);
            Console.Error.WriteLine($"[Appearance] corrected AgentSetAppearance sent " +
                $"({wire.Length} params, {nonDefault} non-zero, height {packet.AgentData.Size.Z:F2} m, " +
                $"{preserved}/{merged.Count} bake slots preserved from the previous appearance)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] correction failed: {ex.Message}");
        }
    }

    /// <summary>FEAT-AVATAR-01: recomposites the baked textures from the currently worn set and
    /// re-sends the appearance — the manual rebake every viewer offers (Firestorm: Ctrl+Alt+R).
    /// Same corrected path as a wearable edit: LibreMetaverse bakes, then
    /// <see cref="SendCorrectedAppearance"/> replaces its scrambled packet. Use it when a wearable
    /// change did not visibly take.</summary>
    /// <summary>FEAT-AVATAR-01: runs the real bake locally and reports what it produced, WITHOUT
    /// uploading and WITHOUT sending anything.
    ///
    /// <para>This is possible because LibreMetaverse's pieces are all public — <c>Baker</c>,
    /// <c>AppearanceManager.DecodeWearableParams</c>, <c>BakeTypeToTextures</c> and the
    /// <c>TextureProvider</c> — so the compositing step can be driven on its own instead of
    /// through <c>RequestSetAppearance</c>, which bakes, uploads AND sends in one call. Every
    /// previous attempt to learn anything about the bake had to send to find out; this does not.</para>
    ///
    /// <para>Mirrors <c>CreateBakeAsync</c>: gather the worn wearables, decode their assets, let
    /// <c>DecodeWearableParams</c> fill the per-index <c>TextureData</c>, fetch those textures,
    /// then feed each bake channel's indices to a <c>Baker</c> and report the result size.</para></summary>
    private IBakeTextureEncoder? _bakeEncoder;

    /// <summary>Supplies the JPEG2000 encoder used for avatar bakes. Set by the composition root
    /// (<c>app</c>), because the codec lives in <c>SLNG.Assets</c> and the layering forbids
    /// <c>SLNG.Net</c> from referencing it — see <see cref="IBakeTextureEncoder"/>.</summary>
    public void UseBakeEncoder(IBakeTextureEncoder encoder) => _bakeEncoder = encoder;

    /// <summary>Builds a <see cref="ManagedImage"/> from tightly packed 8-bit BGRA — the inverse of
    /// <see cref="ToBgra"/>, so a generated pattern can be handed to the baker as if it had been
    /// downloaded and decoded like any other texture.</summary>
    private static ManagedImage ToManagedImage(byte[] bgra, int size)
    {
        var image = new ManagedImage(size, size,
            ManagedImage.ImageChannels.Color | ManagedImage.ImageChannels.Alpha);

        for (int i = 0; i < size * size; i++)
        {
            image.Blue[i] = bgra[i * 4 + 0];
            image.Green[i] = bgra[i * 4 + 1];
            image.Red[i] = bgra[i * 4 + 2];
            image.Alpha[i] = bgra[i * 4 + 3];
        }

        return image;
    }

    /// <summary>Converts a composited bake into the tightly packed 8-bit BGRA the encoder expects.
    /// Mirrors <c>ManagedImage.ExportBitmap</c> so the two cannot drift, but stays in plain bytes so
    /// no SkiaSharp type has to cross into this assembly.</summary>
    private static byte[] ToBgra(ManagedImage img)
    {
        int n = img.Width * img.Height;
        var raw = new byte[n * 4];
        // Test lengths, not nulls: ManagedImage initialises every channel to Array.Empty<byte>(),
        // so an absent channel is an empty array and a null check passes straight into an
        // out-of-range read. Measured on the Color-only EyesIris texture.
        bool color = img.Red.Length >= n && img.Green.Length >= n && img.Blue.Length >= n;
        bool alpha = img.Alpha.Length >= n;

        for (int i = 0; i < n; i++)
        {
            if (color)
            {
                raw[i * 4 + 0] = img.Blue![i];
                raw[i * 4 + 1] = img.Green![i];
                raw[i * 4 + 2] = img.Red![i];
            }
            else if (alpha)
            {
                // Alpha-only layer: replicate to RGB the way ExportBitmap does.
                raw[i * 4 + 0] = raw[i * 4 + 1] = raw[i * 4 + 2] = img.Alpha![i];
            }
            raw[i * 4 + 3] = color && alpha ? img.Alpha![i] : byte.MaxValue;
        }

        return raw;
    }

    /// <summary>
    /// FEAT-AVATAR-01: tells the grid what the avatar now looks like — the last step of the bake.
    ///
    /// <para>Everything here was built from measurement, because this exact packet corrupted a real
    /// avatar three times. The visual parameters come from <see cref="AgentAppearanceParams"/>,
    /// which orders them the way the wire does; LibreMetaverse's own encoder mis-orders 195 of 218
    /// and truncates 35 more. The baked-texture ids come from bakes this client composited, encoded
    /// and uploaded itself, verified against the reference viewer's own bakes for the same avatar at
    /// a mean channel difference of 2.6/255.</para>
    ///
    /// <para>Three refusals stand in front of the send, in order of how badly each failed before:
    /// the parameter array must survive a round trip, every essential bake slot must hold a real id,
    /// and an incomplete set falls back to the ids the simulator already had rather than sending
    /// empties — an empty slot is persisted and renders the avatar untextured for everyone.</para>
    /// </summary>
    /// <param name="bakes">Bake slot (<c>AvatarTextureIndex</c>) → uploaded asset id.</param>
    /// <param name="wearableParams">The worn wearables' decoded paramId → weight maps, in layer order.</param>
    /// <returns>True when the packet was sent.</returns>
    private bool SendAppearanceFromOwnBake(
        IReadOnlyDictionary<int, LibreMetaverse.UUID> bakes,
        IReadOnlyList<IReadOnlyDictionary<int, float>> wearableParams)
    {
        if (!_client.Network.Connected)
        {
            Console.Error.WriteLine("[Appearance] not sent: not connected");
            return false;
        }

        // 1. Shape. Never LibreMetaverse's array -- see AgentAppearanceParams for why.
        byte[] wire;
        if (wearableParams.Count > 0)
        {
            wire = AgentAppearanceParams.BuildWireArray(wearableParams);
            if (!AgentAppearanceParams.VerifyRoundTrip(wire, wearableParams, out var failure))
            {
                Console.Error.WriteLine($"[Appearance] NOT sent: built params failed verification ({failure})");
                return false;
            }
        }
        else if (_lastSelfRelayVisualParams.Length > 0)
        {
            // No decoded wearables: send back the shape the simulator itself last reported, which is
            // already in wire order and is by definition what the account holds.
            wire = _lastSelfRelayVisualParams;
            Console.Error.WriteLine($"[Appearance] no decoded wearables -- keeping the simulator's own shape ({wire.Length} params)");
        }
        else
        {
            Console.Error.WriteLine("[Appearance] NOT sent: no shape to send (no wearables decoded, no relay seen)");
            return false;
        }

        // 2. Bake slots. Anything we did not upload falls back to what the grid already had; a hole
        //    after that means refusing, because sending an empty slot strips the avatar for everyone.
        var current = bakes.ToDictionary(kv => kv.Key, kv => kv.Value.Guid);
        var merged = AgentAppearanceParams.MergeBakeSlots(current, _lastSelfRelayBakes, out bool complete);
        if (!complete)
        {
            Console.Error.WriteLine("[Appearance] NOT sent: incomplete bake set (" +
                string.Join(" ", merged.OrderBy(k => k.Key).Select(k => $"{k.Key}={(k.Value == Guid.Empty ? "EMPTY" : "ok")}")) +
                ") -- sending it would strip the avatar's textures");
            return false;
        }

        // 3. Texture entry: the bake ids in their slots, the default avatar texture everywhere else,
        //    which is what a viewer sends.
        var entry = new Primitive.TextureEntry(AppearanceManager.DEFAULT_AVATAR_TEXTURE);
        foreach (var kv in merged)
            entry.CreateFace((uint)kv.Key).TextureID = new LibreMetaverse.UUID(kv.Value);

        var packet = new LibreMetaverse.Packets.AgentSetAppearancePacket
        {
            AgentData =
            {
                AgentID = _client.Self.AgentID,
                SessionID = _client.Self.SessionID,
                SerialNum = _appearanceSerial++,
                // Height comes from params 33/198/503/682/692/842, resolved by id rather than by
                // loop position -- LibreMetaverse reads them off whatever its mis-ordered loop
                // landed on, so its Size is wrong for the same reason its array is.
                Size = new Vector3(0.45f, 0.6f, AgentAppearanceParams.ComputeAgentHeight(wearableParams)),
            },
            ObjectData = { TextureEntry = entry.GetBytes() },
            VisualParam = wire
                .Select(b => new LibreMetaverse.Packets.AgentSetAppearancePacket.VisualParamBlock { ParamValue = b })
                .ToArray(),
            WearableData = Array.Empty<LibreMetaverse.Packets.AgentSetAppearancePacket.WearableDataBlock>(),
        };

        _client.Network.SendPacket(packet);

        // Keep LibreMetaverse's own copy consistent with what the grid now holds, so anything else
        // reading it is not looking at the scrambled array.
        _client.Appearance.MyVisualParameters = wire;
        foreach (var kv in merged) _lastSelfRelayBakes[kv.Key] = kv.Value;

        int fromUs = merged.Count(kv => current.TryGetValue(kv.Key, out var c) && c == kv.Value && c != Guid.Empty);
        Console.Error.WriteLine($"[Appearance] SENT: {wire.Length} params, height {packet.AgentData.Size.Z:F2} m, " +
            $"{fromUs}/{merged.Count} bake slots from this bake -- " +
            string.Join(" ", merged.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.ToString()[..8]}")));

        // Tell our own renderer what we just baked. Nothing else will: the simulator stores an
        // AgentSetAppearance but does not echo an AvatarAppearance back to the sender, so the only
        // path that carries new bake ids to the scene never fires for the local avatar. Measured
        // 2026-09-01 -- the avatar stayed untextured after a correct bake and correct upload, and
        // came up right on the next login, which is the same ids arriving through the login path
        // instead. A viewer knows its own bake; this is the local half of that.
        AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
            _client.Network.CurrentSim?.Handle ?? 0,
            _client.Self.AgentID.Guid,
            wire,
            merged.ToDictionary(kv => kv.Key, kv => kv.Value),
            _lastSelfHoverOffsetZ));

        return true;
    }

    private uint _appearanceSerial = 1;

    /// <summary>Builds the worn set from the Current Outfit Folder, which is what actually defines
    /// what an avatar wears — and, unlike the legacy <c>AgentWearablesReply</c>, can hold several
    /// layers of one type. Also reports every link it finds, since "which of my layers does the
    /// client see" turned out to be the question behind a wrong face.</summary>
    private async Task<List<AppearanceManager.WearableData>> CollectWornWearablesForBakeAsync(bool verbose, CancellationToken ct)
    {
        var worn = new List<AppearanceManager.WearableData>();
        var cof = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cof == LibreMetaverse.UUID.Zero) return worn;

        // Ask for the folder rather than reading the store: the store only holds what has already
        // been fetched, and at bake time nothing has opened the inventory yet. Reading it straight
        // reported an empty COF and silently fell back to the region's stale list.
        var links = await _client.Inventory.FolderContentsAsync(
            cof, _client.Self.AgentID, fetchFolders: false, fetchItems: true,
            LibreMetaverse.InventorySortOrder.ByName, ct).ConfigureAwait(false);
        if (links == null) return worn;

        // A COF entry is a link; the wearable it points at is a separate item that also has to be
        // present before its type and asset can be read.
        var targets = new Dictionary<LibreMetaverse.UUID, LibreMetaverse.UUID>();
        foreach (var entry in links)
        {
            if (entry is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;
            var target = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (target != LibreMetaverse.UUID.Zero) targets[target] = _client.Self.AgentID;
        }

        var store = _client.Inventory.Store;
        if (targets.Count > 0 && targets.Keys.Any(id => store?.GetNodeOrDefault(id)?.Data is not LibreMetaverse.InventoryWearable))
        {
            try
            {
                _client.Inventory.RequestFetchInventory(targets);
                // No completion event covers a bulk fetch, so give the replies a moment to land.
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best effort -- unresolved targets are reported below */ }
        }

        // One link per wearable. A COF can hold several links to the same item -- this one holds 20
        // for 10 wearables, each item linked twice, once with an ordering token and once without.
        // Wearing a layer twice draws it twice, and the untokened copy sinks to the bottom of the
        // stack, so a duplicate of an opaque skin quietly reappears underneath everything. Keep the
        // link that carries a valid token; it is the one the viewer's own ordering is built on.
        int unresolved = 0, duplicates = 0;
        var chosen = new Dictionary<LibreMetaverse.UUID, (LibreMetaverse.InventoryWearable Wearable, string? Description)>();
        var order = new List<LibreMetaverse.UUID>();

        foreach (var entry in links)
        {
            if (entry is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var target = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (target == LibreMetaverse.UUID.Zero) continue;

            if (store?.GetNodeOrDefault(target)?.Data is not LibreMetaverse.InventoryWearable w)
            {
                // Attachments live in the COF too, so only count links that stayed unresolved.
                if (link.AssetType is LibreMetaverse.AssetType.Clothing or LibreMetaverse.AssetType.Bodypart)
                    unresolved++;
                continue;
            }

            bool tokened = WearableLayerOrder.IsValidOrderString(link.Description, (int)w.WearableType);

            if (chosen.TryGetValue(target, out var existing))
            {
                duplicates++;
                bool existingTokened = WearableLayerOrder.IsValidOrderString(existing.Description, (int)w.WearableType);
                if (tokened && !existingTokened) chosen[target] = (w, link.Description);
                continue;
            }

            chosen[target] = (w, link.Description);
            order.Add(target);
        }

        foreach (var target in order)
        {
            var (w, description) = chosen[target];
            worn.Add(new AppearanceManager.WearableData
            {
                ItemID = target,
                AssetID = w.AssetUUID,
                AssetType = w.AssetType,
                WearableType = w.WearableType,
            });
            _cofLinkDescriptions[target] = description ?? string.Empty;

            if (verbose)
                Console.Error.WriteLine($"[Bake]   COF {w.WearableType,-10} \"{w.Name}\"" +
                    (string.IsNullOrEmpty(description) ? "" : $"  desc=\"{description}\""));
        }

        if (duplicates > 0 && verbose)
            Console.Error.WriteLine($"[Bake]   COF: dropped {duplicates} duplicate link(s) -- the outfit folder links some items more than once");
        if (unresolved > 0)
            Console.Error.WriteLine($"[Bake]   COF: {unresolved} wearable link(s) did not resolve -- baking would miss them");

        return worn;
    }

    /// <summary>Layer-ordering tokens read off the COF links while collecting the worn set, kept so
    /// the ordering step uses the same link the wearable was chosen from rather than looking the
    /// folder up again — with duplicate links present, a second lookup can pick the other one.</summary>
    private readonly Dictionary<LibreMetaverse.UUID, string> _cofLinkDescriptions = new();

    /// <summary>Puts the worn wearables into the layer order Second Life actually stacks them in:
    /// grouped by type, and within a type sorted by the ordering token the viewer stores in the
    /// Current Outfit Folder link's description. Bottom layer first — see
    /// <see cref="WearableLayerOrder"/> for the rule and its source.</summary>
    private List<AppearanceManager.WearableData> OrderWearablesAsTheViewerDoes(List<AppearanceManager.WearableData> worn, bool verbose)
    {
        // The tokens recorded while the worn set was collected. Deliberately not a fresh folder
        // lookup: with duplicate links present, looking up again can land on the other link -- the
        // untokened one -- and lose the ordering that was just resolved.
        var descriptions = _cofLinkDescriptions;

        var result = new List<AppearanceManager.WearableData>();
        foreach (var group in worn.GroupBy(w => w.WearableType))
        {
            result.AddRange(WearableLayerOrder.Sort(
                group,
                (int)group.Key,
                w => descriptions.TryGetValue(w.ItemID, out var d) ? d : null,
                w => w.ItemID.ToString()));
        }

        int tokened = worn.Count(w => descriptions.TryGetValue(w.ItemID, out var d)
                                      && WearableLayerOrder.IsValidOrderString(d, (int)w.WearableType));
        if (verbose)
            Console.Error.WriteLine($"[Bake] layer order: {tokened}/{worn.Count} wearables carry a COF ordering token");

        return result;
    }

    /// <summary>Writes a bake input or result out as a PNG so it can be looked at. Everything about
    /// this task that was decided from numbers alone turned out to be decidable only from the
    /// picture.
    ///
    /// Refuses on a Linden grid (TPV Policy §2.b). The "in_" / "ref_" dumps are DECODED wearable
    /// textures worn by whoever is currently baking -- other creators' skins, tattoos, clothing
    /// layers -- written to disk as plain PNGs. That is an export SL's own viewer has no
    /// equivalent of, and the policy requires verifying the SL creator name matches the viewer
    /// user's own before any such export, "including content that may be set to 'full
    /// permissions.'" No such check exists here, so the safe answer on SL is not to write the
    /// file at all; OpenSim carries no such restriction, and this is the only environment where
    /// SLNG_BAKE_VERBOSE has ever been used to chase a bake defect.</summary>
    private void DumpPreview(string name, ManagedImage? image)
    {
        if (image?.Red == null || _bakeEncoder == null) return;
        if (_isLindenGrid)
        {
            Console.Error.WriteLine($"[Bake]   preview '{name}' skipped -- disabled on a Linden grid (TPV Policy §2.b)");
            return;
        }

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "slng_bake");
            Directory.CreateDirectory(dir);
            var png = _bakeEncoder.EncodePreviewPng(ToBgra(image), image.Width, image.Height);
            if (png.Length == 0) return;

            var path = Path.Combine(dir, $"{name}.png");
            File.WriteAllBytes(path, png);
            Console.Error.WriteLine($"[Bake]   preview -> {path}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Bake]   preview '{name}' failed: {ex.Message}"); }
    }

    /// <summary>
    /// FEAT-AVATAR-01: creates a known-answer test skin in the inventory — three generated textures
    /// plus a Skin bodypart that references them. See <see cref="TestSkinTextures"/> for why a
    /// synthetic skin answers questions a real one cannot.
    ///
    /// <para>Creates inventory items and uploads assets, so it is a deliberate action rather than
    /// part of any automatic path. It does not wear anything: the new skin appears in Body Parts and
    /// is put on like any other, which keeps the thing being tested (wearing a skin and rebaking)
    /// the thing the tester actually does.</para>
    ///
    /// <para>Refused on a Linden grid: three texture uploads and one inventory-item creation are
    /// real L$ upload fees on Agni, spent on a diagnostic tool this task built specifically for
    /// the OpenSim (XBakes) bake path -- see <see cref="BakeAvatarAsync"/>'s own SSB guard, which
    /// this mirrors. Nothing here is needed to test SL: a real skin already answers the same
    /// question there.</para>
    /// </summary>
    /// <returns>A short status line for the chat.</returns>
    public async Task<string> CreateTestSkinAsync(CancellationToken ct = default)
    {
        if (!_client.Network.Connected) return "nicht verbunden";
        if (_bakeEncoder == null) return "kein Bake-Encoder verfügbar";
        if (_isLindenGrid)
        {
            Console.Error.WriteLine("[TestSkin] skipped: refused on a Linden grid -- costs real upload fees for a diagnostic OpenSim-only tool");
            return "Auf Second Life gesperrt — das Testmuster ist ein OpenSim-Diagnosewerkzeug und würde echte Upload-Gebühren kosten.";
        }

        try
        {
            const uint all = (uint)LibreMetaverse.PermissionMask.All;
            var perms = new LibreMetaverse.Permissions(all, all, all, all, all);

            var textureFolder = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.Texture);
            var bodypartFolder = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.BodyPart);
            // Plain ASCII, no colon and no dash: the legacy create packet mangled a name
            // containing "07:46:24 — Kopf" into a hex dump truncated at the colon.
            string stamp = DateTime.Now.ToString("HHmmss");

            Console.Error.WriteLine($"[TestSkin] folders: textures={textureFolder} bodyparts={bodypartFolder}");
            if (bodypartFolder == LibreMetaverse.UUID.Zero || textureFolder == LibreMetaverse.UUID.Zero)
                return "Inventarordner (Texturen / Körperteile) noch nicht geladen — Inventar einmal öffnen und erneut versuchen";

            var slots = new[]
            {
                (Slot: AvatarTextureIndex.HeadBodypaint, Label: "Kopf", Ascii: "Head"),
                (Slot: AvatarTextureIndex.UpperBodypaint, Label: "Oberkörper", Ascii: "Upper"),
                (Slot: AvatarTextureIndex.LowerBodypaint, Label: "Unterkörper", Ascii: "Lower"),
            };

            var textures = new Dictionary<AvatarTextureIndex, LibreMetaverse.UUID>();
            foreach (var (slot, label, ascii) in slots)
            {
                var bgra = TestSkinTextures.Build(slot);
                var encoder = _bakeEncoder;
                var j2k = await Task.Run(
                    () => encoder.EncodeBake(bgra, TestSkinTextures.Size, TestSkinTextures.Size), ct)
                    .ConfigureAwait(false);
                if (j2k.Length == 0) return $"Testtextur ({label}) konnte nicht kodiert werden";

                var (ok, texItem, assetId, how) = await CreateInventoryItemVerifiedAsync(
                    j2k, $"SLNG Testhaut {stamp} {ascii}", "Generierte Testtextur (FEAT-AVATAR-01)",
                    LibreMetaverse.AssetType.Texture, LibreMetaverse.InventoryType.Texture,
                    wearableType: null, textureFolder, perms, ct).ConfigureAwait(false);

                if (!ok || assetId == LibreMetaverse.UUID.Zero)
                    return $"Upload der {label}-Textur fehlgeschlagen ({how}) — Details im Log";

                textures[slot] = assetId;
                Console.Error.WriteLine($"[TestSkin] {label}: item={texItem} asset={assetId} " +
                    $"({j2k.Length} bytes, via {how})");
            }

            // The wearable itself. No visual params on purpose: a skin's colour params tint every
            // layer it contributes, and the whole point here is that what comes out is exactly what
            // went in.
            var skin = new LibreMetaverse.Assets.AssetBodypart
            {
                Name = $"SLNG Testhaut {stamp}",
                Description = "Bekannte Farben pro Kanal (Kopf grün, Oberkörper blau, Unterkörper rot)",
                WearableType = LibreMetaverse.WearableType.Skin,
                Creator = _client.Self.AgentID,
                Owner = _client.Self.AgentID,
                LastOwner = _client.Self.AgentID,
                Permissions = perms,
            };
            foreach (var kv in textures) skin.Textures[kv.Key] = kv.Value;

            // A skin with no visual params at all is not something a viewer ever writes, and an
            // asset that no viewer would produce is a poor thing to test a grid with. These are the
            // three the skin wearable is defined by -- rainbow, ruddiness, pigment -- at neutral
            // values, so the test colours come through exactly as generated.
            skin.Params[108] = 0f;  // rainbow colour
            skin.Params[110] = 0f;  // red skin (ruddiness)
            skin.Params[111] = 0.5f; // pigment
            skin.Encode();

            var (skinOk, itemId, _, skinHow) = await CreateInventoryItemVerifiedAsync(
                skin.AssetData, skin.Name, skin.Description,
                LibreMetaverse.AssetType.Bodypart, LibreMetaverse.InventoryType.Wearable,
                LibreMetaverse.WearableType.Skin, bodypartFolder, perms, ct).ConfigureAwait(false);

            if (!skinOk || itemId == LibreMetaverse.UUID.Zero)
                return $"Anlegen der Testhaut fehlgeschlagen ({skinHow}) — Details im Log";

            Console.Error.WriteLine($"[TestSkin] created \"{skin.Name}\" ({itemId}) via {skinHow}");

            // Verify rather than trust the response. Measured 2026-09-01: the grid returned real
            // item and asset ids for all four creates, and after a relog not one of them was in the
            // inventory. A create call that reports success and leaves nothing behind is worse than
            // one that fails, because it sends the user looking for something that is not there.
            return $"\"{skin.Name}\" liegt in Körperteile — anziehen, dann neu backen. " +
                   "Kopf grün, Oberkörper blau, Unterkörper rot.";
        }
        catch (OperationCanceledException) { return "abgebrochen"; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestSkin] failed: {ex}");
            return $"Testhaut fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>
    /// Creates one inventory item from asset data, and confirms it exists afterwards.
    ///
    /// <para>Two paths, because the modern one is not dependable. Measured on OSGrid 2026-09-01:
    /// <c>NewFileAgentInventory</c> answered every create with a real <c>new_inventory_item</c> and
    /// <c>new_asset</c> — which is all LibreMetaverse checks before reporting success — and stored
    /// nothing. Four items, none of them in their folder afterwards, none of them there after a
    /// relog. So the capability is tried, the result is verified against the folder, and on failure
    /// the legacy transaction path is used instead: upload the asset, then create the item
    /// referencing the same transaction id.</para>
    ///
    /// <para>The verification is the point. A create that reports success and leaves nothing behind
    /// is worse than one that fails, because it sends the user looking for something that is not
    /// there — which is exactly what happened.</para>
    /// </summary>
    private async Task<(bool Ok, LibreMetaverse.UUID ItemId, LibreMetaverse.UUID AssetId, string How)>
        CreateInventoryItemVerifiedAsync(
            byte[] data, string name, string description,
            LibreMetaverse.AssetType assetType, LibreMetaverse.InventoryType invType,
            LibreMetaverse.WearableType? wearableType, LibreMetaverse.UUID folder,
            LibreMetaverse.Permissions perms, CancellationToken ct)
    {
        // 1. The capability.
        try
        {
            var (ok, status, itemId, assetId) = await _client.Inventory.RequestCreateItemFromAssetAsync(
                data, name, description, assetType, invType, folder, perms, ct).ConfigureAwait(false);

            if (ok && itemId != LibreMetaverse.UUID.Zero
                   && await FolderHoldsAsync(folder, itemId, "verify(cap)", ct).ConfigureAwait(false))
            {
                return (true, itemId, assetId, "capability");
            }

            Console.Error.WriteLine($"[Inventory] NewFileAgentInventory did not store \"{name}\" " +
                $"(ok={ok}, status='{status}') -- falling back to the legacy transaction path");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inventory] NewFileAgentInventory failed for \"{name}\": {ex.Message}");
        }

        // 2. The legacy path: upload the asset under a transaction id, then create the item that
        //    references it. Older, and on this grid the one that is actually wired up.
        try
        {
            var transaction = LibreMetaverse.UUID.Random();
            var assetId = await _client.Assets
                .RequestUploadAsync(assetType, data, storeLocal: false, transaction, ct).ConfigureAwait(false);
            if (assetId == LibreMetaverse.UUID.Zero)
            {
                Console.Error.WriteLine($"[Inventory] legacy upload of \"{name}\" returned no asset id");
                return (false, LibreMetaverse.UUID.Zero, LibreMetaverse.UUID.Zero, "legacy-upload-failed");
            }

            var item = wearableType.HasValue
                ? await _client.Inventory.CreateItemAsync(folder, name, description, assetType, transaction,
                    invType, wearableType.Value, LibreMetaverse.PermissionMask.All, ct).ConfigureAwait(false)
                : await _client.Inventory.CreateItemAsync(folder, name, description, assetType, transaction,
                    invType, LibreMetaverse.PermissionMask.All, ct).ConfigureAwait(false);

            var itemId = item?.UUID ?? LibreMetaverse.UUID.Zero;
            Console.Error.WriteLine($"[Inventory] legacy create \"{name}\": asset={assetId} item=" +
                (item == null ? "NULL (no CreateInventoryItem reply)" : itemId.ToString()));

            if (itemId == LibreMetaverse.UUID.Zero)
                return (false, LibreMetaverse.UUID.Zero, assetId, "legacy-no-item");

            // The sim indexes a new item a moment after acknowledging it, so a single immediate
            // listing can miss one that is really there. Give it one retry before calling it lost --
            // reporting a working create as a failure is its own kind of wrong answer.
            bool present = await FolderHoldsAsync(folder, itemId, "verify(legacy)", ct).ConfigureAwait(false);
            if (!present)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                present = await FolderHoldsAsync(folder, itemId, "verify(legacy, retry)", ct).ConfigureAwait(false);
            }

            return (present, itemId, assetId, present ? "legacy" : "legacy-not-stored");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Inventory] legacy create of \"{name}\" failed: {ex.Message}");
            return (false, LibreMetaverse.UUID.Zero, LibreMetaverse.UUID.Zero, "legacy-threw");
        }
    }

    /// <summary>Re-reads a folder from the grid and reports what is in it — the check that separates
    /// "the create call answered with an id" from "the item exists". Pass <c>UUID.Zero</c> to just
    /// list the folder.</summary>
    private async Task<bool> FolderHoldsAsync(
        LibreMetaverse.UUID folder, LibreMetaverse.UUID item, string label, CancellationToken ct)
    {
        try
        {
            var listing = await _client.Inventory.FolderContentsAsync(
                folder, _client.Self.AgentID, fetchFolders: false, fetchItems: true,
                LibreMetaverse.InventorySortOrder.ByName, ct).ConfigureAwait(false);

            bool present = item != LibreMetaverse.UUID.Zero && listing?.Any(e => e.UUID == item) == true;
            var names = listing?.Take(6).Select(e => e.Name) ?? Enumerable.Empty<string>();

            Console.Error.WriteLine($"[TestSkin] {label}: {listing?.Count ?? -1} item(s)" +
                (item != LibreMetaverse.UUID.Zero ? $", the new one is {(present ? "PRESENT" : "MISSING")}" : "") +
                (names.Any() ? "  [" + string.Join(" | ", names) + "]" : ""));
            return present;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestSkin] {label}: listing failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>FEAT-AVATAR-01's manual bake path: "Avatar neu backen" and "Testmuster backen".
    /// Composites locally, uploads the result as textures, and sends a bake-carrying
    /// <c>AgentSetAppearance</c> -- the OpenSim (XBakes) path, needed only because that server does
    /// not composite for the client.
    ///
    /// <b>Refuses on a region with real Second Life server-side baking</b> (see
    /// <see cref="RegionHasServerSideBaking"/>). SL composites bakes itself from an
    /// <c>UpdateAvatarAppearance</c> cap POST of <c>{ cof_version }</c> -- no visual params, no
    /// shape, no uploaded textures. Running this path there anyway would upload textures the
    /// server already has no use for and hand-send a raw <c>AgentSetAppearance</c> alongside SL's
    /// own pipeline: an unrequested protocol departure (TPV Policy §1.a) and the most likely way to
    /// leave the avatar looking wrong to everyone else in the room. The wearable-edit path already
    /// makes this distinction correctly (<c>WearWearableAsync</c>'s SSB branch); this is the same
    /// rule applied to the two menu commands that skip that path entirely.</summary>
    public async Task<string> BakeAvatarAsync(bool testPattern = false, CancellationToken ct = default)
    {
        if (!_client.Network.Connected)
        {
            Console.Error.WriteLine("[Bake] skipped: not connected");
            return "nicht verbunden";
        }

        if (RegionHasServerSideBaking())
        {
            Console.Error.WriteLine("[Bake] skipped: this region bakes server-side (Second Life) -- " +
                "the client-side composite path is for OpenSim (XBakes) only");
            return "Dieses Grid backt serverseitig — der lokale Bake ist nicht nötig und wird übersprungen.";
        }

        // The bake pipeline is proven, so its running commentary is noise on every wardrobe
        // change. Everything that diagnosed it stays one env var away; what remains by default is
        // the outcome plus anything that went wrong.
        bool verbose = Environment.GetEnvironmentVariable("SLNG_BAKE_VERBOSE") == "1";
        testPattern |= Environment.GetEnvironmentVariable("SLNG_BAKE_TESTPATTERN") == "1";

        try
        {
            // 1. Worn wearables. LLUDP first -- the COF route measured empty on OSGrid.
            await RequestWornWearablesViaLludpAsync(ct).ConfigureAwait(false);
            var legacy = _client.Appearance.GetWearables().ToList();

            // The Current Outfit Folder is what actually defines the worn set. The legacy
            // AgentWearablesReply is whatever the region last had written to it, and that is not the
            // same thing: measured 2026-08-31, the same code resolved 9 wearables before a Firestorm
            // login and 5 after it, because Firestorm rewrote the region's list on login. The four
            // that vanished were tattoo layers -- including the skin the avatar is actually wearing.
            // Baking from the region's copy would have replaced the user's face with a different one.
            var worn = await CollectWornWearablesForBakeAsync(verbose, ct).ConfigureAwait(false);
            if (verbose)
                Console.Error.WriteLine($"[Bake] worn wearables: COF {worn.Count}, region's legacy list {legacy.Count}");

            if (worn.Count == 0)
            {
                Console.Error.WriteLine("[Bake] COF empty -- falling back to the region's list");
                worn = legacy;
            }
            else if (worn.Count < legacy.Count)
            {
                // The COF is authoritative, but fewer entries than the region knows about usually
                // means links are still unresolved rather than genuinely unworn -- and baking from a
                // short set drops layers off the avatar. Say so instead of quietly proceeding.
                Console.Error.WriteLine("[Bake] WARNING: the COF resolved fewer wearables than the region lists; " +
                    "some links may not have loaded yet");
            }
            if (worn.Count == 0) { Console.Error.WriteLine("[Bake] nothing to bake from"); return "nichts zum Backen gefunden"; }

            // 2. Decode each wearable's asset -- DecodeWearableParams reads wearable.Asset.
            int decoded = 0;
            foreach (var w in worn)
            {
                if (w.Asset != null) { decoded++; continue; }
                try
                {
                    var asset = await _client.Assets
                        .RequestAssetAsync(w.AssetID, w.AssetType, priority: true, ct).ConfigureAwait(false);
                    if (asset is LibreMetaverse.Assets.AssetWearable aw && aw.Decode()) { w.Asset = aw; decoded++; }
                    else Console.Error.WriteLine($"[Bake]   {w.WearableType}: asset {w.AssetID} did not fetch/decode");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"[Bake]   {w.WearableType}: {ex.Message}"); }
            }
            if (decoded < worn.Count)
                Console.Error.WriteLine($"[Bake] WARNING: only {decoded}/{worn.Count} wearable assets decoded -- layers will be missing");

            // 2b. What each worn wearable actually declares. The Head bake came back with no skin
            //     texture at all -- its only input was Hair:32x32 -- so it composited the built-in
            //     Linden head instead of the avatar's own face. DecodeWearableParams is a straight
            //     copy of wearable.Asset.Textures with one exception: an entry pointing at
            //     DEFAULT_AVATAR_TEXTURE is mapped to Zero and disappears. This says which of the
            //     two it is -- a skin that declares no head texture, or one whose head texture is
            //     the default and is being dropped on purpose.
            foreach (var w in verbose ? worn.Where(w => w.Asset != null) : Enumerable.Empty<AppearanceManager.WearableData>())
            {
                var declared = w.Asset!.Textures;
                Console.Error.WriteLine($"[Bake]   worn {w.WearableType,-10} " +
                    (declared.Count == 0
                        ? "declares NO textures"
                        : string.Join("  ", declared.Select(e =>
                            $"{e.Key}=" + (e.Value == AppearanceManager.DEFAULT_AVATAR_TEXTURE ? "DEFAULT"
                                : e.Value == LibreMetaverse.UUID.Zero ? "ZERO"
                                : e.Value.ToString()[..8])))));
            }

            // 3. One layer per WEARABLE per texture slot -- not one per slot.
            //
            //    AppearanceManager keeps a single TextureData[] indexed by AvatarTextureIndex and
            //    calls DecodeWearableParams once per wearable into it, so each wearable overwrites
            //    the previous one's slot. Measured 2026-08-31 with five worn Tattoo layers: four
            //    distinct HeadTattoo textures and two UpperTattoo textures were silently discarded,
            //    and because the last tattoo declares HeadTattoo=DEFAULT (which maps to Zero) the
            //    head slot ended up empty. That is why the Head bake had no skin at all and
            //    composited the built-in Linden head.
            //
            //    The real viewer keeps a local texture per (slot, wearable) -- LLLocalTextureObject
            //    -- and Baker.Bake is already built for it: AddTexture appends to a flat list, the
            //    layer loop draws each in turn, and tattooTextures is a List. It simply never
            //    receives more than one. So give each wearable its own scratch array, which keeps
            //    LibreMetaverse's own colour and alpha-mask logic, and collect the results in wear
            //    order (bottom layer first, as SL stacks them).
            //    Order matters as much as membership: where two layers of one type overlap, the
            //    topmost wins, and two of the worn tattoos are fully opaque head skins. See
            //    WearableLayerOrder -- the position lives in the COF link's description, not in the
            //    order LibreMetaverse returns.
            var ordered = OrderWearablesAsTheViewerDoes(worn.Where(w => w.Asset != null).ToList(), verbose);

            var layers = new List<AppearanceManager.TextureData>();
            foreach (var w in ordered)
            {
                var scratch = new AppearanceManager.TextureData[(int)AvatarTextureIndex.NumberOfEntries];
                for (int i = 0; i < scratch.Length; i++) scratch[i] = new AppearanceManager.TextureData();
                AppearanceManager.DecodeWearableParams(w, ref scratch);

                for (int i = 0; i < scratch.Length; i++)
                {
                    if (scratch[i].TextureID == LibreMetaverse.UUID.Zero) continue;
                    scratch[i].TextureIndex = (AvatarTextureIndex)i;
                    layers.Add(scratch[i]);
                }
            }
            if (verbose)
                Console.Error.WriteLine($"[Bake] layers from {worn.Count(w => w.Asset != null)} wearables: {layers.Count}");

            // 4. Fetch every referenced texture.
            var wanted = layers.Select(t => t.TextureID).Distinct().ToList();
            if (verbose)
                Console.Error.WriteLine($"[Bake] textures referenced by the worn set: {wanted.Count}");
            int got = 0, decodedTex = 0;
            foreach (var id in wanted)
            {
                try
                {
                    var tex = await _client.Appearance.TextureProvider.RequestTextureAsync(id, ct).ConfigureAwait(false);
                    if (tex == null) { Console.Error.WriteLine($"[Bake]   texture {id} -> null"); continue; }
                    got++;

                    // Report the decode instead of swallowing it. AssetTexture.Decode runs
                    // J2kImage.DecodeToImage<SKBitmap>, which throws outright when CoreJ2K's Skia
                    // image creator is not registered -- and a bake composited from undecoded
                    // textures comes out blank, which looks like "the baker did nothing".
                    bool ok;
                    try { ok = tex.Decode(); }
                    catch (Exception dex) { ok = false; Console.Error.WriteLine($"[Bake]   texture {id} decode threw: {dex.Message}"); }

                    if (ok && tex.Image != null)
                    {
                        decodedTex++;
                        if (verbose)
                            Console.Error.WriteLine($"[Bake]   texture {id.ToString()[..8]} -> {tex.Image.Width}x{tex.Image.Height} " +
                                $"channels={tex.Image.Channels} ({tex.AssetData?.Length ?? 0} bytes asset)");
                    }
                    else
                    {
                        Console.Error.WriteLine($"[Bake]   texture {id.ToString()[..8]} -> decode FAILED " +
                            $"(ok={ok}, image={(tex.Image == null ? "null" : "set")}) -- this channel will bake blank");
                    }

                    // Every layer referencing this id -- several wearables can share one texture.
                    foreach (var t in layers) if (t.TextureID == id) t.Texture = tex;

                    // Write the input out too. The Head composite came back holding what looks like
                    // two faces, one of them inverted, while UpperBody composited cleanly -- so the
                    // question is whether a single input already looks like that or whether the
                    // layering produces it, and only the inputs themselves answer it.
                    if (verbose)
                    {
                        var slot = layers.FirstOrDefault(t => t.TextureID == id)?.TextureIndex;
                        DumpPreview($"in_{slot}_{id.ToString()[..8]}", tex.Image);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"[Bake]   texture {id} -> {ex.Message}"); }
            }
            if (verbose)
                Console.Error.WriteLine($"[Bake] textures downloaded: {got}/{wanted.Count}, decoded: {decodedTex}/{got}");
            else if (decodedTex < wanted.Count)
                Console.Error.WriteLine($"[Bake] WARNING: only {decodedTex}/{wanted.Count} textures decoded -- the bake will be incomplete");

            // 4b. Test pattern. The generated skin proved impossible to see: as a Skin it is the
            //     bottom of the stack and two worn tattoo layers are fully opaque, and getting an
            //     item to persist and stay worn on this grid cost two rounds without ever answering
            //     the question it was created for. Feeding the same known-answer textures straight
            //     into the bake as the topmost layer of each channel answers it directly -- no
            //     inventory item, no COF link, no layer ordering, nothing that can quietly cover it.
            if (testPattern)
            {
                foreach (var slot in new[]
                {
                    AvatarTextureIndex.HeadBodypaint,
                    AvatarTextureIndex.UpperBodypaint,
                    AvatarTextureIndex.LowerBodypaint,
                })
                {
                    layers.Add(new AppearanceManager.TextureData
                    {
                        TextureIndex = slot,
                        TextureID = LibreMetaverse.UUID.Random(),
                        Texture = new LibreMetaverse.Assets.AssetTexture(
                            ToManagedImage(TestSkinTextures.Build(slot), TestSkinTextures.Size)),
                    });
                }
                Console.Error.WriteLine("[Bake] TEST PATTERN on top of every channel " +
                    "(head green, upper body blue, lower body red)");
            }

            // 5. Bake each channel, and -- when asked -- upload it. Uploading is deliberately
            //    separated from sending: RequestUploadBakedTextureAsync goes through the
            //    UploadBakedTexture capability, which stores an asset and returns its id. It costs
            //    nothing, creates no inventory item and changes nothing about the avatar. What
            //    changes an avatar is the AgentSetAppearance that carries the new ids, and that
            //    still does not happen here. This is the step that proves the grid accepts a bake
            //    of this size before anything irreversible is built on top of it.
            // Baking applies by default now: the pipeline was verified against the reference
            // viewer's own bakes for this avatar (head 2.6/255) and confirmed in-world. SLNG_BAKE_DRY
            // still holds everything back, which is what to reach for if an outfit ever bakes wrong
            // -- it composites and previews without touching the account.
            bool send = Environment.GetEnvironmentVariable("SLNG_BAKE_DRY") != "1";
            bool upload = send;
            var uploaded = new Dictionary<int, LibreMetaverse.UUID>();
            bool sent = false;
            if (!send)
                Console.Error.WriteLine("[Bake] SLNG_BAKE_DRY=1 -- composited only; nothing uploaded, nothing sent");

            foreach (var bakeType in new[] { BakeType.Head, BakeType.UpperBody, BakeType.LowerBody, BakeType.Eyes, BakeType.Hair })
            {
                var indices = AppearanceManager.BakeTypeToTextures(bakeType);
                var oven = new LibreMetaverse.Imaging.Baker(bakeType);
                int fed = 0, usable = 0;
                var detail = new List<string>();
                // Every layer belonging to this channel, in wear order -- several may share a slot
                // (five tattoos all contribute a HeadTattoo), which is exactly what the old
                // one-slot-one-texture feed threw away.
                foreach (var t in layers.Where(l => indices.Contains(l.TextureIndex)))
                {
                    oven.AddTexture(t);
                    if (t.Texture == null) continue;
                    fed++;
                    // Decoded image present is what the baker can actually composite; a fetched
                    // but undecoded texture contributes nothing and is the difference between a
                    // real bake and a 507-byte blank.
                    if (t.Texture.Image != null) { usable++; detail.Add($"{t.TextureIndex}:{t.Texture.Image.Width}x{t.Texture.Image.Height}"); }
                    else detail.Add($"{t.TextureIndex}:UNDECODED");
                }

                await Task.Run(() => oven.Bake(), ct).ConfigureAwait(false);
                int bytes = oven.BakedTexture?.AssetData?.Length ?? 0;

                // Separate "the compositing wrote nothing" from "it composited and the ENCODER
                // produced nothing". Baker.Bake ends in AssetTexture.Encode ->
                // CompleteConfigurationPresets.Streaming.Encode(Image.ExportBitmap()), so a filled
                // ManagedImage with a tiny AssetData means the encode is at fault, while a uniform
                // image means DrawLayer never put anything in. Distinct red values is the cheapest
                // test: a blank image has exactly one.
                string composed = "image=null";
                var img = oven.BakedTexture?.Image;
                if (img?.Red != null)
                {
                    var seen = new HashSet<byte>();
                    for (int i = 0; i < img.Red.Length && seen.Count <= 8; i++) seen.Add(img.Red[i]);
                    composed = $"image={img.Width}x{img.Height} distinctRed={(seen.Count > 8 ? ">8" : seen.Count.ToString())}";
                }

                // Re-encode what the Baker composited. LibreMetaverse's own Encode is hardcoded to a
                // CoreJ2K preset that is broken in the pinned version -- see IBakeTextureEncoder --
                // so the bytes above are meaningless no matter how good the image is. This is the
                // number that says whether a bake could actually be uploaded.
                int reBytes = 0;
                byte[] slngBake = Array.Empty<byte>();
                if (img != null && _bakeEncoder != null)
                {
                    var bgra = ToBgra(img);
                    var encoder = _bakeEncoder;
                    int w = img.Width, h = img.Height;
                    slngBake = await Task.Run(() => encoder.EncodeBake(bgra, w, h), ct).ConfigureAwait(false);
                    reBytes = slngBake.Length;
                }

                // Write the composite out so it can actually be looked at. Only on request: these
                // are megabyte PNGs and a wardrobe change should not spend that every time.
                if (verbose) DumpPreview(bakeType.ToString(), img);

                string uploadNote = string.Empty;
                if (upload && reBytes > 0)
                {
                    try
                    {
                        var id = await _client.Assets.RequestUploadBakedTextureAsync(slngBake, ct).ConfigureAwait(false);
                        if (id != LibreMetaverse.UUID.Zero)
                        {
                            // Read it back. An upload capability answering with an asset id is not
                            // the same as the asset existing -- NewFileAgentInventory did exactly
                            // that for four inventory items on this grid -- and an appearance
                            // pointing at bake ids that resolve to nothing renders the avatar
                            // untextured, which is what SLNG shows while Firestorm, using its own
                            // bake, shows the outfit correctly.
                            var readBack = await _client.Appearance.TextureProvider
                                .RequestTextureAsync(id, ct).ConfigureAwait(false);
                            bool readable = false;
                            try { readable = readBack != null && readBack.Decode() && readBack.Image != null; }
                            catch { readable = false; }

                            uploaded[(int)AppearanceManager.BakeTypeToAgentTextureIndex(bakeType)] = id;
                            uploadNote = readable
                                ? $"  uploaded={id.ToString()[..8]} (verified {readBack!.Image!.Width}x{readBack.Image.Height})"
                                : $"  uploaded={id.ToString()[..8]} but READ-BACK FAILED -- the grid did not keep it";

                            if (!readable)
                                Console.Error.WriteLine($"[Bake] WARNING: {bakeType} bake {id} cannot be fetched back; " +
                                    "the avatar will render untextured for anyone using it");
                        }
                        else uploadNote = "  upload REJECTED (grid returned no asset id)";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { uploadNote = $"  upload FAILED: {ex.Message}"; }
                }

                if (verbose)
                {
                    Console.Error.WriteLine($"[Bake] {bakeType,-10} inputs={indices.Count} withTexture={fed} usable={usable} " +
                        $"-> lmv {(bytes > 0 ? bytes + "B" : "NOTHING")} / slng {(reBytes > 0 ? reBytes + "B" : "NOTHING")}  {composed}" +
                        (detail.Count > 0 ? "  [" + string.Join(" ", detail) + "]" : "") + uploadNote);
                }
                else if (reBytes == 0)
                {
                    Console.Error.WriteLine($"[Bake] WARNING: {bakeType} produced no texture{uploadNote}");
                }
            }

            if (upload)
            {
                // A partial set is the dangerous case: an appearance built from it would carry empty
                // slots and strip the avatar. Report completeness explicitly so the next step can
                // refuse rather than discover it on a live avatar.
                var have = AgentAppearanceParams.EssentialBakeSlots.Count(s => uploaded.ContainsKey(s));
                if (verbose || have < AgentAppearanceParams.EssentialBakeSlots.Length)
                    Console.Error.WriteLine($"[Bake] uploaded {have}/{AgentAppearanceParams.EssentialBakeSlots.Length} " +
                    $"essential slots: {string.Join("  ", AgentAppearanceParams.EssentialBakeSlots.Select(s => $"{s}=" + (uploaded.TryGetValue(s, out var u) ? u.ToString()[..8] : "MISSING")))}");

                if (send)
                {
                    // The wearables in layer order, each as its decoded paramId -> weight map. Same
                    // list the bake was composited from, so shape and textures describe one outfit.
                    var wearableParams = ordered
                        .Where(w => w.Asset != null)
                        .Select(w => (IReadOnlyDictionary<int, float>)w.Asset!.Params)
                        .ToList();

                    sent = SendAppearanceFromOwnBake(uploaded, wearableParams);
                }

            }

            // 6. The reference: the bakes a working viewer produced for this same avatar, which the
            //    simulator still holds. Comparing our composite against those is the only check that
            //    says "right" rather than "plausible", and it costs nothing since they are ordinary
            //    texture assets. Skipped once we have sent, because the relay now holds OUR ids and
            //    fetching them would only compare the bake against itself.
            if (send || !verbose)
            {
                // Nothing to compare against once we have sent -- the relay now holds our own ids --
                // and five texture fetches are not worth spending on an unasked-for comparison.
            }
            else
            {
                foreach (var (slot, name) in new[] { (8, "Head"), (9, "UpperBody"), (10, "LowerBody"), (11, "Eyes"), (20, "Hair") })
                {
                    if (!_lastSelfRelayBakes.TryGetValue(slot, out var id) || id == Guid.Empty) continue;
                    try
                    {
                        var tex = await _client.Appearance.TextureProvider
                            .RequestTextureAsync(new LibreMetaverse.UUID(id), ct).ConfigureAwait(false);
                        if (tex == null) continue;
                        try { if (!tex.Decode()) continue; } catch { continue; }
                        DumpPreview($"ref_{name}_{id.ToString()[..8]}", tex.Image);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Console.Error.WriteLine($"[Bake]   reference {name}: {ex.Message}"); }
                }
            }

            return send
                ? (sent ? (testPattern
                        ? "Testmuster gebacken und gesendet — Kopf grün, Oberkörper blau, Unterkörper rot."
                        : $"Aussehen neu gebacken ({worn.Count} Kleidungsstücke).")
                        : "Bake fertig, aber NICHT gesendet — Grund steht im Log ([Appearance]-Zeile).")
                : "Bake fertig — nichts gesendet (SLNG_BAKE_DRY=1).";
        }
        catch (OperationCanceledException) { return "abgebrochen"; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bake] failed: {ex.Message}");
            return $"Bake fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>"Avatar neu backen" -- forces a fresh appearance composite. FEAT-SL-01 audit
    /// finding: this was a COMPLETE NO-OP on every grid including SL, because the 2026-08-31 hard
    /// stop below refuses unconditionally. That stop is correct for OpenSim / legacy baking --
    /// <c>RequestSetAppearance</c> is NOT gated by <c>SendAppearance = false</c> on that path, so
    /// calling it sends whatever <c>MakeAppearancePacket</c> produces, which was the scrambled/
    /// all-zero send that broke this avatar repeatedly (see the stop's own comment). It is WRONG
    /// for a server-side-baking region: there, <c>RequestSetAppearance</c> never reaches
    /// <c>MakeAppearancePacket</c> at all -- verified against the pinned package's own branch
    /// (<c>AppearanceManager.RequestSetAppearanceAsync</c>: <c>useClientSideBaking = false</c> on
    /// SSB skips straight to <c>UpdateAvatarAppearanceAsync</c>, a <c>{ cof_version }</c> capability
    /// POST, no visual params, no textures) -- none of the Aug-31 concerns apply, and refusing it
    /// left "Avatar neu backen" silently doing nothing while the chat message claimed a bake had
    /// happened. On SL this is also the only way SLNG could ever ask the sim to re-push a self
    /// avatar's <c>AvatarAppearance</c> outside of an actual wearable edit -- e.g. after a login
    /// whose initial appearance never arrived or was dropped.</summary>
    public void RebakeAvatar()
    {
        if (!_client.Network.Connected)
        {
            Console.Error.WriteLine("[Appearance] rebake skipped: not connected");
            return;
        }

        if (RegionHasServerSideBaking())
        {
            Console.Error.WriteLine("[Appearance] rebake requested on a server-side-baking region -- " +
                "nudging the region to re-composite (UpdateAvatarAppearance cap)");
            _ = RequestServerSideRebakeAsync();
            return;
        }

        // Report the bake state first -- it is the one number that says whether a rebake can work
        // at all (all-ZERO means LibreMetaverse composited nothing), and it costs nothing.
        try
        {
            var packet = _client.Appearance.MakeAppearancePacket();
            var te = packet.ObjectData.TextureEntry;
            var entry = te is { Length: > 1 } ? new Primitive.TextureEntry(te, 0, te.Length) : null;

            var report = new List<string>();
            foreach (var idx in new[] { 8, 9, 10, 11, 20 }) // head, upper, lower, eyes, hair
            {
                var face = entry?.FaceTextures is { } faces && idx < faces.Length ? faces[idx] : null;
                var id = face?.TextureID ?? LibreMetaverse.UUID.Zero;
                report.Add($"{idx}=" + (id == LibreMetaverse.UUID.Zero ? "ZERO"
                    : id == AppearanceManager.DEFAULT_AVATAR_TEXTURE ? "DEFAULT"
                    : id.ToString()[..8]));
            }

            int wearables = _client.Appearance.GetWearables().Count();
            int decoded = _client.Appearance.GetWearables().Count(w => w.Asset != null);
            var relay = string.Join(" ", _lastSelfRelayBakes
                .Where(kv => kv.Key is 8 or 9 or 10 or 11 or 20)
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value.ToString("N")[..8]}"));

            Console.Error.WriteLine(
                $"[Appearance] rebake requested -- state before:\n" +
                $"  LibreMetaverse bake slots : {string.Join("  ", report)}\n" +
                $"  worn wearables            : {wearables} ({decoded} decoded)\n" +
                $"  simulator's last relay    : {(relay.Length == 0 ? "(none seen)" : relay)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] bake-state report failed: {ex.Message}");
        }

        // HARD STOP 2026-08-31. Every appearance send is disabled while SendAppearance is off.
        // RequestSetAppearance is NOT gated by that flag -- it bakes and sends whatever
        // MakeAppearancePacket produces -- so a wearable edit or a rebake still reached the grid
        // even with the pipeline nominally disabled, from a cold state where LibreMetaverse has no
        // decoded wearables and no bakes. That combination is what has broken this avatar
        // repeatedly. Nothing here sends until the whole path has been proven on a throwaway alt.
        // This stop covers LibreMetaverse's OWN send path only. SLNG composites, uploads and sends
        // its own appearance (SendAppearanceFromOwnBake) -- so this is no longer a refusal the user
        // needs to hear about, and reporting it as a discarded change was simply wrong.
        Console.Error.WriteLine("[Appearance] LibreMetaverse's own send stays disabled; SLNG bakes and sends its own");
        return;
    }

    /// <summary>The SSB half of <see cref="RebakeAvatar"/>. Nudges the region to re-composite the
    /// avatar by POSTing <c>{ "cof_version": N }</c> to the <c>UpdateAvatarAppearance</c> cap --
    /// exactly what <c>LLAppearanceMgr::serverAppearanceUpdateCoro</c> does (<c>llappearancemgr.cpp</c>).
    ///
    /// <para>Deliberately NOT <c>AppearanceManager.RequestSetAppearance</c>: with
    /// <c>SendAppearance</c> off, LibreMetaverse's cache is empty, so that call rebuilds the worn
    /// set from a fresh COF fetch inside itself (<c>RezMultipleAttachmentsFromInv</c>) and, on a
    /// rate-limited grid, silently drops a worn attachment link whose target doesn't resolve in the
    /// window -- live on Agni 2026-09-03, worn hair/shoes gone after a rebake (BUG-AVATAR-03). The
    /// cap POST here is a pure nudge: the sim composites from its OWN copy of the COF at
    /// <c>cof_version</c> and pushes a fresh <c>AvatarAppearance</c> back, touching nothing local.</para></summary>
    /// <summary>Public entry for "Avatar neu backen" / Ctrl+Alt+R on a server-side-baking region:
    /// the pure <c>{ cof_version }</c> cap POST, returning a user-facing status line so Boot can
    /// show the real outcome in chat instead of the meaningless OpenSim <c>BakeAvatarAsync</c>
    /// reply (BUG-AVATAR-01). Caller should first check <see cref="RegionHasServerSideBaking"/>.</summary>
    public Task<string> RequestServerSideRebakeAsync(CancellationToken ct = default)
        => SendServerAppearanceUpdateAsync(ct);

    /// <summary>FEAT-AVATAR-03: sends the local hover-height offset to the sim, fire-and-forget --
    /// same "no user-facing failure path" contract as the rest of the self-avatar send surface here,
    /// because a slider drag has nowhere to show an error and shouldn't block the caller.
    ///
    /// <para>Wire mechanism confirmed against <c>scratch/slviewer</c>'s <c>LLVOAvatarSelf::
    /// sendHoverHeight</c> (llvoavatarself.cpp) and the pinned LibreMetaverse 3.1.3 package by
    /// reflection: an HTTP CAPS POST to the <b>AgentPreferences</b> capability -- NOT the
    /// "AvatarHoverHeight" cap this task's own spec assumed, and NOT an <c>AgentUpdate</c> UDP
    /// field (there is no hover field on that message; the only wire occurrence of "HoverHeight" in
    /// the message template is the unrelated inbound <c>AvatarAppearance.AppearanceHover</c> block).
    /// LibreMetaverse 3.1.3 already implements the send as <c>AgentManager.SetHoverHeightAsync</c>;
    /// no raw LLSD needed here.</para>
    ///
    /// <para>Deliberately NOT gated on a simulator-features check: <c>Simulator.Features</c> (the
    /// property the real viewer and a newer LibreMetaverse checkout use to gate the UI on
    /// <c>AvatarHoverHeightEnabled</c>) does not exist on the pinned 3.1.3 package -- verified by
    /// reflection, not assumed. <c>SetHoverHeightAsync</c> already no-ops silently (no exception)
    /// when the region's <c>AgentPreferences</c> cap is absent (OpenSim: expected to be, per
    /// protocol-re's source read), which is exactly "degrade cleanly" for a region without support.</para>
    ///
    /// <para>Clamped defensively to the real viewer's actual range (<c>MIN_HOVER_Z</c>/
    /// <c>MAX_HOVER_Z</c>, llvoavatar.cpp) rather than this feature's own ±2.0 m UI range, in case a
    /// future caller doesn't go through <c>AvatarHoverSettings</c>' own clamp.</para></summary>
    public void SetHoverHeight(float metres)
    {
        float clamped = Math.Clamp(metres, -3.0f, 3.0f);
        _ = SetHoverHeightAsyncInternal(clamped);
    }

    private async Task SetHoverHeightAsyncInternal(float metres)
    {
        try
        {
            await _client.Self.SetHoverHeightAsync(metres).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Avatar] SetHoverHeight({metres:0.00}) failed: {ex.Message}");
        }
    }

    /// <summary>Builds the <c>UpdateAvatarAppearance</c> POST body -- pure + internal so a test can
    /// pin the shape (<c>{ "cof_version": &lt;int&gt; }</c>, mirroring the reference viewer's
    /// <c>postData["cof_version"] = cofVersion</c>).</summary>
    internal static OSDMap BuildServerAppearanceUpdate(int cofVersion) =>
        new() { ["cof_version"] = OSD.FromInteger(cofVersion) };

    /// <summary>Current Outfit Folder version from LibreMetaverse's local store -- the value the
    /// SSB cap POST is keyed on. -1 (<c>InventoryFolder.VERSION_UNKNOWN</c>) when the COF folder
    /// isn't in the store yet. AIS write-backs (CreateLink / RemoveItem) update this in place, so
    /// it tracks a wearable edit without needing a re-fetch.</summary>
    private int GetCofVersion()
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero) return -1;
        return (_client.Inventory.Store?.GetNodeOrDefault(cofUuid)?.Data
            as LibreMetaverse.InventoryFolder)?.Version ?? -1;
    }

    /// <summary>POSTs <c>{ cof_version }</c> to the region's <c>UpdateAvatarAppearance</c> cap and,
    /// on a version-mismatch reply (<c>{ success:false, expected:M }</c>), retries with the
    /// server's expected version (up to 3x, 500 ms apart) -- the reference viewer's
    /// <c>serverAppearanceUpdateCoro</c> retry loop, minus the UDP texture re-request.
    ///
    /// <para>Returns a short user-facing status line (German) so the caller can show what actually
    /// happened -- BUG-AVATAR-01: "Avatar neu backen" on SSB used to print the OpenSim path's
    /// "backt serverseitig -- übersprungen" reply, which contradicted the "wird neu gebacken"
    /// message and left no sign the cap POST had landed. Fire-and-forget callers just ignore it.</para></summary>
    private async Task<string> SendServerAppearanceUpdateAsync(CancellationToken ct = default)
    {
        var uri = _client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAvatarAppearance");
        if (uri == null)
        {
            Console.Error.WriteLine("[Appearance] no UpdateAvatarAppearance cap on this region -- cannot nudge a rebake");
            return "Diese Region bietet keine UpdateAvatarAppearance-Capability — Server-Rebake nicht möglich.";
        }

        int cofVersion = GetCofVersion();
        if (cofVersion < 0)
        {
            Console.Error.WriteLine("[Appearance] COF version unknown -- skipping the rebake nudge");
            return "Outfit-Version noch nicht bekannt — kurz warten und erneut versuchen.";
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var (res, data) = await _client.HttpCapsClient
                    .PostAsync(uri, OSDFormat.Xml, BuildServerAppearanceUpdate(cofVersion), ct).ConfigureAwait(false);
                int status = (int)(res?.StatusCode ?? 0);
                var reply = data is { Length: > 0 } ? OSDParser.Deserialize(data) as OSDMap : null;

                if (reply != null && reply["success"].AsBoolean())
                {
                    Console.Error.WriteLine($"[Appearance] server appearance update accepted (cof_version={cofVersion}, HTTP {status})");
                    return $"Server-Rebake angenommen (cof_version {cofVersion}). Der Sim schickt ein frisches Aussehen zurück.";
                }

                int expected = reply != null && reply.ContainsKey("expected") ? reply["expected"].AsInteger() : -1;
                if (expected > cofVersion)
                {
                    Console.Error.WriteLine($"[Appearance] server appearance update: sent cof_version={cofVersion}, server expected {expected} -- retrying");
                    cofVersion = expected;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                string err = reply != null && reply.ContainsKey("error") ? reply["error"].AsString() : $"HTTP {status}";
                Console.Error.WriteLine($"[Appearance] server appearance update rejected (cof_version={cofVersion}): {err}");
                return $"Server-Rebake abgelehnt (cof_version {cofVersion}): {err}";
            }
            catch (OperationCanceledException) { return "Server-Rebake abgebrochen."; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Appearance] server appearance update failed: {ex.Message}");
                return $"Server-Rebake fehlgeschlagen: {ex.Message}";
            }
        }
        Console.Error.WriteLine($"[Appearance] server appearance update: gave up after 3 cof_version retries (last {cofVersion})");
        return $"Server-Rebake: Versions-Konflikt, nach 3 Versuchen aufgegeben (zuletzt cof_version {cofVersion}).";
    }

    /// <summary>Set once the simulator has told us our OWN baked-texture ids. Watched by
    /// <see cref="ArmSelfBakeWatchdog"/>.</summary>
    private volatile bool _selfAppearanceWithBakesSeen;

    private int _selfBakeWatchdogArmed;

    /// <summary>
    /// One-shot safety net for "the avatar logged in with no bake at all".
    ///
    /// <para>The simulator does not reliably send the local agent its own <c>AvatarAppearance</c>
    /// after login. When it doesn't, every bake channel stays <c>Guid.Empty</c>, and the result is
    /// not a subtle one: the system hair mesh renders as its full uncut helmet, the head renders
    /// blank, and worn alpha layers do not cut the system body (live, Agni 2026-09-03 --
    /// *"das backen des avatars geht nicht mehr"*). Other avatars in the same scene bake normally,
    /// because their appearance arrives with their ObjectUpdate; only our own is missing.</para>
    ///
    /// <para>The remedy is the one the reference viewer already uses on every login,
    /// <c>LLAppearanceMgr::serverAppearanceUpdateCoro</c>: POST <c>{ cof_version }</c> to the
    /// <c>UpdateAvatarAppearance</c> cap and let the sim composite from its own copy of the COF.
    /// <see cref="SendServerAppearanceUpdateAsync"/> is that POST and nothing else -- explicitly
    /// NOT <c>RequestSetAppearance</c>, which reconciles the worn set from a COF fetch inside
    /// itself and drops worn attachments on a rate-limited grid (BUG-AVATAR-03).</para>
    ///
    /// <para>Two attempts, then it stops. The first waits 25 s, long enough for the COF to reach
    /// LibreMetaverse's store -- the POST refuses to send with an unknown <c>cof_version</c>, so
    /// firing earlier would just waste the attempt. Armed once per session; a fresh login is a
    /// fresh process.</para>
    /// </summary>
    /// <summary>
    /// Reads the local agent's bake ids out of its own <c>ObjectUpdate</c> TextureEntry, for when
    /// the simulator never sent us an <c>AvatarAppearance</c>.
    ///
    /// <para>Why this is needed at all: <c>AvatarComponent.BakedTextures</c> has exactly ONE source,
    /// the <c>AvatarAppearance</c> packet (<c>WorldSimulation.ApplyAvatarAppearance</c>). Miss that
    /// packet and there is no second chance — the head renders blank and the system hair as an uncut
    /// helmet until the next login. But the bake ids are also carried in the avatar's TextureEntry,
    /// which LibreMetaverse parses out of the ordinary ObjectUpdate into
    /// <c>Avatar.Textures.FaceTextures</c>, in the same per-slot layout
    /// <see cref="OnAvatarAppearance"/> already reads. So the information is usually sitting right
    /// there, unused.</para>
    ///
    /// <para>This is the better first move than the cap nudge, and the reference viewer says why.
    /// <c>LLAppearanceMgr::serverAppearanceUpdateCoro</c> (llappearancemgr.cpp:3899-3925) refuses to
    /// send at all when <c>cofVersion &lt;= mLastUpdateReceivedCOFVersion</c>: <b>the server will not
    /// re-composite for a COF version it has already served.</b> Measured live 2026-09-04 — the
    /// watchdog POSTed twice, the region answered <c>HTTP 200</c> both times for
    /// <c>cof_version=40</c>, and no appearance ever came back. A successful POST is not a bake.
    /// </para>
    ///
    /// <para>Publishes with an EMPTY visual-param array on purpose: <c>ApplyAvatarAppearance</c>
    /// treats that as "this event carries no shape" and keeps the shape it already has, which is
    /// exactly right here — this event knows about textures and nothing else. The hover offset is
    /// read from the same cached Avatar so it is not silently reset to zero.</para>
    /// </summary>
    private bool TryPublishSelfBakesFromScene()
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return false;

        LibreMetaverse.Avatar? me = null;
        foreach (var kv in sim.ObjectsAvatars)
        {
            if (kv.Value != null && kv.Value.ID == _client.Self.AgentID) { me = kv.Value; break; }
        }

        var faces = me?.Textures?.FaceTextures;
        if (faces == null) return false;

        var textures = new Dictionary<int, Guid>();
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            if (face != null && face.TextureID != LibreMetaverse.UUID.Zero)
                textures[i] = face.TextureID.Guid;
        }
        if (textures.Count == 0) return false;

        _lastSelfRelayBakes = new Dictionary<int, Guid>(textures);
        _selfAppearanceWithBakesSeen = true;

        Console.Error.WriteLine(
            $"[Appearance] recovered {textures.Count} bake id(s) from our own ObjectUpdate TextureEntry " +
            "-- the sim never sent an AvatarAppearance for us");

        AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
            sim.Handle,
            _client.Self.AgentID.Guid,
            Array.Empty<byte>(),
            textures,
            _lastSelfHoverOffsetZ));
        return true;
    }

    private void ArmSelfBakeWatchdog()
    {
        if (System.Threading.Interlocked.Exchange(ref _selfBakeWatchdogArmed, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (int delaySeconds in new[] { 25, 30 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                    if (!_client.Network.Connected) return;
                    if (_selfAppearanceWithBakesSeen) return;

                    // Look before asking. The ids are usually already in our own ObjectUpdate, and
                    // the cap POST cannot help when the server has already served this COF version
                    // (see TryPublishSelfBakesFromScene for the viewer's own check and the live
                    // measurement of a POST that was accepted and changed nothing).
                    if (TryPublishSelfBakesFromScene()) return;

                    // OpenSim's client-side bake path is a different mechanism entirely and this
                    // cap does not exist there.
                    if (!RegionHasServerSideBaking()) return;

                    Console.Error.WriteLine(
                        "[Appearance] the sim has not sent our own bake ids -- nudging a server re-composite " +
                        "(until it arrives the system hair renders as an uncut helmet and the head blank)");
                    await SendServerAppearanceUpdateAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Appearance] self-bake watchdog failed: {ex.Message}");
            }
        });
    }

    private System.Threading.CancellationTokenSource? _wearableRebakeCts;

    /// <summary>Second Life only shows a system-wearable edit (wear / take off / swap an alpha
    /// layer) once the avatar re-composites. Fires that nudge automatically, debounced so a swap
    /// (a take-off + a wear, or several layers) coalesces into ONE POST ~1.8 s after the last edit
    /// settles -- long enough for the AIS COF write-backs to bump <c>cof_version</c> in the local
    /// store. SSB only, and the nudge is the pure cap POST (<see cref="SendServerAppearanceUpdateAsync"/>),
    /// NOT the attachment-dropping <c>RequestSetAppearance</c> path (BUG-AVATAR-03).</summary>
    private void ScheduleRebakeAfterWearableEdit()
    {
        _wearableRebakeCts?.Cancel();
        var cts = _wearableRebakeCts = new System.Threading.CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1800), token).ConfigureAwait(false);
                if (token.IsCancellationRequested || !_client.Network.Connected) return;
                if (!RegionHasServerSideBaking()) return;
                Console.Error.WriteLine("[Appearance] wearable edit settled -- nudging a server re-composite");
                await SendServerAppearanceUpdateAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] auto rebake nudge failed: {ex.Message}"); }
        });
    }

    // Both blockers are now handled, each by a guard that refuses to send rather than guessing:
    //
    //   1. Visual-param ORDER. LibreMetaverse writes 195 of 218 params to the wrong slot;
    //      AgentAppearanceParams rebuilds them in wire order and VerifyRoundTrip re-reads the
    //      result the way the simulator will before anything goes out.
    //   2. Bake TEXTURES. LibreMetaverse only composites under SendAppearance, which is off, so its
    //      Textures[] can be all-zero -- measured 2026-08-31 as 8/9/10/11/20 = ZERO. Sending that
    //      says "I have no baked textures" and strips the avatar on the grid, which is exactly what
    //      happened. MergeBakeSlots fills empty slots from the simulator's own last relay and the
    //      send is refused outright if a hole remains.
    //
    // What this cannot do is produce a NEW bake, so a change that needs one (an alpha layer
    // altering what the system body shows) may not become visible until another viewer re-bakes.
    // It is non-destructive either way, which is the property that was missing.

    /// <summary>Collects the worn system wearables from the Current Outfit Folder as
    /// (itemId, wearableType) pairs — the payload of <c>AgentIsNowWearing</c>.
    ///
    /// <para>Read from the COF rather than <c>AppearanceManager.Wearables</c> on purpose: the
    /// legacy <c>AgentWearablesUpdate</c> that populates the latter carries only ONE wearable per
    /// type slot, so a modern multi-layer outfit (several skin/tattoo layers) is unrepresentable in
    /// it. The COF is the complete set, and OpenSim's handler does
    /// <c>Wearables[type].Add(...)</c> — an add, not an assign — so multiple layers of one type are
    /// accepted.</para></summary>
    private List<(LibreMetaverse.UUID ItemId, byte WearableType)> CollectWornWearablesFromCof(
        LibreMetaverse.UUID? excludeItem = null, (LibreMetaverse.UUID Id, byte Type)? extra = null)
    {
        var worn = new List<(LibreMetaverse.UUID, byte)>();
        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;
        if (cofNode == null) return worn;

        foreach (var childNode in cofNode.Nodes.Values)
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var targetUuid = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (targetUuid == LibreMetaverse.UUID.Zero) continue;
            if (excludeItem.HasValue && (targetUuid == excludeItem.Value || link.UUID == excludeItem.Value)) continue;

            if (store?.GetNodeOrDefault(targetUuid)?.Data is not LibreMetaverse.InventoryWearable w) continue;
            worn.Add((targetUuid, (byte)w.WearableType));
        }

        if (extra.HasValue && !worn.Any(e => e.Item1 == extra.Value.Id))
            worn.Add((extra.Value.Id, extra.Value.Type));

        return worn;
    }

    /// <summary>Tells the simulator which system wearables are worn now.
    ///
    /// <para>This is the ONE appearance-related packet that is safe to send from SLNG today: it
    /// carries item ids and wearable-type bytes and <b>nothing else</b> — no visual parameters, no
    /// texture entry — so it cannot write a wrong shape or strip a bake, which is what every
    /// previous attempt did. OpenSim's <c>AvatarFactoryModule</c> applies it to
    /// <c>sp.Appearance.Wearables</c> and persists it (<c>QueueAppearanceSave</c>), then waits for
    /// a viewer to bake. So the change is genuinely recorded server-side; it becomes VISIBLE once
    /// something re-bakes, which SLNG cannot do yet.</para></summary>
    private void SendAgentIsNowWearing(List<(LibreMetaverse.UUID ItemId, byte WearableType)> worn)
    {
        var packet = new LibreMetaverse.Packets.AgentIsNowWearingPacket
        {
            AgentData = { AgentID = _client.Self.AgentID, SessionID = _client.Self.SessionID },
            WearableData = worn
                .Select(w => new LibreMetaverse.Packets.AgentIsNowWearingPacket.WearableDataBlock
                {
                    ItemID = w.ItemId,
                    WearableType = w.WearableType,
                })
                .ToArray(),
        };

        _client.Network.SendPacket(packet);
        Console.Error.WriteLine($"[Appearance] AgentIsNowWearing sent ({worn.Count} wearable(s))");
    }

    /// <summary>Puts a system wearable on: adds its Current-Outfit link, then tells the simulator
    /// the new worn set. Deliberately does NOT go through <c>AppearanceManager.AddToOutfit</c>,
    /// which ends in the appearance send that has corrupted this avatar three times.
    ///
    /// <para><b>The new link is written before the old one is removed, and a refusal aborts.</b>
    /// AIS answers a link it will not accept with a bare <c>Bad Request</c>, which
    /// <c>CreateLinkAsync</c> passes on as <c>null</c> — and this ignored it. It then took the old
    /// body part's link out, told the simulator the new worn set and nudged a server re-composite,
    /// so the server rebuilt the avatar from a Current Outfit Folder that had just lost its skin
    /// and never got the replacement: a washed-out default body, reported live as *"wenn ich über
    /// das Inventar die Skin anziehe sieht der Avi kaputt aus"* (2026-09-09 — seven refusals in one
    /// session, every one of them followed by the broken bake and never by a good one). Creating
    /// first, and stopping when that fails, means a refusal leaves the avatar exactly as it
    /// was.</para></summary>
    private async Task WearWearableAsync(LibreMetaverse.InventoryItem wearable, bool replace)
    {
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        if (cofUuid == LibreMetaverse.UUID.Zero)
        {
            Console.Error.WriteLine($"[Appearance] wear of \"{wearable.Name}\" not sent: no Current Outfit folder");
            WearableEditUnavailable?.Invoke(this, wearable.Name);
            return;
        }

        try
        {
            byte type = wearable is LibreMetaverse.InventoryWearable iw ? (byte)iw.WearableType : (byte)0;
            var wearType = wearable is LibreMetaverse.InventoryWearable iw2
                ? iw2.WearableType : LibreMetaverse.WearableType.Invalid;
            bool replacesSameType = WearableRules.ReplacesSameType(wearable.AssetType, wearType);

            // The same resolution the attachment path has had since BUG-INV-01 and this one never
            // did: walk a link chain down to the base item, and copy a #Library item into our own
            // inventory first. Both are things AIS refuses a COF link for.
            var target = await ResolveCofLinkTargetAsync(wearable).ConfigureAwait(false);
            if (target == null)
            {
                WearableEditUnavailable?.Invoke(this, wearable.Name);
                return;
            }

            if (FindCofLinkTo(target.UUID) != null)
            {
                Console.Error.WriteLine($"[Appearance] \"{target.Name}\" is already in the Current Outfit " +
                    "-- re-composite nudged, no second link written");
                SendAgentIsNowWearing(CollectWornWearablesFromCof());
                WornItemsChanged?.Invoke(this, EventArgs.Empty);
                ScheduleRebakeAfterWearableEdit();
                return;
            }

            // The COF link's description is where Second Life keeps the layer's position in the
            // stack -- '@' + type * 100 + index, see WearableLayerOrder. Passing the item's own
            // description there, as this did, leaves every layer SLNG puts on untokened, and an
            // untokened layer sorts BELOW every tokened one. So anything worn here landed at the
            // bottom of its type's stack and disappeared under whatever was already on. A new layer
            // belongs on top, which is index = however many of that type are already worn -- except
            // a body part, which ends up the only one of its type whatever is still on right now.
            int existing = replacesSameType
                ? 0
                : CollectWornWearablesFromCof().Count(e => e.WearableType == type);
            string linkDescription = wearable is LibreMetaverse.InventoryWearable
                ? WearableLayerOrder.BuildOrderString(type, existing)
                : wearable.Description;

            var created = await CreateCofLinkAsync(
                cofUuid, target, linkDescription, LibreMetaverse.InventoryType.Wearable).ConfigureAwait(false);
            if (created == null)
            {
                Console.Error.WriteLine($"[Appearance] wear of \"{target.Name}\" abandoned -- the Current-Outfit " +
                    "link was refused, so nothing was changed and the avatar is as it was");
                WearableEditUnavailable?.Invoke(this, wearable.Name);
                return;
            }

            // Body parts replace, they do not layer: an avatar has exactly one shape, skin, hair
            // and eyes. This runs AFTER the new link exists -- doing it first is what left the
            // avatar with no skin at all whenever the create was refused.
            if (replacesSameType)
            {
                int replaced = await RemoveCofLinksOfWearableTypeAsync(type, keep: target.UUID)
                    .ConfigureAwait(false);
                if (replaced > 0)
                    Console.Error.WriteLine($"[Appearance] replaced {replaced} worn {wearType} " +
                        "-- body parts are replaced, not layered");
            }

            SendAgentIsNowWearing(CollectWornWearablesFromCof(extra: (target.UUID, type)));

            Console.Error.WriteLine($"[Appearance] wore \"{target.Name}\" ({target.AssetType}) " +
                "-- recorded server-side; auto re-composite scheduled");
            WornItemsChanged?.Invoke(this, EventArgs.Empty);
            ScheduleRebakeAfterWearableEdit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] wear of \"{wearable.Name}\" failed: {ex.Message}");
            WearableEditUnavailable?.Invoke(this, wearable.Name);
        }
    }

    /// <summary>Takes a system wearable off: deletes its Current-Outfit link(s), then tells the
    /// simulator the new worn set.
    ///
    /// <para>The link is REMOVED, not trashed. BUG-NET-02 moves stale COF links to Trash because
    /// there the link is the only evidence of an ambiguous state and might be wanted back. Here the
    /// removal is what the user asked for, and a COF link is a pointer, not content — the wearable
    /// itself stays in inventory and wearing it again just makes a new link. Trashing would only
    /// pile up junk (raised live: "die Links landen dann aber nicht jedes Mal im Trash?").</para></summary>
    /// <summary>Removes the Current Outfit links for every worn wearable of one type, except
    /// <paramref name="keep"/> — the "replace" half of wearing a body part. Returns how many were
    /// taken off.</summary>
    private async Task<int> RemoveCofLinksOfWearableTypeAsync(byte wearableType, LibreMetaverse.UUID keep)
    {
        var store = _client.Inventory.Store;
        var cof = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cof != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cof) : null;
        if (cofNode == null) return 0;

        var doomed = new List<LibreMetaverse.UUID>();
        foreach (var child in cofNode.Nodes.Values)
        {
            if (child.Data is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var target = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (target == keep || target == LibreMetaverse.UUID.Zero) continue;
            if (store?.GetNodeOrDefault(target)?.Data is not LibreMetaverse.InventoryWearable worn) continue;
            if ((byte)worn.WearableType != wearableType) continue;

            doomed.Add(link.UUID);
        }

        foreach (var linkId in doomed)
        {
            try { await _client.Inventory.RemoveItemAsync(linkId).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] could not remove COF link: {ex.Message}"); }
        }

        return doomed.Count;
    }

    private async Task<DetachResult> RemoveWearableAsync(LibreMetaverse.InventoryItem wearable)
    {
        // Body parts are replace-only: an avatar always has exactly one shape, skin, hair and eyes,
        // and a real viewer offers no take-off for them at all. Removing the COF link the way this
        // does for clothing would leave the avatar with no shape.
        var wearableType = wearable is LibreMetaverse.InventoryWearable w
            ? w.WearableType : LibreMetaverse.WearableType.Invalid;
        if (!WearableRules.CanTakeOff(wearable.AssetType, wearableType))
        {
            string reason = WearableRules.TakeOffRefusedReason(wearableType);
            Console.Error.WriteLine($"[Appearance] refused to take off \"{wearable.Name}\": {reason}");
            WearableEditRefused?.Invoke(this, reason);
            return new DetachResult(false, 0);
        }

        var store = _client.Inventory.Store;
        var cofUuid = _client.Inventory.FindFolderForType(LibreMetaverse.FolderType.CurrentOutfit);
        var cofNode = cofUuid != LibreMetaverse.UUID.Zero ? store?.GetNodeOrDefault(cofUuid) : null;

        if (cofNode == null)
        {
            Console.Error.WriteLine($"[Appearance] detach of \"{wearable.Name}\" not sent: no Current Outfit folder");
            WearableEditUnavailable?.Invoke(this, wearable.Name);
            return new DetachResult(false, 0);
        }

        int removed = 0;
        foreach (var childNode in cofNode.Nodes.Values.ToList())
        {
            if (childNode.Data is not LibreMetaverse.InventoryItem link) continue;
            if (link.AssetType == LibreMetaverse.AssetType.LinkFolder) continue;

            var targetUuid = link.IsLink() ? link.ResolvedItemID : link.UUID;
            if (targetUuid != wearable.UUID && link.UUID != wearable.UUID) continue;

            try
            {
                await _client.Inventory.RemoveItemAsync(link.UUID).ConfigureAwait(false);
                cofNode.Nodes.Remove(link.UUID);
                removed++;
            }
            catch (Exception ex) { Console.Error.WriteLine($"[Appearance] could not remove COF link: {ex.Message}"); }
        }

        if (removed == 0)
        {
            Console.Error.WriteLine($"[Appearance] \"{wearable.Name}\" has no Current-Outfit link -- nothing to take off");
            return new DetachResult(false, 0);
        }

        SendAgentIsNowWearing(CollectWornWearablesFromCof(excludeItem: wearable.UUID));
        Console.Error.WriteLine($"[Appearance] removed \"{wearable.Name}\" ({wearable.AssetType}) " +
            $"-- {removed} outfit link(s) removed, recorded server-side; auto re-composite scheduled");
        WornItemsChanged?.Invoke(this, EventArgs.Empty);
        ScheduleRebakeAfterWearableEdit();

        return new DetachResult(false, removed, WearableRemoved: true);
    }

    /// <summary>The simulator's own last relay of the self avatar's shape, captured in
    /// <see cref="OnAvatarAppearance"/>. In <c>VisualParams.Group0ParamIds</c> order — the wire /
    /// decoder order that <c>AvatarShapeService.ComputeEffectiveWeights</c> indexes positionally.
    /// Kept because it is the only shape array in that order: LibreMetaverse's
    /// <c>MyVisualParameters</c> is built by <c>MakeAppearancePacket</c> in a different one
    /// (see <see cref="OnAppearanceSet"/>). FEAT-AVATAR-01.</summary>
    private byte[] _lastSelfRelayVisualParams = Array.Empty<byte>();

    private float _lastSelfHoverOffsetZ;

    /// <summary>The simulator's own last view of our baked textures, keyed by AvatarTextureIndex
    /// (8 head, 9 upper, 10 lower, 11 eyes, 20 hair). These demonstrably work — another viewer
    /// composited and uploaded them. FEAT-AVATAR-01 keeps them as the reference the bake diagnostic
    /// compares LibreMetaverse's own <c>Textures[]</c> against.</summary>
    private Dictionary<int, Guid> _lastSelfRelayBakes = new();

    // Dedup for the on-disk self-appearance cache: the last visual-param array we wrote out.
    private byte[]? _selfAppearanceCacheVp;

    private int _selfAppearanceRestoreArmed;

    /// <summary>BUG-AVATAR-04: 1 once the cache restore has driven the renderer, cleared again the
    /// moment a genuine relay arrives. Without it the 12 s summary reads <c>_lastSelfRelayVisualParams</c>,
    /// sees the shape WE just restored, and reports "FROM SIM" — the one thing the summary exists
    /// to get right.</summary>
    private int _selfShapeFromCache;

    /// <summary>When this login's appearance clock started, for the relay-latency measurement.</summary>
    private readonly System.Diagnostics.Stopwatch _selfAppearanceClock = new();

    private int _selfRelayLatencyLogged;

    /// <summary>BUG-AVATAR-04: how long to wait before falling back to the cached shape.
    ///
    /// <para>Deliberately short. The old flat 12 s was the entire visible defect on a failed login
    /// — blank head, helmet hair, for twelve seconds. Firing early costs nothing when the sim does
    /// answer (the relay overrides it), and 2.5 s is enough for a prompt relay to win the race and
    /// avoid a needless swap. The `[Appearance] self AvatarAppearance relay arrived N s after
    /// login` line exists to replace this estimate with a measurement.</para></summary>
    private static readonly TimeSpan EarlyRestoreDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>BUG-AVATAR-04 root cause: derive our own shape from the WEARABLE ASSETS, the way
    /// the reference viewer does, instead of waiting for the simulator to echo an
    /// <c>AvatarAppearance</c> back at us.
    ///
    /// <para>Why this exists. <c>Settings.Agent.SendAppearance</c> is off (see the constructor, and
    /// it stays off: with it on, LibreMetaverse writes an appearance to the account on every login
    /// before a human can react). That flag also gates LibreMetaverse's wearable decoding, so
    /// <c>GetWearables()</c> is empty and it holds no visual parameters — which left the sim's
    /// unprompted echo of our own appearance as SLNG's ONLY source for the ~253 params. Second Life
    /// does not owe us that echo, and after an outfit change it is least likely to arrive: measured
    /// live 2026-09-09, change outfit → relog → no <c>AvatarAppearance</c> at all.</para>
    ///
    /// <para>The viewer never depends on the echo. It reads the worn wearables and takes the params
    /// out of the assets themselves. Everything needed for that already existed here for the bake
    /// path — <see cref="CollectWornWearablesForBakeAsync"/> reads the Current Outfit Folder and
    /// downloads each asset, <c>OrderWearablesAsTheViewerDoes</c> puts them in layer order, and
    /// <see cref="AgentAppearanceParams.BuildWireArray"/> resolves them into the wire array — it was
    /// simply never wired into the LOGIN path. Purely local: nothing is sent, so this carries none
    /// of the risk the flag does.</para>
    ///
    /// <para>Returns false when the COF yields no usable wearables, in which case the on-disk cache
    /// is still the fallback.</para></summary>
    /// <summary>Diagnostic only — changes nothing, sends nothing. Re-derives the self shape from
    /// the WORN wearable assets (what the reference viewer actually renders itself from) and
    /// reports where it disagrees with the simulator's <c>AvatarAppearance</c> echo, which is what
    /// SLNG currently prefers.
    ///
    /// <para>The two are the same shape only as long as the server's stored copy still matches the
    /// worn Shape asset. It is the stored copy, so anything that ever wrote a wrong one — another
    /// viewer, an interrupted bake, one of this project's own earlier appearance sends — leaves
    /// SLNG rendering a shape the reference viewer never shows, which reads exactly like "the face
    /// is wrong and the head is the wrong size" (BUG-AVATAR-07). Until this has been seen to agree
    /// in the field, "SLNG's shape source is fine" is an assumption, not a fact.</para></summary>
    private async Task<bool> CompareSelfShapeSourcesAsync(CancellationToken ct)
    {
        // Pure diagnostic: it decodes every worn wearable just to print a comparison. Nothing acts
        // on the result, so outside --diag it is work AND log noise for nothing.
        if (!Diag.Verbose) return false;

        var relay = _lastSelfRelayVisualParams;
        if (relay is not { Length: > 0 }) return false;

        try
        {
            var worn = await CollectWornWearablesForBakeAsync(verbose: false, ct).ConfigureAwait(false);
            foreach (var w in worn)
            {
                if (w.Asset != null) continue;
                if (w.AssetType is not (LibreMetaverse.AssetType.Bodypart or LibreMetaverse.AssetType.Clothing))
                    continue;
                try
                {
                    var asset = await _client.Assets
                        .RequestAssetAsync(w.AssetID, w.AssetType, priority: false, ct).ConfigureAwait(false);
                    if (asset is LibreMetaverse.Assets.AssetWearable aw && aw.Decode()) w.Asset = aw;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* one missing wearable must not cost the whole comparison */ }
            }

            var ordered = OrderWearablesAsTheViewerDoes(worn.Where(w => w.Asset != null).ToList(), verbose: false);
            var wearableParams = ordered
                .Where(w => w.Asset != null)
                .Select(w => (IReadOnlyDictionary<int, float>)w.Asset!.Params)
                .ToList();
            if (wearableParams.Count == 0)
            {
                Console.Error.WriteLine("[ShapeSource] cannot compare: no worn wearable assets decoded");
                return false;
            }

            var fromWearables = AgentAppearanceParams.BuildWireArray(wearableParams);
            var ids = LibreMetaverse.VisualParams.Group0ParamIds;

            int differing = 0;
            var worst = new List<(int Id, string Name, float Sim, float Worn, float Diff)>();
            for (int i = 0; i < ids.Length && i < relay.Length && i < fromWearables.Length; i++)
            {
                if (relay[i] == fromWearables[i]) continue;
                differing++;
                if (!LibreMetaverse.VisualParams.Params.TryGetValue(ids[i], out var vp)) continue;
                float simW = LibreMetaverse.Utils.ByteToFloat(relay[i], vp.MinValue, vp.MaxValue);
                float wornW = LibreMetaverse.Utils.ByteToFloat(fromWearables[i], vp.MinValue, vp.MaxValue);
                worst.Add((ids[i], vp.Name, simW, wornW, Math.Abs(simW - wornW)));
            }

            if (differing == 0)
            {
                Console.Error.WriteLine(
                    $"[ShapeSource] sim echo and worn wearables AGREE on all {Math.Min(relay.Length, fromWearables.Length)} params " +
                    "— the rendered shape is not a stale-server-copy problem");
                return true;
            }

            Console.Error.WriteLine(
                $"[ShapeSource] sim echo and worn wearables DISAGREE on {differing} of " +
                $"{Math.Min(relay.Length, fromWearables.Length)} params — SLNG renders the SIM's copy, the reference " +
                "viewer renders the worn wearables. Largest differences (param: sim -> worn):");
            foreach (var d in worst.OrderByDescending(x => x.Diff).Take(12))
                Console.Error.WriteLine($"[ShapeSource]   {d.Id,4} {d.Name,-28} {d.Sim,8:0.0000} -> {d.Worn,8:0.0000}  (delta {d.Diff:0.0000})");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ShapeSource] comparison failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryDeriveSelfShapeFromWearablesAsync(CancellationToken ct)
    {
        try
        {
            var worn = await CollectWornWearablesForBakeAsync(verbose: false, ct).ConfigureAwait(false);

            // The collector resolves the COF links and fills in ItemID / AssetID / WearableType --
            // it does NOT download the assets; the bake path does that itself right after calling
            // it. Skipping this step is why the first live run reported "9 wearable(s), 0 with a
            // downloaded asset" and fell through to the cache (v0.21.25 log). Same request the bake
            // path uses, so a wearable already fetched for a bake this session is a cache hit.
            int decoded = 0;
            foreach (var w in worn)
            {
                if (w.Asset != null) { decoded++; continue; }
                // Only body parts and clothing carry visual params; the COF also holds attachments.
                if (w.AssetType is not (LibreMetaverse.AssetType.Bodypart or LibreMetaverse.AssetType.Clothing))
                    continue;
                try
                {
                    var asset = await _client.Assets
                        .RequestAssetAsync(w.AssetID, w.AssetType, priority: true, ct).ConfigureAwait(false);
                    if (asset is LibreMetaverse.Assets.AssetWearable aw && aw.Decode()) { w.Asset = aw; decoded++; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Appearance]   {w.WearableType}: wearable asset {w.AssetID} did not fetch/decode: {ex.Message}");
                }
            }

            var ordered = OrderWearablesAsTheViewerDoes(worn.Where(w => w.Asset != null).ToList(), verbose: false);
            var wearableParams = ordered
                .Where(w => w.Asset != null)
                .Select(w => (IReadOnlyDictionary<int, float>)w.Asset!.Params)
                .ToList();

            // Say WHY when this cannot produce a shape. Returning a bare false was the same mistake
            // the healthy path made before the login summary existed: the first live run (v0.21.24)
            // fell through to the cache and the log could not say whether the Current Outfit Folder
            // was empty, the assets had not downloaded, or the resolved array failed the health
            // check. One line per failed login, and the next relog is decisive.
            if (wearableParams.Count == 0)
            {
                Console.Error.WriteLine(
                    $"[Appearance] cannot derive a shape: Current Outfit Folder gave {worn.Count} wearable(s), " +
                    $"{decoded} with a downloaded asset — nothing to read params from");
                return false;
            }

            var wire = AgentAppearanceParams.BuildWireArray(wearableParams);
            if (!VisualParamsHealthy(wire))
            {
                int defaulted = wire.Count(b => b == 0 || b == 128);
                Console.Error.WriteLine(
                    $"[Appearance] cannot derive a shape: {wearableParams.Count} wearable(s) resolved to " +
                    $"{wire.Length} params but {defaulted} of them are default (0/128) — needs at least " +
                    $"{MinHealthyVisualParams} params and one non-default value");
                return false;
            }

            _lastSelfRelayVisualParams = wire;
            System.Threading.Volatile.Write(ref _selfShapeFromCache, 0);

            // FEAT-AVATAR-01, its last unchecked acceptance criterion: LogVisualParamHealth() must
            // read a full, non-default parameter set. It reads AppearanceManager.MyVisualParameters,
            // which stays empty because Settings.Agent.SendAppearance is off, and the only thing
            // that ever filled it was the simulator's relay — so on precisely the logins
            // BUG-AVATAR-04 is about it reported "LibreMetaverse holds NO visual parameters".
            //
            // The array just built from the worn wearable ASSETS is exactly such a set, and it is
            // arguably the better source: it comes from what the avatar is actually wearing rather
            // than from what the sim happened to echo. Seeding is diagnostic-only either way —
            // nothing sends from this store while the flag is off.
            TrySeedVisualParams(wire, $"{wearableParams.Count} worn wearable(s)");

            Console.Error.WriteLine(
                $"[Appearance] derived our own shape from {wearableParams.Count} worn wearable(s) " +
                $"({wire.Length} params) — no AvatarAppearance needed");

            // Persist it too: a later login that cannot reach the assets in time still has it.
            MaybeSaveSelfAppearanceCache();

            var sim = _client.Network.CurrentSim;
            if (sim != null)
                AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
                    sim.Handle, _client.Self.AgentID.Guid, wire,
                    new Dictionary<int, Guid>(_lastSelfRelayBakes), _lastSelfHoverOffsetZ));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Appearance] deriving the shape from worn wearables failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Loads the cached shape and drives the renderer with it. Returns false when there is
    /// nothing usable cached. <paramref name="provisional"/> only picks the wording: the early
    /// attempt may still be overtaken by a real relay, the 12 s one is the verdict.</summary>
    private bool TryRestoreSelfShapeFromCache(bool provisional)
    {
        if (!SelfAppearanceCache.TryLoad(_client.Self.AgentID.Guid, out var vp, out var bakes, out var hoverZ)
            || !VisualParamsHealthy(vp))
            return false;

        _lastSelfRelayVisualParams = vp;
        if (bakes.Count > 0 && _lastSelfRelayBakes.Count == 0) _lastSelfRelayBakes = bakes;
        if (_lastSelfHoverOffsetZ == 0f) _lastSelfHoverOffsetZ = hoverZ;
        System.Threading.Volatile.Write(ref _selfShapeFromCache, 1);

        if (provisional)
            Console.Error.WriteLine(
                $"[Appearance] no relay yet after {EarlyRestoreDelay.TotalSeconds:F1} s — showing the cached shape " +
                $"({vp.Length} params, {bakes.Count} bake id(s)); a real relay still overrides it{DescribeAppearanceCacheAge()}");
        else
            Console.Error.WriteLine(
                $"[Appearance] login summary: shape RESTORED FROM CACHE ({vp.Length} params) + {bakes.Count} bake id(s) " +
                $"— the sim sent no AvatarAppearance for us this login{DescribeAppearanceCacheAge()}");

        var sim = _client.Network.CurrentSim;
        if (sim != null)
            AvatarAppearanceReceived?.Invoke(this, new AvatarAppearanceEvent(
                sim.Handle, _client.Self.AgentID.Guid, vp,
                _lastSelfRelayBakes.Count > 0 ? new Dictionary<int, Guid>(_lastSelfRelayBakes) : bakes,
                _lastSelfHoverOffsetZ));
        return true;
    }

    /// <summary>BUG-AVATAR-04: reports how long the simulator took to relay our own
    /// <c>AvatarAppearance</c>, once per login.
    ///
    /// <para>This is the number that sets <see cref="EarlyRestoreDelay"/>. The restore used to wait
    /// a flat 12 s, which is why a failed login rendered a blank head and helmet hair for twelve
    /// seconds before recovering — measured live 2026-09-09, and exactly the "kam kaputt, baute
    /// sich dann sauber auf" report. Pulling the restore earlier is safe (a real relay overrides
    /// it through the same event), but firing it *before* a healthy relay would arrive shows the
    /// cached shape for no reason. So: measure the healthy case, then set the delay from data
    /// rather than taste.</para></summary>
    private void NoteSelfAppearanceRelayArrived()
    {
        System.Threading.Volatile.Write(ref _selfShapeFromCache, 0);
        if (!_selfAppearanceClock.IsRunning) return;
        if (System.Threading.Interlocked.Exchange(ref _selfRelayLatencyLogged, 1) != 0) return;
        Console.Error.WriteLine(
            $"[Appearance] self AvatarAppearance relay arrived {_selfAppearanceClock.Elapsed.TotalSeconds:F1} s after login " +
            $"(early restore fires at {EarlyRestoreDelay.TotalSeconds:F1} s — BUG-AVATAR-04)");
    }

    /// <summary>Persists the last <b>healthy</b> self appearance so a later login that receives no
    /// <c>AvatarAppearance</c> can still render the real shape (see <see cref="SelfAppearanceCache"/>
    /// and <see cref="ArmSelfAppearanceRestore"/>). Only writes when the shape actually changed.</summary>
    private void MaybeSaveSelfAppearanceCache()
    {
        var vp = _lastSelfRelayVisualParams;
        if (!VisualParamsHealthy(vp)) return;
        if (_selfAppearanceCacheVp != null && _selfAppearanceCacheVp.AsSpan().SequenceEqual(vp)) return;

        SelfAppearanceCache.Save(_client.Self.AgentID.Guid, vp, _lastSelfRelayBakes, _lastSelfHoverOffsetZ);
        _selfAppearanceCacheVp = (byte[])vp.Clone();
    }

    /// <summary>BUG-AVATAR-04: " · cache written 2026-09-08 19:44 UTC (14 h ago)", or " · no cache"
    /// — appended to every login-summary line.
    ///
    /// <para>Reported on the healthy path too, on purpose. The cache is written only when an
    /// <c>AvatarAppearance</c> arrives, so its age is the direct test for the second candidate
    /// cause: after an outfit change that received no further relay, the newest entry predates the
    /// change and a later restore brings back the OLD outfit. A timestamp older than the last
    /// outfit change confirms that; a fresh one rules it out.</para></summary>
    private string DescribeAppearanceCacheAge()
    {
        var written = SelfAppearanceCache.LastWrittenUtc(_client.Self.AgentID.Guid);
        if (written == null) return " · no cache";
        var age = DateTime.UtcNow - written.Value;
        string ageText = age.TotalMinutes < 90
            ? $"{age.TotalMinutes:F0} min ago"
            : age.TotalHours < 48 ? $"{age.TotalHours:F0} h ago" : $"{age.TotalDays:F0} d ago";
        return $" · cache written {written.Value:yyyy-MM-dd HH:mm} UTC ({ageText})";
    }

    /// <summary>One-shot: if no healthy self <c>AvatarAppearance</c> has arrived a little after
    /// login, load the last one from <see cref="SelfAppearanceCache"/> and drive the renderer with
    /// it — the sim's own relay is missing on roughly every second Agni login and the ~253 visual
    /// parameters have no other source. Purely local: nothing is sent, and a real relay arriving
    /// afterwards overrides this through the same event.</summary>
    private void ArmSelfAppearanceRestore()
    {
        if (System.Threading.Interlocked.Exchange(ref _selfAppearanceRestoreArmed, 1) != 0) return;
        // The clock starts at login success (see LoginAsync), NOT here: this runs from
        // OnEventQueueRunning, after the caps handshake, which a healthy relay beats.
        if (!_selfAppearanceClock.IsRunning) _selfAppearanceClock.Restart();

        _ = Task.Run(async () =>
        {
            try
            {
                // BUG-AVATAR-04: try the cache EARLY, then report the verdict at the old 12 s mark.
                //
                // A failed login used to render a blank head and helmet hair for the full twelve
                // seconds before the restore fired -- measured live 2026-09-09, and precisely the
                // "kam kaputt, baute sich dann sauber auf" report. Restoring early is safe by
                // construction: a genuine relay arriving later overrides it through the same
                // event, and NoteSelfAppearanceRelayArrived clears the from-cache marker so the
                // summary below still tells the truth about what the SIM did.
                //
                // This is mitigation, not the fix. The open question is why the simulator sends no
                // AvatarAppearance after an outfit change at all -- see the spec.
                await Task.Delay(EarlyRestoreDelay).ConfigureAwait(false);
                if (!_client.Network.Connected) return;
                if (!VisualParamsHealthy(_lastSelfRelayVisualParams)) TryRestoreSelfShapeFromCache(provisional: true);

                await Task.Delay(TimeSpan.FromSeconds(12) - EarlyRestoreDelay).ConfigureAwait(false);
                if (!_client.Network.Connected) return;

                // BUG-AVATAR-04, the actual fix rather than the mitigation above: if the simulator
                // still has not echoed our appearance, stop waiting for it and read the worn
                // wearables ourselves, which is what the reference viewer does in the first place.
                // Tried BEFORE falling back to the cache, because the assets are the current truth
                // and the cache is only the last thing we happened to see.
                if (!VisualParamsHealthy(_lastSelfRelayVisualParams)
                    || System.Threading.Volatile.Read(ref _selfShapeFromCache) != 0)
                {
                    if (await TryDeriveSelfShapeFromWearablesAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        Console.Error.WriteLine(
                            "[Appearance] login summary: shape DERIVED FROM WORN WEARABLES — the sim sent no " +
                            $"AvatarAppearance for us this login{DescribeAppearanceCacheAge()}");
                        return;
                    }
                }

                // BUG-AVATAR-04: say out loud, once per login, WHICH path produced this avatar.
                //
                // This used to be silent on the healthy path -- it simply returned -- so a session
                // where the sim behaved and a session where this code never ran looked identical
                // in the log, and "kam kaputt, baute sich dann sauber auf" could not be told apart
                // from ordinary progressive loading. Months of test logins passed without the
                // restore below ever executing and nobody could tell.
                //
                // With the 2026-09-09 repro (change outfit, then relog) one line here decides the
                // open question: whether the sim withholds AvatarAppearance, or whether an outfit
                // change of our own leaves us without one. Unconditional, not behind --diag: it is
                // one line per login.
                bool fromCache = System.Threading.Volatile.Read(ref _selfShapeFromCache) != 0;
                if (VisualParamsHealthy(_lastSelfRelayVisualParams) && !fromCache)
                {
                    Console.Error.WriteLine(
                        $"[Appearance] login summary: shape FROM SIM ({_lastSelfRelayVisualParams!.Length} params), " +
                        $"{_lastSelfRelayBakes.Count} bake id(s){DescribeAppearanceCacheAge()}");
                    await CompareSelfShapeSourcesAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (fromCache)
                {
                    Console.Error.WriteLine(
                        $"[Appearance] login summary: shape RESTORED FROM CACHE ({_lastSelfRelayVisualParams!.Length} params) " +
                        $"+ {_lastSelfRelayBakes.Count} bake id(s) — the sim sent no AvatarAppearance for us this login" +
                        $"{DescribeAppearanceCacheAge()}");
                    return;
                }

                if (!TryRestoreSelfShapeFromCache(provisional: false))
                    Console.Error.WriteLine(
                        "[Appearance] login summary: NO shape — the sim sent no AvatarAppearance and there is no " +
                        $"cached one to fall back on; the avatar keeps the default shape until a relay arrives{DescribeAppearanceCacheAge()}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Appearance] self-appearance restore failed: {ex.Message}");
            }
        });
    }

    private void OnAppearanceSet(object? sender, AppearanceSetEventArgs e)
    {
        if (!e.Success) return;

        // FEAT-UI-16: a completed bake / outfit apply changes the worn set.
        WornItemsChanged?.Invoke(this, EventArgs.Empty);

        // Records what LibreMetaverse just put into MyVisualParameters. That array is in
        // MakeAppearancePacket's (wrong) order, so this is a diagnostic only -- never a shape.
        LogVisualParamHealth();

        // FEAT-AVATAR-01: LibreMetaverse has just baked (correctly) and sent an AgentSetAppearance
        // whose visual params are scrambled. Replace it with a correctly-ordered one while its
        // fresh bake textures are still what MakeAppearancePacket hands out.
        //
        // UNCONDITIONAL, deliberately. With SendAppearance on, LibreMetaverse bakes and sends on
        // its own -- at login (Simulator_OnCapabilitiesReceived), on a region change, and on the
        // simulator's RebakeAvatarTextures request -- not only after a wearable edit we initiated.
        // Every one of those writes the scrambled param array to the account, so every one of them
        // has to be followed by the correction. Gating this on a user action would have left the
        // login send uncorrected, which is the single most damaging one.
        SendCorrectedAppearance();

        var te = _client.Appearance.MyTextures;
        var faces = te?.FaceTextures;
        if (faces == null) return;

        // Mirror OnAvatarAppearance: key by face index, drop empty slots. Also drop the generic
        // DEFAULT_AVATAR_TEXTURE that MyTextures carries for never-baked slots, so we only ever
        // emit genuinely-composited bakes and never regress a real bake to the default skin.
        var textures = new Dictionary<int, Guid>();
        for (int i = 0; i < faces.Length; i++)
        {
            var face = faces[i];
            if (face != null
                && face.TextureID != LibreMetaverse.UUID.Zero
                && face.TextureID != AppearanceManager.DEFAULT_AVATAR_TEXTURE)
            {
                textures[i] = face.TextureID.Guid;
            }
        }

        if (textures.Count == 0) return;

        // FEAT-AVATAR-01 -- the shape MUST NOT come from Appearance.MyVisualParameters here.
        //
        // Two different orderings exist and they are not interchangeable (pinned by
        // SLNG.Assets.Tests.VisualParamOrderTests):
        //   * DECODER order = VisualParams.Group0ParamIds, ascending id over the TRANSMITTED params.
        //     This is what the simulator sends, what LMV's Avatar.DecodeVisualParams assumes, and
        //     what AvatarShapeService.ComputeEffectiveWeights indexes positionally.
        //   * ENCODER order = whatever AppearanceManager.MakeAppearancePacket produces: it iterates
        //     VisualParams.Params -- ALL params, including the never-transmitted group-1/2 ones --
        //     and takes the first 218, then copies that into MyVisualParameters.
        // The first 218 of Params are provably NOT the first 218 of Group0ParamIds, so
        // MyVisualParameters assigns each byte to the WRONG parameter when read as a shape.
        //
        // This event only fires when LibreMetaverse runs its own bake, i.e. never while
        // SendAppearance is off -- which is why the bug stayed latent. With SLNG_APPEARANCE_SYNC on
        // it fires, and feeding the encoder-order array to the shape service scrambled every
        // skeletal param: live 2026-08-31 the rigged mesh head tore apart.
        //
        // The valuable part of this event is the freshly composited BAKE IDS. Take those, and pair
        // them with the simulator's own last relay of the shape, which is in decoder order.
        RaiseAvatarAppearance(new AvatarAppearanceEvent(
            _client.Network.CurrentSim?.Handle ?? 0,
            _client.Self.AgentID.Guid,
            _lastSelfRelayVisualParams,
            textures));
    }

    private readonly Dictionary<Guid, Guid> _selfAnimationSources = new();

    private void OnAvatarAnimation(object? sender, LibreMetaverse.AvatarAnimationEventArgs e)
    {
        var animIds = new List<Guid>(e.Animations.Count);
        // FEAT-ANIM-03: the packet's AnimationSourceList says which object started each animation,
        // which is the only way to tell a furniture pose from a worn AO's. Carried as neutral
        // Guids -- no LibreMetaverse type crosses this boundary.
        //
        // This event, not AgentManager.AnimationsChanged: the self-agent event genuinely throws the
        // source list away (see the FIXME in AgentManager.PacketHandlers.cs), while
        // AvatarManager.AvatarAnimation fires for every avatar INCLUDING self and keeps it.
        var signals = new List<AnimationSignal>(e.Animations.Count);
        foreach (var anim in e.Animations)
        {
            animIds.Add(anim.AnimationID.Guid);
            signals.Add(new AnimationSignal(anim.AnimationID.Guid, anim.AnimationSourceObjectID.Guid));
        }

        if (e.AvatarID == _client.Self.AgentID)
        {
            lock (_selfAnimationSources)
            {
                _selfAnimationSources.Clear();
                foreach (var anim in e.Animations)
                {
                    if (anim.AnimationSourceObjectID != LibreMetaverse.UUID.Zero)
                    {
                        _selfAnimationSources[anim.AnimationID.Guid] = anim.AnimationSourceObjectID.Guid;
                    }
                }
            }
        }

        AvatarAnimationReceived?.Invoke(this, new AvatarAnimationEvent(
            e.AvatarID.Guid,
            animIds,
            signals
        ));
    }
}
