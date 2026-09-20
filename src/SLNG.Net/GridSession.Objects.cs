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
    /// <summary>Scans each object's raw ExtraParams bytes for a Light (0x20) block, independent
    /// of LibreMetaverse's own parsing -- see <see cref="_lightPresentByLocalId"/> for why this
    /// can't be read back from the high-level Primitive object. Byte layout matches
    /// Primitive.SetExtraParamsFromBytes: 1 count byte, then per entry a UInt16 type + UInt32
    /// length + that many payload bytes.</summary>
    private void OnRawObjectUpdatePacket(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdatePacket update) return;

        foreach (var block in update.ObjectData)
        {
            _lightPresentByLocalId[block.ID] = ExtraParamsContainsLight(block.ExtraParams);
            _reflectionProbeByLocalId[block.ID] = ExtraParamsReflectionProbe(block.ExtraParams);
        }
    }

    private static bool ExtraParamsContainsLight(byte[]? data)
    {
        if (data == null || data.Length < 1) return false;

        int i = 0;
        byte count = data[i++];
        for (int k = 0; k < count; k++)
        {
            if (i + 6 > data.Length) break;
            ushort type = Utils.BytesToUInt16(data, i); i += 2;
            uint len = Utils.BytesToUInt(data, i); i += 4;
            if (type == 0x20) return true; // ExtraParamType.Light
            i += (int)len;
        }
        return false;
    }

    /// <summary>Reads the Reflection Probe (0x90) block out of the same raw ExtraParams scan, or
    /// null when this update carries none. Payload is <c>LLReflectionProbeParams::pack</c>
    /// (llprimitive.cpp:1837): F32 ambiance, F32 clip distance, U8 flags, little-endian.
    ///
    /// Length-checked against the payload the packet actually declares rather than assuming 9
    /// bytes: the block is one Linden Lab could extend, and a short read of a longer future
    /// block would silently produce nonsense flags -- which here means inventing or losing a
    /// mirror.</summary>
    internal static SLNG.Core.ReflectionProbeParams? ExtraParamsReflectionProbe(byte[]? data)
    {
        if (data == null || data.Length < 1) return null;

        int i = 0;
        byte count = data[i++];
        for (int k = 0; k < count; k++)
        {
            if (i + 6 > data.Length) break;
            ushort type = Utils.BytesToUInt16(data, i); i += 2;
            uint len = Utils.BytesToUInt(data, i); i += 4;
            if (type == 0x90) // ExtraParamType.ReflectionProbe
            {
                if (len < SLNG.Core.ReflectionProbeParams.WireSize
                    || i + SLNG.Core.ReflectionProbeParams.WireSize > data.Length) return null;
                return new SLNG.Core.ReflectionProbeParams(
                    Utils.BytesToFloat(data, i),
                    Utils.BytesToFloat(data, i + 4),
                    data[i + 8]);
            }
            i += (int)len;
        }
        return null;
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

    private void OnObjectUpdate(object? sender, PrimEventArgs e) => RaiseObjectUpdate(e.Simulator, e.Prim, isFullUpdate: true);

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

        foreach (var block in packet.ObjectData)
        {
            byte[]? raw = CompressedParticleRepair.ExtractParticleBlock(block.Data);
            if (raw is null || !CompressedParticleRepair.TryReadLocalId(block.Data, out uint localId))
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

            var repaired = new Primitive.ParticleSystem(raw, 0);
            if (prim.ParticleSys.Equals(repaired))
            {
                // Already correct: an object whose particles have not changed sends the same block
                // on every compressed update, and re-raising each one would double the work of
                // every moving emitter in the region.
                continue;
            }

            prim.ParticleSys = repaired;
            RaiseObjectUpdate(e.Simulator, prim, isFullUpdate: true);
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

        // Primitive.Light never resets itself when a light is disabled (see
        // _lightPresentByLocalId) -- if our own raw-packet scan positively saw this update's
        // ExtraParams WITHOUT a Light block, trust that over the stale Primitive.Light, and
        // correct the shared Primitive object too so any other code reading prim.Light directly
        // (not just this event) also sees the fix from here on.
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

        bool lightEnabled = prim.Light.Intensity > 0f;
        if (_lightPresentByLocalId.TryGetValue(prim.LocalID, out bool lightBlockPresent) && !lightBlockPresent && lightEnabled)
        {
            prim.Light = new Primitive.LightData();
            lightEnabled = false;
        }

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
            (byte)prim.PrimData.ProfileCurve,
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
            // lightEnabled (computed above) is prim.Light.Intensity>0f corrected for the
            // never-resets-on-disable bug -- prim.Light itself is also corrected by then, so the
            // fields below are consistent with it either way.
            lightEnabled,
            new System.Numerics.Vector3(prim.Light.Color.R, prim.Light.Color.G, prim.Light.Color.B),
            prim.Light.Intensity,
            prim.Light.Radius,
            prim.Light.Falloff,
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
            _reflectionProbeByLocalId.TryGetValue(prim.LocalID, out var probe) ? probe : null,
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
            prim.Flags.HasFlag(PrimFlags.ObjectYouOwner)));
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
        ObjectRemovedReceived?.Invoke(this, new ObjectRemovedEvent(e.Simulator.Handle, e.ObjectLocalID));
    }

    private void OnKillObjects(object? sender, KillObjectsEventArgs e)
    {
        foreach (var localId in e.ObjectLocalIDs)
        {
            CheckAndStopMotionOnKill(e.Simulator, localId);
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

    /// <summary>Touches (clicks) an object — the SL grab/de-grab pair
    /// <see cref="ObjectManager.ClickObjectAsync"/> sends 50ms apart, which is what fires
    /// touch_start/touch_end on any touch script the object carries. <paramref name="localId"/>
    /// is the SL scene-local id (<c>Entity.LocalId</c>), not the persistent asset/object UUID.
    /// Surface hit details are optional (all-zero if omitted, like LibreMetaverse's own
    /// no-detail overload) — a HUD button script rarely inspects them, but pass real ones (face
    /// index, hit position/normal) when available for scripts that do.</summary>
    public async System.Threading.Tasks.Task ClickObjectAsync(
        uint localId,
        int faceIndex = 0,
        System.Numerics.Vector3 position = default,
        System.Numerics.Vector3 normal = default,
        System.Numerics.Vector3 uvCoord = default,
        System.Numerics.Vector3 stCoord = default,
        System.Numerics.Vector3 binormal = default)
    {
        var sim = _client.Network.CurrentSim;
        if (sim == null) return;

        await _client.Objects.ClickObjectAsync(
            sim, localId,
            ToOmv(uvCoord), ToOmv(stCoord), faceIndex,
            ToOmv(position), ToOmv(normal), ToOmv(binormal));
    }

    public void SelectObject(uint localId)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.SelectObject(_client.Network.CurrentSim, localId);
    }

    public void DeselectObject(uint localId)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        _client.Objects.DeselectObject(_client.Network.CurrentSim, localId);
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
    public void SelectObjects(System.Collections.Generic.IReadOnlyList<uint> localIds)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        if (localIds.Count == 0) return;
        if (localIds.Count == 1) { SelectObject(localIds[0]); return; }

        var ids = new uint[localIds.Count];
        for (int i = 0; i < localIds.Count; i++) ids[i] = localIds[i];
        _client.Objects.SelectObjects(_client.Network.CurrentSim, ids);
    }

    public void DeselectObjects(System.Collections.Generic.IReadOnlyList<uint> localIds)
    {
        if (!_client.Network.Connected || _client.Network.CurrentSim == null) return;
        if (localIds.Count == 0) return;
        if (localIds.Count == 1) { DeselectObject(localIds[0]); return; }

        var ids = new uint[localIds.Count];
        for (int i = 0; i < localIds.Count; i++) ids[i] = localIds[i];
        _client.Objects.DeselectObjects(_client.Network.CurrentSim, ids);
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
