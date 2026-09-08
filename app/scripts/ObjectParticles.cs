using System;
using Godot;
using SLNG.Assets;
using SLNG.Core.Components;

namespace SLNG.App;

/// <summary>
/// Renders one prim's SL particle system as a Godot <see cref="CpuParticles3D"/>.
///
/// <para>The node is a child of the prim's <c>MeshInstance3D</c>, so it inherits the prim's
/// position and rotation -- which is what SL wants, because the angle patterns emit along the
/// object's local axes (llviewerpartsource.cpp:412). It must NOT inherit the prim's scale:
/// particle sizes, burst radii and speeds are absolute metres, so a 5 m prim would otherwise
/// emit 5x everything. <see cref="SyncToParent"/> cancels the parent's scale out again.</para>
///
/// <para>SL emits <c>BurstPartCount</c> particles every <c>BurstRate</c> seconds; Godot emits a
/// fixed pool evenly over <c>Lifetime</c>. The two agree once the pool holds exactly as many
/// particles as SL has alive in steady state -- see
/// <see cref="ParticleSystemData.SteadyStateParticleCount"/> -- which is why the pool size is
/// derived rather than configured.</para>
///
/// <para>Direction comes from the emission normals, not from <see cref="CpuParticles3D.Spread"/>:
/// SL's patterns are a fan and a cone with an inner cutout (and, for old scripts, an extra
/// rotation), none of which Godot's single spread angle can express. Sampling the viewer's own
/// direction formula into <see cref="CpuParticles3D.EmissionNormals"/> reproduces all of them,
/// and pairs each direction with the position SL puts it at -- <c>BurstRadius</c> along the SAME
/// vector (llviewerpartsource.cpp:367).</para>
/// </summary>
public partial class ObjectParticles : CpuParticles3D
{
    /// <summary>Pool cap. A Godot engine budget, not an SL rule -- CPUParticles3D allocates and
    /// simulates every slot whether it is alive or not, so an emitter asking for 25k particles
    /// (SL allows a burst every 10 ms) must not be given them.</summary>
    private const int MaxPoolSize = 2048;

    /// <summary>How many emission directions to sample. Godot draws one at random per particle,
    /// so this is the resolution of the pattern, not the particle count.</summary>
    private const int DirectionSamples = 128;

    /// <summary>Guards the reciprocal in <see cref="SyncToParent"/> against a degenerate prim.</summary>
    private const float MinParentScale = 0.0001f;

    /// <summary>How much of an emitter's history to simulate up front so it does not visibly
    /// ramp up from empty when it streams into view.</summary>
    private const double MaxPreprocessSeconds = 2.0;

    /// <summary>Shared fallback for a system that names no texture. SL's own default is a soft
    /// round blob; a hard-edged quad reads as a bug, so this is a radial alpha falloff.</summary>
    private static Texture2D? _defaultTexture;

    /// <summary>The owning entity's id, for the diagnostic line only — set by
    /// <c>ObjectRenderer</c> so a "which emitter is object X" report is answerable from the log.</summary>
    public Guid EmitterEntityId { get; set; }

    private ParticleSystemData? _data;
    private Guid _textureId;

    /// <summary>Whether <see cref="_textureId"/> holds a resolved id rather than its default.
    /// Needed because <see cref="Guid.Empty"/> is itself a valid id here -- see ResolveTexture.</summary>
    private bool _textureResolved;
    private QuadMesh? _quad;
    private StandardMaterial3D? _drawMaterial;

    /// <summary>Seconds since this system was (re)configured, against
    /// <see cref="ParticleSystemData.SourceMaxAge"/>.</summary>
    private double _sourceAge;

    /// <summary>Exponentially-smoothed PART_END_SCALE / PART_END_COLOR. A flame script re-sends the
    /// whole system ~10x/sec with a randomised end scale/colour; the viewer's per-particle
    /// interpolation naturally averages that out (each live particle keeps interpolating along the
    /// end value that was current at ITS birth), which is what reads as a soft "morph". Godot's
    /// CpuParticles3D scale/colour curves are shared across the whole pool with no per-particle
    /// birth snapshot, so applying each new end value verbatim snaps every live particle at once --
    /// and because the random end scale frequently has a near-zero Y while X stays ~0.1, the whole
    /// emitter collapses to a flat horizontal streak on those frames. Smoothing the end value here
    /// approximates the viewer's averaging: the shared curve drifts instead of snapping.</summary>
    private System.Numerics.Vector2 _smoothEndScale;
    private System.Numerics.Vector4 _smoothEndColor;
    private System.Numerics.Vector2 _targetEndScale;
    private System.Numerics.Vector4 _targetEndColor;
    private bool _smoothSeeded;
    private bool _interpScale;
    private bool _interpColor;

    /// <summary>Time constant for easing the shared curve endpoint toward the latest end value the
    /// script sent. ~1.2 s ≈ the viewer's own morph pace. X and Y are eased independently, so the
    /// (independently jittering) raw X/Y feed through as an independent slow wander -- the flame
    /// gets a bit narrower, then a bit wider, gently, instead of holding one frozen aspect.</summary>
    private const float EndParamEaseTau = 1.2f;

