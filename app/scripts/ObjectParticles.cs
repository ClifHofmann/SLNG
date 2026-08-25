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

    /// <summary>Whether <see cref="_textureId"/> holds a resolved id rather than its default.
    /// Needed because <see cref="Guid.Empty"/> is itself a valid id here -- see ResolveTexture.</summary>
    private bool _textureResolved;
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
            ? new Color(data.PartEndColor.X, data.PartEndColor.Y, data.PartEndColor.Z, data.PartEndColor.W)
                .SrgbToLinear()
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
