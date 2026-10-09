using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.Core;

/// <summary>
/// Bridges a world-event source (the networking layer) with the ECS world.
///
/// World events arrive on LibreMetaverse's background network threads, so the handlers
/// only <b>enqueue</b> them. The queued mutations are applied to the (deliberately
/// non-thread-safe) <see cref="World"/> by <see cref="Pump"/>, which the host calls once
/// per frame on the main thread — making world mutation single-threaded. The core
/// depends on <see cref="IWorldEventSource"/>, never on the networking layer.
/// </summary>
public sealed class WorldSimulation : IDisposable
{
    private readonly World _world;
    private readonly IWorldEventSource _source;
    private readonly ConcurrentQueue<IWorldEvent> _pending = new();

    // (region, parentLocalId) -> child entity ids, so a linkset root's children can be
    // re-composed when the root arrives or moves. Touched only on the pump thread.
    private readonly Dictionary<(ulong, uint), HashSet<System.Guid>> _children = new();

    // FEAT-NET-06: Teleport keep reconciliation
    private readonly System.Diagnostics.Stopwatch _reconcileStopwatch = new();
    private long _lastUpdateTimestamp;
    private int _initialKeptCount;
    private readonly HashSet<System.Guid> _reconfirmedEntityIds = new();
    private int _updatesSinceReconcileStart;
    private bool _isReconciling;

    private const double ReconciliationMinWaitSeconds = 5.0;
    private const double ReconciliationQuietSeconds = 3.0;
    private const double ReconciliationMaxWaitSeconds = 30.0;

    /// <summary>FEAT-UI-05: raised when a prim joins or leaves a linkset, which in practice only
    /// happens on a link or an unlink. Lets the UI re-read a state it cannot poll for cheaply.</summary>
    public event System.EventHandler<Entity>? ObjectReparented;

    /// <summary>Raised when an object that WAS an attachment is one no longer. Both renderers
    /// have to swap sides on it: the avatar renderer drops the worn nodes it built on a bone,
    /// and the object renderer builds the standalone visual it refused to build while the object
    /// counted as worn.</summary>
    public event System.EventHandler<Entity>? AttachmentCleared;

    /// <summary>FEAT-UI-05: does this prim have anything linked UNDER it -- i.e. is it a linkset
    /// root? A child knows its own parent through <c>TransformComponent.ParentLocalId</c>, but a
    /// root has nothing on itself that says so, and the alternative is scanning every entity in
    /// the region. This index already exists for re-composing children, so it answers for free.</summary>
    public bool HasChildren(ulong regionHandle, uint localId)
        => _children.TryGetValue((regionHandle, localId), out var set) && set.Count > 0;

    /// <summary>The prims linked directly under this one. A linkset is flat -- every part names
    /// the ROOT as its parent -- so for a root this is the whole rest of the object.</summary>
    public IReadOnlyCollection<System.Guid> ChildrenOf(ulong regionHandle, uint localId)
        => _children.TryGetValue((regionHandle, localId), out var set)
            ? set
            : (IReadOnlyCollection<System.Guid>)System.Array.Empty<System.Guid>();

    /// <summary>
    /// Raised when WorldSimulation determines that an animation on the local agent should be stopped
    /// (e.g. its source attachment was detached/removed or the avatar stood up from a seat).
    /// </summary>
    public event System.Action<System.Guid>? SelfAnimationStopRequested;

    public WorldSimulation(World world, IWorldEventSource source)
    {
        _world = world;
        _source = source;

        _source.ObjectUpdateReceived += OnObjectUpdate;
        _source.AvatarUpdateReceived += OnAvatarUpdate;
        _source.ObjectRemovedReceived += OnObjectRemoved;
        _source.TerrainPatchReceived += OnTerrainPatch;
        _source.TerrainSettingsReceived += OnTerrainSettings;
        _source.RegionDisconnectedReceived += OnRegionDisconnected;
        _source.AvatarAppearanceReceived += OnAvatarAppearance;
        _source.AvatarAnimationReceived += OnAvatarAnimation;
        _source.ObjectPropertiesReceived += OnObjectProperties;
        _source.PhysicsPropertiesReceived += OnPhysicsProperties;
        _source.ObjectMediaReceived += OnObjectMedia;
        _source.ObjectAnimationReceived += OnObjectAnimation;
        _source.DisplayNameResolved += OnDisplayNameResolved;
        _source.TeleportProgressReceived += OnTeleportProgress;
        _world.EntityRemoved += OnEntityRemoved;
    }

    // These run on background network threads: enqueue only, never touch the world.
    private void OnObjectUpdate(object? sender, ObjectUpdateEvent e) => _pending.Enqueue(e);
    private void OnAvatarUpdate(object? sender, AvatarUpdateEvent e) => _pending.Enqueue(e);
    private void OnObjectRemoved(object? sender, ObjectRemovedEvent e) => _pending.Enqueue(e);
    private void OnObjectProperties(object? sender, ObjectPropertiesEvent e) => _pending.Enqueue(e);
    private void OnPhysicsProperties(object? sender, PhysicsPropertiesEvent e) => _pending.Enqueue(e);
    private void OnObjectMedia(object? sender, ObjectMediaEvent e) => _pending.Enqueue(e);
    private void OnObjectAnimation(object? sender, ObjectAnimationEvent e) => _pending.Enqueue(e);
    private void OnTerrainPatch(object? sender, TerrainPatchEvent e) => _pending.Enqueue(e);
    private void OnTerrainSettings(object? sender, TerrainSettingsEvent e) => _pending.Enqueue(e);
    private void OnRegionDisconnected(object? sender, RegionDisconnectedEvent e) => _pending.Enqueue(e);
    private void OnAvatarAppearance(object? sender, AvatarAppearanceEvent e) => _pending.Enqueue(e);
    private void OnAvatarAnimation(object? sender, AvatarAnimationEvent e) => _pending.Enqueue(e);
    private void OnDisplayNameResolved(object? sender, NameResolvedEvent e) => _pending.Enqueue(e);
    private void OnTeleportProgress(object? sender, TeleportProgressEvent e) => _pending.Enqueue(e);

