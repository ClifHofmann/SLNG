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

    private ParticleSystemData? _data;
    private Guid _textureId;
    private QuadMesh? _quad;
    private StandardMaterial3D? _drawMaterial;

    /// <summary>Seconds since this system was (re)configured, against
    /// <see cref="ParticleSystemData.SourceMaxAge"/>.</summary>
    private double _sourceAge;

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

        _data = data;
        _sourceAge = 0.0;
        _omegaRotation = Quaternion.Identity;

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
                GD.Print($"[Particles] inert: partMaxAge={data.PartMaxAge:0.###}s "
                    + $"burstCount={data.BurstPartCount} -- nothing to draw");
            }
            return;
        }

        SlParticleDataFlags partFlags = data.PartDataFlags;
        bool interpolateColor = partFlags.HasFlag(SlParticleDataFlags.InterpColor);
        bool interpolateScale = partFlags.HasFlag(SlParticleDataFlags.InterpScale);
        bool emissive = partFlags.HasFlag(SlParticleDataFlags.Emissive);
        bool followSource = partFlags.HasFlag(SlParticleDataFlags.FollowSrc);

        Lifetime = data.PartMaxAge;
        Amount = data.SteadyStateParticleCount(MaxPoolSize);
        OneShot = false;
        LocalCoords = followSource;
        // An emitter usually predates our arrival in the region, so a system that ramps up from
        // empty is wrong twice over -- it is visible, and it is visible exactly when an object
        // streams into view. Capped because Godot preprocesses on the main thread in 1/30 s
        // steps: a full 30 s lifetime across a 2048 particle pool is 1.8M updates in one frame.
        Preprocess = Math.Min(data.PartMaxAge, MaxPreprocessSeconds);

        ConfigureEmission(data, followSource);
        ConfigureColor(data, interpolateColor);
        ConfigureScale(data, interpolateScale);
        ConfigureMaterial(emissive);

        // Region axes (X east, Y north, Z up) -> Godot (X east, Y up, Z south).
        _worldAcceleration = new Vector3(
            data.PartAcceleration.X, data.PartAcceleration.Z, -data.PartAcceleration.Y);

        var omega = new Vector3(data.AngularVelocity.X, data.AngularVelocity.Z, -data.AngularVelocity.Y);
        _omegaSpeed = omega.Length();
        _omegaAxis = _omegaSpeed > 0f ? omega / _omegaSpeed : Vector3.Up;

        ResolveTexture(data.TextureId, gpuCache, assetService);

        // Force the transform/gravity sync: the pattern, the acceleration and the parent's scale
        // can all have changed under us.
        _syncedAgainst = new Transform3D(Basis.Identity, new Vector3(float.NaN, 0f, 0f));
        SyncToParent();

        Emitting = true;
        Restart();

        if (Diagnostics.Enabled)
        {
            GD.Print($"[Particles] {data.Pattern} pool={Amount} life={Lifetime:0.##}s "
                + $"burst={data.BurstPartCount}/{data.BurstRate:0.###}s "
                + $"speed={data.BurstSpeedMin:0.##}-{data.BurstSpeedMax:0.##} radius={data.BurstRadius:0.##} "
                + $"scale={data.PartStartScaleX:0.##}x{data.PartStartScaleY:0.##}"
                + $"->{data.PartEndScaleX:0.##}x{data.PartEndScaleY:0.##} "
                + $"flags={data.PartDataFlags} src={data.SourceFlags} "
                + $"tex={(data.TextureId == Guid.Empty ? "default" : data.TextureId.ToString())}");
        }
    }

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
        };
        _quad.Material = _drawMaterial;
        Mesh = _quad;

        CastShadow = ShadowCastingSetting.Off;
        // Overlapping translucent quads need back-to-front order or they punch holes in each other.
        DrawOrder = DrawOrderEnum.ViewDepth;
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

    private void ConfigureColor(ParticleSystemData data, bool interpolateColor)
    {
        var start = new Color(
            data.PartStartColor.X, data.PartStartColor.Y, data.PartStartColor.Z, data.PartStartColor.W);
        // Alpha is carried verbatim, including 0: fading in from nothing and out to nothing is
        // what PSYS_PART_START_ALPHA / _END_ALPHA are for.
        Color end = interpolateColor
            ? new Color(data.PartEndColor.X, data.PartEndColor.Y, data.PartEndColor.Z, data.PartEndColor.W)
            : start;

        ColorRamp = new Gradient
        {
            Offsets = new[] { 0f, 1f },
            Colors = new[] { start, end },
        };
        Color = Colors.White;
    }

    private void ConfigureScale(ParticleSystemData data, bool interpolateScale)
    {
        float endX = interpolateScale ? data.PartEndScaleX : data.PartStartScaleX;
        float endY = interpolateScale ? data.PartEndScaleY : data.PartStartScaleY;

        // Godot's final size is ScaleAmount * ScaleCurve. Putting the metre sizes in the curves
        // and leaving the amount at 1 is what lets X and Y differ -- SL particles are rectangles,
        // not squares -- and it is the only path that needs SplitScale.
        ScaleAmountMin = 1f;
        ScaleAmountMax = 1f;
        SplitScale = true;
        ScaleCurveX = BuildScaleCurve(data.PartStartScaleX, endX);
        ScaleCurveY = BuildScaleCurve(data.PartStartScaleY, endY);
        ScaleCurveZ = BuildScaleCurve(1f, 1f);
    }

    /// <summary>
    /// A two-point curve from birth to death size. <see cref="Curve.MaxValue"/> has to be widened
    /// first: Godot's default range is 0..1, and it clamps the points as they are added -- which
    /// silently caps every particle larger than one metre.
    /// </summary>
    private static Curve BuildScaleCurve(float start, float end)
    {
        var curve = new Curve
        {
            MinValue = 0f,
            MaxValue = Mathf.Max(1f, Mathf.Max(start, end)),
        };
        curve.AddPoint(new Vector2(0f, start));
        curve.AddPoint(new Vector2(1f, end));
        return curve;
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
        if (_drawMaterial is null || textureId == _textureId)
        {
            return;
        }
        _textureId = textureId;

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
    }

    private static Texture2D DefaultTexture()
    {
        if (_defaultTexture is not null)
        {
            return _defaultTexture;
        }

        var falloff = new Gradient
        {
            Offsets = new[] { 0f, 0.4f, 1f },
            Colors = new[] { Colors.White, Colors.White, new Color(1f, 1f, 1f, 0f) },
        };
        _defaultTexture = new GradientTexture2D
        {
            Gradient = falloff,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Width = 32,
            Height = 32,
        };
        return _defaultTexture;
    }
}