    /// <summary>Persistent scale curves / colour ramp, re-pointed in place instead of reallocated.
    /// Created on the first Apply.</summary>
    private Curve? _curveX;
    private Curve? _curveY;
    private Curve? _curveZ;
    private Gradient? _colorRamp;

    /// <summary>SL's <c>PSYS_SRC_ACCEL</c> in Godot world axes. Applied as-is when particles live
    /// in world space, rotated into the emitter's frame when they follow the source.</summary>
    private Vector3 _worldAcceleration;

    /// <summary><c>PSYS_SRC_OMEGA</c>, converted to a Godot-local axis and rad/s.</summary>
    private Vector3 _omegaAxis = Vector3.Up;
    private float _omegaSpeed;
    private Quaternion _omegaRotation = Quaternion.Identity;

    /// <summary>Last parent transform the local transform and gravity were computed against.
    /// Seeded with NaN so the first <see cref="SyncToParent"/> always runs.</summary>
    private Transform3D _syncedAgainst = new(Basis.Identity, new Vector3(float.NaN, 0f, 0f));

    /// <summary>
    /// Applies a particle system to this node. Cheap to call on every ObjectUpdate: identical
    /// data is a no-op, so a prim that merely moved does not restart its particles.
    /// </summary>
    public void Apply(ParticleSystemData data, GpuCache gpuCache, AssetService assetService)
    {
        if (_data is not null && _data == data)
        {
            return;
        }

        // A flame/spark script commonly re-sends the WHOLE particle system every ObjectUpdate with
        // just a jittered PSYS_PART_END_SCALE or _END_COLOR (an "organic flicker" trick -- seen live
        // ~10x/sec). Restart() clears and refills the entire pool, which strobes the emitter dark
        // for a frame or two on every one of those, so it is now gated on a change that actually
        // needs a fresh pool: lifetime, particle count, pattern, emission geometry, or the flag
        // word. A pure scale/colour tweak just re-points the curves in place, the way the viewer
        // re-parameterises without dropping live particles.
        ParticleSystemData? prev = _data;
        bool structuralChange = prev is null
            || prev.PartMaxAge != data.PartMaxAge
            || prev.SteadyStateParticleCount(MaxPoolSize) != data.SteadyStateParticleCount(MaxPoolSize)
            || prev.Pattern != data.Pattern
            || prev.PartDataFlags != data.PartDataFlags
            || prev.SourceFlags != data.SourceFlags
            || prev.BurstRadius != data.BurstRadius
            || prev.BurstSpeedMin != data.BurstSpeedMin
            || prev.BurstSpeedMax != data.BurstSpeedMax
            || prev.InnerAngle != data.InnerAngle
            || prev.OuterAngle != data.OuterAngle
            || prev.BurstPartCount != data.BurstPartCount
            || prev.BurstRate != data.BurstRate;

        _data = data;
        // Reset the source age on EVERY re-send, not just a structural change. The viewer deletes
        // the old particle source and builds a fresh one (age 0) every time a particle block
        // arrives (llviewerobject.cpp LLViewerObject::setParticleSource -> deleteParticleSource +
        // createPSS) while leaving the already-emitted particles alive. A flame script that
        // re-sends ~10x/sec with a short PSYS_SRC_MAX_AGE was therefore expiring here between
        // re-sends -- _Process flips Emitting off on age, the next Apply flips it back on -- which
        // is the "an/aus" strobe. Re-arming every Apply keeps it emitting continuously while the
        // script is active, and it still stops correctly once the script stops re-sending.
        _sourceAge = 0.0;
        if (structuralChange)
        {
            _omegaRotation = Quaternion.Identity;
        }

        // PART_END_SCALE / _END_COLOR handling. A flicker script re-randomises these every server
        // frame; the viewer stays smooth because each particle captures the value current at ITS
        // birth and interpolates along that for its whole life. Godot's scale/colour curves are
        // SHARED across the pool, so re-pointing them to each incoming value pulses every live
        // particle in lockstep. Instead: Apply only records the raw target, and _Process eases the
        // shared endpoint toward it over EndParamEaseTau. X and Y ease independently so the flame's
        // aspect gently wanders (narrower / wider) the way the viewer's does, just pool-wide rather
        // than per-particle.
        _targetEndScale = new System.Numerics.Vector2(data.PartEndScaleX, data.PartEndScaleY);
        _targetEndColor = data.PartEndColor;
        if (structuralChange || !_smoothSeeded)
        {
            _smoothEndScale = _targetEndScale;
            _smoothEndColor = _targetEndColor;
            _smoothSeeded = true;
        }

        EnsureResources();

        if (data.IsInert)
        {
            // PartMaxAge 0 means the particle is already older than its maximum on its first
            // simulation step (llviewerpartsim.cpp:400) -- the viewer renders nothing, and so
            // must we. Godot would reject a Lifetime of 0 outright.
            Emitting = false;
            // Said out loud, because "this emitter is legitimately invisible" and "the renderer
            // dropped it" look identical from in-world -- and the bug that motivated all of this
            // presented exactly as an emitter that quietly rendered nothing.
            if (Diagnostics.Enabled)
            {
                if (Diagnostics.Enabled) GD.Print($"[Particles] inert: partMaxAge={data.PartMaxAge:0.###}s "
                    + $"burstCount={data.BurstPartCount} -- nothing to draw");
            }
            return;
        }

        SlParticleDataFlags partFlags = data.PartDataFlags;
        bool interpolateColor = partFlags.HasFlag(SlParticleDataFlags.InterpColor);
        bool interpolateScale = partFlags.HasFlag(SlParticleDataFlags.InterpScale);
        bool emissive = partFlags.HasFlag(SlParticleDataFlags.Emissive);
        bool followSource = partFlags.HasFlag(SlParticleDataFlags.FollowSrc);
        // PSYS_PART_FOLLOW_VELOCITY (llvopartgroup.cpp:518-547): the particle's quad ALWAYS faces
        // the camera; the flag only ROLLS that camera-facing quad in screen space so its local +Y
        // points along the screen-space projection of the velocity. It is NOT an align-to-velocity
        // that can turn the quad edge-on. Godot's CpuParticles3D.AlignYToVelocity does exactly that
        // wrong thing (quad goes edge-on, invisible from the side, and it also discards the
        // per-particle scale), so it is deliberately NOT used here -- a faithful implementation
        // needs a custom billboard shader that reads the velocity and applies the screen-space
        // roll. Tracked as a follow-up; for now these render as an un-rolled camera-facing quad,
        // same as every other particle.
        bool followVelocity = partFlags.HasFlag(SlParticleDataFlags.FollowVelocity);

        // ONLY the pool-shaping properties on a structural change. Assigning CpuParticles3D.Amount
        // (and, in Godot, several of its siblings) rebuilds the particle buffer and deactivates
        // every live particle -- doing that on every one of a flicker script's ~10-45 re-sends per
        // second wipes and re-seeds the whole flame that many times a second, which is the "in
        // Firestorm one morph takes ~1 s, in SLNG it has already looped 5 times" report. A pure
        // scale/colour re-send only needs the curves re-pointed (done below, unconditionally --
        // Godot samples those live, no reset).
        if (structuralChange)
        {
            Lifetime = data.PartMaxAge;
            Amount = data.SteadyStateParticleCount(MaxPoolSize);
            OneShot = false;
            LocalCoords = followSource;
            // An emitter usually predates our arrival in the region, so a system that ramps up
            // from empty is wrong twice over -- it is visible, and it is visible exactly when an
            // object streams into view. Capped because Godot preprocesses on the main thread in
            // 1/30 s steps: 30 s over a 2048 pool is 1.8M updates in one frame.
            Preprocess = Math.Min(data.PartMaxAge, MaxPreprocessSeconds);
            ConfigureEmission(data, followSource);
            ConfigureMaterial(emissive);
        }

        ConfigureColor(data, interpolateColor, _smoothEndColor);
        ConfigureScale(data, interpolateScale, _smoothEndScale);

        // Region axes (X east, Y north, Z up) -> Godot (X east, Y up, Z south).
        _worldAcceleration = new Vector3(
            data.PartAcceleration.X, data.PartAcceleration.Z, -data.PartAcceleration.Y);

        var omega = new Vector3(data.AngularVelocity.X, data.AngularVelocity.Z, -data.AngularVelocity.Y);
        _omegaSpeed = omega.Length();
        // BUG-NET-13: a non-finite / denormalised omega gives a non-unit or NaN axis, and the
        // per-frame `new Quaternion(_omegaAxis, ...)` in _Process then logs "Vector3 cannot be
        // normalized" every frame. Only accept a finite, positive, normalisable speed.
        _omegaAxis = (float.IsFinite(_omegaSpeed) && _omegaSpeed > 1e-6f && omega.IsFinite())
            ? omega / _omegaSpeed
            : Vector3.Up;
        if (!float.IsFinite(_omegaSpeed) || _omegaSpeed <= 1e-6f) _omegaSpeed = 0f;

        ResolveTexture(data.TextureId, gpuCache, assetService);

        // Force the transform/gravity sync: the pattern, the acceleration and the parent's scale
        // can all have changed under us.
        _syncedAgainst = new Transform3D(Basis.Identity, new Vector3(float.NaN, 0f, 0f));
        SyncToParent();

        Emitting = true;
        // Restart() clears and refills the whole pool -- only ever do it on the FIRST apply, for
        // the initial Preprocess pre-warm. The viewer keeps every already-emitted particle alive
        // across a re-send (only the source is rebuilt), which is what makes a flicker script read
        // as one continuous, overlapping, morphing flame instead of an on/off strobe. Even a
        // genuine structural change (pattern, count, lifetime) is applied live by Godot here --
        // Amount/Lifetime/emission all update without a restart -- so there is no case left that
        // needs the pool wiped.
        if (prev is null)
        {
            Restart();
        }

        // Always on, deduped per distinct config: a "horizontally smeared" or "wrong size"
        // emitter report needs the parsed START/END scale (X vs Y), the PART_FLAGS (FollowVelocity
        // / DataBlend / DataGlow are all unimplemented and could each cause it), and the node's
        // ACTUAL world scale after SyncToParent's counter-scale -- if that isn't ~(1,1,1) the
        // per-particle metre size is being multiplied by a residual, which BillboardKeepScale then
        // stretches. Same "one line per distinct config, not per frame" convention as [FaceAlpha].
        // Sig deliberately excludes PART_END_SCALE/COLOR -- those jitter every packet on a flicker
        // script and would turn this into per-frame spam; the structural state is what's worth one
        // line. The raw (un-smoothed) end scale still prints in the message for the first one.
        string sig = $"{data.Pattern}:{data.PartStartScaleX:0.###}x{data.PartStartScaleY:0.###}"
            + $":{data.PartMaxAge:0.##}:{Amount}:{data.PartDataFlags}:{data.SourceFlags}";
        if (_loggedSig != sig)
        {
            _loggedSig = sig;
            Vector3 ws = IsInsideTree() ? GlobalTransform.Basis.Scale : Vector3.One;
            float cx0 = ScaleCurveX?.Sample(0f) ?? -1f, cx1 = ScaleCurveX?.Sample(1f) ?? -1f;
            float cy0 = ScaleCurveY?.Sample(0f) ?? -1f, cy1 = ScaleCurveY?.Sample(1f) ?? -1f;
            if (Diagnostics.Enabled) GD.Print($"[Particles] obj={EmitterEntityId:N} {data.Pattern} pool={Amount} life={Lifetime:0.##}s "
                + $"srcMaxAge={data.SourceMaxAge:0.##} srcStartAge={data.SourceStartAge:0.##} "
                + $"burst={data.BurstPartCount}/{data.BurstRate:0.###}s "
                + $"speed={data.BurstSpeedMin:0.##}-{data.BurstSpeedMax:0.##} radius={data.BurstRadius:0.##} "
                + $"accel=({data.PartAcceleration.X:0.##},{data.PartAcceleration.Y:0.##},{data.PartAcceleration.Z:0.##}) "
                + $"startScale={data.PartStartScaleX:0.###}x{data.PartStartScaleY:0.###} "
                + $"endScale={data.PartEndScaleX:0.###}x{data.PartEndScaleY:0.###} "
                + $"partFlags={data.PartDataFlags} srcFlags={data.SourceFlags} "
                + $"localCoords={LocalCoords} nodeWorldScale=({ws.X:0.###},{ws.Y:0.###},{ws.Z:0.###}) "
                + $"quad={_quad?.Size} amt={ScaleAmountMin}-{ScaleAmountMax} split={SplitScale} "
                + $"curveX[{cx0:0.###}->{cx1:0.###}] curveY[{cy0:0.###}->{cy1:0.###}] "
                + $"startColor=({data.PartStartColor.X:0.##},{data.PartStartColor.Y:0.##},{data.PartStartColor.Z:0.##},a{data.PartStartColor.W:0.##}) "
                + $"endColor=({data.PartEndColor.X:0.##},{data.PartEndColor.Y:0.##},{data.PartEndColor.Z:0.##},a{data.PartEndColor.W:0.##}) "
                + $"bbMode={_drawMaterial?.BillboardMode} keepScale={_drawMaterial?.BillboardKeepScale} "
                + $"tex={(data.TextureId == Guid.Empty ? "default" : data.TextureId.ToString())}");
        }
    }

