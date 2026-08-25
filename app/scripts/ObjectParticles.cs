using System;
using Godot;
using SLNG.Core.Components;
using SLNG.Assets;

namespace SLNG.App;

public partial class ObjectParticles : CpuParticles3D
{
    private Guid _currentTextureId;
    private GpuCache? _gpuCache;
    private ParticleSystemData? _lastData;

    private float S(float v, float def = 0f) => float.IsNaN(v) ? def : v;

    public void Apply(ParticleSystemData data, GpuCache gpuCache, AssetService assetService)
    {
        if (_lastData != null && _lastData.Equals(data))
        {
            return;
        }
        _lastData = data;
        _gpuCache = gpuCache;

        // Counter-act parent scale to ensure global scale is exactly (1,1,1).
        // GPUParticles3D in Godot 4 with LocalCoords = false throws normalization errors if global scale != 1
        var parent = GetParent() as Node3D;
        if (parent != null)
        {
            Vector3 pScale = parent.Scale;
            Scale = new Vector3(1f / pScale.X, 1f / pScale.Y, 1f / pScale.Z);
        }

        // SL's PSYS_PART_FLAGS values (simplified mapped)
        bool interpolateColor = (data.Flags & 0x001) != 0;
        bool interpolateScale = (data.Flags & 0x002) != 0;
        bool emissive = (data.Flags & 0x100) != 0;

        // Godot CPUParticles3D base settings
        float burstRate = S(data.BurstRate, 0.1f);
        float rate = burstRate > 0.001f ? burstRate : 0.1f;
        
        float maxAge = S(data.MaxAge, 1.0f);
        int burstCount = (int)data.BurstPartCount; // byte
        
        Amount = burstCount > 0 ? (int)((burstCount / rate) * maxAge) + 1 : 100;
        Amount = Math.Clamp(Amount, 1, 4096);
        Lifetime = maxAge > 0f ? maxAge : 1.0f;
        OneShot = false;
        Emitting = true;
        
        bool followSource = (data.Flags & 0x010) != 0;
        LocalCoords = followSource;

        // CPUParticles3D doesn't use ParticleProcessMaterial, it sets properties directly on the node
        Direction = new Vector3(0, 1, 0); // Emit along local UP by default
        
        if ((data.Pattern & 0x02) != 0) // Explode
        {
            Spread = 180f; // Spherical
        }
        else
        {
            Spread = S(data.OuterAngle) * (180f / (float)Math.PI);
        }

        if ((data.Pattern & 0x01) != 0) // Drop
        {
            InitialVelocityMin = 0f;
            InitialVelocityMax = 0f;
        }
        else
        {
            InitialVelocityMin = S(data.BurstSpeedMin, 0f);
            InitialVelocityMax = S(data.BurstSpeedMax, 0f);
        }
        
        float gx = S(data.PartAcceleration.X);
        float gz = S(data.PartAcceleration.Z);
        float gy = -S(data.PartAcceleration.Y);
        if (float.IsInfinity(gx)) gx = 0;
        if (float.IsInfinity(gy)) gy = 0;
        if (float.IsInfinity(gz)) gz = 0;
        Gravity = new Vector3(gx, gz, gy);

        float sAlpha = S(data.StartColor.W, 1f) == 0f ? 1f : S(data.StartColor.W, 1f);
        float eAlpha = S(data.EndColor.W, 1f) == 0f ? 1f : S(data.EndColor.W, 1f);

        var grad = new Gradient();
        if (interpolateColor)
        {
            grad.Offsets = new float[] { 0f, 1f };
            grad.Colors = new Color[] {
                new Color(S(data.StartColor.X, 1f), S(data.StartColor.Y, 1f), S(data.StartColor.Z, 1f), sAlpha),
                new Color(S(data.EndColor.X, 1f), S(data.EndColor.Y, 1f), S(data.EndColor.Z, 1f), eAlpha)
            };
        }
        else
        {
            grad.Offsets = new float[] { 0f, 1f };
            grad.Colors = new Color[] {
                new Color(S(data.StartColor.X, 1f), S(data.StartColor.Y, 1f), S(data.StartColor.Z, 1f), sAlpha),
                new Color(S(data.StartColor.X, 1f), S(data.StartColor.Y, 1f), S(data.StartColor.Z, 1f), sAlpha)
            };
        }

        ColorRamp = grad;
        Color = new Color(1, 1, 1, 1);

        if (Mesh == null)
        {
            Mesh = new QuadMesh();
        }
        var quadMesh = (QuadMesh)Mesh;
        quadMesh.Size = new Vector2(1f, 1f);

        float sScale = S(data.StartScaleX, 0.1f);
        float eScale = S(data.EndScaleX, 1.0f);

        if (interpolateScale)
        {
            var scaleCurve = new Curve();
            scaleCurve.AddPoint(new Vector2(0f, sScale)); 
            scaleCurve.AddPoint(new Vector2(1f, eScale));
            ScaleCurveX = scaleCurve;
            ScaleCurveY = scaleCurve;
            // CPUParticles3D handles min/max scaling differently, so we reset base scales
            ScaleAmountMin = 1.0f;
            ScaleAmountMax = 1.0f;
        }
        else
        {
            ScaleAmountMin = sScale;
            ScaleAmountMax = sScale;
            ScaleCurveX = null;
            ScaleCurveY = null;
        }

        StandardMaterial3D drawMat = quadMesh.Material as StandardMaterial3D;
        if (drawMat == null)
        {
            drawMat = new StandardMaterial3D();
            quadMesh.Material = drawMat;
        }

        drawMat.ShadingMode = emissive ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel;
        drawMat.BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles;
        drawMat.BillboardKeepScale = true;
        drawMat.VertexColorUseAsAlbedo = true;
        drawMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;

        if (data.TextureId != Guid.Empty && _currentTextureId != data.TextureId)
        {
            _currentTextureId = data.TextureId;
            var loadTask = gpuCache.GetOrUploadTextureAsync(data.TextureId, assetService, generateMipmaps: true, initialRefCount: 0);
            loadTask.ContinueWith(t => {
                if (t.IsCompletedSuccessfully && t.Result != null)
                {
                    CallDeferred(nameof(ApplyTextureDeferred), t.Result);
                }
            });
        }
        else if (data.TextureId == Guid.Empty)
        {
            _currentTextureId = Guid.Empty;
            if (drawMat.AlbedoTexture == null)
            {
                var circleTex = new GradientTexture2D();
                var gradCircle = new Gradient();
                gradCircle.Offsets = new float[] { 0f, 0.4f, 1f };
                gradCircle.Colors = new Color[] {
                    new Color(1, 1, 1, 1),
                    new Color(1, 1, 1, 1),
                    new Color(1, 1, 1, 0)
                };
                circleTex.Gradient = gradCircle;
                circleTex.Fill = GradientTexture2D.FillEnum.Radial;
                circleTex.FillFrom = new Vector2(0.5f, 0.5f);
                circleTex.FillTo = new Vector2(1.0f, 0.5f);
                circleTex.Width = 32;
                circleTex.Height = 32;
                drawMat.AlbedoTexture = circleTex;
            }
        }
        
        Restart();
    }

    private void ApplyTextureDeferred(Texture2D tex)
    {
        var quadMesh = (QuadMesh)Mesh;
        if (quadMesh.Material is StandardMaterial3D drawMat)
        {
            drawMat.AlbedoTexture = tex;
        }
    }
}