    /// <summary>
    /// Applies all queued world events to the world. Call once per frame on the main
    /// thread; this is the only place that mutates the world.
    /// </summary>
    public void Pump()
    {
        while (_pending.TryDequeue(out var evt))
        {
            _updatesSinceReconcileStart++;
            if (_isReconciling)
            {
                _lastUpdateTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int kind;
            switch (evt)
            {
                case ObjectUpdateEvent e: kind = 0; ApplyObjectUpdate(e); break;
                case AvatarUpdateEvent e: kind = 1; ApplyAvatarUpdate(e); break;
                case ObjectRemovedEvent e: kind = 2; ApplyObjectRemoved(e); break;
                case ObjectPropertiesEvent e: kind = 3; ApplyObjectProperties(e); break;
                case PhysicsPropertiesEvent e: kind = 4; ApplyPhysicsProperties(e); break;
                case ObjectMediaEvent e: kind = 5; ApplyObjectMedia(e); break;
                case ObjectAnimationEvent e: kind = 6; ApplyObjectAnimation(e); break;
                case TerrainPatchEvent e: kind = 7; ApplyTerrainPatch(e); break;
                case TerrainSettingsEvent e: kind = 8; ApplyTerrainSettings(e); break;
                // BUG-NET-24: a region the whole session took with it stays as it was --
                // UnloadAllRegions clears it once the client leaves.
                case RegionDisconnectedEvent { SessionEnded: true }:
                    kind = 9;
                    break;
                case RegionDisconnectedEvent e:
                    kind = 9;
                    ParkTerrain(e.RegionHandle);
                    _world.RemoveRegion(e.RegionHandle);
                    DropPendingAnimations(e.RegionHandle); // FEAT-ANIMESH-02: for objects that never arrived
                    _avatarCacheDirty = true; // takes every avatar in that region with it
                    StartReconciliation();
                    break;
                case AvatarAppearanceEvent e: kind = 10; ApplyAvatarAppearance(e); break;
                case AvatarAnimationEvent e: kind = 11; ApplyAvatarAnimation(e); break;
                case NameResolvedEvent e: kind = 12; ApplyDisplayNameResolved(e); break;
                case TeleportProgressEvent e: kind = 13; ApplyTeleportProgress(e); break;
                default: kind = DrainKinds.Length - 1; break;
            }
            _drainTicks[kind] += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            _drainCounts[kind]++;
        }

        // BUG-PERF-10: this looks at every entity in the world, and it ran once per frame for as long as a
        // teleport was being reconciled (up to 30 s) -- ~2 ms of a 53,000-entity scene per frame for
        // nothing. The test is about seconds, so a few times a second is as good.
        if (_isReconciling)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastReconCheckTimestamp >= System.Diagnostics.Stopwatch.Frequency / 4)
            {
                _lastReconCheckTimestamp = now;
                CheckReconciliationProgress();
            }
        }
    }

    private long _lastReconCheckTimestamp;

    // BUG-PERF-10: the local agent's entity. Looking it up meant visiting every entity in the world, and
    // that happened for every update of the local agent (several a second), for every attachment re-keyed
    // after a teleport, and when a teleport started. The cached entity is only trusted while it is still in
    // the world and still flagged; otherwise the world is searched again, exactly as before.
    private Entity? _localAgentCache;

    private Entity? FindLocalAgent()
    {
        var cached = _localAgentCache;
        if (cached != null
            && cached.GetComponent<AvatarComponent>()?.IsLocalAgent == true
            && ReferenceEquals(_world.GetEntity(cached.Id), cached))
            return cached;

        return _localAgentCache = _world.GetAllEntities()
            .FirstOrDefault(ent => ent.GetComponent<AvatarComponent>()?.IsLocalAgent == true);
    }

    // BUG-PERF-10: what draining the world costs, by kind of event. Counted per Pump call, read by the
    // main thread's perf report.
    private static readonly string[] DrainKinds =
    {
        "ObjectUpdate", "AvatarUpdate", "ObjectRemoved", "ObjectProperties", "PhysicsProperties", "ObjectMedia",
        "ObjectAnimation", "TerrainPatch", "TerrainSettings", "RegionDisconnected", "AvatarAppearance",
        "AvatarAnimation", "NameResolved", "TeleportProgress", "other",
    };
    private readonly long[] _drainTicks = new long[DrainKinds.Length];
    private readonly int[] _drainCounts = new int[DrainKinds.Length];

    /// <summary>One line: for each kind of event drained since the last call, how many and how long,
    /// then the tally cleared. Null when nothing was drained. Main thread only, like <see cref="Pump"/>.</summary>
    public string? TakeDrainReport()
    {
        var parts = new List<string>();
        for (int i = 0; i < DrainKinds.Length; i++)
        {
            if (_drainCounts[i] == 0) continue;
            double ms = _drainTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            parts.Add($"{DrainKinds[i]}=n{_drainCounts[i]}/{ms:F0}ms({ms / _drainCounts[i]:F3}/ea)");
            _drainTicks[i] = 0;
            _drainCounts[i] = 0;
        }
        return parts.Count == 0 ? null : $"[WorldDrain] entities={_world.EntityCount} {string.Join(" ", parts)}";
    }

    /// <summary>
    /// BUG-NET-24: unloads every region still in the world, as a disconnect used to. Call on the
    /// main thread when the client leaves a session for the login screen: a session the grid ended
    /// is left standing on screen behind its message, and this is where it goes.
    ///
    /// <para>Whatever the session still had queued is dropped first. Nothing more arrives once it
    /// has ended, and pumping the rest later would only re-create what this removes.</para>
    /// </summary>
    public void UnloadAllRegions()
    {
        while (_pending.TryDequeue(out _)) { }
        CancelReconciliation();
        _parkedTerrain.Clear();
        _parkedOrder.Clear();
        _pendingAnimations.Clear();
        _pendingAnimationNodes.Clear();
        _world.RemoveAllRegions();
        _avatarCacheDirty = true;
    }

    // Deliberately much shorter than the real viewer's generic LLViewerObject::
    // interpolateLinearMotion constants (sPhaseOutUpdateInterpolationTime=2s /
    // sMaxUpdateInterpolationTime=3s -- appropriate for physics objects, whose velocity vector
    // doesn't change direction abruptly). Live-tested console data (2026-07-23, [AvatarMove] diag)
    // showed a healthy ~60-160ms packet cadence with occasional ~1.4s gaps; a walking avatar can
    // change direction (the user turns) well within that gap, so holding the OLD velocity's
    // direction at full weight for up to 2s -- as the generic constants would -- accumulates real
    // position error in a now-wrong direction for over a second before the next packet corrects
    // it, which is exactly what reads as a sideways pop. Freezing in place after a short gap
    // (rather than continuing to fling the avatar along a stale direction) is the better failure
    // mode here: a brief pause is much less visible than a growing-then-corrected sideways drift.
    private const float ExtrapolationPhaseOutStartSeconds = 0.4f;
    private const float ExtrapolationMaxSeconds = 0.8f;

    private const float LocalExtrapolationMaxSeconds = 5.0f;
    private const float LocalExtrapolationPhaseOutStartSeconds = 4.5f;

    // Rotation has no reliable AngularVelocity to dead-reckon from for a turning avatar (see
    // TransformComponent.TargetRotation's doc comment), so instead of phase-out/cutoff timing it's
    // a simple framerate-independent exponential approach toward TargetRotation: reaches ~95% of
    // the way there in about 3/RotationSmoothingRate seconds (~0.36s at this rate) -- fast enough
    // to feel responsive, slow enough to smooth over the sim's own packet gaps (typically
    // 150-300ms).
    private const float RotationSmoothingRate = 8.3f;

    // Same exponential-approach technique as RotationSmoothingRate, applied to Position easing
    // toward TargetPosition (see TransformComponent.TargetPosition's doc comment): reaches ~95% of
    // the way there in about 0.3s. Fast enough that ordinary small corrections are imperceptible,
    // slow enough that a multi-metre catch-up (after a real packet gap) reads as a quick glide
    // instead of an instant pop.
    private const float PositionSmoothingRate = 10f;

    // Above this distance, a TargetPosition update is treated as a genuinely discontinuous move
    // (teleport, sit/stand, a large server-side correction) rather than an ordinary packet-gap
    // catch-up, and Position snaps to it instantly instead of easing -- those moves SHOULD look
    // instant. Comfortably above the largest catch-up distances seen in live-test data (~3.3m for
    // a 2.2s gap at normal walk speed) so ordinary movement never snaps.
    private const float TeleportSnapDistanceMeters = 5f;

    /// <summary>Dead-reckons every avatar's TargetPosition from its last network-reported Velocity
    /// (phasing the contribution out over time), eases the rendered Position toward TargetPosition,
    /// and smooths Rotation toward TargetRotation -- instead of leaving Position static / snapping
    /// Position or Rotation straight to each packet's value. Call once per frame on the main thread,
    /// after Pump() has applied this frame's network updates. TargetPosition/TargetRotation are only
    /// ever set authoritatively by ApplyAvatarUpdate (server-driven, local agent included — see its
    /// doc comment); this method only smooths the visual gap between those updates.</summary>
    // Avatars, cached. Rebuilt only when the world's entity count changes, which covers every way
    // an avatar can appear or leave, and is an O(1) test rather than the O(entities) scan Query
    // performs. See the field's use below for the measurement that forced this.
    private readonly List<Entity> _avatarCache = new();

    /// <summary>
    /// Set whenever an avatar could have appeared or gone away, which is the only thing that changes
    /// this cache's contents.
    ///
    /// The first version keyed invalidation on the world's total entity count instead, which was
    /// wrong in exactly the situation the cache exists for: while a region streams in, objects arrive
    /// continuously, so the count changed nearly every frame and the cache was rebuilt every frame.
    /// It measured as an improvement only because loading eventually stops -- extrapolate fell from
    /// 226 to 49 ms per second, when it should have fallen much further. Avatars come and go rarely;
    /// objects constantly. Watch the former.
    /// </summary>
    private bool _avatarCacheDirty = true;

    /// <summary>Safety net in case some path adds or removes an avatar without going through the
    /// handlers below. Two seconds is far below anything a person would notice in an avatar's motion
    /// -- extrapolation only smooths between network packets -- and far above the per-frame rate that
    /// made the scan expensive.</summary>
    private const float AvatarCacheMaxAgeSeconds = 2f;
    private float _avatarCacheAge;

    /// <summary>BUG-NET-13: entities already reported by <see cref="SanitizeAvatarTransform"/>, so a
    /// per-frame NaN doesn't flood the log. Keyed by entity id + field name.</summary>
    private readonly HashSet<string> _nanGuardLogged = new();

    /// <summary>
    /// Dead-reckons avatar positions between network updates. Runs every frame.
    ///
    /// The avatar list is cached because Query&lt;AvatarComponent&gt; is a linear scan over every
    /// entity in the world. On a 24,000-entity region at 160 fps that is ~3.8 million dictionary
    /// probes per second, and [PhaseCost] measured this method at 226 ms per second of wall clock --
    /// more than every other main-thread phase in the client put together, to move a handful of
    /// avatars.
    /// </summary>
    public void ExtrapolateMovement(float deltaSeconds)
    {
        _avatarCacheAge += deltaSeconds;
        if (_avatarCacheDirty || _avatarCacheAge >= AvatarCacheMaxAgeSeconds)
        {
            _avatarCacheDirty = false;
            _avatarCacheAge = 0f;
            _avatarCache.Clear();
            _avatarCache.AddRange(_world.Query<AvatarComponent>());
        }

        foreach (var entity in _avatarCache)
        {
            var transform = entity.GetComponent<TransformComponent>();
            if (transform == null) continue;

            // BUG-NET-13: a non-finite Position/Rotation here reaches the renderer as a NaN basis and
            // produces the engine's "Vector3 cannot be normalized" warning every single frame (9212
            // copies in one teleport-heavy session). The upstream cause is meant to be fixed
            // (stale-circuit churn feeding half-populated transforms), but repair + name it here so a
            // survivor is caught, not silently flooding.
            SanitizeAvatarTransform(entity, transform);

            transform.TimeSinceUpdate += deltaSeconds;

            var avatarComponent = entity.GetComponent<AvatarComponent>();
            bool isLocalAgent = avatarComponent?.IsLocalAgent == true;
            // MVP2-1: while seated, AvatarController's ground-clamp/camera-yaw ownership of
            // Z/Rotation (the two guards below) is suspended -- see AvatarController._Process's
            // own isSitting gate, which stops writing transform.Rotation/Z entirely while seated.
            // A seated local agent needs the exact same network-driven smoothing as a remote
            // avatar instead (e.g. to follow a moving vehicle's seat), so it's excluded from both
            // "local agent owns this" guards below.
            bool isSeatedLocalAgent = isLocalAgent && avatarComponent!.SittingOnLocalId != 0;

            float maxSecs = isLocalAgent ? LocalExtrapolationMaxSeconds : ExtrapolationMaxSeconds;
            float phaseOutStartSecs = isLocalAgent ? LocalExtrapolationPhaseOutStartSeconds : ExtrapolationPhaseOutStartSeconds;

            if (transform.Velocity != Vector3.Zero && transform.TimeSinceUpdate < maxSecs)
            {
                float phaseOutT = System.Math.Clamp(
                    (transform.TimeSinceUpdate - phaseOutStartSecs)
                        / (maxSecs - phaseOutStartSecs),
                    0f, 1f);
                float weight = 1f - phaseOutT;

                // Scale by the originating sim's TimeDilation, exactly like LibreMetaverse's own
                // InterpolationService (`adjSeconds = seconds * sim.Stats.Dilation`) -- a busy/laggy
                // sim (e.g. an OSGrid megaregion under load) runs its own physics below real-time,
                // so extrapolating at full real-time speed overshoots what the sim actually
                // simulated by the time its next (delayed) packet arrives, reading as an extra
                // correction pop on a busy sim that a quiet one wouldn't show.
                transform.TargetPosition += transform.Velocity * deltaSeconds * weight * transform.TimeDilation;
            }

            float posT = 1f - MathF.Exp(-PositionSmoothingRate * deltaSeconds);
            var easedPosition = Vector3.Lerp(transform.Position, transform.TargetPosition, posT);
            if (isLocalAgent && !isSeatedLocalAgent)
            {
                // Local Z is owned entirely by AvatarController's per-frame ground-clamp (see
                // ApplyAvatarUpdate: TargetPosition.Z is pinned to whatever Position.Z the clamp
                // last produced, and never taken from the network). But TargetPosition.Z only gets
                // re-pinned when a packet arrives -- between packets the clamp keeps moving
                // Position.Z (terrain height changes, gravity) while TargetPosition.Z stays frozen,
                // so easing Position toward TargetPosition on Z would drag it back toward the stale
                // value every frame, fighting the clamp exactly the way the network X/Y sync used
                // to fight the old client prediction. Keep the clamp's Z untouched here; only X/Y
                // ease toward the network target.
                easedPosition.Z = transform.Position.Z;
            }
            transform.Position = easedPosition;

            // The local agent's Rotation is entirely owned by AvatarController (the player's own
            // camera yaw, written directly EVERY FRAME now) -- see ApplyAvatarUpdate's matching
            // guard on TargetRotation. Slerping it here too would fight those writes every frame.
            if (!isLocalAgent || isSeatedLocalAgent)
            {
                float rotT = 1f - MathF.Exp(-RotationSmoothingRate * deltaSeconds);
                transform.Rotation = Quaternion.Slerp(transform.Rotation, transform.TargetRotation, rotT);
            }

            _world.NotifyComponentUpdated(entity, transform);
        }
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W)
        && (q.X != 0f || q.Y != 0f || q.Z != 0f || q.W != 0f); // a zero quaternion normalizes to NaN

    /// <summary>BUG-NET-13: repairs a non-finite avatar transform before <see cref="ExtrapolateMovement"/>
    /// feeds it to the renderer (a NaN basis is what triggers the engine's per-frame
    /// "Vector3 cannot be normalized" warning). A bad Position/TargetPosition falls back to the other
    /// of the pair, then to <see cref="Vector3.Zero"/>; a bad Rotation/TargetRotation falls back to the
    /// other, then to <see cref="Quaternion.Identity"/>. Each (entity, field) pair is logged once.</summary>
    internal void SanitizeAvatarTransform(Entity entity, TransformComponent t)
    {
        bool localAgent = entity.GetComponent<AvatarComponent>()?.IsLocalAgent == true;

        void Report(string field)
        {
            if (_nanGuardLogged.Add($"{entity.Id}:{field}"))
                System.Console.WriteLine(
                    $"[NaNGuard] entity={entity.Id} region={entity.RegionHandle} localAgent={localAgent} field={field} -- non-finite avatar transform repaired");
        }

        if (!IsFinite(t.Position))
        {
            t.Position = IsFinite(t.TargetPosition) ? t.TargetPosition : Vector3.Zero;
            Report(nameof(t.Position));
        }
        if (!IsFinite(t.TargetPosition))
        {
            t.TargetPosition = IsFinite(t.Position) ? t.Position : Vector3.Zero;
            Report(nameof(t.TargetPosition));
        }
        if (!IsFinite(t.Rotation))
        {
            t.Rotation = IsFinite(t.TargetRotation) ? t.TargetRotation : Quaternion.Identity;
            Report(nameof(t.Rotation));
        }
        if (!IsFinite(t.TargetRotation))
        {
            t.TargetRotation = IsFinite(t.Rotation) ? t.Rotation : Quaternion.Identity;
            Report(nameof(t.TargetRotation));
        }
    }

    private readonly HashSet<(ulong, uint)> _objNanLogged = new();

    /// <summary>BUG-NET-13: repair a non-finite prim transform before it reaches the renderer, and
    /// name it once (`[NaNGuard] object region=<h> localId=<id> field=<name>`). Complements
    /// <see cref="SanitizeAvatarTransform"/>, which only covers avatars.</summary>
    private void SanitizeObjectTransform(ulong region, uint localId, TransformComponent t)
    {
        void Report(string field)
        {
            if (_objNanLogged.Add((region, localId)))
                System.Console.WriteLine(
                    $"[NaNGuard] object region={region} localId={localId} field={field} -- non-finite prim transform repaired");
        }

        if (!IsFinite(t.Position)) { t.Position = IsFinite(t.LocalPosition) ? t.LocalPosition : Vector3.Zero; Report(nameof(t.Position)); }
        if (!IsFinite(t.LocalPosition)) { t.LocalPosition = IsFinite(t.Position) ? t.Position : Vector3.Zero; Report(nameof(t.LocalPosition)); }
        if (!IsFinite(t.Rotation)) { t.Rotation = IsFinite(t.LocalRotation) ? t.LocalRotation : Quaternion.Identity; Report(nameof(t.Rotation)); }
        if (!IsFinite(t.LocalRotation)) { t.LocalRotation = IsFinite(t.Rotation) ? t.Rotation : Quaternion.Identity; Report(nameof(t.LocalRotation)); }
    }

    private void ApplyObjectUpdate(ObjectUpdateEvent e)
    {
        if (_regionDataLogged.Add(e.RegionHandle))
            System.Console.WriteLine($"[RegionData] first object update for region {e.RegionHandle}");

        Entity? entity = null;
        if (e.ObjectId != System.Guid.Empty)
        {
            Entity? existing = null;
            if (_objectIndex.TryGetValue(e.ObjectId, out var at))
            {
                existing = _world.GetEntity(at.Region, at.LocalId);
            }
            // BUG-PERF-10: this fallback looks at every entity in the world, and it used to run for every
            // object seen for the first time -- i.e. every object of a region that is streaming in, with
            // the world growing as it goes. 53,000 cached objects across five regions cost ~550 ms of
            // every second of the main thread. The only entities it can find are the ones kept across a
            // teleport, whose UUIDs are known when the reconciliation starts (_keptObjectIds).
            if (existing == null && _keptObjectIds.Contains(e.ObjectId))
            {
                existing = _world.GetAllEntities()
                    .FirstOrDefault(ent => ent.GetComponent<MetadataComponent>()?.Id == e.ObjectId
                                           && (ent.GetComponent<AttachmentComponent>() != null || ent.GetComponent<AvatarComponent>()?.IsLocalAgent == true));
            }

            if (existing != null && (existing.RegionHandle != e.RegionHandle || existing.LocalId != e.LocalId))
            {
                var att = existing.GetComponent<AttachmentComponent>();
                var av = existing.GetComponent<AvatarComponent>();
                var localAgent = FindLocalAgent();
                bool isSelfAttachment = att != null && (localAgent != null && att.AvatarEntityId == localAgent.Id);
                if (isSelfAttachment || av?.IsLocalAgent == true)
                {
                    ulong oldRegion = existing.RegionHandle;
                    uint oldLocalId = existing.LocalId;
                    _world.RekeyEntity(existing, e.RegionHandle, e.LocalId);
                    _objectIndex[e.ObjectId] = (e.RegionHandle, e.LocalId);
                    if (att != null)
                    {
                        att.AwaitingReconfirmation = false;
                        NoteAttachmentReconfirmed(existing.Id);
                    }
                    if (_children.TryGetValue((oldRegion, oldLocalId), out var childSet))
                    {
                        _children.Remove((oldRegion, oldLocalId));
                        var newKey = (e.RegionHandle, e.LocalId);
                        if (!_children.TryGetValue(newKey, out var existingSet))
                            _children[newKey] = childSet;
                        else
                            existingSet.UnionWith(childSet);
                    }
                    entity = existing;
                }
            }
            else if (existing != null && existing.RegionHandle == e.RegionHandle && existing.LocalId == e.LocalId)
            {
                var att = existing.GetComponent<AttachmentComponent>();
                if (att != null && att.AwaitingReconfirmation)
                {
                    att.AwaitingReconfirmation = false;
                    NoteAttachmentReconfirmed(existing.Id);
                }
                entity = existing;
            }
        }

        entity ??= _world.GetOrCreateEntity(e.RegionHandle, e.LocalId);

        var transform = entity.GetComponent<TransformComponent>() ?? new TransformComponent();

        // FEAT-UI-04: while the user is dragging this object with the in-world gizmo, the local
        // position is authoritative. The simulator's echo trails the cursor by a round trip, so
        // adopting it would yank the object back to where it was a moment ago, over and over --
        // that is the stutter this guard exists to stop. Only POSITION is withheld: rotation,
        // scale, textures and everything else below still apply normally, because the drag makes
        // no claim on them.
        if (!transform.LocallyDragged)
        {
            transform.LocalPosition = e.Position;
        }
        transform.LocalRotation = e.Rotation;

        // FEAT-UI-05: a prim can change parent while we are watching it -- that is exactly what
        // linking and unlinking do, and nothing here ever removed it from its OLD parent's child
        // set. The position survives that (ResolveWorldTransform reads the prim's own
        // ParentLocalId and returns early once it is 0), so the damage is to every question
        // answered FROM the index: HasChildren keeps reporting the old root as a linkset root,
        // which would leave Unlink offered on a prim that has nothing left under it. It also
        // re-composes prims that are no longer its concern on every move.
        uint previousParent = transform.ParentLocalId;
        bool reparented = previousParent != e.ParentLocalId;
        if (reparented && previousParent != 0
            && _children.TryGetValue((e.RegionHandle, previousParent), out var oldSiblings))
        {
            oldSiblings.Remove(entity.Id);
        }

        transform.ParentLocalId = e.ParentLocalId;
        // Linked child prims send their transform relative to the root; compose to world space.
        ResolveWorldTransform(transform, e.RegionHandle);

        // BUG-NET-13: a non-finite object transform reaches the renderer and the engine re-normalizes
        // it every frame -> the "Vector3 cannot be normalized" flood that starts right after
        // "[RegionData] first object update" for a teleport destination. SanitizeAvatarTransform only
        // covers avatars; this catches a bad prim. Snap the offending field to Identity/Zero and name
        // the entity + region once.
        SanitizeObjectTransform(e.RegionHandle, e.LocalId, transform);

        entity.SetComponent(transform);
        _world.NotifyComponentUpdated(entity, transform);

        // Index this entity under its parent so the parent can re-compose it later, and
        // re-compose any children already waiting on this entity (handles either arrival order).
        if (e.ParentLocalId != 0)
        {
            var key = (e.RegionHandle, e.ParentLocalId);
            if (!_children.TryGetValue(key, out var set)) _children[key] = set = new HashSet<System.Guid>();
            set.Add(entity.Id);
        }
        RecomposeChildren(e.RegionHandle, e.LocalId);

        if (reparented) ObjectReparented?.Invoke(this, entity);

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim == null)
        {
            // BUG-RENDER-07: legacyMaterialId was missing from this constructor call entirely (the
            // constructor itself had no parameter for it) -- every object's LegacyMaterialId stayed
            // Guid.Empty from the moment it was first created, only ever picking up the real value
            // if/when a LATER update happened to touch the same entity again (the `else` branch
            // below, which DOES set prim.LegacyMaterialId). A static object that loads once and is
            // never incidentally re-updated keeps a legacy Blinn-Phong material (alpha mask cutoff,
            // normal/specular maps) invisible to the renderer forever, silently falling back to
            // ApplyAlphaCutout's DetectAlpha() pixel guess and a hardcoded 0.5 threshold instead of
            // the creator's real declared material -- found chasing a tree-canopy flicker report
            // where Firestorm's own material inspector showed a real Blinn-Phong material
            // (Alpha-Masking, cutoff 100, a normal map) that never once appeared in this app's own
            // [LegacyMaterial] diagnostic log for that exact object.
            prim = new PrimitiveComponent(e.Scale, e.ProfileCurve, e.IsMesh, e.MeshId, e.TextureId, e.RenderMaterialId, e.ColorTint, e.RepeatU, e.RepeatV, e.OffsetU, e.OffsetV, e.TextureRotation, e.Shape, e.IsSculpt, e.SculptId, e.SculptType, e.Faces, e.TexGen, e.Particles, e.Fullbright, e.LegacyMaterialId);
            entity.SetComponent(prim);
        }
        else
        {
            prim.Scale = e.Scale;
            prim.ProfileCurve = e.ProfileCurve;
            prim.IsMesh = e.IsMesh;
            prim.MeshId = e.MeshId;
            prim.TextureId = e.TextureId;
            prim.RenderMaterialId = e.RenderMaterialId;
            prim.LegacyMaterialId = e.LegacyMaterialId;
            prim.ColorTint = e.ColorTint;
            prim.RepeatU = e.RepeatU;
            prim.RepeatV = e.RepeatV;
            prim.OffsetU = e.OffsetU;
            prim.OffsetV = e.OffsetV;
            prim.Rotation = e.TextureRotation;
            prim.Shape = e.Shape;
            prim.IsSculpt = e.IsSculpt;
            prim.SculptId = e.SculptId;
            prim.SculptType = e.SculptType;
            prim.TexGen = e.TexGen;
            prim.Fullbright = e.Fullbright;
            prim.Faces = e.Faces;
            entity.SetComponent(prim);
        }
        prim.AttachmentPoint = e.AttachmentPoint;
        // Unlike the flags/light fields below, Material comes from the same PrimData block as
        // Shape/ProfileCurve above, which the network layer keeps current on a terse update by
        // reading it off the cached, already-decoded object -- so no IsFullUpdate guard here.
        //
        // That holds only because GridSession.IsUnpopulatedPrimitive drops the one case where it
        // would not: a terse update for an object LibreMetaverse has never decoded, where it hands
        // over a manufactured Primitive whose PrimData is all zeros. Such an event used to reach
        // this method and assign a default PrimShape over a perfectly good one (and a zero Scale,
        // and empty Faces), which surfaced downstream as a placeholder cylinder and a
        // [PrimMeshFallback] pathScale=(0,0) warning. Guarding the fields here would have been the
        // wrong half of the fix -- the event should never have been raised.
        prim.Material = e.Material;
        prim.ClickAction = e.ClickAction;
        // Terse-sourced events (ImprovedTerseObjectUpdate -- fast position streaming for moving
        // objects) never carry real flags on the wire; LibreMetaverse leaves Primitive.Flags at
        // whatever the last full update said. Applying that here would repeatedly stomp a flag
        // just changed locally (e.g. Physical/Temporary) back to its stale pre-change value on
        // every subsequent terse ping, which arrive constantly while the object is moving.
        if (e.IsFullUpdate)
        {
            prim.IsPhysical = e.IsPhysical;
            prim.IsTemporary = e.IsTemporary;
            prim.IsPhantom = e.IsPhantom;
            prim.CastsShadows = e.CastsShadows;
            prim.IsTouch = e.IsTouch;
            prim.TakesMoney = e.TakesMoney;
            // FEAT-SEC-04: per-agent permissions ride the same ObjectUpdate flags word as
            // IsPhysical above, so they carry the same terse-update staleness and belong under
            // the same guard. Applying a terse-sourced default here would tell the edit UI the
            // agent may do nothing, every time an object moved.
            prim.YouCanModify = e.YouCanModify;
            prim.YouCanMove = e.YouCanMove;
            prim.YouCanCopy = e.YouCanCopy;
            prim.YouCanTransfer = e.YouCanTransfer;
            prim.YouAreOwner = e.YouAreOwner;
            prim.LightEnabled = e.LightEnabled;
            prim.LightColor = e.LightColor;
            prim.LightIntensity = e.LightIntensity;
            prim.LightRadius = e.LightRadius;
            prim.LightFalloff = e.LightFalloff;
            // Same reason as the flags: a terse update carries no TextureAnim block, so applying
            // a terse-sourced null here would stop every animated texture the moment its object
            // starts moving.
            prim.TextureAnim = e.TextureAnim;
            prim.Particles = e.Particles;
            // Same gate, same reason: the Reflection Probe block rides ExtraParams, which a terse
            // update does not carry -- applying a terse-sourced null would un-mirror every mirror
            // the moment something near it moved.
            prim.ReflectionProbe = e.ReflectionProbe;
            // And again: the Extended Mesh block is ExtraParams too. A terse-sourced false would
            // stand an animesh back up as a plain rigged mesh every time it moved, and the sim
            // signals "no longer animesh" only by omitting the block from a FULL update.
            prim.IsAnimatedMesh = e.IsAnimatedMesh;
        }

        // FEAT-ANIMESH-02: ObjectAnimation names a prim by its object UUID. Index it now, and take
        // up any list that arrived before the object did -- before the notification below, so a
        // listener's first look at the prim already has it rather than seeing it a beat later.
        if (e.ObjectId != System.Guid.Empty)
        {
            _objectIndex[e.ObjectId] = (e.RegionHandle, e.LocalId);
            if (TakePendingAnimations(e.ObjectId) is { } held) prim.SignaledAnimations = held;
        }
        _world.NotifyComponentUpdated(entity, prim);

        // Seed the entity's real simulator object UUID (distinct from Entity.Id, which is an
        // internal ECS identity) so ApplyObjectProperties can later resolve the ObjectPropertiesFamily
        // response back to this entity. Never overwrite Name/Description/etc. here -- those only
        // ever come from ApplyObjectProperties.
        var meta = entity.GetComponent<MetadataComponent>();
        if (meta == null)
        {
            entity.SetComponent(new MetadataComponent(e.ObjectId));
        }
        else if (meta.Id == System.Guid.Empty && e.ObjectId != System.Guid.Empty)
        {
            meta.Id = e.ObjectId;
        }

        // An attachment is any object whose parent is EITHER an avatar directly, OR another
        // object that is itself already an attachment (recursively). A detailed mesh product
        // (e.g. a Bento mesh head) commonly ships as dozens of LINKED prims — one root plus many
        // children — and every rigged child needs to be treated as its own independent
        // attachment for rendering: a rigged mesh binds straight to the avatar skeleton via its
        // own skin data, completely ignoring prim-hierarchy position, so there is no reason to
        // exclude children the way a naive "only the root is an attachment" rule does. Excluding
        // them (the previous rule here) silently dropped every child prim from rendering —
        // for one real asset, 21 of 22 linked prims (the entire head shell, eyes, most of the
        // teeth) never rendered, leaving only the root prim visible.
        // If the parent isn't resolved yet (arrived out of order), PropagateAttachment (called
        // from LinkPendingAttachments and from the two branches below) picks this up once
        // whichever ancestor resolves the chain down to this entity.
        if (e.ParentLocalId != 0)
        {
            var parentEntity = _world.GetEntity(e.RegionHandle, e.ParentLocalId);
            var parentAvatar = parentEntity?.GetComponent<AvatarComponent>();
            var parentAttachment = parentEntity?.GetComponent<AttachmentComponent>();
            if (parentAvatar != null)
                SetAttachment(entity, parentEntity!.Id, e.AttachmentPoint);
            else if (parentAttachment != null)
                SetAttachment(entity, parentAttachment.AvatarEntityId, parentAttachment.AttachmentPoint);
            else if (parentEntity != null)
                ClearAttachment(entity, transform, e.RegionHandle);
        }
        else
        {
            // No parent at all: a standalone world object. If it used to be worn, it is not any
            // more -- it was detached, dropped, or rezzed from inventory into the world.
            ClearAttachment(entity, transform, e.RegionHandle);
        }
    }

    /// <summary>Takes attachment status away again.</summary>
    /// <remarks>
    /// The counterpart <see cref="SetAttachment"/> never had: the component was only ever added.
    /// An object that stopped being worn -- detached, dropped, or re-parented into a world
    /// linkset -- therefore kept it forever, and everything downstream went on treating it as
    /// worn. Its position is the visible half of that: an attachment's Position stays LOCAL to
    /// its attach point (see ResolveWorldTransform), so the region coordinate the simulator now
    /// sends was read as an offset and the object was drawn a hundred metres from the avatar.
    /// It was reported in-world as an object vanishing the moment it was resized -- a resize is
    /// simply the first thing that makes the simulator send a fresh position.
    ///
    /// The other half is invisible: ObjectRenderer refuses to build a standalone visual for
    /// anything carrying this component, so the object was drawn by the avatar renderer only,
    /// at a bone, wherever it had once hung.
    ///
    /// Only called when the parent is KNOWN. A parent that has not streamed in yet is the
    /// ordinary out-of-order case and must not be read as "not worn".
    /// </remarks>
    private void ClearAttachment(Entity entity, TransformComponent transform, ulong region)
    {
        if (entity.GetComponent<AttachmentComponent>() == null) return;

        entity.RemoveComponent<AttachmentComponent>();

        // The transform was composed earlier in this same update, while the answer was still
        // "worn" -- which is exactly the case that leaves Position local. Redo it.
        ResolveWorldTransform(transform, region);
        _world.NotifyComponentUpdated(entity, transform);
        AttachmentCleared?.Invoke(this, entity);

        // Children inherited the status from this entity (PropagateAttachmentToChildren); they
        // lose it with it.
        if (!_children.TryGetValue((region, entity.LocalId), out var set)) return;
        foreach (var childId in set.ToList())
        {
            var child = _world.GetEntity(childId);
            var childTransform = child?.GetComponent<TransformComponent>();
            if (child == null || childTransform == null) continue;
            ClearAttachment(child, childTransform, region);
        }
    }

    private void SetAttachment(Entity entity, System.Guid avatarEntityId, byte attachmentPoint)
    {
        var attachment = entity.GetComponent<AttachmentComponent>();
        bool changed = attachment == null || attachment.AvatarEntityId != avatarEntityId || attachment.AttachmentPoint != attachmentPoint;
        if (attachment == null)
        {
            attachment = new AttachmentComponent(avatarEntityId, attachmentPoint);
            entity.SetComponent(attachment);
        }
        else
        {
            attachment.AvatarEntityId = avatarEntityId;
            attachment.AttachmentPoint = attachmentPoint;
        }
        _world.NotifyComponentUpdated(entity, attachment);

        // Cascade to any children already waiting on this entity — they may have streamed in
        // before this entity itself became an attachment (out-of-order arrival is common: a
        // linkset's prims and the avatar itself can each arrive in any order).
        if (changed) PropagateAttachmentToChildren(entity.RegionHandle, entity.LocalId, avatarEntityId, attachmentPoint);
    }

    /// <summary>Pushes attachment status down to every child already indexed under
    /// <paramref name="localId"/>, recursively — see the cascading-attachment comment in
    /// <see cref="ApplyObjectUpdate"/> for why every level of a linked attachment needs this,
    /// not just the immediate children of the avatar.</summary>
    private void PropagateAttachmentToChildren(ulong region, uint localId, System.Guid avatarEntityId, byte attachmentPoint)
    {
        if (!_children.TryGetValue((region, localId), out var set) || set.Count == 0) return;

        foreach (var childId in set.ToList())
        {
            var child = _world.GetEntity(childId);
            if (child == null) continue;
            var existing = child.GetComponent<AttachmentComponent>();
            if (existing != null && existing.AvatarEntityId == avatarEntityId && existing.AttachmentPoint == attachmentPoint) continue;

            SetAttachment(child, avatarEntityId, attachmentPoint);
        }
    }

    /// <summary>Wires up attachment roots that streamed in before their wearer's avatar entity
    /// existed. The avatar's direct children (indexed by parent local id) are exactly its
    /// attachment roots; each root's own children cascade via <see cref="SetAttachment"/>.
    /// Called when an avatar appears/updates.</summary>
    private void LinkPendingAttachments(ulong region, uint avatarLocalId, System.Guid avatarEntityId)
    {
        if (!_children.TryGetValue((region, avatarLocalId), out var set) || set.Count == 0) return;

        foreach (var childId in set.ToList())
        {
            var child = _world.GetEntity(childId);
            if (child == null) continue;
            if (child.GetComponent<AttachmentComponent>()?.AvatarEntityId == avatarEntityId) continue;

            byte point = child.GetComponent<PrimitiveComponent>()?.AttachmentPoint ?? 0;
            SetAttachment(child, avatarEntityId, point);
        }
    }

    /// <summary>Computes a transform's world-space Position/Rotation from its parent (linkset
    /// root). Roots, avatar attachments, and orphans (parent not yet present) stay at their
    /// local values.</summary>
    private void ResolveWorldTransform(TransformComponent t, ulong region)
    {
        if (t.ParentLocalId == 0)
        {
            t.Position = t.LocalPosition;
            t.Rotation = t.LocalRotation;
            return;
        }

        var parent = _world.GetEntity(region, t.ParentLocalId);
        var parentT = parent?.GetComponent<TransformComponent>();
        if (parent == null || parentT == null || parent.GetComponent<AvatarComponent>() != null)
        {
            // No prim parent (yet) — leave local; avatar attachments are placed via bones.
            t.Position = t.LocalPosition;
            t.Rotation = t.LocalRotation;
            return;
        }

        // One composition, shared with the inverse the edit tools need (LinksetTransform) so the
        // two can never drift apart.
        (t.Position, t.Rotation) = LinksetTransform.ToWorld(
            t.LocalPosition, t.LocalRotation, parentT.Position, parentT.Rotation);
    }

    /// <summary>Re-composes the world transform of every child linked to <paramref name="localId"/>
    /// — used when a linkset root arrives or moves so its children follow.</summary>
    /// <remarks>
    /// Public because the edit tools need it too. While a linkset is being dragged, only the root
    /// is written locally; without re-composing here the other prims sit still until the
    /// simulator's echo arrives and then jump, once per network update. The reference viewer has
    /// no such lag because its manipulators move every selected prim themselves.
    /// </remarks>
    public void RecomposeChildren(ulong region, uint localId)
    {
        if (!_children.TryGetValue((region, localId), out var set) || set.Count == 0) return;

        foreach (var childId in set.ToList())
        {
            var child = _world.GetEntity(childId);
            var ct = child?.GetComponent<TransformComponent>();
            if (child == null || ct == null) continue;
            ResolveWorldTransform(ct, region);
            _world.NotifyComponentUpdated(child, ct);
            RecomposeChildren(region, child.LocalId);
        }
    }

    /// <summary>BUG-NET-17: how close an avatar update has to be to a teleport's destination
    /// before the simulator counts as having caught up. Generous, because the agent starts
    /// moving again immediately and the first post-teleport updates are already drifting from
    /// the exact arrival point -- it only has to separate "near where we went" from "still at
    /// the place we left", and those are usually hundreds of metres apart.</summary>
    private const float PostTeleportSettleMeters = 12f;

    private void ApplyAvatarUpdate(AvatarUpdateEvent e)
    {
        // Cheap and unconditional: an avatar update is rare compared with an object update, and
        // deciding here whether the entity is genuinely new would cost more than the rebuild it
        // avoids.
        _avatarCacheDirty = true;

        if (e.IsLocalAgent)
        {
            // Ensure no other entity is marked as the local agent (e.g. leftover from a previous region after teleport)
            var oldAgent = FindLocalAgent();
            if (oldAgent != null && (oldAgent.RegionHandle != e.RegionHandle || oldAgent.LocalId != e.LocalId))
            {
                // FEAT-NET-06: A teleport re-keys the existing self entity to the new region.
                // It must NOT remove and recreate the entity, which would tear down the skeleton/rig in AvatarRenderer!
                ulong oldRegion = oldAgent.RegionHandle;
                uint oldLocalId = oldAgent.LocalId;
                _world.RekeyEntity(oldAgent, e.RegionHandle, e.LocalId);
                if (oldAgent.GetComponent<MetadataComponent>()?.Id is { } agentId && agentId != System.Guid.Empty)
                {
                    _objectIndex[agentId] = (e.RegionHandle, e.LocalId);
                }
                if (_children.TryGetValue((oldRegion, oldLocalId), out var childSet))
                {
                    _children.Remove((oldRegion, oldLocalId));
                    var newKey = (e.RegionHandle, e.LocalId);
                    if (!_children.TryGetValue(newKey, out var existingSet))
                        _children[newKey] = childSet;
                    else
                        existingSet.UnionWith(childSet);
                }
            }
        }

        var entity = _world.GetOrCreateEntity(e.RegionHandle, e.LocalId);

        // MVP2-1: while seated, AvatarController stops writing this entity's Z/Rotation each
        // frame (its ground-clamp/camera-yaw ownership is suspended -- see its own isSitting
        // gate), so the seated local agent needs the network's full Position/Rotation applied
        // exactly like a remote avatar, not just X/Y with a stale local Z (see the two guards
        // below, and their ExtrapolateMovement counterparts).
        bool isSeatedLocalAgent = e.IsLocalAgent && e.SittingOnLocalId != 0;

        var transform = entity.GetComponent<TransformComponent>();
        if (transform == null)
        {
            transform = new TransformComponent(e.Position, e.Rotation);
            entity.SetComponent(transform);
        }
        else
        {
            // TargetPosition is server-authoritative for every avatar, local included — matches the
            // real viewer (LLAgent::getPositionAgent() mirrors LLVOAvatarSelf's network-driven
            // position; there is no separate client-predicted position it reconciles against, see
            // llagent.cpp/llviewerobject.cpp). AvatarController no longer predicts the local agent's
            // position for this exact reason: two independent authorities (client dead reckoning +
            // this network echo) fighting every packet was what originally caused the avatar to
            // visibly pop sideways while walking.
            //
            // The RENDERED Position, however, is NOT set directly to e.Position here anymore. Live-
            // tested console data (2026-07-23, OSGrid) showed 1-2+ second packet gaps recurring
            // throughout ordinary walking -- a genuine server/network characteristic of a busy sim,
            // not something extrapolation can hide (there's no data to extrapolate from during a
            // real gap). ExtrapolateMovement's short phase-out window correctly freezes TargetPosition
            // rather than drifting in a stale direction during such a gap, but hard-snapping Position
            // straight to TargetPosition the instant a delayed packet finally lands still reads as a
            // repeated multi-metre pop -- not an edge case on this kind of connection, a routine
            // occurrence. Below a "clearly still normal movement" distance, Position instead eases
            // toward TargetPosition over a few frames (ExtrapolateMovement/PositionSmoothingRate),
            // turning that catch-up into a quick glide. A genuinely large jump (teleport, sit/stand,
            // initial spawn) still snaps instantly -- those SHOULD look instant, not smoothed.
            // The LOCAL agent's Z is NOT taken from the network at all. AvatarController's
            // ground-clamp runs every frame independent of any packet (a physics raycast against
            // the actual rendered scene, producing groundHeight + halfBodyZ -- see its own doc
            // comment, "Second Life physics model: transform.Position.Z is the collision cylinder
            // center") and writes straight into transform.Position.Z. That's a SEPARATE, already-
            // authoritative source for local Z; feeding the network's e.Position.Z into
            // TargetPosition as well made two independent systems fight over Z every single frame
            // -- not just at packet-arrival moments like the X/Y fight this whole investigation
            // fixed, but continuously, 60 times a second, regardless of network timing. That fight
            // is the far more likely source of judder that persisted unchanged through every
            // network-timing fix in this file (confirmed by live-test feedback: "genau so
            // ruckelig" after the race fix, dilation scaling, phase-out tuning, and rotation
            // smoothing had already landed -- none of which touch this). Remote avatars have no
            // local ground-clamp, so they still take Z from the network as before.
            // BUG-NET-17: a teleport bypasses the local-Z hold entirely -- see IsTeleport's doc
            // comment. Taking the network Z here (rather than transform.Position.Z) is exactly the
            // "two authorities fighting" case the comment above warns against for ORDINARY
            // movement, but there is no fight here: nothing else claims Z during the one frame a
            // teleport resync lands, and AvatarController's ground-clamp re-establishes normal
            // local ownership on its very next tick against the NEW position.
            var targetPosition = (e.IsLocalAgent && !isSeatedLocalAgent && !e.IsTeleport)
                ? new Vector3(e.Position.X, e.Position.Y, transform.Position.Z)
                : e.Position;

            float targetDelta = Vector3.Distance(transform.TargetPosition, targetPosition);

            if (e.IsTeleport || targetDelta > TeleportSnapDistanceMeters)
            {
                transform.Position = targetPosition;
            }
            transform.TargetPosition = targetPosition;
            // Rotation is NOT set directly here -- see TargetRotation's doc comment. Hard-snapping
            // it every packet (previously: transform.Rotation = e.Rotation) was fine for straight
            // walking but visibly choppy while turning; ExtrapolateMovement slerps Rotation toward
            // TargetRotation every frame instead. EXCEPT for the local agent: AvatarController
            // already hard-writes transform.Rotation directly, every 100ms, from the player's own
            // camera yaw (instant local input, not something that should wait on/blend with a
            // network round-trip -- same reasoning as local Z above). Feeding the network's echo of
            // our own previously-sent rotation into TargetRotation too made THAT fight
            // AvatarController's fresh writes every frame: slerp pulls toward a latency-delayed
            // echo of an older yaw for up to 100ms, then AvatarController snaps to the current yaw,
            // repeat -- a sawtooth on every single frame, not just at packet-arrival moments,
            // regardless of turning. Remote avatars have no local camera input, so they keep the
            // full network-driven TargetRotation/slerp path.
            if (!e.IsLocalAgent || isSeatedLocalAgent)
            {
                transform.TargetRotation = e.Rotation;
            }
            entity.SetComponent(transform);
        }
        transform.Velocity = e.Velocity;
        transform.TimeSinceUpdate = 0f;
        transform.TimeDilation = System.Math.Clamp(e.TimeDilation, 0f, 1f);
        _world.NotifyComponentUpdated(entity, transform);

        var avatar = entity.GetComponent<AvatarComponent>();
        if (avatar == null)
        {
            avatar = new AvatarComponent(e.AgentId, e.FirstName, e.LastName, e.IsLocalAgent);
            if (_displayNames.TryGetValue(e.AgentId, out var knownDisplayName)) avatar.DisplayName = knownDisplayName;
            avatar.ScaleZ = e.ScaleZ;
            avatar.GroupTitle = e.GroupTitle ?? string.Empty;  // FEAT-UI-30
            avatar.SittingOnLocalId = e.SittingOnLocalId;
            avatar.SupportPlane = e.SupportPlane;
            entity.SetComponent(avatar);
        }
        else
        {
            // Don't let a later update that failed to resolve these (e.g. a bare TerseObjectUpdate
            // whose Prim isn't an Avatar, or whose LocalID missed LibreMetaverse's ObjectsAvatars
            // cache -- see GridSession's own AgentId-resolution fix, commit 6bcf31e) regress fields
            // we'd already resolved correctly from an earlier update. Found 2026-07-22 (round 5):
            // this used to overwrite unconditionally, so a resolved AgentId could silently revert
            // to Guid.Empty mid-session -- which then re-opens the entity to
            // FindAvatarEntityByAgentId's "any unresolved avatar" fallback match, letting a LATER,
            // unrelated avatar's appearance/animation event land on the WRONG (already-resolved)
            // entity and clobber its real data with someone else's (or stale/default) values.
            if (e.AgentId != System.Guid.Empty) avatar.AgentId = e.AgentId;
            // An avatar first seen before its agent id resolved has no name yet; pick it up now.
            if (string.IsNullOrEmpty(avatar.DisplayName) && _displayNames.TryGetValue(avatar.AgentId, out var lateDisplayName))
                avatar.DisplayName = lateDisplayName;
            if (!string.IsNullOrEmpty(e.FirstName)) avatar.FirstName = e.FirstName;
            if (!string.IsNullOrEmpty(e.LastName)) avatar.LastName = e.LastName;
            // FEAT-UI-30: null is "this event does not carry a title" (a terse update, or the
            // self-teleport echo), empty is the simulator saying there is none -- so an empty
            // string MUST overwrite, or a title could never be taken off again.
            if (e.GroupTitle != null) avatar.GroupTitle = e.GroupTitle;
            avatar.IsLocalAgent = e.IsLocalAgent;
            if (e.ScaleZ > 0f) avatar.ScaleZ = e.ScaleZ;
            uint prevSittingOnLocalId = avatar.SittingOnLocalId;
            avatar.SittingOnLocalId = e.SittingOnLocalId;
            if (prevSittingOnLocalId != 0 && e.SittingOnLocalId == 0)
            {
                HandleAvatarStoodUp(entity, avatar);
            }
            // Kept, not overwritten with null. Only the 140- and 76-byte ObjectData layouts carry
            // a collision plane, so an update that used a shorter layout says nothing about the
            // support surface -- it did not report that the avatar is standing on nothing. Letting
            // such an update blank the plane would hand the ground check a null exactly as often as
            // the simulator happened to send a compact update, which is the same "absence read as
            // information" mistake the light-ExtraParams latch made. A CROSS-region teleport drops
            // the whole entity, so a plane cannot survive that one; a SAME-region ("local") one
            // keeps this exact entity, so without the IsTeleport branch below the OLD plane -- a
            // real surface reading that no longer applies anywhere near the new position -- would
            // silently keep steering the ground clamp (BUG-NET-17: an avatar teleporting to a very
            // different height rendered stuck at the OLD one).
            if (e.IsTeleport)
            {
                // Clear it, and remember where we went: the next few updates can still be
                // pre-teleport ones the simulator had already queued.
                avatar.SupportPlane = null;
                avatar.PendingTeleportDestination = e.Position;
            }
            else if (avatar.PendingTeleportDestination is { } destination)
            {
                if (Vector3.Distance(e.Position, destination) <= PostTeleportSettleMeters)
                {
                    // The simulator is talking about the new location now, so its plane is
                    // about the new location too.
                    avatar.PendingTeleportDestination = null;
                    if (e.SupportPlane.HasValue) avatar.SupportPlane = e.SupportPlane;
                }
                // Otherwise: a stale in-flight update. Its plane describes where we WERE.
            }
            else if (e.SupportPlane.HasValue)
            {
                avatar.SupportPlane = e.SupportPlane;
            }
            entity.SetComponent(avatar);
        }
        _world.NotifyComponentUpdated(entity, avatar);

        // An appearance that arrived before this entity existed is applied now.
        ApplyHeldAppearance(avatar.AgentId, e.RegionHandle);

        // Link any worn mesh that arrived before this avatar entity existed.
        LinkPendingAttachments(e.RegionHandle, e.LocalId, entity.Id);
    }

    /// <summary>Finds the entity carrying <paramref name="agentId"/>'s AvatarComponent, healing the
    /// AgentId-race gap documented on <see cref="ApplyAvatarUpdate"/>: a remote avatar's entity can
    /// exist (created from a bare TerseObjectUpdate, LocalId only) with its AvatarComponent.AgentId
    /// still <see cref="System.Guid.Empty"/> because LibreMetaverse's ObjectsAvatars cache hadn't
    /// resolved the full AgentID yet when GridSession raised the event (see GridSession's own fix,
    /// commit 6bcf31e). A subsequent full ObjectUpdate eventually heals that entity's AgentId via
    /// <see cref="ApplyAvatarUpdate"/> — but ANY per-agent network message that can arrive in the
    /// meantime (animations, appearance/VisualParams, ...) needs this same fallback or it silently
    /// drops forever: an exact-match lookup against an AgentId that's still Guid.Empty never
    /// matches, and unlike ObjectUpdate these messages aren't re-sent on every tick, so a dropped
    /// appearance packet means that avatar keeps its default shape/body-size for the rest of the
    /// session (originally caught only for animations — this shares the fix so appearance gets it
    /// too, since a remote avatar's real BodySizeZ/FootOffsetY come from VisualParams).</summary>
    private Entity? FindAvatarEntityByAgentId(System.Guid agentId)
    {
        var entity = _world.Query<AvatarComponent>()
            .FirstOrDefault(ent => ent.GetComponent<AvatarComponent>()?.AgentId == agentId);

        if (entity == null && agentId != System.Guid.Empty)
        {
            entity = _world.Query<AvatarComponent>()
                .FirstOrDefault(ent =>
                {
                    var av = ent.GetComponent<AvatarComponent>();
                    return av != null && !av.IsLocalAgent && (av.AgentId == System.Guid.Empty || av.AgentId == agentId);
                });
            if (entity != null)
            {
                var av = entity.GetComponent<AvatarComponent>()!;
                av.AgentId = agentId;
                entity.SetComponent(av);
            }
        }

        return entity;
    }

    /// <summary>The last <c>AvatarAppearance</c> per agent whose avatar entity did not exist yet. The
    /// sim sends it once and does not repeat it, but the entity only appears with the agent's first
    /// full ObjectUpdate -- on a heavy region load that was ~20 s after the relay, and an event that
    /// finds no entity used to be dropped for good: the avatar stayed a blank mannequin (no bake, no
    /// shape) until a relog, and <c>GridSession</c> believed the bakes had been delivered so its
    /// watchdog never recovered them. Same shape as <see cref="_displayNames"/>. Drain thread only.</summary>
    private readonly Dictionary<System.Guid, AvatarAppearanceEvent> _heldAppearance = new();

    /// <summary>A bound so agents that never get an entity (left the interest list first) cannot pile up.</summary>
    private const int MaxHeldAppearances = 128;

    private void HoldAppearance(AvatarAppearanceEvent e)
    {
        if (e.AgentId == System.Guid.Empty) return;
        // An event that carries no shape (a bake-only update) must not blank the shape an earlier
        // one brought -- the same rule ApplyAvatarAppearance follows for a live entity.
        if (e.VisualParams is not { Length: > 0 }
            && _heldAppearance.TryGetValue(e.AgentId, out var earlier)
            && earlier.RegionHandle == e.RegionHandle
            && earlier.VisualParams is { Length: > 0 })
        {
            e = e with { VisualParams = earlier.VisualParams };
        }
        if (_heldAppearance.Count >= MaxHeldAppearances && !_heldAppearance.ContainsKey(e.AgentId))
            _heldAppearance.Clear();
        _heldAppearance[e.AgentId] = e;
    }

    /// <summary>Applies the held appearance of <paramref name="agentId"/> to its now-existing entity.
    /// Only if it came from <paramref name="regionHandle"/>: after a teleport the same agent id turns
    /// up in another region, and the old region's textures say nothing about the new body.</summary>
    private void ApplyHeldAppearance(System.Guid agentId, ulong regionHandle)
    {
        if (agentId == System.Guid.Empty || !_heldAppearance.TryGetValue(agentId, out var held)) return;
        if (held.RegionHandle != regionHandle) return;
        _heldAppearance.Remove(agentId);
        ApplyAvatarAppearance(held);
    }

    private void ApplyAvatarAppearance(AvatarAppearanceEvent e)
    {
        var entity = FindAvatarEntityByAgentId(e.AgentId);

        if (entity == null)
        {
            HoldAppearance(e);
            return;
        }

        var avatar = entity.GetComponent<AvatarComponent>()!;
        // An empty parameter array means "this event carries no shape", NOT "use the default
        // shape" -- clobbering a good set with it would visibly reset the avatar's proportions.
        // A bake-completion event legitimately carries fresh textures but no shape of its own
        // (FEAT-AVATAR-01: GridSession.OnAppearanceSet), so keep whatever shape we already had.
        if (e.VisualParams is { Length: > 0 })
        {
            avatar.VisualParams = e.VisualParams;
            if (e.VisualParams.Length > 31)
                avatar.IsMale = e.VisualParams[31] > 127;
        }
        avatar.BakedTextures = e.BakedTextures;
        avatar.HoverOffsetZ = e.HoverOffsetZ;
        entity.SetComponent(avatar);
        _world.NotifyComponentUpdated(entity, avatar);
    }

    /// <summary>Display Names resolved so far, by agent id, whether or not that agent is in the
    /// world right now. The grid is asked for a name once per session per agent (see
    /// <c>GridSession.RequestDisplayName</c>), so the answer has to outlive the avatar entity:
    /// without this a name that arrived before the entity existed was dropped on the floor, and an
    /// avatar that left and came back was rebuilt with no display name and never asked for one
    /// again -- its nametag fell back to the legacy name for the rest of the session. Touched on
    /// the drain thread only, like everything else <see cref="Pump"/> applies.</summary>
    private readonly Dictionary<System.Guid, string> _displayNames = new();

    private void ApplyDisplayNameResolved(NameResolvedEvent e)
    {
        if (e.Id != System.Guid.Empty && !string.IsNullOrEmpty(e.Name)) _displayNames[e.Id] = e.Name;

        var entity = FindAvatarEntityByAgentId(e.Id);
        if (entity != null)
        {
            var avatar = entity.GetComponent<AvatarComponent>()!;
            avatar.DisplayName = e.Name;
            entity.SetComponent(avatar);
            _world.NotifyComponentUpdated(entity, avatar);
        }
    }

    private void ApplyAvatarAnimation(AvatarAnimationEvent e)
    {
        var entity = FindAvatarEntityByAgentId(e.AgentId);

        if (entity != null)
        {
            var avatar = entity.GetComponent<AvatarComponent>()!;
            avatar.ActiveAnimations = e.AnimationIds;
            avatar.AnimationSources = e.Sources;
            // FEAT-ANIM-03: resolve the seat here rather than where SittingOnLocalId is set -- the
            // seat prim may not have arrived yet at that moment, and this runs on every animation
            // change, which is exactly when the answer is needed.
            //
            // MetadataComponent.Id, NOT Entity.Id: the latter is an internal ECS identity
            // (Guid.NewGuid() per entity), while the animation sources carry the simulator's real
            // object UUID. Comparing the two could never match, so the seat rule never fired once
            // -- for anyone, on any seat. ApplyObjectProperties above already documents this exact
            // trap; it caught this one out anyway.
            avatar.SittingOnObjectId = avatar.SittingOnLocalId == 0
                ? Guid.Empty
                : _world.GetEntity(entity.RegionHandle, avatar.SittingOnLocalId)
                    ?.GetComponent<MetadataComponent>()?.Id ?? Guid.Empty;
            entity.SetComponent(avatar);
            _world.NotifyComponentUpdated(entity, avatar);
        }
    }

    /// <summary>BUG-NET-13: region handles for which we've already logged the first terrain patch /
    /// object update, so the "did the sim re-send after a teleport back?" diagnostic prints once
    /// per region, not per packet.</summary>
    private readonly HashSet<ulong> _regionDataLogged = new();

    // BUG-NET-21: what a region's terrain looked like when we left it. Measured on Second Life
    // (Agni): a region left and entered again sends only a fraction of its terrain packets (5-10
    // against 42 on a first arrival) -- the ground, its textures and the water height would be
    // missing. Why the simulator does that is not known. So the terrain is kept aside at
    // departure and put back when the region connects again; whatever the simulator does send
    // overwrites it.
    private const int MaxParkedTerrains = 4;
    private readonly Dictionary<ulong, RegionTerrain> _parkedTerrain = new();
    private readonly List<ulong> _parkedOrder = new();

    private void ParkTerrain(ulong regionHandle)
    {
        if (!_world.Terrains.TryGetValue(regionHandle, out var terrain)) return;
        _parkedTerrain[regionHandle] = terrain;
        _parkedOrder.Remove(regionHandle);
        _parkedOrder.Add(regionHandle);
        while (_parkedOrder.Count > MaxParkedTerrains)
        {
            _parkedTerrain.Remove(_parkedOrder[0]);
            _parkedOrder.RemoveAt(0);
        }
    }

    /// <summary>Puts the terrain kept at departure back, if this region has none loaded. Called
    /// before anything for the region touches the world's terrain.</summary>
    private void RestoreParkedTerrain(ulong regionHandle)
    {
        if (_world.Terrains.ContainsKey(regionHandle)) return;
        if (!_parkedTerrain.Remove(regionHandle, out var parked)) return;
        _parkedOrder.Remove(regionHandle);
        _world.RestoreTerrain(regionHandle, parked);
        System.Console.WriteLine($"[RegionData] restored the terrain held for region {regionHandle} " +
                                 "(the simulator does not resend it to a returning agent)");
    }

    private void ApplyTerrainPatch(TerrainPatchEvent e)
    {
        RestoreParkedTerrain(e.RegionHandle);
        bool firstPatch = !_world.Terrains.ContainsKey(e.RegionHandle);
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle, e.RegionSizeX, e.RegionSizeY);
        terrain.ApplyPatch(e.X, e.Y, e.HeightMap);
        _world.NotifyTerrainUpdated(e.RegionHandle);
        if (firstPatch)
            System.Console.WriteLine($"[RegionData] first terrain patch for region {e.RegionHandle}");
    }

    private void ApplyTerrainSettings(TerrainSettingsEvent e)
    {
        RestoreParkedTerrain(e.RegionHandle);
        var terrain = _world.GetOrCreateTerrain(e.RegionHandle, e.RegionSizeX, e.RegionSizeY);

        // BUG-NET-21: a region whose handshake was never sent again reports nothing filled in.
        // That is "unknown", not "no textures, water at zero" -- keep what we hold.
        bool unfilled = e.Detail0 == Guid.Empty && e.Detail1 == Guid.Empty
                        && e.Detail2 == Guid.Empty && e.Detail3 == Guid.Empty;
        if (unfilled && terrain.TerrainDetail0 != Guid.Empty)
        {
            _world.NotifyTerrainSettingsUpdated(e.RegionHandle); // the restored terrain still needs its textures
            return;
        }

        terrain.TerrainDetail0 = e.Detail0;
        terrain.TerrainDetail1 = e.Detail1;
        terrain.TerrainDetail2 = e.Detail2;
        terrain.TerrainDetail3 = e.Detail3;

        Array.Copy(e.StartHeights, terrain.TerrainStartHeights, 4);
        Array.Copy(e.HeightRanges, terrain.TerrainHeightRanges, 4);

        terrain.WaterHeight = e.WaterHeight;

        _world.NotifyTerrainSettingsUpdated(e.RegionHandle);
    }

    private void ApplyObjectProperties(ObjectPropertiesEvent e)
    {
        // e.ObjectId is the simulator's real object UUID, not Entity.Id (an internal ECS
        // identity generated per-entity) -- resolve via the MetadataComponent seeded from
        // ObjectUpdateEvent, the same way avatar entities are resolved by AgentId.
        var entity = _world.Query<MetadataComponent>()
            .FirstOrDefault(ent => ent.GetComponent<MetadataComponent>()?.Id == e.ObjectId);
        if (entity == null) return;

        var meta = entity.GetComponent<MetadataComponent>();
        if (meta == null)
        {
            meta = new MetadataComponent(e.ObjectId);
            entity.SetComponent(meta);
        }

        meta.Name = e.Name;
        meta.Description = e.Description;
        // ObjectPropertiesFamily never carries CreatorID (always System.Guid.Empty there) -- only
        // the full ObjectProperties message does. Don't let a family response that arrives after
        // the full one blank out a CreatorID we already learned.
        if (e.CreatorId != System.Guid.Empty) meta.CreatorId = e.CreatorId;
        meta.OwnerId = e.OwnerId;
        meta.GroupId = e.GroupId;
        meta.Locked = !e.OwnerCanMove;
        meta.OwnerCanModify = e.OwnerCanModify;
        meta.OwnerCanCopy = e.OwnerCanCopy;
        meta.OwnerCanTransfer = e.OwnerCanTransfer;
        meta.SaleType = e.SaleType;
        meta.SalePrice = e.SalePrice;

        _world.NotifyComponentUpdated(entity, meta);
    }

    /// <summary>Unlike ObjectProperties, PhysicsProperties carries a real LocalID -- resolve
    /// directly by (region, localId) like ApplyObjectUpdate does, no MetadataComponent lookup
    /// needed.</summary>
    private void ApplyPhysicsProperties(PhysicsPropertiesEvent e)
    {
        var entity = _world.GetEntity(e.RegionHandle, e.LocalId);
        if (entity == null) return;

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim == null) return;

        prim.PhysicsShapeType = e.ShapeType;
        prim.PhysicsDensity = e.Density;
        prim.PhysicsFriction = e.Friction;
        prim.PhysicsRestitution = e.Restitution;
        prim.PhysicsGravity = e.GravityMultiplier;
        prim.HasPhysicsProperties = true;
        _world.NotifyComponentUpdated(entity, prim);
    }

    /// <summary>MVP3-3 Phase 1: like ApplyPhysicsProperties, this can arrive for an object that
    /// has since left the world (the fetch completed after the prim left interest list) --
    /// silently drop it.</summary>
    private void ApplyObjectMedia(ObjectMediaEvent e)
    {
        var entity = _world.GetEntity(e.RegionHandle, e.LocalId);
        if (entity == null) return;

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim == null) return;

        prim.MediaVersion = e.Version;
        prim.MediaFaces = e.Faces;
        _world.NotifyComponentUpdated(entity, prim);
    }

    /// <summary>How many ObjectAnimation lists are held for objects that have not arrived yet.
    /// Past this the OLDEST is given up: an object that never materialises (out of interest range,
    /// culled, killed before its first update) must not pin its list for the rest of the session.
    /// Far above any real count -- a region has a handful of animesh, and a list is only held
    /// until the object's first ObjectUpdate, which normally follows within the same second.</summary>
    public const int PendingObjectAnimationLimit = 2048;

    private sealed record PendingAnimationList(System.Guid ObjectId, ulong Region, SignaledAnimation[] Animations);

    // Lists for objects that have no entity yet, oldest first, plus a by-UUID handle on each node
    // so replacing or consuming one is O(1). Pump thread only.
    private readonly LinkedList<PendingAnimationList> _pendingAnimations = new();
    private readonly Dictionary<System.Guid, LinkedListNode<PendingAnimationList>> _pendingAnimationNodes = new();

    // Object UUID -> where it lives. The UUID is what ObjectAnimation addresses and the only thing
    // that identifies the object across a LocalID being recycled; scanning every entity's
    // MetadataComponent per message (as ApplyObjectProperties does for its rarer message) is what
    // a region full of animesh would pay for on every animation change. Dropped with the entity
    // (OnEntityRemoved).
    private readonly Dictionary<System.Guid, (ulong Region, uint LocalId)> _objectIndex = new();

    /// <summary>FEAT-ANIMESH-02. Mirrors <c>process_object_animation</c> (llviewermessage.cpp:4108):
    /// the message REPLACES the prim's whole list, an empty list stops everything, and the list is
    /// kept even if the object has not arrived (the viewer stores it in a map keyed by UUID before it
    /// looks the object up). Unlike the viewer's map, which is never erased, a held list is dropped
    /// with its region and the oldest are given up past <see cref="PendingObjectAnimationLimit"/>.</summary>
    private void ApplyObjectAnimation(ObjectAnimationEvent e)
    {
        if (e.ObjectId == System.Guid.Empty) return;

        // The component owns its list: the event's collection belongs to whoever raised it.
        var animations = e.Animations is { Count: > 0 }
            ? e.Animations.ToArray()
            : System.Array.Empty<SignaledAnimation>();

        var entity = FindObjectEntity(e.ObjectId);
        if (entity == null)
        {
            HoldAnimations(e.RegionHandle, e.ObjectId, animations);
            return;
        }

        // It has a home now; nothing is waiting for it any more.
        TakePendingAnimations(e.ObjectId);

        var prim = entity.GetComponent<PrimitiveComponent>();
        if (prim == null) return;

        // The sim repeats a prim's list whenever anything about its animation state changes. An
        // unchanged list must not make every listener (the renderer) re-run for it; a changed
        // sequence id is a change -- it is how the sim restarts an animation already playing.
        if (prim.SignaledAnimations.SequenceEqual(animations)) return;

        prim.SignaledAnimations = animations;
        _world.NotifyComponentUpdated(entity, prim);
    }

    /// <summary>The entity of the object with this UUID, or null if there is none. The index is
    /// kept exact rather than checked on use: set by every ObjectUpdate, and removed by
    /// <see cref="OnEntityRemoved"/> whenever its entity leaves the world by any route, so a
    /// recycled LocalID can never hand one object's list to another.</summary>
    private Entity? FindObjectEntity(System.Guid objectId)
        => _objectIndex.TryGetValue(objectId, out var at) ? _world.GetEntity(at.Region, at.LocalId) : null;

    private void HoldAnimations(ulong region, System.Guid objectId, SignaledAnimation[] animations)
    {
        // Whatever was held is superseded -- and an empty list is itself the newest word ("stop"),
        // so it replaces rather than adds: there is nothing to apply later.
        TakePendingAnimations(objectId);
        if (animations.Length == 0) return;

        while (_pendingAnimationNodes.Count >= PendingObjectAnimationLimit && _pendingAnimations.First is { } oldest)
        {
            _pendingAnimations.RemoveFirst();
            _pendingAnimationNodes.Remove(oldest.Value.ObjectId);
        }

        _pendingAnimationNodes[objectId] =
            _pendingAnimations.AddLast(new PendingAnimationList(objectId, region, animations));
    }

    /// <summary>Removes and returns the list held for this object, or null if none is.</summary>
    private SignaledAnimation[]? TakePendingAnimations(System.Guid objectId)
    {
        if (!_pendingAnimationNodes.Remove(objectId, out var node)) return null;

        _pendingAnimations.Remove(node);
        return node.Value.Animations;
    }

    /// <summary>A region went away: lists held for objects that were to arrive in it never will.</summary>
    private void DropPendingAnimations(ulong region)
    {
        for (var node = _pendingAnimations.First; node != null;)
        {
            var next = node.Next;
            if (node.Value.Region == region)
            {
                _pendingAnimations.Remove(node);
                _pendingAnimationNodes.Remove(node.Value.ObjectId);
            }
            node = next;
        }
    }

    /// <summary>Fires for every way an entity leaves the world -- a kill, a region unloading, the
    /// stale local agent being replaced -- so the UUID index never outlives its entity.</summary>
    private void OnEntityRemoved(object? sender, EntityEventArgs e)
    {
        var objectId = e.Entity.GetComponent<MetadataComponent>()?.Id ?? System.Guid.Empty;
        if (objectId == System.Guid.Empty) return;

        // Only if it still points at THIS entity: a recycled LocalID may already carry another
        // object whose entry replaced the old one.
        if (_objectIndex.TryGetValue(objectId, out var at)
            && at.Region == e.Entity.RegionHandle && at.LocalId == e.Entity.LocalId)
        {
            _objectIndex.Remove(objectId);
        }
    }

    private void ApplyObjectRemoved(ObjectRemovedEvent e)
    {
        // A removal event does not say whether it was an avatar, so assume it might have been.
        _avatarCacheDirty = true;

        HandleAttachmentRemoved(e.RegionHandle, e.LocalId);
        RemoveEntityRecursive(e.RegionHandle, e.LocalId);
    }

    private void HandleAttachmentRemoved(ulong regionHandle, uint localId)
    {
        var rootEntity = _world.GetEntity(regionHandle, localId);
        if (rootEntity == null) return;

        var linksetEntities = new List<Entity> { rootEntity };
        CollectChildrenEntities(regionHandle, localId, linksetEntities);

        AttachmentComponent? attachment = null;
        foreach (var ent in linksetEntities)
        {
            attachment = ent.GetComponent<AttachmentComponent>();
            if (attachment != null) break;
        }

        if (attachment == null) return;

        var avatarEntity = _world.GetEntity(attachment.AvatarEntityId);
        var avatar = avatarEntity?.GetComponent<AvatarComponent>();
        if (avatar == null || avatar.AnimationSources == null || avatar.AnimationSources.Count == 0) return;

        var removedSourceIds = new HashSet<System.Guid>();
        foreach (var ent in linksetEntities)
        {
            var metaId = ent.GetComponent<MetadataComponent>()?.Id ?? System.Guid.Empty;
            if (metaId != System.Guid.Empty) removedSourceIds.Add(metaId);
        }

        if (removedSourceIds.Count == 0) return;

        var animsToStop = new List<System.Guid>();
        foreach (var signal in avatar.AnimationSources)
        {
            if (removedSourceIds.Contains(signal.SourceObjectId))
            {
                animsToStop.Add(signal.AnimId);
            }
        }

        if (animsToStop.Count == 0) return;

        var animsToStopSet = new HashSet<System.Guid>(animsToStop);

        if (avatar.IsLocalAgent)
        {
            foreach (var animId in animsToStopSet)
            {
                SelfAnimationStopRequested?.Invoke(animId);
            }
        }

        if (avatar.ActiveAnimations != null)
        {
            avatar.ActiveAnimations = avatar.ActiveAnimations.Where(id => !animsToStopSet.Contains(id)).ToList();
        }
        avatar.AnimationSources = avatar.AnimationSources.Where(s => !animsToStopSet.Contains(s.AnimId)).ToList();

        avatarEntity!.SetComponent(avatar);
        _world.NotifyComponentUpdated(avatarEntity, avatar);
    }

    private void CollectChildrenEntities(ulong regionHandle, uint localId, List<Entity> result)
    {
        var key = (regionHandle, localId);
        if (_children.TryGetValue(key, out var childSet))
        {
            foreach (var childEntityId in childSet)
            {
                var childEntity = _world.GetEntity(childEntityId);
                if (childEntity != null)
                {
                    result.Add(childEntity);
                    CollectChildrenEntities(regionHandle, childEntity.LocalId, result);
                }
            }
        }
    }

    private void HandleAvatarStoodUp(Entity avatarEntity, AvatarComponent avatar)
    {
        if (avatar.SittingOnObjectId == System.Guid.Empty) return;

        var seatId = avatar.SittingOnObjectId;
        avatar.SittingOnObjectId = System.Guid.Empty;

        if (avatar.AnimationSources == null || avatar.AnimationSources.Count == 0) return;

        var animsToStop = new List<System.Guid>();
        foreach (var sig in avatar.AnimationSources)
        {
            if (sig.SourceObjectId == seatId)
            {
                animsToStop.Add(sig.AnimId);
            }
        }

        if (animsToStop.Count == 0) return;

        var animsToStopSet = new HashSet<System.Guid>(animsToStop);

        if (avatar.IsLocalAgent)
        {
            foreach (var animId in animsToStopSet)
            {
                SelfAnimationStopRequested?.Invoke(animId);
            }
        }

        if (avatar.ActiveAnimations != null)
        {
            avatar.ActiveAnimations = avatar.ActiveAnimations.Where(id => !animsToStopSet.Contains(id)).ToList();
        }
        avatar.AnimationSources = avatar.AnimationSources.Where(s => !animsToStopSet.Contains(s.AnimId)).ToList();

        avatarEntity.SetComponent(avatar);
        _world.NotifyComponentUpdated(avatarEntity, avatar);
    }

    private void RemoveEntityRecursive(ulong regionHandle, uint localId)
    {
        var key = (regionHandle, localId);
        if (_children.TryGetValue(key, out var childSet))
        {
            var childrenList = childSet.ToList();
            _children.Remove(key);
            foreach (var childEntityId in childrenList)
            {
                var childEntity = _world.GetEntity(childEntityId);
                if (childEntity != null)
                {
                    RemoveEntityRecursive(regionHandle, childEntity.LocalId);
                }
            }
        }
        _world.RemoveEntity(regionHandle, localId);
    }

    private void StartReconciliation()
    {
        var unconfirmed = _world.GetAllEntities()
            .Where(ent => ent.GetComponent<AttachmentComponent>()?.AwaitingReconfirmation == true)
            .ToList();

        if (unconfirmed.Count == 0)
        {
            _isReconciling = false;
            return;
        }

        _isReconciling = true;
        _reconcileStopwatch.Restart();
        _lastUpdateTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _updatesSinceReconcileStart = 0;
        _reconfirmedEntityIds.Clear();

        var localAgent = FindLocalAgent();
        _initialKeptCount = unconfirmed.Count + (localAgent != null ? 1 : 0);

        _keptObjectIds.Clear();
        foreach (var kept in unconfirmed)
            if (kept.GetComponent<MetadataComponent>()?.Id is { } keptId && keptId != System.Guid.Empty)
                _keptObjectIds.Add(keptId);
        if (localAgent?.GetComponent<MetadataComponent>()?.Id is { } agentUuid && agentUuid != System.Guid.Empty)
            _keptObjectIds.Add(agentUuid);
    }

    /// <summary>UUIDs of the entities a teleport kept (attachments awaiting reconfirmation and the local
    /// agent), for as long as the reconciliation lasts. See the fallback in <see cref="ApplyObjectUpdate"/>.</summary>
    private readonly HashSet<System.Guid> _keptObjectIds = new();

    private void CancelReconciliation()
    {
        _keptObjectIds.Clear();
        _isReconciling = false;
        _reconcileStopwatch.Reset();
        _reconfirmedEntityIds.Clear();
    }

    private void ApplyTeleportProgress(TeleportProgressEvent e)
    {
        if (e.Stage == TeleportStage.Failed || e.Stage == TeleportStage.Cancelled)
        {
            CancelReconciliation();
        }
    }

    private void NoteAttachmentReconfirmed(System.Guid entityId)
    {
        if (_isReconciling)
        {
            _reconfirmedEntityIds.Add(entityId);
            _lastUpdateTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    internal void CheckReconciliationProgress(double? elapsedOverride = null, double? quietSecondsOverride = null)
    {
        if (!_isReconciling) return;

        double elapsed = elapsedOverride ?? _reconcileStopwatch.Elapsed.TotalSeconds;
        double quiet = quietSecondsOverride ?? ((System.Diagnostics.Stopwatch.GetTimestamp() - _lastUpdateTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency);

        var unconfirmed = _world.GetAllEntities()
            .Where(ent => ent.GetComponent<AttachmentComponent>()?.AwaitingReconfirmation == true)
            .ToList();

        if (unconfirmed.Count == 0)
        {
            FinishReconciliation(unconfirmed, elapsed);
            return;
        }

        bool settled = (elapsed >= ReconciliationMinWaitSeconds && quiet >= ReconciliationQuietSeconds);
        bool timedOut = elapsed >= ReconciliationMaxWaitSeconds;

        if (settled || timedOut)
        {
            FinishReconciliation(unconfirmed, elapsed);
        }
    }

    private void FinishReconciliation(List<Entity> unconfirmedToRemove, double elapsedSeconds)
    {
        int removedCount = unconfirmedToRemove.Count;
        int reconfirmedCount = _reconfirmedEntityIds.Count;

        foreach (var entity in unconfirmedToRemove)
        {
            HandleAttachmentRemoved(entity.RegionHandle, entity.LocalId);
            RemoveEntityRecursive(entity.RegionHandle, entity.LocalId);
        }

        System.Console.WriteLine($"[TeleportKeep] kept {_initialKeptCount} self entities, {reconfirmedCount} re-confirmed, {removedCount} removed after {elapsedSeconds:0.#}s");

        _keptObjectIds.Clear();
        _isReconciling = false;
        _reconcileStopwatch.Reset();
        _reconfirmedEntityIds.Clear();
    }

    public void Dispose()
    {
        _source.ObjectUpdateReceived -= OnObjectUpdate;
        _source.AvatarUpdateReceived -= OnAvatarUpdate;
        _source.ObjectRemovedReceived -= OnObjectRemoved;
        _source.ObjectPropertiesReceived -= OnObjectProperties;
        _source.PhysicsPropertiesReceived -= OnPhysicsProperties;
        _source.ObjectMediaReceived -= OnObjectMedia;
        _source.TerrainPatchReceived -= OnTerrainPatch;
        _source.TerrainSettingsReceived -= OnTerrainSettings;
        _source.RegionDisconnectedReceived -= OnRegionDisconnected;
        _source.AvatarAppearanceReceived -= OnAvatarAppearance;
        _source.AvatarAnimationReceived -= OnAvatarAnimation;
        _source.ObjectAnimationReceived -= OnObjectAnimation;
        _world.EntityRemoved -= OnEntityRemoved;
    }
}