    /// <summary>Last config signature logged by <see cref="Apply"/>, so an emitter whose script
    /// re-sends the same system every ObjectUpdate prints once, not per frame.</summary>
    private string _loggedSig = "";

    public override void _Process(double delta)
    {
        if (_data is null || _data.IsInert)
        {
            return;
        }

        // The emitter's own lifetime. The viewer kills the source once start age plus elapsed
        // time passes max age, with 0 meaning "never" (llviewerpartsource.cpp:192).
        if (Emitting && _data.SourceMaxAge > 0f
            && _data.SourceStartAge + _sourceAge > _data.SourceMaxAge)
        {
            Emitting = false;
        }
        _sourceAge += delta;

        EaseEndParams((float)delta);

        if (_omegaSpeed > 0f && !LocalCoords)
        {
            // PSYS_SRC_OMEGA spins the emission frame, not the particles already in flight
            // (llviewerpartsource.cpp:224-229). Spinning this node does exactly that while
            // particles live in world space; when they follow the source it would drag them
            // along too, so it is left off in that case rather than made wrong.
            _omegaRotation = (_omegaRotation * new Quaternion(_omegaAxis, _omegaSpeed * (float)delta))
                .Normalized();
            SyncToParent(force: true);
        }
        else
        {
            SyncToParent();
        }
    }

