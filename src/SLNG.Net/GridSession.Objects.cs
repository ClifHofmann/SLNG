using System.Buffers.Binary;
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

// GridSession, Objects part.
//
// ObjectUpdate in all its forms, object properties and physics, media-on-a-prim,
// script dialogs and permissions, and the in-world edit/create writes.
//
// Split out of the single 10,042-line GridSession.cs -- a pure move, no member
// changed. The class is still one type; `partial` only spreads it over files that
// can be read, reviewed and merged independently.
public sealed partial class GridSession
{
    /// <summary>Reads each object's raw ExtraParams bytes -- the Light (0x20), Reflection Probe
    /// (0x90) and Extended Mesh (0x70) blocks -- independent of LibreMetaverse's own parsing: see
    /// <see cref="ExtraParamsScan"/> for why none of them can be read back from the high-level
    /// Primitive object. Runs for every ObjectUpdate; <see cref="OnObjectUpdateCompressedRaw"/> does
    /// the same for the compressed form.</summary>
    private void OnRawObjectUpdatePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdatePacket update) return;

        // BUG-RENDER-47: an uncompressed update supersedes any cached compressed state.
        foreach (var block in update.ObjectData)
        {
            InvalidateCachedObject(e.Simulator.Handle, block.ID);
        }

        // BUG-NET-25 / FEAT-ANIMESH-01: this callback runs AFTER LibreMetaverse's own handler has
        // queued the event for these same blocks (its handler sits ahead of ours in the same
        // invocation list, and the event itself goes out on a thread-pool work item), so that event
        // may have been built before the latches were written. Raised again once, only for an
        // object whose stored answer actually changed -- otherwise a mirror, a light switched off or
        // an animesh would be reported one update late. Done after the loop so one misbehaving
        // subscriber cannot stop the rest of the packet's blocks being latched.
        var changed = LatchObjectUpdateBlocks(e.Simulator.Handle, update.ObjectData);
        RaiseUpdatesFor(e.Simulator, changed);
    }

    /// <summary>Latches every block of one ObjectUpdate packet and returns the objects whose stored
    /// answer changed -- each object once, however many of its latches moved or however many blocks
    /// of the packet name it. A block whose ExtraParams are malformed changes nothing it cannot
    /// vouch for (see <see cref="ExtraParamsScan"/>); in particular it does not stop the blocks
    /// behind it.</summary>
    internal List<uint> LatchObjectUpdateBlocks(
        ulong regionHandle, IEnumerable<ObjectUpdatePacket.ObjectDataBlock> blocks)
    {
        var changed = new List<uint>();
        foreach (var block in blocks)
        {
            if (LatchExtraParams(regionHandle, block.ID, ExtraParamsScan.Read(block.ExtraParams))
                && !changed.Contains(block.ID))
            {
                changed.Add(block.ID);
            }
        }
        return changed;
    }

    /// <summary>The compressed counterpart of one block of <see cref="LatchObjectUpdateBlocks"/>:
    /// reads the object's ExtraParams out of the middle of the blob and latches them. Returns
    /// whether any stored answer changed. A layout that cannot be established changes nothing.</summary>
    internal bool LatchCompressedExtraParams(ulong regionHandle, uint localId, byte[]? block)
        => CompressedExtraParams(block) is { } scan && LatchExtraParams(regionHandle, localId, scan);

    private bool LatchExtraParams(ulong regionHandle, uint localId, in ExtraParamsScan scan)
    {
        // Each kind is updated only when the scan KNOWS its answer: found, or the whole buffer was
        // read and it is not there. All three are evaluated (no short-circuit): one object can
        // change in several ways at once, and every latch has to see the update.
        bool light = scan.LightKnown && LatchLight(regionHandle, localId, scan.Light);
        bool probe = scan.ProbeKnown && LatchReflectionProbe(regionHandle, localId, scan.Probe);
        bool mesh = scan.AnimatedMeshKnown && LatchAnimatedMesh(regionHandle, localId, scan.AnimatedMesh);
        return light || probe || mesh;
    }

    /// <summary>Raises the update for each listed object once, from the Primitive LibreMetaverse
    /// holds for it -- a no-op for one it does not (yet) have: the library's own event then
    /// follows with the latches already written.</summary>
    private void RaiseUpdatesFor(LibreMetaverse.Simulator simulator, IEnumerable<uint> localIds)
    {
        foreach (uint localId in localIds)
        {
            if (simulator.ObjectsPrimitives.TryGetValue(localId, out Primitive? prim) && prim is not null)
                RaiseObjectUpdate(simulator, prim, isFullUpdate: true);
        }
    }

    /// <summary>
    /// Whether this ExtraParams buffer carries an Extended Mesh (0x70) block with the animated-mesh
    /// bit set. False for an absent block, a clear bit, and anything malformed -- a short or
    /// truncated block must read as "not animesh", never as garbage. FEAT-ANIMESH-01.
    ///
    /// <para>Wire layout (lldatapacker.cpp:292-334, llprimitive.cpp:2273-2285): <c>U8 count</c>, then
    /// per entry <c>U16 type</c>, <c>S32 size</c>, <c>size</c> payload bytes, all little-endian. The
    /// 0x70 payload is one <c>U32</c> flags word and <c>ANIMATED_MESH_ENABLED_FLAG</c> is bit 0
    /// (<see cref="SLNG.Core.ExtendedMeshParams"/>). The walk, and its validation of the signed size
    /// field, is <see cref="ExtraParamsScan"/>'s.</para>
    ///
    /// <para>A payload longer than four bytes is read for its first four, as the probe block is:
    /// if Linden Lab appends fields, the leading word still means what it means today. A payload
    /// shorter than four is refused.</para>
    /// </summary>
    internal static bool ExtraParamsAnimatedMesh(byte[]? data) => ExtraParamsScan.Read(data).AnimatedMesh;

    /// <summary>Whether this ExtraParams buffer carries a Light (0x20) block. False for an absent
    /// block and for anything malformed before it.</summary>
    internal static bool ExtraParamsContainsLight(byte[]? data) => ExtraParamsScan.Read(data).Light;

    /// <summary>Reads the Reflection Probe (0x90) block out of the same raw ExtraParams scan, or
    /// null when this update carries none. Payload is <c>LLReflectionProbeParams::pack</c>
    /// (llprimitive.cpp:1837): F32 ambiance, F32 clip distance, U8 flags, little-endian.
    ///
    /// Length-checked against the payload the packet actually declares rather than assuming 9
    /// bytes: the block is one Linden Lab could extend, and a short read of a longer future
    /// block would silently produce nonsense flags -- which here means inventing or losing a
    /// mirror.</summary>
    internal static SLNG.Core.ReflectionProbeParams? ExtraParamsReflectionProbe(byte[]? data)
        => ExtraParamsScan.Read(data).Probe;

    /// <summary>
    /// The three ExtraParams blocks of an <c>ObjectUpdateCompressed</c> object, where the ExtraParams
    /// are not a field of their own but a section in the middle of one blob. The offset comes from
    /// <see cref="CompressedParticleRepair.TryFindExtraParams"/>, which shares its walk with the
    /// particle repair. Null means the layout could not be established (too short, cut off inside
    /// an optional section, or a scratch-pad object whose width the two decoders disagree on) -- the
    /// caller must then leave what it already knows alone rather than treat it as "no blocks". A
    /// located but malformed buffer is a scan like any other: what it showed, with
    /// <see cref="ExtraParamsScan.Complete"/> false.
    /// </summary>
    internal static ExtraParamsScan? CompressedExtraParams(byte[]? block)
    {
        if (block is null || !CompressedParticleRepair.TryFindExtraParams(block, out int offset))
        {
            return null;
        }
        return ExtraParamsScan.Read(block.AsSpan(offset));
    }

    /// <summary>
    /// <see cref="ExtraParamsAnimatedMesh"/> for an <c>ObjectUpdateCompressed</c> object. Null means
    /// the layout could not be established (see <see cref="CompressedExtraParams"/>); a located
    /// but malformed block is an answer: false.
    /// </summary>
    internal static bool? CompressedAnimatedMesh(byte[]? block) => CompressedExtraParams(block)?.AnimatedMesh;

    // --- The latches. Three, one shape: keyed by (region, LocalId) -- LocalIds are handed out per
    // region and a neighbour (MultipleSims) reuses the same numbers --, holding only the objects
    // that ever showed the block, answering "did the stored answer CHANGE" so the callback can
    // re-raise once, and pruned with the object (kill) and with the region (disconnect).

    /// <summary>Records whether an object is an animated mesh, and says whether that CHANGED --
    /// which is what makes a re-raise worth doing. Only the true state is stored: "false" for an
    /// object never seen as animesh (every ordinary prim, on every update) is not a change and
    /// costs nothing.</summary>
    internal bool LatchAnimatedMesh(ulong regionHandle, uint localId, bool animatedMesh) =>
        animatedMesh
            ? _animatedMeshObjects.TryAdd((regionHandle, localId), true)
            : _animatedMeshObjects.TryRemove((regionHandle, localId), out _);

    internal bool IsAnimatedMeshLatched(ulong regionHandle, uint localId) =>
        _animatedMeshObjects.ContainsKey((regionHandle, localId));

    /// <summary>Records whether an object's current ExtraParams carry a Light block, and says
    /// whether that CHANGED (appeared, went away, or came back).
    ///
    /// <para>The value is "the most recent ExtraParams positively had no Light block". It matters
    /// only for an object that once showed a light -- <c>Primitive.Light</c> is never cleared, so a
    /// light that went away is the one case it reports wrongly; every other object's is already the
    /// default -- so only those objects are held: an entry exists once a light has been seen, true
    /// while it is there and false once it is gone. No entry keeps meaning "trust the Primitive",
    /// which is also what an object whose ExtraParams were never readable gets.</para></summary>
    internal bool LatchLight(ulong regionHandle, uint localId, bool present)
    {
        var key = (regionHandle, localId);
        if (present)
        {
            // First sighting, or false -> true. true -> true is no change (TryUpdate fails).
            return _lightObjects.TryAdd(key, true) || _lightObjects.TryUpdate(key, true, false);
        }
        // true -> false. No entry, or already false: nothing to say.
        return _lightObjects.TryUpdate(key, false, true);
    }

    /// <summary>True when this object showed a light and its latest ExtraParams no longer carry
    /// one: <c>Primitive.Light</c> is then stale-on and must not be believed.</summary>
    internal bool IsLightRemovedLatched(ulong regionHandle, uint localId) =>
        _lightObjects.TryGetValue((regionHandle, localId), out bool present) && !present;

    /// <summary>The light this object really has: the Primitive's, unless the latch says it was
    /// switched off, in which case a fresh default (off) one. Never rewrites the Primitive:
    /// LibreMetaverse owns it, and a re-enable arriving while a stale read is in flight would
    /// otherwise be wiped -- leaving the re-raise nothing to bring back.</summary>
    internal Primitive.LightData EffectiveLight(ulong regionHandle, uint localId, Primitive.LightData light) =>
        light.Intensity > 0f && IsLightRemovedLatched(regionHandle, localId)
            ? new Primitive.LightData()
            : light;

    /// <summary>Records an object's Reflection Probe block (null when its ExtraParams carry none)
    /// and says whether that CHANGED. Only objects that have one are held; none and unknown both
    /// read as null.</summary>
    internal bool LatchReflectionProbe(ulong regionHandle, uint localId, SLNG.Core.ReflectionProbeParams? probe)
    {
        var key = (regionHandle, localId);
        if (probe is not { } value)
        {
            return _reflectionProbes.TryRemove(key, out _);
        }

        while (true)
        {
            if (_reflectionProbes.TryAdd(key, value)) return true;
            if (!_reflectionProbes.TryGetValue(key, out var old)) continue; // removed in between: add again
            if (old.Equals(value)) return false;
            if (_reflectionProbes.TryUpdate(key, value, old)) return true;
        }
    }

    internal SLNG.Core.ReflectionProbeParams? ReflectionProbeLatched(ulong regionHandle, uint localId) =>
        _reflectionProbes.TryGetValue((regionHandle, localId), out var probe) ? probe : null;

    /// <summary>Drops one object's animesh state without raising anything: it is gone, and its
    /// LocalID may be handed to a different object that must start clean.</summary>
    internal void ForgetAnimatedMesh(ulong regionHandle, uint localId) =>
        _animatedMeshObjects.TryRemove((regionHandle, localId), out _);

    /// <summary>Drops every animesh object of a region that has been disconnected: after a restart
    /// the same LocalIDs belong to different objects.</summary>
    internal void ForgetAnimatedMeshRegion(ulong regionHandle)
    {
        foreach (var key in _animatedMeshObjects.Keys)
        {
            if (key.Region == regionHandle) _animatedMeshObjects.TryRemove(key, out _);
        }
    }

    /// <summary>Drops everything the ExtraParams latches hold for one object (kill).</summary>
    internal void ForgetObjectLatches(ulong regionHandle, uint localId)
    {
        _lightObjects.TryRemove((regionHandle, localId), out _);
        _reflectionProbes.TryRemove((regionHandle, localId), out _);
        ForgetAnimatedMesh(regionHandle, localId);
    }

    /// <summary>Drops everything the ExtraParams latches hold for a region (disconnect).</summary>
    internal void ForgetObjectLatchesRegion(ulong regionHandle)
    {
        foreach (var key in _lightObjects.Keys)
        {
            if (key.Region == regionHandle) _lightObjects.TryRemove(key, out _);
        }
        foreach (var key in _reflectionProbes.Keys)
        {
            if (key.Region == regionHandle) _reflectionProbes.TryRemove(key, out _);
        }
        ForgetAnimatedMeshRegion(regionHandle);
    }

    private void OnObjectPropertiesFamily(object? sender, ObjectPropertiesFamilyEventArgs e)
    {
        // ObjectPropertiesFamily never carries CreatorID (LibreMetaverse leaves
        // Primitive.ObjectProperties.CreatorID unset for this message type) -- only the full
        // ObjectProperties message below has it. WorldSimulation.ApplyObjectProperties knows not
        // to let this empty value stomp a CreatorID already learned from the full message.
        ObjectPropertiesReceived?.Invoke(this, new ObjectPropertiesEvent(
            e.Simulator.Handle,
            e.Properties.ObjectID.Guid,
            e.Properties.Name ?? "",
            e.Properties.Description ?? "",
            e.Properties.CreatorID.Guid,
            e.Properties.OwnerID.Guid,
            e.Properties.GroupID.Guid,
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Move),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Modify),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Copy),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Transfer),
            (SLNG.Core.PrimSaleType)(byte)e.Properties.SaleType,
            e.Properties.SalePrice
        ));
    }

    /// <summary>The full ObjectProperties message, sent automatically by the simulator when an
    /// object is selected (see GridSession.SelectObject). Unlike the family variant, this one
    /// includes CreatorID.</summary>
    private void OnObjectPropertiesFull(object? sender, ObjectPropertiesEventArgs e)
    {
        ObjectPropertiesReceived?.Invoke(this, new ObjectPropertiesEvent(
            e.Simulator.Handle,
            e.Properties.ObjectID.Guid,
            e.Properties.Name ?? "",
            e.Properties.Description ?? "",
            e.Properties.CreatorID.Guid,
            e.Properties.OwnerID.Guid,
            e.Properties.GroupID.Guid,
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Move),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Modify),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Copy),
            e.Properties.Permissions.OwnerMask.HasFlag(PermissionMask.Transfer),
            (SLNG.Core.PrimSaleType)(byte)e.Properties.SaleType,
            e.Properties.SalePrice
        ));
    }

    /// <summary>Unlike ObjectUpdate/ObjectProperties, this only arrives after an explicit
    /// object-select request (see SelectObject) -- delivered asynchronously over the EventQueue
    /// CAP, not plain UDP, so it can land well after the select call returns.</summary>
    private void OnPhysicsProperties(object? sender, PhysicsPropertiesEventArgs e)
    {
        var p = e.PhysicsProperties;
        byte shapeByte = (byte)p.PhysicsShapeType;
        PhysicsPropertiesReceived?.Invoke(this, new PhysicsPropertiesEvent(
            e.Simulator.Handle,
            p.LocalID,
            shapeByte <= 2 ? (SLNG.Core.PrimPhysicsShapeType)shapeByte : SLNG.Core.PrimPhysicsShapeType.Prim,
            p.Density,
            p.Friction,
            p.Restitution,
            p.GravityMultiplier
        ));
    }

    private void OnScriptDialog(object? sender, ScriptDialogEventArgs e)
    {
        ScriptDialogReceived?.Invoke(this, new ScriptDialogEvent(
            e.ObjectID.Guid, e.ObjectName, e.OwnerID.Guid,
            $"{e.FirstName} {e.LastName}".Trim(),
            e.Message, e.Channel, e.ButtonLabels));
    }

    private void OnScriptQuestion(object? sender, ScriptQuestionEventArgs e)
    {
        ScriptPermissionRequested?.Invoke(this, new ScriptPermissionRequestEvent(
            e.TaskID.Guid, e.ItemID.Guid, e.ObjectName ?? string.Empty,
            e.ObjectOwnerName ?? string.Empty, (int)e.Questions));
    }

    /// <summary>
    /// Answers an <c>llRequestPermissions</c> question. <b>Only ever call this from a deliberate
    /// user decision</b> — the flags include spending the agent's money.
    ///
    /// <para>A refusal is a real answer, not silence: the reference viewer always sends the reply
    /// and simply zeroes the granted bits when the user says no (llviewermessage.cpp:5600-5632,
    /// "if any other button was clicked, the permissions were denied" — then the same
    /// <c>ScriptAnswerYes</c> goes out with <c>Questions = 0</c>). Sending it is what lets the
    /// script stop waiting and tell the user it was refused.</para>
    ///
    /// <para><paramref name="granted"/> is a bit field, not a boolean, so a caller can grant a
    /// subset — pass 0 to refuse everything.</para>
    /// </summary>
    public void RespondToScriptPermissionRequest(Guid taskId, Guid itemId, int granted)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null || !_client.Network.Connected) return;

        _client.Self.ScriptQuestionReply(
            sim, new UUID(itemId), new UUID(taskId), (ScriptPermission)granted);
    }

    /// <summary>Answers an llDialog popup by sending the chosen button back over the proper
    /// ScriptDialogReply protocol path (M5-4) -- NOT Self.Chat on the channel, which is a
    /// separate internal helper for negative-channel gesture/debug chat, not dialog replies.</summary>
    public void ReplyToScriptDialog(Guid objectId, int channel, int buttonIndex, string buttonLabel)
    {
        if (_client.Network.Connected)
            _client.Self.ReplyToScriptDialog(channel, buttonIndex, buttonLabel, new UUID(objectId));
    }

    private void OnObjectUpdate(object? sender, PrimEventArgs e)
    {
        InvalidateCachedObject(e.Simulator.Handle, e.Prim.LocalID);
        RaiseObjectUpdate(e.Simulator, e.Prim, isFullUpdate: true);
    }

    /// <summary>FEAT-ANIMESH-02. The sim's <c>ObjectAnimation</c> message for one prim -- see
    /// <see cref="ObjectAnimationConverter"/>. Every region's, not just the current one: a
    /// neighbour's animated mesh moves too, and the world holds the neighbour's objects. Passed
    /// straight on; WorldSimulation queues it and applies it on the pump thread.</summary>
    private void OnObjectAnimation(object? sender, ObjectAnimationEventArgs e)
    {
        var evt = ObjectAnimationConverter.FromWire(e.Simulator.Handle, e.ObjectID, e.Animations);
        if (Diag.Verbose) Console.Error.WriteLine(ObjectAnimationConverter.Describe(evt));
        ObjectAnimationReceived?.Invoke(this, evt);
    }

    /// <summary>
    /// Puts back the particle system LibreMetaverse drops from every <c>ObjectUpdateCompressed</c>
    /// object -- see <see cref="CompressedParticleRepair"/> for what it gets wrong and why the
    /// failure is silent. Runs on a network thread, like every other LibreMetaverse handler.
    /// </summary>
    private void OnObjectUpdateCompressedRaw(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdateCompressedPacket packet)
        {
            return;
        }

        List<uint>? toRaise = null;
        foreach (var block in packet.ObjectData)
        {
            if (!CompressedParticleRepair.TryReadLocalId(block.Data, out uint localId))
            {
                continue;
            }

            // BUG-NET-25 / FEAT-ANIMESH-01: LibreMetaverse does not parse the Reflection Probe
            // block at all, and its Light and Extended Mesh fields are never cleared when a block
            // goes away -- so, as for a full update, the ExtraParams are read here. A compressed
            // object carries them in the middle of its one blob; this runs for the cache replay as
            // well, which feeds this same handler. A layout that cannot be established leaves
            // every latch as it was.
            bool latchChanged = LatchCompressedExtraParams(e.Simulator.Handle, localId, block.Data);

            byte[]? raw = CompressedParticleRepair.ExtractParticleBlock(block.Data);
            if (raw is null && !latchChanged)
            {
                continue;
            }

            // The object LibreMetaverse has just finished decoding. If it is not there, this
            // callback beat the library's own handler and there is nothing to correct yet -- the
            // next update carries the same block.
            if (!e.Simulator.ObjectsPrimitives.TryGetValue(localId, out Primitive? prim) || prim is null)
            {
                continue;
            }

            // The library's event may have been built before the latches above were written (the
            // same ordering OnRawObjectUpdatePacket describes), so a change is raised again.
            bool raise = latchChanged;
            if (raw is not null)
            {
                var repaired = new Primitive.ParticleSystem(raw, 0);
                // Already correct: an object whose particles have not changed sends the same block
                // on every compressed update, and re-raising each one would double the work of
                // every moving emitter in the region.
                if (!prim.ParticleSys.Equals(repaired))
                {
                    prim.ParticleSys = repaired;
                    raise = true;
                }
            }

            // Once per object per packet, however much of it changed: deferred to after the loop.
            if (raise && !(toRaise ??= new List<uint>()).Contains(localId))
            {
                toRaise.Add(localId);
            }
        }

        if (toRaise is not null)
        {
            RaiseUpdatesFor(e.Simulator, toRaise);
        }
    }

    /// <summary>ImprovedTerseObjectUpdate -- the lightweight packet a physically-moving object
    /// (falling, rolling, pushed) streams position/rotation/velocity through while it's actually
    /// in motion. Without this subscription, only full ObjectUpdate packets reach our pipeline,
    /// which the sim sends on state changes (select, flag/property edits) but NOT continuously
    /// while an object is just physically moving -- so a physical object's position only ever
    /// visibly updated here when something else incidentally forced a full resync, never smoothly
    /// while actually falling/rolling.</summary>
    private void OnTerseObjectUpdate(object? sender, TerseObjectUpdateEventArgs e)
    {
        if (e.Update.Avatar || e.Prim is Avatar)
        {
            // See OnAvatarUpdate: neighbor sims (MultipleSims, BUG-NET-03) also stream avatar
            // terse updates, including our own child-agent copy -- the current sim is the sole
            // authority for avatars.
            if (e.Simulator != _client.Network.CurrentSim) return;

            Guid agentId = e.Prim.ID.Guid;
            string firstName = (e.Prim as Avatar)?.FirstName ?? "";
            string lastName = (e.Prim as Avatar)?.LastName ?? "";

            if (e.Simulator.ObjectsAvatars.TryGetValue(e.Prim.LocalID, out var knownAv) && knownAv != null)
            {
                if (agentId == Guid.Empty) agentId = knownAv.ID.Guid;
                if (string.IsNullOrEmpty(firstName)) firstName = knownAv.FirstName;
                if (string.IsNullOrEmpty(lastName)) lastName = knownAv.LastName;
            }

            bool isLocalAgent = agentId == _client.Self.AgentID.Guid || e.Prim.LocalID == _client.Self.LocalID;
            // Position/Rotation/Velocity come from e.Update (the freshly-decoded
            // ObjectMovementUpdate for THIS packet), never from e.Prim: LibreMetaverse's
            // ImprovedTerseObjectUpdateHandler fires this event via ThreadPool.QueueUserWorkItem
            // BEFORE it writes the decoded values onto the shared, cached e.Prim object ("Fire the
            // pre-emptive notice (before we stomp the object)" -- ObjectManager.PacketHandlers.cs
            // ~line 619). Reading e.Prim here races that later write; under load (many queued
            // avatar updates while walking) the handler can run before or after the stomp, so
            // e.Prim.Velocity is sometimes last packet's value or zero. Since ExtrapolateMovement
            // dead-reckons Position purely from Velocity between packets, a stale/zero read here
            // silently killed the extrapolation for that interval -- the avatar would sit still
            // until the next (correct) packet snapped it forward, reading as juddery/stuttering
            // motion. e.Update is race-free: it's the packet's own decoded struct, not a shared
            // mutable cache.
            //
            // e.Prim.ParentID (MVP2-1 seat lookup) does NOT race that write: ImprovedTerseObjectUpdate
            // never carries ParentID at all (only a full ObjectUpdate changes it), so unlike
            // Position/Rotation/Velocity above, the cached e.Prim's ParentID is always current here.
            if (!ResolveSeatedTransform(e.Simulator, e.Update.Position, e.Update.Rotation, e.Prim.ParentID,
                out var worldPos, out var worldRot))
            {
                RequestUnresolvedSeat(e.Simulator, e.Prim.ParentID);
            }

            // From e.Update, not e.Prim -- the same race the comment above describes. The terse
            // update is where the plane actually moves: it rides every avatar movement packet,
            // which is how the viewer keeps foot placement current while walking.
            var supportPlane = ToSupportPlane(e.Update.CollisionPlane);

            AvatarUpdateReceived?.Invoke(this, new AvatarUpdateEvent(
                e.Simulator.Handle,
                e.Prim.LocalID,
                agentId,
                new System.Numerics.Vector3(worldPos.X, worldPos.Y, worldPos.Z),
                new System.Numerics.Quaternion(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
                firstName,
                lastName,
                isLocalAgent,
                e.Prim.Scale.Z,
                new System.Numerics.Vector3(e.Update.Velocity.X, e.Update.Velocity.Y, e.Update.Velocity.Z),
                e.TimeDilation / 65535.0f,
                e.Prim.ParentID,
                supportPlane));
            return;
        }

        // Position/Rotation/Velocity from e.Update, not e.Prim -- same race as the avatar branch
        // above (LibreMetaverse fires this event before stomping the shared cached Primitive).
        RaiseObjectUpdate(
            e.Simulator, e.Prim, isFullUpdate: false,
            positionOverride: new System.Numerics.Vector3(e.Update.Position.X, e.Update.Position.Y, e.Update.Position.Z),
            rotationOverride: new System.Numerics.Quaternion(e.Update.Rotation.X, e.Update.Rotation.Y, e.Update.Rotation.Z, e.Update.Rotation.W),
            velocityOverride: new System.Numerics.Vector3(e.Update.Velocity.X, e.Update.Velocity.Y, e.Update.Velocity.Z),
            timeDilation: e.TimeDilation / 65535.0f);
    }

    /// <summary>True for a <see cref="Primitive"/> LibreMetaverse manufactured rather than decoded
    /// — one that carries no construction data, no scale and no textures.
    ///
    /// <para>ImprovedTerseObjectUpdate carries a localID and a position, nothing else, so
    /// <c>ImprovedTerseObjectUpdateHandler</c> resolves the object with
    /// <c>GetPrimitive(sim, localID, UUID.Zero)</c>. That helper is get-<b>or-create</b>: on a cache
    /// miss it adds <c>new Primitive { LocalID, RegionHandle }</c> and assigns <c>ID = fullID</c> —
    /// which the terse caller passes as <c>UUID.Zero</c> (ObjectManager.cs:2686-2726, identical in
    /// v3.1.3 and v3.1.6 — this is long-standing behaviour, not something the 3.1.6 upgrade
    /// introduced). A cache miss is routine, not exotic: an object moving into view, crossing a
    /// region border, or whose full update was lost or simply has not arrived yet.</para>
    ///
    /// <para>Both signals mean the same thing and either one is conclusive. <c>ID</c> is
    /// <c>UUID.Zero</c> only on an object LMV created for a terse update — a full ObjectUpdate
    /// always supplies the real FullID. <c>PCode</c> is <c>None</c> (0) only on a default
    /// <c>ConstructionData</c>; every renderable object is Prim (9), Avatar (47), Grass (95),
    /// NewTree (111) or Tree (255).</para>
    ///
    /// <para>This is what produced <c>[PrimMeshFallback] profile=0 path=0 pathScale=(0,0) …</c>:
    /// the default struct reached <c>PrimMeshService</c>, which correctly refused to mesh a prim
    /// with a zero path scale, and the face came out as a placeholder cylinder. Pinned by
    /// <c>TersePlaceholderPrimitiveTests</c>.</para></summary>
    internal static bool IsUnpopulatedPrimitive(Primitive prim)
        => prim.ID == LibreMetaverse.UUID.Zero || prim.PrimData.PCode == LibreMetaverse.PCode.None;

    private int _unpopulatedPrimCount;

    /// <summary>Reports the first dropped update in full and then every thousandth. A handful over
    /// a session is the ordinary race and needs no attention; a stream of them would mean full
    /// updates are not arriving at all, which is a different bug and has to be visible.</summary>
    private void NoteUnpopulatedPrimitive(uint localId, bool isFullUpdate)
    {
        int n = System.Threading.Interlocked.Increment(ref _unpopulatedPrimCount);
        if (n != 1 && n % 1000 != 0) return;

        Console.Error.WriteLine(
            $"[ObjectUpdate] dropped {(isFullUpdate ? "FULL" : "terse")} update for localId={localId}: " +
            $"LibreMetaverse had no decoded object for it and manufactured an empty one " +
            $"(no shape/scale/textures). Waiting for the full ObjectUpdate. Count so far: {n}");
    }

    /// <summary>Builds and raises an ObjectUpdateEvent from a LibreMetaverse Primitive.
    /// <paramref name="positionOverride"/>, <paramref name="rotationOverride"/> and
    /// <paramref name="velocityOverride"/>, when set, are used instead of the same-named field on
    /// <paramref name="prim"/> -- required for a terse-sourced call (see OnTerseObjectUpdate):
    /// LibreMetaverse's ImprovedTerseObjectUpdateHandler fires its event via
    /// ThreadPool.QueueUserWorkItem BEFORE writing the decoded values onto the shared, cached
    /// Primitive ("fire the pre-emptive notice before we stomp the object" --
    /// ObjectManager.PacketHandlers.cs ~line 619), so reading prim.Position/Rotation/Velocity
    /// directly races that later write (confirmed root cause of the identical avatar-side bug fixed
    /// in OnTerseObjectUpdate's avatar branch). The full ObjectUpdate path has no such race --
    /// ObjectUpdateHandler stomps the Primitive synchronously before queuing its event (same file,
    /// ~line 371-385) -- so OnObjectUpdate's call leaves these null and reads straight off prim.
    /// Every other field (mesh, texture, flags, ...) always reads off prim regardless: terse updates
    /// don't carry them on the wire at all, so prim already holds the last full update's values --
    /// but ONLY once a full update has actually populated it. See
    /// <see cref="IsUnpopulatedPrimitive"/> for the case where it has not.</summary>
    private void RaiseObjectUpdate(
        LibreMetaverse.Simulator simulator, Primitive prim, bool isFullUpdate,
        System.Numerics.Vector3? positionOverride = null,
        System.Numerics.Quaternion? rotationOverride = null,
        System.Numerics.Vector3? velocityOverride = null,
        float timeDilation = 1f)
    {
        NoteObjectAnswered(simulator.Handle, prim.LocalID); // FEAT-NET-04: a sign of life, for the cache check

        // An object LibreMetaverse invented to answer a terse update carries no data at all --
        // no shape, no scale, no textures. Publishing it would either create a world entity out
        // of nothing or overwrite a good one with defaults. Drop it; the sim's full ObjectUpdate
        // for the same localID follows and populates the very object LMV just cached.
        if (IsUnpopulatedPrimitive(prim))
        {
            NoteUnpopulatedPrimitive(prim.LocalID, isFullUpdate);
            return;
        }

        // BUG-NET-03: neighbor sims (MultipleSims) replicate worn attachments as child-agent
        // copies with foreign LocalIds and a parent avatar we deliberately don't track (see
        // OnAvatarUpdate). Passing the local agent's copies through churned the skeleton
        // (ObjectDisposedException from AvatarRenderer.UpdateAttachment); other residents'
        // neighbor attachments are just orphans. World objects from neighbors are the whole point
        // of MultipleSims, so drop only attachment-flagged prims from a non-current sim.
        if (simulator != _client.Network.CurrentSim
            && prim.PrimData.AttachmentPoint != LibreMetaverse.AttachmentPoint.Default)
            return;

        var resolvedPosition = positionOverride ?? new System.Numerics.Vector3(prim.Position.X, prim.Position.Y, prim.Position.Z);
        var resolvedRotation = rotationOverride ?? new System.Numerics.Quaternion(prim.Rotation.X, prim.Rotation.Y, prim.Rotation.Z, prim.Rotation.W);
        var resolvedVelocity = velocityOverride ?? new System.Numerics.Vector3(prim.Velocity.X, prim.Velocity.Y, prim.Velocity.Z);

        bool isMesh = false;
        Guid meshId = Guid.Empty;
        bool isSculpt = false;
        Guid sculptId = Guid.Empty;
        byte sculptType = 0;

        if (prim.Sculpt != null && prim.Sculpt.SculptTexture != LibreMetaverse.UUID.Zero)
        {
            if (prim.Sculpt.Type == LibreMetaverse.SculptType.Mesh)
            {
                isMesh = true;
                meshId = prim.Sculpt.SculptTexture.Guid;
            }
            else
            {
                // ANY sculpt block with a map is a sculpt, INCLUDING stitching type None (0).
                //
                // This used to require `Type != None`, which silently demoted such a prim to its
                // underlying profile/path curve -- and since sculpties keep whatever base shape
                // they were built from, that came out as a smooth torus or sphere sitting where
                // the real object should be. Measured on OSGrid, The Dangazi Forest 2026-08-23: a
                // reef rock at <163.76, 197.57, 18.41> rendered here as a featureless 26x7x71
                // ellipse, while Firestorm's own build floater reported it as "Geformt"
                // (sculpted), stitching "Plane/None", Invert set -- i.e. a type byte of 0x40,
                // whose low three bits are zero.
                //
                // The viewer decides this on PRESENCE OF THE BLOCK, not on the stitching value:
                //     bool LLVOVolume::isSculpted() const
                //     { if (getSculptParams()) return true; return false; }   (llvovolume.cpp:3633)
                // and that predicate is what gates the sculpt texture fetch and the sculpted
                // rendering path. (LLVolumeParams::isSculpt(), which DOES test
                // `(mSculptType & MASK) != NONE`, is a different predicate used elsewhere -- it
                // was the one that made this look correct when the condition was written.)
                //
                // Stitching 0 then behaves exactly like PLANE when the map is wrapped:
                // sculptGenerateMapVertices special-cases only SPHERE (pole pinch), TORUS (T wrap)
                // and CYLINDER (S wrap), so anything else clamps to the map's edges
                // (llvolume.cpp:3072-3113). PrimMeshService.GenerateSculpt already matches that --
                // its `_ => plane` default covers 0 -- so passing the byte through is all that is
                // needed here.
                isSculpt = true;
                sculptId = prim.Sculpt.SculptTexture.Guid;
                // The SL sculpt-type byte packs the base type (low 3 bits) with two render flags:
                // Invert (0x40, render inside-out) and Mirror (0x80, mirror on X). LibreMetaverse's
                // prim.Sculpt.Type PROPERTY masks those flags off (& 7), so reading it alone silently
                // dropped them — a sculpt authored inverted/mirrored (very common for organic sculpts
                // like trees) was then built with the wrong winding/handedness: internally clean
                // geometry (no NaN, no spikes) but wrapped wrong, so it rendered "disintegrated".
                // Re-pack the flags so the whole byte reaches the mesher (SculptData.Type's setter
                // stores it verbatim, and its Invert/Mirror getters read the flag bits back).
                sculptType = (byte)((byte)prim.Sculpt.Type
                    | (prim.Sculpt.Invert ? (byte)LibreMetaverse.SculptType.Invert : 0)
                    | (prim.Sculpt.Mirror ? (byte)LibreMetaverse.SculptType.Mirror : 0));
            }
        }

        Guid textureId = Guid.Empty;
        Guid renderMaterialId = Guid.Empty;
        Guid legacyMaterialId = Guid.Empty;
        System.Numerics.Vector4 colorTint = new System.Numerics.Vector4(1, 1, 1, 1);

        var defaultFace = prim.Textures?.DefaultTexture;
        if (defaultFace != null)
        {
            textureId = defaultFace.TextureID.Guid;
            renderMaterialId = defaultFace.RenderMaterialID.Guid;
            legacyMaterialId = defaultFace.MaterialID.Guid;
            colorTint = new System.Numerics.Vector4(defaultFace.RGBA.R, defaultFace.RGBA.G, defaultFace.RGBA.B, defaultFace.RGBA.A);
        }

        // Per-face textures: each prim face can have its own texture/colour. Resolve each face
        // (its own entry, or the default) to a neutral FaceTexture indexed by face number.
        FaceTexture[]? faces = null;
        var faceArr = prim.Textures?.FaceTextures;
        // MVP3-3 Phase 1: whether ANY face's TextureEntry byte carries the MOAP "has media" bit
        // (TEM_MEDIA_MASK) right now, regardless of whether that changed on this update. Feeds
        // MaybeQueueMediaFetch below -- the actual per-face content is fetched separately.
        bool anyFaceHasMedia = defaultFace?.MediaFlags ?? false;
        if (faceArr != null && faceArr.Length > 0 && defaultFace != null)
        {
            // Each face carries TWO material ids: RenderMaterialID (glTF PBR) and MaterialID
            // (legacy Blinn-Phong -- normal + specular map). They are separate systems and a face
            // can have either, both or neither. Only the glTF one was read until FEAT-RENDER-04,
            // so a face whose detail lives in its normal/specular maps rendered as nothing but its
            // bare diffuse texture.
            faces = new FaceTexture[faceArr.Length];
            for (int i = 0; i < faceArr.Length; i++)
            {
                var f = faceArr[i] ?? defaultFace;
                if (f.MediaFlags) anyFaceHasMedia = true;
                faces[i] = new FaceTexture(
                    f.TextureID.Guid,
                    f.RenderMaterialID.Guid,
                    f.MaterialID.Guid,
                    new System.Numerics.Vector4(f.RGBA.R, f.RGBA.G, f.RGBA.B, f.RGBA.A),
                    f.RepeatU,
                    f.RepeatV,
                    f.OffsetU,
                    f.OffsetV,
                    f.Rotation,
                    (byte)f.TexMapType,
                    f.Fullbright,
                    // >> 6, NOT a plain cast. LibreMetaverse's Shininess enum holds the value
                    // still packed in the protocol byte's top two bits -- None 0, Low 0x40,
                    // Medium 0x80, High 0xC0 (TextureEntry.cs:83) -- while the viewer reads it as
                    // `mBump >> 6`, i.e. 0-3, which is what FaceTexture.Shiny is documented to
                    // carry. Casting straight across fed 64/128/192 into a 0-3 lookup, so every
                    // shiny level fell through to "none" and FEAT-RENDER-19 was inert on arrival:
                    // no highlight and no environment reflection on any face in the world.
                    (byte)((byte)f.Shiny >> 6),
                    f.MediaFlags);
            }
        }

        // Convert the prim's construction data to a neutral PrimShape so the asset layer can
        // regenerate real geometry without seeing a LibreMetaverse type.
        //
        // IMPORTANT: pd.profileCurve (raw field, lowercase) is a single packed byte carrying BOTH
        // the profile curve type (Circle/Square/Triangle/... in the low nibble, 0x00-0x05) AND the
        // hollow-cut's own shape (HoleType Same/Circle/Square/Triangle, pre-shifted into the high
        // nibble as 0x00/0x10/0x20/0x30 — see LibreMetaverse.Types.EnumsPrimitive). pd.ProfileCurve
        // (the PROPERTY, capital P) masks that byte down to just the low nibble
        // (`profileCurve & PROFILE_MASK`), silently discarding the hole-shape bits. Using the
        // property here (as this line previously did) meant every hollow prim's hole shape got
        // zeroed out end-to-end -- reconstructed as HoleType.Same regardless of what the creator
        // actually chose, which is only coincidentally correct when "Same" was already picked.
        // Passing the raw packed byte through lets PrimMeshService.Generate() assign it straight
        // back onto ConstructionData.profileCurve (also the raw field) and get BOTH nibbles right.
        var pd = prim.PrimData;
        var shape = new PrimShape(
            pd.profileCurve,
            (byte)pd.PathCurve,
            pd.PathBegin, pd.PathEnd,
            pd.PathScaleX, pd.PathScaleY,
            pd.PathShearX, pd.PathShearY,
            pd.PathTaperX, pd.PathTaperY,
            pd.PathTwist, pd.PathTwistBegin,
            pd.PathRadiusOffset, pd.PathSkew, pd.PathRevolutions,
            pd.ProfileBegin, pd.ProfileEnd, pd.ProfileHollow,
            (byte)pd.PCode);

        // llSetTextureAnim. LibreMetaverse copies the four wire bytes verbatim into
        // Primitive.TextureAnim without the viewer's unpack rules (signed face byte, non-smooth
        // size clamp), so the raw values go through TextureAnimation.FromWire rather than being
        // read off the struct field by field. Unlike Primitive.Light this one does NOT latch:
        // ObjectUpdateHandler reassigns prim.TextureAnim unconditionally on every full update, so
        // an animation switched off really does come back as ANIM_OFF here.
        SLNG.Core.TextureAnimation? textureAnim = null;
        if ((prim.TextureAnim.Flags & Primitive.TextureAnimMode.ANIM_ON) != 0)
        {
            textureAnim = SLNG.Core.TextureAnimation.FromWire(
                (byte)prim.TextureAnim.Flags,
                (byte)prim.TextureAnim.Face,
                (byte)prim.TextureAnim.SizeX,
                (byte)prim.TextureAnim.SizeY,
                prim.TextureAnim.Start,
                prim.TextureAnim.Length,
                prim.TextureAnim.Rate);
        }

        // Primitive.Light never resets itself when a light is disabled (see _lightObjects) -- if our
        // own raw scan positively saw this object's latest ExtraParams WITHOUT a Light block, trust
        // that over the stale Primitive.Light. BUG-NET-25: reported from a local copy and no longer
        // written back into the Primitive. The write-back was safe only while the latch could not
        // be stale; this event can be built just before the callback that writes the latch (see
        // OnRawObjectUpdatePacket), and a light switched back ON in that window was read as
        // "removed" and wiped from the shared object -- after which the corrective re-raise had
        // nothing left to report. Nothing else reads prim.Light.
        var light = EffectiveLight(simulator.Handle, prim.LocalID, prim.Light);
        bool lightEnabled = light.Intensity > 0f;

        // MVP3-3 Phase 1: like TextureAnim/Light above, the media doorbell only exists on a full
        // update -- ImprovedTerseObjectUpdate carries neither a TextureEntry nor a MediaURL.
        if (isFullUpdate) MaybeQueueMediaFetch(simulator, prim, anyFaceHasMedia);

        // BUG-NET-16 follow-up: THIS update might be the seat RequestUnresolvedSeat asked for.
        // Any avatar already known to be sitting on this prim was resolved (wrongly, at the
        // relative offset) from the ONE packet that raced it, and nothing else re-asks for a
        // seated, otherwise-motionless avatar -- so without this, the corrected position sits in
        // sim.ObjectsPrimitives now but never reaches World until she happens to move.
        ReapplySeatedAvatarsOn(simulator, prim.LocalID);

        ObjectUpdateReceived?.Invoke(this, new ObjectUpdateEvent(
            simulator.Handle,
            prim.LocalID,
            resolvedPosition,
            resolvedRotation,
            new System.Numerics.Vector3(prim.Scale.X, prim.Scale.Y, prim.Scale.Z),
            shape.ProfileCurve,
            isMesh,
            meshId,
            textureId,
            renderMaterialId,
            colorTint,
            defaultFace?.RepeatU ?? 1.0f,
            defaultFace?.RepeatV ?? 1.0f,
            defaultFace?.OffsetU ?? 0.0f,
            defaultFace?.OffsetV ?? 0.0f,
            defaultFace?.Rotation ?? 0.0f,
            prim.ParentID,
            (byte)prim.PrimData.AttachmentPoint,
            shape,
            isSculpt, sculptId, sculptType,
            faces,
            prim.ID.Guid,
            prim.Flags.HasFlag(PrimFlags.Physics),
            prim.Flags.HasFlag(PrimFlags.Temporary),
            prim.Flags.HasFlag(PrimFlags.Phantom),
            prim.Flags.HasFlag(PrimFlags.CastShadows),
            // Light is an ExtraParams block, not a PrimFlags bit -- LibreMetaverse's own
            // convention (mirrored in SetObjectLight below) is Intensity>0 means "the block is
            // active"; Primitive.Light is never null (ObjectManager always constructs a default).
            // `light` (taken above) is prim.Light corrected for the never-resets-on-disable bug,
            // so lightEnabled and the fields below are one consistent snapshot.
            lightEnabled,
            new System.Numerics.Vector3(light.Color.R, light.Color.G, light.Color.B),
            light.Intensity,
            light.Radius,
            light.Falloff,
            // OpenMetaverse.Material and SLNG.Core.PrimMaterial share the same 0-6 numeric values
            // by design (see PrimMaterial's doc comment) -- the enum's rare/vestigial value 7
            // ("Light", unrelated to the point-light feature, not user-selectable in the real SL
            // viewer either) has no matching PrimMaterial member; clamp it to Wood rather than
            // let an unnamed enum value reach the UI.
            (byte)prim.PrimData.Material <= 6 ? (SLNG.Core.PrimMaterial)(byte)prim.PrimData.Material : SLNG.Core.PrimMaterial.Wood,
            (byte)prim.ClickAction,
            isFullUpdate,
            resolvedVelocity,
            timeDilation,
            // The DEFAULT face's texgen. LibreMetaverse's MappingType is already the raw SL
            // value (Default=0, Planar=2, ...), and FaceTexture.TexGen stores it unconverted,
            // so this is a straight cast -- see FaceTexture.TexGen on why it is NOT 1.
            defaultFace != null ? (byte)defaultFace.TexMapType : FaceTexture.TexGenDefault,
            textureAnim,
            legacyMaterialId,
            ParticleSystemConverter.FromWire(prim.ParticleSys),
            // The DEFAULT face's fullbright flag -- see FaceTexture.Fullbright. Per-face entries
            // in `faces` carry their own; this is for prims that send no per-face entries.
            defaultFace?.Fullbright ?? false,
            ReflectionProbeLatched(simulator.Handle, prim.LocalID),
            prim.Flags.HasFlag(PrimFlags.Touch),
            prim.Flags.HasFlag(PrimFlags.Money),
            // FEAT-SEC-04: the sim's per-agent permission answer, already computed for us. These
            // bits sat in prim.Flags all along, next to the four read above, and were dropped --
            // which is why the edit window could only report what the OWNER may do and had to
            // label it "unknown whether these are yours". Bit values verified against the
            // viewer's own object_flags.h.
            prim.Flags.HasFlag(PrimFlags.ObjectModify),
            prim.Flags.HasFlag(PrimFlags.ObjectMove),
            prim.Flags.HasFlag(PrimFlags.ObjectCopy),
            prim.Flags.HasFlag(PrimFlags.ObjectTransfer),
            prim.Flags.HasFlag(PrimFlags.ObjectYouOwner),
            // FEAT-ANIMESH-01: read from the latch, last, right before the event goes out -- see
            // OnRawObjectUpdatePacket for why this can race the callback that writes it and what
            // makes that harmless.
            IsAnimatedMeshLatched(simulator.Handle, prim.LocalID)));
    }

    /// <summary>MVP3-3 Phase 1: notices the MOAP "doorbell" (<paramref name="anyFaceHasMedia"/>
    /// plus <c>prim.MediaURL</c>'s <c>x-mv:</c> version string) and fires the <c>ObjectMedia</c>
    /// GET ourselves -- LibreMetaverse raises no event for either half of this, so nothing else
    /// will. Gated on the version string actually changing (see
    /// <see cref="_lastMediaVersionByLocalId"/>, committed only on SUCCESS -- see
    /// <see cref="FetchAndPublishObjectMediaAsync"/>'s doc comment for why) and de-duplicated
    /// against an already in-flight fetch for the same version
    /// (<see cref="_inFlightMediaFetchByLocalId"/>). Fire-and-forget, like <c>ClickObjectAsync</c>
    /// callers elsewhere in this file -- the result arrives later via <see cref="ObjectMediaReceived"/>.</summary>
    private void MaybeQueueMediaFetch(LibreMetaverse.Simulator simulator, Primitive prim, bool anyFaceHasMedia)
    {
        if (!anyFaceHasMedia) return;

        string? version = prim.MediaURL;
        if (!SLNG.Core.MediaVersionString.IsMediaVersion(version)) return;
        if (_lastMediaVersionByLocalId.TryGetValue(prim.LocalID, out var applied) && applied == version) return;
        if (_inFlightMediaFetchByLocalId.TryGetValue(prim.LocalID, out var inFlight) && inFlight == version) return;
        _inFlightMediaFetchByLocalId[prim.LocalID] = version!;

        _ = FetchAndPublishObjectMediaAsync(prim.ID.Guid, simulator.Handle, prim.LocalID, version!);
    }

    /// <summary>Per-LocalID version currently being fetched, so a burst of ObjectUpdates for the
    /// same still-unresolved version (the fetch can take a while when it has to wait out the
    /// caps race below) doesn't queue the same GET twice. Cleared, unconditionally, once the
    /// fetch for that call finishes -- a rare race where a newer fetch's marker gets cleared by
    /// an older one completing just queues one harmless extra GET, not a correctness bug.</summary>
    private readonly ConcurrentDictionary<uint, string> _inFlightMediaFetchByLocalId = new();

    /// <summary>How long to wait, and how many times, for the <c>ObjectMedia</c> capability to
    /// resolve before giving up on one fetch. Same magnitude as the environment code's own
    /// login-race workarounds elsewhere in this file (a few seconds), not a long background
    /// poll -- <see cref="RetryPendingMediaFetches"/> is the real fallback once
    /// <c>RegionCapabilitiesReady</c> fires, this just covers a fetch that started fractionally
    /// before that point.</summary>
    private const int MediaCapWaitRetries = 5;

    private static readonly TimeSpan MediaCapWaitDelay = TimeSpan.FromSeconds(1);

    /// <summary>Fetches and publishes one prim's media, tolerating the SAME capability-seeding
    /// race documented on <see cref="RegionHasServerSideBaking"/>: <c>CapabilityURI("ObjectMedia")</c>
    /// can read null for a few seconds right after region entry even on a region that has the
    /// cap, because the caps seed isn't necessarily resolved yet. The very first ObjectUpdate for
    /// a region -- exactly the one most likely to carry an already-in-view MOAP prim -- can race
    /// this. Measured live on Agni 2026-09-17: LibreMetaverse's own
    /// <c>ObjectManager.RequestObjectMediaAsync</c> logged "ObjectMedia capability not available"
    /// and returned failure on that very first update.
    ///
    /// <b>The original Phase 1 cut got this wrong</b>: it committed the version to
    /// <see cref="_lastMediaVersionByLocalId"/> BEFORE awaiting the fetch, so a transient
    /// capability-race failure permanently marked that version "already handled" -- the media
    /// was then silently never fetched for the rest of the session, because nothing else would
    /// ever ask again for a prim whose ObjectUpdate does not repeat. Fixed two ways: this method
    /// only commits the version on actual success, and it waits out a short capability-seeding
    /// window itself rather than failing on the first miss.</summary>
    private async Task FetchAndPublishObjectMediaAsync(Guid objectId, ulong regionHandle, uint localId, string version)
    {
        await _mediaFetchSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var sim = _client.Network.CurrentSim;
            if (sim == null || sim.Handle != regionHandle) return;

            Uri? capUri = sim.Caps?.CapabilityURI("ObjectMedia");
            for (int attempt = 0; capUri == null && attempt < MediaCapWaitRetries; attempt++)
            {
                await Task.Delay(MediaCapWaitDelay).ConfigureAwait(false);
                if (!_client.Network.Connected || _client.Network.CurrentSim != sim) return;
                capUri = sim.Caps?.CapabilityURI("ObjectMedia");
            }
            if (capUri == null)
            {
                // Leaves _lastMediaVersionByLocalId uncommitted -- RetryPendingMediaFetches
                // sweeps every known primitive again once RegionCapabilitiesReady actually fires.
                Console.Error.WriteLine(
                    $"[Media] object {localId}: ObjectMedia capability did not appear on {sim.Name} " +
                    $"after {MediaCapWaitRetries}s -- will retry once region capabilities are confirmed ready");
                return;
            }

            var result = await RequestObjectMediaAsync(objectId).ConfigureAwait(false);
            if (result == null) return; // also leaves the gate uncommitted; a real (non-race) failure just self-heals on the next natural ObjectUpdate

            var (fetchedVersion, faces) = result.Value;
            _lastMediaVersionByLocalId[localId] = fetchedVersion;

            int withMedia = 0;
            foreach (var f in faces) if (f != null) withMedia++;
            Console.Error.WriteLine(
                $"[Media] object {localId} ({objectId.ToString()[..8]}) version={fetchedVersion} faces={withMedia}/{faces.Length}");

            ObjectMediaReceived?.Invoke(this, new SLNG.Core.ObjectMediaEvent(regionHandle, localId, objectId, fetchedVersion, faces));
        }
        finally
        {
            _inFlightMediaFetchByLocalId.TryRemove(localId, out _);
            _mediaFetchSemaphore.Release();
        }
    }

    /// <summary>Re-evaluates every primitive LibreMetaverse already knows about for this sim
    /// against the MOAP doorbell, called once the region's capability handshake is confirmed done
    /// (see <see cref="OnEventQueueRunning"/> / <see cref="RegionCapabilitiesReady"/>). Exists
    /// because the doorbell otherwise only fires from <see cref="RaiseObjectUpdate"/>'s per-packet
    /// path -- a prim whose ObjectUpdate raced the caps handshake and does not update again has no
    /// other trigger to ever be asked about. Reads only LibreMetaverse's own per-sim cache and
    /// never touches <c>World</c> directly, same as every other network-thread callback here.</summary>
    private void RetryPendingMediaFetches(LibreMetaverse.Simulator sim)
    {
        if (sim.Caps?.CapabilityURI("ObjectMedia") == null) return;

        foreach (var prim in sim.ObjectsPrimitives.Values)
        {
            if (prim?.Textures == null) continue;

            bool anyFaceHasMedia = prim.Textures.DefaultTexture?.MediaFlags ?? false;
            var faceArr = prim.Textures.FaceTextures;
            if (faceArr != null)
            {
                foreach (var f in faceArr)
                {
                    if (f != null && f.MediaFlags) { anyFaceHasMedia = true; break; }
                }
            }

            if (anyFaceHasMedia) MaybeQueueMediaFetch(sim, prim, anyFaceHasMedia);
        }
    }

    /// <summary>MVP3-3 Phase 1: fetches one prim's per-face MOAP media from the region's
    /// <c>ObjectMedia</c> capability, converted at the boundary -- no LibreMetaverse
    /// <c>MediaEntry</c>/<c>UUID</c> crosses this method's return. Returns null when the region has
    /// no cap, the fetch failed, or (OpenSim MoapModule.cs:286-346) the sim answered with a valid
    /// empty response. <paramref name="objectId"/> is the persistent object UUID -- unlike most of
    /// this file's other per-object calls, the <c>ObjectMedia</c> cap takes the full id, not the
    /// scene-local one (verified against llmediadataclient.cpp:872-879).</summary>
    public async Task<(string Version, SLNG.Core.MediaFace?[] Faces)?> RequestObjectMediaAsync(
        Guid objectId, CancellationToken cancellationToken = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return null;

        var (success, version, faceMedia) = await _client.Objects
            .RequestObjectMediaAsync(new LibreMetaverse.UUID(objectId), sim, cancellationToken)
            .ConfigureAwait(false);
        if (!success) return null;

        if (faceMedia == null) return (version, Array.Empty<SLNG.Core.MediaFace?>());

        var faces = new SLNG.Core.MediaFace?[faceMedia.Length];
        for (int i = 0; i < faceMedia.Length; i++) faces[i] = ToMediaFace(faceMedia[i]);
        return (version, faces);
    }

    /// <summary>Converts one wire <c>MediaEntry</c> (null = this face carries no media, the
    /// GET response's positional-array convention, llmediadataclient.cpp:942-964) to the neutral
    /// <see cref="SLNG.Core.MediaFace"/> DTO.</summary>
    internal static SLNG.Core.MediaFace? ToMediaFace(LibreMetaverse.MediaEntry? entry)
    {
        if (entry == null) return null;
        return new SLNG.Core.MediaFace(
            entry.HomeURL ?? "",
            entry.CurrentURL ?? "",
            entry.AutoPlay,
            entry.AutoLoop,
            entry.AutoScale,
            entry.AutoZoom,
            entry.InteractOnFirstClick,
            (SLNG.Core.MediaControlStyle)(byte)entry.Controls,
            entry.Width,
            entry.Height,
            (SLNG.Core.MediaPermission)(byte)entry.ControlPermissions,
            (SLNG.Core.MediaPermission)(byte)entry.InteractPermissions,
            entry.EnableWhiteList,
            entry.WhiteList ?? Array.Empty<string>(),
            entry.EnableAlternativeImage);
    }

    private void OnKillObject(object? sender, KillObjectEventArgs e)
    {
        CheckAndStopMotionOnKill(e.Simulator, e.ObjectLocalID);
        ForgetObjectLatches(e.Simulator.Handle, e.ObjectLocalID);
        ObjectRemovedReceived?.Invoke(this, new ObjectRemovedEvent(e.Simulator.Handle, e.ObjectLocalID));
    }

    private void OnKillObjects(object? sender, KillObjectsEventArgs e)
    {
        foreach (var localId in e.ObjectLocalIDs)
        {
            CheckAndStopMotionOnKill(e.Simulator, localId);
            ForgetObjectLatches(e.Simulator.Handle, localId);
            ObjectRemovedReceived?.Invoke(this, new ObjectRemovedEvent(e.Simulator.Handle, localId));
        }
    }

    private void CheckAndStopMotionOnKill(LibreMetaverse.Simulator sim, uint localId)
    {
        if (sim?.ObjectsPrimitives == null) return;
        if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim != null)
        {
            bool isSource = false;
            lock (_selfAnimationSources)
            {
                isSource = _selfAnimationSources.ContainsValue(prim.ID.Guid);
            }
            if (isSource || prim.ParentID == _client.Self.LocalID)
            {
                var candidateSourceIds = new HashSet<Guid> { prim.ID.Guid };
                var attId = ExtractAttachItemId(prim);
                if (attId != Guid.Empty) candidateSourceIds.Add(attId);
                foreach (var child in sim.ObjectsPrimitives.Values)
                {
                    if (child != null && child.ParentID == prim.LocalID)
                    {
                        candidateSourceIds.Add(child.ID.Guid);
                    }
                }
                StopMotionsFromSources(candidateSourceIds);
            }
        }
    }

    /// <summary>
    /// The simulator a message about an object must be addressed to: the region the OBJECT is
    /// in, never the agent's own. Null when that region is not connected, and the caller must
    /// then send nothing (BUG-NET-19).
    /// </summary>
    /// <remarks>
    /// A local id is unique only WITHIN one simulator, and SLNG draws neighbouring regions
    /// (BUG-NET-03), so at a border the object under the cursor routinely belongs to a different
    /// simulator than the agent. Sending its id to <c>CurrentSim</c> has two outcomes and no
    /// third: that simulator drops it, or -- worse -- it resolves the same id to a DIFFERENT
    /// object and acts on that one. Both are silent, which is how this survived: "nothing
    /// happened" reads as an object that simply does not react.
    ///
    /// <para>The reference viewer addresses the object's region explicitly and never the
    /// agent's: <c>send_ObjectGrab_message</c> ends
    /// <c>msg-&gt;sendMessage(object-&gt;getRegion()-&gt;getHost())</c> (lltoolgrab.cpp:1174), and
    /// <c>handleHoverNonPhysical</c>'s ObjectGrabUpdate does the same.</para>
    ///
    /// <para>Not connected means not sent. A message to the wrong simulator is worse than no
    /// message, because only one of the two can act on the wrong object.</para>
    /// </remarks>
    private Simulator? SimulatorFor(ulong regionHandle, string what, uint localId)
    {
        if (!_client.Network.Connected) return null;

        var current = _client.Network.CurrentSim;
        // A caller that genuinely means "wherever I am" -- an own HUD, an agent-level action --
        // passes 0 rather than inventing a handle.
        if (regionHandle == 0) return current;
        if (current != null && current.Handle == regionHandle) return current;

        var sim = _client.Network.FindSimulator(regionHandle);
        if (sim == null)
        {
            Console.Error.WriteLine(
                $"[Region] {what} for {localId}: object is in region {regionHandle}, which is not " +
                $"connected -- nothing sent (addressing {current?.Name ?? "no region"} instead " +
                "could act on a different object with the same local id)");
            return null;
        }

        if (SLNG.Core.Diag.Verbose)
            Console.WriteLine(
                $"[Region] {what} for {localId}: object is in {sim.Name}, not the current region " +
                $"{current?.Name ?? "?"} -- addressed {sim.Name}");
        return sim;
    }

    /// <summary>Touches (clicks) an object — the SL grab/de-grab pair
    /// <see cref="ObjectManager.ClickObjectAsync"/> sends 50ms apart, which is what fires
    /// touch_start/touch_end on any touch script the object carries. <paramref name="localId"/>
    /// is the SL scene-local id (<c>Entity.LocalId</c>), not the persistent asset/object UUID.
    /// Surface hit details are optional (all-zero if omitted, like LibreMetaverse's own
    /// no-detail overload) — a HUD button script rarely inspects them, but pass real ones (face
    /// index, hit position/normal) when available for scripts that do.</summary>
    /// <param name="regionHandle">The region the object is in -- <c>Entity.RegionHandle</c>, not
    /// the agent's. 0 means the agent's own region, for an own HUD or attachment.</param>
    public async System.Threading.Tasks.Task ClickObjectAsync(
        ulong regionHandle,
        uint localId,
        int faceIndex = 0,
        System.Numerics.Vector3 position = default,
        System.Numerics.Vector3 normal = default,
        System.Numerics.Vector3 uvCoord = default,
        System.Numerics.Vector3 stCoord = default,
        System.Numerics.Vector3 binormal = default)
    {
        var sim = SimulatorFor(regionHandle, "touch", localId);
        if (sim == null)
        {
            if (SLNG.Core.Diag.Verbose)
                Console.WriteLine($"[Touch] {localId}: no simulator to send to -- nothing sent");
            return;
        }

        // Callers fire this and forget it (a touch has no result to await), so an exception in
        // here would vanish into a discarded Task and the click would look like it was sent.
        // That is the one gap the caller's own log line cannot close: it records the CALL, not
        // the packet.
        try
        {
            await _client.Objects.ClickObjectAsync(
                sim, localId,
                ToOmv(uvCoord), ToOmv(stCoord), faceIndex,
                ToOmv(position), ToOmv(normal), ToOmv(binormal));

            if (SLNG.Core.Diag.Verbose)
                Console.WriteLine($"[Touch] grab+release sent for {localId} to {sim.Name}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Touch] {localId}: send failed -- {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void SelectObject(ulong regionHandle, uint localId)
    {
        var sim = SimulatorFor(regionHandle, "select", localId);
        if (sim == null) return;
        _client.Objects.SelectObject(sim, localId);
    }

    public void DeselectObject(ulong regionHandle, uint localId)
    {
        var sim = SimulatorFor(regionHandle, "deselect", localId);
        if (sim == null) return;
        _client.Objects.DeselectObject(sim, localId);
    }

    /// <summary>Selects every prim of an object at once -- what the reference viewer does for any
    /// click that is not "edit linked parts".</summary>
    /// <remarks>
    /// LLSelectMgr::selectObjectAndFamily walks up to the linkset's root, collects
    /// <c>addThisAndNonJointChildren</c>, and sends ONE ObjectSelect naming all of them
    /// (llselectmgr.cpp:521). It is not a formality: the simulator keeps a per-agent selection,
    /// and an edit that is meant to act on the whole linkset acts on what that selection holds.
    /// Selecting the root alone left every other prim unselected, and a linked-set resize then
    /// visibly changed the root prim and nothing else.
    /// </remarks>
    public void SelectObjects(ulong regionHandle, System.Collections.Generic.IReadOnlyList<uint> localIds)
    {
        if (localIds.Count == 0) return;
        if (localIds.Count == 1) { SelectObject(regionHandle, localIds[0]); return; }

        var sim = SimulatorFor(regionHandle, "select", localIds[0]);
        if (sim == null) return;

        var ids = new uint[localIds.Count];
        for (int i = 0; i < localIds.Count; i++) ids[i] = localIds[i];
        _client.Objects.SelectObjects(sim, ids);
    }

    public void DeselectObjects(ulong regionHandle, System.Collections.Generic.IReadOnlyList<uint> localIds)
    {
        if (localIds.Count == 0) return;
        if (localIds.Count == 1) { DeselectObject(regionHandle, localIds[0]); return; }

        var sim = SimulatorFor(regionHandle, "deselect", localIds[0]);
        if (sim == null) return;

        var ids = new uint[localIds.Count];
        for (int i = 0; i < localIds.Count; i++) ids[i] = localIds[i];
        _client.Objects.DeselectObjects(sim, ids);
    }

    /// <summary>FEAT-UI-05: links standalone objects into one linkset.</summary>
    /// <param name="rootLocalId">The prim that becomes the linkset's root -- in the viewer, the
    /// object selected LAST. It keeps its position and rotation; every other prim's transform
    /// becomes an offset from it.</param>
    /// <param name="childLocalIds">Everything else in the selection.</param>
    /// <remarks>
    /// The root goes FIRST in the packet. LibreMetaverse's own doc comment on <c>LinkPrims</c>
    /// says the opposite ("the last object in the array will be the root"), and it is wrong:
    /// OpenSim's <c>LLClientView.HandleObjectLink</c> reads <c>ObjectData[0]</c> as the parent
    /// and every later entry as a child. LMV's comments have already been wrong once on this
    /// path (see <see cref="UpdateObjectTransform"/>), so this is taken from the server that
    /// has to act on it.
    /// </remarks>
    public void LinkObjects(uint rootLocalId, IReadOnlyList<uint> childLocalIds)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        if (childLocalIds.Count == 0) return;

        var ids = new System.Collections.Generic.List<uint>(childLocalIds.Count + 1) { rootLocalId };
        foreach (uint id in childLocalIds)
        {
            // A duplicate would make the sim try to parent the root to itself.
            if (id != rootLocalId) ids.Add(id);
        }
        if (ids.Count < 2) return;

        _client.Objects.LinkPrims(_client.Network.CurrentSim, ids);
    }

    /// <summary>FEAT-UI-05: splits a linkset back into standalone prims.</summary>
    /// <param name="localIds">The prims to detach. Passing the root's id detaches the whole
    /// linkset, which is what the viewer's Unlink button does with a whole selection.</param>
    public void UnlinkObjects(IReadOnlyList<uint> localIds)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        if (localIds.Count == 0) return;

        _client.Objects.DelinkPrims(_client.Network.CurrentSim,
            new System.Collections.Generic.List<uint>(localIds));
    }

    public void RequestObjectProperties(Guid objectId)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.RequestObjectPropertiesFamily(_client.Network.CurrentSim, new LibreMetaverse.UUID(objectId));
    }

    /// <param name="singlePrim">Move/turn this ONE prim inside its linkset instead of the whole
    /// linkset -- the reference viewer's "edit linked parts" mode. It decides two things at once,
    /// and they have to agree:
    ///
    /// <para>The flag on the wire. LibreMetaverse's three-argument <c>SetPosition</c> sends
    /// <c>UpdateType.Position | UpdateType.Linked</c>, and <c>SetRotation</c> an
    /// <c>ObjectRotation</c> packet; both mean "the whole group". The simulator then reads the
    /// vector as the GROUP's absolute position (<c>SceneGraph.UpdatePrimGroupPosition</c>).
    /// Without <c>Linked</c> it takes the single-prim route instead:
    /// <c>SceneObjectGroup.UpdateSinglePosition</c>, which is <c>UpdateRootPosition</c> for the
    /// root part and <c>part.UpdateOffSet</c> for any other.</para>
    ///
    /// <para>And therefore the FRAME the caller has to pass: a group update and a single-root
    /// update both want the root's absolute region position, but a single update for a child
    /// wants its offset from the root. Sending a child's offset with <c>Linked</c> still set
    /// teleports the entire linkset to that offset read as a region coordinate -- from the
    /// viewer's point of view the object simply vanishes, which is how this was found.</para>
    /// </param>
    public void UpdateObjectTransform(uint localId, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale, bool singlePrim = false, TransformFields fields = TransformFields.All, bool uniformScale = false)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;

        if (fields == TransformFields.None) return;

        // ONE MultipleObjectUpdate carrying everything that changed, which is what the reference
        // viewer sends (LLSelectMgr::packMultipleUpdate: position, then rotation, then scale,
        // each 12 bytes, in that order, and only the ones the type mask names).
        //
        // LibreMetaverse offers SetPosition / SetRotation / SetScale instead, one packet each.
        // For a world prim that is equivalent. For an ATTACHMENT it is not: a stretch then
        // reaches the simulator as a scale update with no position beside it, and Second Life
        // answers that by reporting the attachment back at a REGION coordinate -- which a viewer
        // reads as an offset from the attach point, so the item lands a hundred metres away.
        // Reported in-world as "I stretch it and it is gone". No real viewer sends the pieces
        // separately, so no real viewer sees it.
        //
        // The three helpers also disagreed about the linked flag: SetScale's third argument is
        // childOnly, not linked, and was passed positionally as `true`, so the scale always went
        // out as a single-prim update whatever the caller asked for.
        var (type, data) = TransformUpdatePayload.Build(fields, singlePrim, position, rotation, scale, uniformScale);

        var update = new LibreMetaverse.Packets.MultipleObjectUpdatePacket
        {
            AgentData =
            {
                AgentID = _client.Self.AgentID,
                SessionID = _client.Self.SessionID,
            },
            ObjectData = new[]
            {
                new LibreMetaverse.Packets.MultipleObjectUpdatePacket.ObjectDataBlock
                {
                    ObjectLocalID = localId,
                    Type = type,
                    Data = data,
                },
            },
        };
        _client.Network.SendPacket(update, _client.Network.CurrentSim);
    }

    /// <summary>Sets Physical/Temporary/Phantom/CastShadows together in one ObjectFlagUpdate --
    /// the wire message replaces all four at once, so callers must pass the object's full
    /// current state, not just the one flag being toggled.
    ///
    /// Must send OpenSim's PhysShapeType.invalid (255) as the extra-physics shape type, NOT
    /// LibreMetaverse's default PhysicsShapeType.Prim (0, a real value on the wire). OpenSim's
    /// SceneGraph.UpdatePrimFlags branches on this byte: anything other than invalid is treated
    /// as "also apply extra physics data" (density/friction/shape), and THAT branch never calls
    /// group.UpdateFlags(...) at all -- so a Prim-shape request silently never touches
    /// Physical/Temporary/Phantom no matter how good the object's permissions are. LMV's
    /// simplified 6-arg SetFlags() overload hardcodes Prim, which is why every flag toggle from
    /// this client was being swallowed (confirmed: a full-perm object the same agent already
    /// edits fine in Firestorm still silently rejected our ObjectFlagUpdate).</summary>
    /// <summary>Flags AND physics-shape/material data share one wire message
    /// (ObjectFlagUpdate) -- every call resends both, so the caller must pass the object's
    /// current known physics values (not just the flag being changed), the same way
    /// ObjectEditWindow.SendObjectFlags already threads through its other unchanged flags.
    /// physicsShapeType=(byte)255/invalid is the sentinel meaning "leave extra-physics data
    /// alone" (see opensim-objectflagupdate-physshapetype memory) -- pass a real
    /// PrimPhysicsShapeType value only when the caller actually knows/wants to set one.</summary>
    public void SetObjectFlags(uint localId, bool physical, bool temporary, bool phantom, bool castsShadows,
        PrimPhysicsShapeType physicsShapeType, float density, float friction, float restitution, float gravityMultiplier)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SetFlags(_client.Network.CurrentSim, localId, physical, temporary, phantom, castsShadows,
            (PhysicsShapeType)(byte)physicsShapeType, density, friction, restitution, gravityMultiplier);
    }

    /// <summary>SL's "Locked" build-floater checkbox isn't a wire flag -- it's expressed by
    /// removing/restoring the owner's Move permission.</summary>
    public void SetObjectLocked(uint localId, bool locked)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SetPermissions(_client.Network.CurrentSim, new List<uint> { localId }, PermissionWho.Owner, PermissionMask.Move, !locked);
    }

    /// <summary>SL's point-light ("Light") prim property -- an ExtraParams block, not a
    /// PrimFlags bit (see ObjectManager.SetLight). Disabling sends the block with Intensity 0
    /// rather than omitting it, matching LibreMetaverse's own enabled/disabled convention.</summary>
    public void SetObjectLight(uint localId, bool enabled, System.Numerics.Vector3 color, float intensity, float radius, float falloff)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        var light = new Primitive.LightData
        {
            Color = new Color4(color.X, color.Y, color.Z, 1f),
            Intensity = enabled ? intensity : 0f,
            Radius = radius,
            Falloff = falloff,
            Cutoff = 0f
        };
        _client.Objects.SetLight(_client.Network.CurrentSim, localId, light);
    }

    /// <summary>Classic material (Stone/Metal/.../Rubber) -- collision sound/friction. Sends a
    /// dedicated ObjectMaterial packet, unrelated to the flags/light messages above.</summary>
    public void SetObjectMaterial(uint localId, SLNG.Core.PrimMaterial material)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SetMaterial(_client.Network.CurrentSim, localId, (Material)(byte)material);
    }

    public void SetObjectClickAction(uint localId, byte clickAction)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        var packet = new ObjectClickActionPacket
        {
            AgentData =
            {
                AgentID = _client.Self.AgentID,
                SessionID = _client.Self.SessionID
            },
            ObjectData = new ObjectClickActionPacket.ObjectDataBlock[]
            {
                new()
                {
                    ObjectLocalID = localId,
                    ClickAction = clickAction
                }
            }
        };
        _client.Network.CurrentSim.SendPacket(packet);
    }

    /// <summary>Rezzes a new basic-shape prim at the given region-local position. The sim only
    /// treats this as an approximate placement (see ObjectManager.AddPrim's remarks) -- the
    /// object streams back in shortly after via the normal ObjectUpdate path, same as any other
    /// object.</summary>
    public void CreatePrim(SLNG.Core.BasicPrimType type, System.Numerics.Vector3 position)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;

        var lmvType = type switch
        {
            SLNG.Core.BasicPrimType.Box => PrimType.Box,
            SLNG.Core.BasicPrimType.Cylinder => PrimType.Cylinder,
            SLNG.Core.BasicPrimType.Prism => PrimType.Prism,
            SLNG.Core.BasicPrimType.Sphere => PrimType.Sphere,
            SLNG.Core.BasicPrimType.Torus => PrimType.Torus,
            SLNG.Core.BasicPrimType.Tube => PrimType.Tube,
            SLNG.Core.BasicPrimType.Ring => PrimType.Ring,
            _ => PrimType.Box
        };

        var constructionData = ObjectManager.BuildBasicShape(lmvType);
        var slPos = new LibreMetaverse.Vector3(position.X, position.Y, position.Z);
        _client.Objects.AddPrim(_client.Network.CurrentSim, constructionData, UUID.Zero, slPos,
            new LibreMetaverse.Vector3(0.5f, 0.5f, 0.5f), LibreMetaverse.Quaternion.Identity);
    }
}