    /// <summary>Eases the shared curve/gradient endpoint toward the latest script value over
    /// <see cref="EndParamEaseTau"/> and re-points the (persistent, live-sampled) curves in place.
    /// Frame-rate independent. Cheap enough per frame -- no allocation, and this only touches
    /// point values, never Amount/Lifetime/emission (those reset the pool; see Apply).</summary>
    private void EaseEndParams(float delta)
    {
        if (!_smoothSeeded || delta <= 0f)
        {
            return;
        }

        float k = 1f - Mathf.Exp(-delta / EndParamEaseTau);
        var newScale = System.Numerics.Vector2.Lerp(_smoothEndScale, _targetEndScale, k);
        var newColor = System.Numerics.Vector4.Lerp(_smoothEndColor, _targetEndColor, k);

        if ((newScale - _smoothEndScale).LengthSquared() > 1e-8f)
        {
            _smoothEndScale = newScale;
            if (_interpScale && _curveX is not null)
            {
                SetCurveEnd(_curveX, _smoothEndScale.X);
                SetCurveEnd(_curveY, _smoothEndScale.Y);
                SetCurveEnd(_curveZ, _smoothEndScale.X);
            }
        }
        if ((newColor - _smoothEndColor).LengthSquared() > 1e-8f)
        {
            _smoothEndColor = newColor;
            if (_interpColor && _colorRamp is { } ramp && ramp.GetPointCount() == 2)
            {
                ramp.SetColor(1, new Color(newColor.X, newColor.Y, newColor.Z, newColor.W).SrgbToLinear());
            }
        }
    }

    private static void SetCurveEnd(Curve? c, float end)
    {
        if (c is null || c.PointCount != 2)
        {
            return;
        }
        if (c.MaxValue < end)
        {
            c.MaxValue = Mathf.Max(1f, end);
        }
        c.SetPointValue(1, end);
    }

    /// <summary>
    /// Keeps the emitter at unit world scale and keeps the acceleration in whichever space the
    /// particles are simulated in. Both depend on the parent's transform, which changes without
    /// any particle data changing -- a resize or a rotation.
    /// </summary>
    private void SyncToParent(bool force = false)
    {
        if (!IsInsideTree() || GetParent() is not Node3D parent)
        {
            return;
        }

        Transform3D parentTransform = parent.GlobalTransform;
        if (!force && parentTransform.IsEqualApprox(_syncedAgainst))
        {
            return;
        }
        _syncedAgainst = parentTransform;

        Vector3 parentScale = parentTransform.Basis.Scale;
        var counterScale = new Vector3(
            1f / Mathf.Max(MinParentScale, Mathf.Abs(parentScale.X)),
            1f / Mathf.Max(MinParentScale, Mathf.Abs(parentScale.Y)),
            1f / Mathf.Max(MinParentScale, Mathf.Abs(parentScale.Z)));

        Transform = new Transform3D(new Basis(_omegaRotation).Scaled(counterScale), Vector3.Zero);

        // Gravity is applied in the space the particles are simulated in: world when they are
        // left behind, emitter-local when they follow the source.
        Gravity = LocalCoords
            ? GlobalTransform.Basis.Orthonormalized().Inverse() * _worldAcceleration
            : _worldAcceleration;
    }

    private void EnsureResources()
    {
        if (_quad is not null)
        {
            return;
        }

        // Size 1 x 1 so the per-particle scale IS the metre size. The viewer builds the quad as
        // centre +/- 0.5 * scale (llvopartgroup.cpp:549-550), i.e. scale is the full extent.
        _quad = new QuadMesh { Size = Vector2.One };
        _drawMaterial = new StandardMaterial3D
        {
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            // Draw after the water. Both surfaces are transparent, and water.gdshader is
            // blend_mix with depth_draw_always, so at equal priority Godot orders the two by
            // camera distance -- and the region's water plane is centred on the region while an
            // emitter is wherever it is, so the order FLIPS as the camera moves. That is why the
            // artefact came and went with the zoom rather than sitting still.
            //
            // The viewer does not leave this to a sort either: it splits the alpha pass in two
            // around water (POOL_ALPHA_PRE_WATER / POOL_WATER / POOL_ALPHA_POST_WATER,
            // lldrawpool.h:74-78) and draws the same faces twice with a water-plane clip whose
            // sign selects the half (lldrawpoolalpha.cpp:149-159). A fixed priority is the
            // above-water half of that, which is where particles almost always are; a billboard
            // straddling the surface still gets cut by the water's depth write instead of being
            // clipped and blended per fragment. Doing that properly belongs to the water pass.
            RenderPriority = 1,
        };
        _quad.Material = _drawMaterial;
        Mesh = _quad;

        CastShadow = ShadowCastingSetting.Off;
        // Overlapping translucent quads need back-to-front order or they punch holes in each other;
        // for a genuinely spread emitter that has to be per-particle camera distance.
        DrawOrder = DrawOrderEnum.ViewDepth;

        // FixedFps 0, not Godot's default 30. At 30 the whole simulation -- emission AND death --
        // batches into 30 discrete steps per second: a 200-particle/s emitter spawns ~7 at once
        // every 33 ms and they die 7 at a time a lifetime later, so the particle DENSITY pulses at
        // 30 Hz, which reads as fast flicker on an emissive alpha cloud. The viewer emits in the
        // script's own sub-frame bursts (2 every 10 ms here), i.e. far finer-grained. 0 runs the
        // sim at the render frame rate -- still batched, but per-frame, which is much finer.
        FixedFps = 0;

        Emitting = false;
    }

    private void ConfigureEmission(ParticleSystemData data, bool followSource)
    {
        // The emission normals carry the direction, so Godot's own cone sampling must be off.
        Direction = Vector3.Back;
        Spread = 0f;
        Flatness = 0f;

        SlParticlePattern pattern = data.Pattern;
        bool directed = pattern.HasFlag(SlParticlePattern.Explode)
            || pattern.HasFlag(SlParticlePattern.Angle)
            || pattern.HasFlag(SlParticlePattern.AngleCone);

        // Drop, and every pattern the viewer does not recognise, put the particle at the source
        // with no velocity (llviewerpartsource.cpp:346-349, 424-429).
        if (pattern.HasFlag(SlParticlePattern.Drop) || !directed)
        {
            EmissionShape = EmissionShapeEnum.Point;
            EmissionPoints = Array.Empty<Vector3>();
            EmissionNormals = Array.Empty<Vector3>();
            InitialVelocityMin = 0f;
            InitialVelocityMax = 0f;
            return;
        }

        // A particle that follows its source ignores the burst radius (llviewerpartsource.cpp:433).
        float radius = followSource || data.PartDataFlags.HasFlag(SlParticleDataFlags.TargetLinear)
            ? 0f
            : data.BurstRadius;

        Vector3[] directions = BuildEmissionDirections(data);
        var points = new Vector3[directions.Length];
        for (int i = 0; i < directions.Length; i++)
        {
            points[i] = directions[i] * radius;
        }

        EmissionShape = EmissionShapeEnum.DirectedPoints;
        EmissionPoints = points;
        EmissionNormals = directions;
        InitialVelocityMin = data.BurstSpeedMin;
        InitialVelocityMax = data.BurstSpeedMax;
    }

    /// <summary>
    /// Samples the viewer's emission direction formula (llviewerpartsource.cpp:351-421) into a
    /// fixed set of unit vectors, in this node's local space.
    /// </summary>
    private static Vector3[] BuildEmissionDirections(ParticleSystemData data)
    {
        // Deterministic: the same system produces the same sample set every time, so a visual
        // difference between two runs is a real difference and not the RNG.
        var rng = new Random(unchecked((int)data.Pattern * 397 + data.BurstPartCount));
        var directions = new Vector3[DirectionSamples];

        bool explode = data.Pattern.HasFlag(SlParticlePattern.Explode);
        bool cone = data.Pattern.HasFlag(SlParticlePattern.AngleCone);
        bool deprecatedAngles = !data.SourceFlags.HasFlag(SlParticleSourceFlags.UseNewAngle);

        for (int i = 0; i < directions.Length; i++)
        {
            Vector3 slDirection;
            if (explode)
            {
                // Uniform on the unit sphere. The viewer rejection-samples the unit ball and
                // normalises, which is the same distribution.
                float z = 1f - 2f * (float)rng.NextDouble();
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
                float phi = Mathf.Tau * (float)rng.NextDouble();
                slDirection = new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), z);
            }
            else
            {
                // Start straight up the source's local Z, tilt by an angle drawn from the band
                // between the inner and outer angle, on a randomly chosen side.
                float angle = data.InnerAngle
                    + (float)rng.NextDouble() * (data.OuterAngle - data.InnerAngle);
                if (rng.NextDouble() < 0.5)
                {
                    angle = -angle;
                }

                slDirection = RotateAboutX(new Vector3(0f, 0f, 1f), angle);
                if (cone)
                {
                    slDirection = RotateAboutZ(slDirection, (float)rng.NextDouble() * 4f * Mathf.Pi);
                }
                if (deprecatedAngles)
                {
                    slDirection = RotateAboutX(slDirection, data.OuterAngle);
                }
            }

            // SL local (x east, y north, z up) -> Godot local (x, z, -y), the same swizzle the
            // prim's own rotation gets, so the parent node's rotation carries these correctly.
            directions[i] = new Vector3(slDirection.X, slDirection.Z, -slDirection.Y).Normalized();
        }

        return directions;
    }

    private static Vector3 RotateAboutX(Vector3 v, float angle)
    {
        float s = Mathf.Sin(angle);
        float c = Mathf.Cos(angle);
        return new Vector3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
    }

    private static Vector3 RotateAboutZ(Vector3 v, float angle)
    {
        float s = Mathf.Sin(angle);
        float c = Mathf.Cos(angle);
        return new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
    }

    private void ConfigureColor(ParticleSystemData data, bool interpolateColor, System.Numerics.Vector4 smoothedEndColor)
    {
        _interpColor = interpolateColor;
        // SL particle colours are display-referred sRGB, and Godot's particle colour reaches the
        // shader as a raw vertex colour multiplied into an already-linear albedo. Handing it the
        // sRGB triple unconverted makes every mid-tone too bright: PSYS_PART_START_COLOR
        // <1, 0.5, 0> leaves the frame as <1, 0.71, 0>, orange rendered as gold. The endpoints 0
        // and 1 are fixed points of the transfer, which is why a pure red end colour looked right
        // while the orange start colour did not.
        //
        // This is exact rather than approximate here: Boot.SetupEnvironment sets TonemapMode to
        // Linear (the identity), so the frame reaches the screen through nothing but
        // linear_to_srgb and a clamp -- the same last pass as the viewer's.
        // Alpha is carried verbatim, including 0 -- it is not a colour and gets no transfer
        // applied. Fading in from nothing and out to nothing is what
        // PSYS_PART_START_ALPHA / _END_ALPHA are for.
        var start = new Color(
            data.PartStartColor.X, data.PartStartColor.Y, data.PartStartColor.Z, data.PartStartColor.W)
            .SrgbToLinear();
        Color end = interpolateColor
            ? new Color(smoothedEndColor.X, smoothedEndColor.Y, smoothedEndColor.Z, smoothedEndColor.W)
                .SrgbToLinear()
            : start;

        if (_colorRamp is null || _colorRamp.GetPointCount() != 2)
        {
            _colorRamp = new Gradient { Offsets = new[] { 0f, 1f }, Colors = new[] { start, end } };
            ColorRamp = _colorRamp;
        }
        else
        {
            _colorRamp.SetColor(0, start);
            _colorRamp.SetColor(1, end);
        }
        Color = Colors.White;
    }

    private void ConfigureScale(ParticleSystemData data, bool interpolateScale, System.Numerics.Vector2 smoothedEndScale)
    {
        _interpScale = interpolateScale;
        float endW = interpolateScale ? smoothedEndScale.X : data.PartStartScaleX;  // SL width
        float endH = interpolateScale ? smoothedEndScale.Y : data.PartStartScaleY;  // SL height

        if (ScaleAmountMin != 1f || ScaleAmountMax != 1f)
        {
            ScaleAmountMin = 1f;
            ScaleAmountMax = 1f;
        }
        if (!SplitScale)
        {
            SplitScale = true;
        }

        // SL scale is <width, height, _>: X -> ScaleCurveX, Y -> ScaleCurveY. Z tracks X so a
        // flat quad's zero-extent third axis is never a lone outlier against the other two.
        // NOTE: a blue-flame test (SLS 0.3 script, endSize <.5, 1.0>) still renders visibly wider
        // than Firestorm even though these curves log the right values -- an unresolved
        // CpuParticles3D + BILLBOARD_PARTICLES interaction, tracked separately; swapping X/Y here
        // was tried and had no visible effect, so the width is not coming from the split curves.
        _curveX = SetScaleCurve(_curveX, data.PartStartScaleX, endW);
        _curveY = SetScaleCurve(_curveY, data.PartStartScaleY, endH);
        _curveZ = SetScaleCurve(_curveZ, data.PartStartScaleX, endW);
        // Bind the curve resources once; after that SetScaleCurve mutates them in place and Godot
        // re-samples without a reset. Re-assigning the property every call risks a rebuild.
        if (ScaleCurveX != _curveX)
        {
            ScaleCurveX = _curveX;
            ScaleCurveY = _curveY;
            ScaleCurveZ = _curveZ;
        }
    }

    /// <summary>Two-point 0..1 curve, created once then re-pointed. <see cref="Curve.MaxValue"/>
    /// must be widened past 1 first or Godot silently clamps a &gt;1 m particle down as the point
    /// is added.</summary>
    private static Curve SetScaleCurve(Curve? c, float start, float end)
    {
        float max = Mathf.Max(1f, Mathf.Max(start, end));
        if (c is null || c.PointCount != 2)
        {
            c = new Curve { MinValue = 0f, MaxValue = max };
            c.AddPoint(new Vector2(0f, start));
            c.AddPoint(new Vector2(1f, end));
            return c;
        }
        if (c.MaxValue < max)
        {
            c.MaxValue = max;
        }
        c.SetPointValue(0, start);
        c.SetPointValue(1, end);
        return c;
    }

    private void ConfigureMaterial(bool emissive)
    {
        if (_drawMaterial is null)
        {
            return;
        }

        // SL's EMISSIVE is fullbright, not additive blending: the viewer sets LLFace::FULLBRIGHT
        // and skips writing a normal for the particle (llvopartgroup.cpp:346, 632).
        _drawMaterial.ShadingMode = emissive
            ? BaseMaterial3D.ShadingModeEnum.Unshaded
            : BaseMaterial3D.ShadingModeEnum.PerPixel;
    }

    private void ResolveTexture(Guid textureId, GpuCache gpuCache, AssetService assetService)
    {
        // _textureResolved, rather than comparing against a default-initialised _textureId:
        // Guid.Empty is a real value here -- it is what PSYS_SRC_TEXTURE "" means, and it is by
        // far the most common one. Treating "not resolved yet" as "already Guid.Empty" skips the
        // assignment below on the first call and leaves the material with no albedo texture at
        // all, which draws every particle as an opaque tinted SQUARE.
        if (_drawMaterial is null || (_textureResolved && textureId == _textureId))
        {
            return;
        }
        _textureId = textureId;
        _textureResolved = true;

        // Until the asset lands, the default blob stands in -- an untextured particle would draw
        // as an opaque white square, which reads as a rendering bug rather than as a pending fetch.
        _drawMaterial.AlbedoTexture = DefaultTexture();
        if (textureId == Guid.Empty)
        {
            return;
        }

        var pending = gpuCache.GetOrUploadTextureAsync(textureId, assetService, generateMipmaps: true, initialRefCount: 0);
        pending.ContinueWith(task =>
        {
            if (task is { IsCompletedSuccessfully: true, Result: not null })
            {
                CallDeferred(nameof(ApplyTextureDeferred), task.Result, textureId.ToString());
            }
        });
    }

    private void ApplyTextureDeferred(Texture2D texture, string textureId)
    {
        // A slow fetch can land after the script has already switched to another texture.
        if (_drawMaterial is null || !Guid.TryParse(textureId, out Guid resolved) || resolved != _textureId)
        {
            return;
        }
        _drawMaterial.AlbedoTexture = texture;

        // The particle textures reported as "stretched wide" all decode to a square PNG when read
        // straight from the cache -- so if the ImageTexture that actually reaches the material is
        // NOT square (a decode/resize/stride bug in the fetch path), that is the whole thing. One
        // line per emitter, so this is not per-frame spam.
        if (Diagnostics.Enabled) GD.Print($"[Particles] obj={EmitterEntityId:N} albedo resolved {texture.GetWidth()}x{texture.GetHeight()} "
            + $"(tex {resolved.ToString("N")[..8]})");
    }

    private static Texture2D DefaultTexture()
    {
        if (_defaultTexture is not null)
        {
            return _defaultTexture;
        }

        // The viewer's own default is pixiesmall.j2c, a file in the viewer SKIN rather than a grid
        // asset, so there is no id to fetch and it cannot be shipped either -- it is Linden art.
        // What can be reproduced is its shape: the stops below are its measured radial alpha,
        // averaged in 5% rings out of the decoded 128x128 image. Its RGB is pure white everywhere,
        // so the texture is nothing but an alpha mask.
        //
        //   from scratch/slviewer/indra/newview/skins/default/textures/pixiesmall.j2c
        //   r     0.00  0.10  0.15  0.20  0.25  0.30  0.40  0.50  0.65  0.80  1.00
        //   alpha 1.00  0.99  0.85  0.71  0.56  0.47  0.38  0.27  0.19  0.09  0.02
        //
        // The shape matters more than it sounds: the core is only a tenth of the radius wide and
        // the rest is a long faint halo. A gentler ramp of the same width reads as a fat mushy
        // blob rather than a bright speck of dust, which is what a hand-tuned one produced.
        // The last stop is taken to 0 rather than the measured 0.02, so the quad's corners cannot
        // show up as a faint square.
        var falloff = new Gradient
        {
            Offsets = new[] { 0f, 0.1f, 0.15f, 0.2f, 0.25f, 0.3f, 0.4f, 0.5f, 0.65f, 0.8f, 0.9f, 1f },
            Colors = new[]
            {
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 0.99f),
                new Color(1f, 1f, 1f, 0.85f),
                new Color(1f, 1f, 1f, 0.71f),
                new Color(1f, 1f, 1f, 0.56f),
                new Color(1f, 1f, 1f, 0.47f),
                new Color(1f, 1f, 1f, 0.38f),
                new Color(1f, 1f, 1f, 0.27f),
                new Color(1f, 1f, 1f, 0.19f),
                new Color(1f, 1f, 1f, 0.09f),
                new Color(1f, 1f, 1f, 0.03f),
                new Color(1f, 1f, 1f, 0f),
            },
        };
        _defaultTexture = new GradientTexture2D
        {
            Gradient = falloff,
            // FillFrom centre to FillTo edge-midpoint means gradient offset 1.0 sits at UV radius
            // 0.5 -- exactly the radius the measurement above calls 1.0.
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Width = 64,
            Height = 64,
        };
        return _defaultTexture;
    }
}
