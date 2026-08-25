using System;
using Godot;
using SLNG.Core.Components;
using SLNG.Assets;

namespace SLNG.App;

public partial class ObjectParticles : GpuParticles3D
{
    private Guid _currentTextureId;
    private GpuCache? _gpuCache;

    public void Apply(ParticleSystemData data, GpuCache gpuCache, AssetService assetService)
    {
        _gpuCache = gpuCache;

        // SL's PSYS_PART_FLAGS values (simplified mapped)
        bool interpolateColor = (data.Flags & 0x001) != 0;
        bool interpolateScale = (data.Flags & 0x002) != 0;
        bool emissive = (data.Flags & 0x100) != 0;

        // Godot GPUParticles3D base settings
        float rate = data.BurstRate > 0.001f ? data.BurstRate : 0.1f;
        Amount = data.BurstPartCount > 0 ? (int)((data.BurstPartCount / rate) * data.MaxAge) + 1 : 100;
        Amount = Math.Clamp(Amount, 1, 4096);
        Lifetime = data.MaxAge > 0f ? data.MaxAge : 1.0f;
        OneShot = false;
        Emitting = true;
        
        bool followSource = (data.Flags & 0x010) != 0;
        LocalCoords = followSource;

        ParticleProcessMaterial mat = ProcessMaterial as ParticleProcessMaterial;
        if (mat == null)
        {
            mat = new ParticleProcessMaterial();
            ProcessMaterial = mat;
        }

        // SL Patterns
        // 0x01 = Drop, 0x02 = Explode, 0x04 = Angle, 0x08 = Cone, 0x10 = AngleCone
        mat.Direction = new Vector3(0, 0, 1);
        
        if ((data.Pattern & 0x02) != 0) // Explode
        {
            mat.Spread = 180f; // Spherical
        }
        else
        {
            mat.Spread = data.OuterAngle * (180f / (float)Math.PI);
        }

        if ((data.Pattern & 0x01) != 0) // Drop
        {
            mat.InitialVelocityMin = 0f;
            mat.InitialVelocityMax = 0f;
        }
        else
        {
            mat.InitialVelocityMin = data.BurstSpeedMin;
            mat.InitialVelocityMax = data.BurstSpeedMax;
        }
        
        // SL Gravity is passed as PartAcceleration
        mat.Gravity = new Vector3(data.PartAcceleration.X, data.PartAcceleration.Y, data.PartAcceleration.Z);

        // SL Start and End Colors mapped to Godot ColorRamp
        var grad = new Gradient();
        grad.AddPoint(0.0f, new Color(data.StartColor.X, data.StartColor.Y, data.StartColor.Z, data.StartColor.W));
        if (interpolateColor)
        {
            grad.AddPoint(1.0f, new Color(data.EndColor.X, data.EndColor.Y, data.EndColor.Z, data.EndColor.W));
        }
        else
        {
            grad.AddPoint(1.0f, new Color(data.StartColor.X, data.StartColor.Y, data.StartColor.Z, data.StartColor.W));
        }

        var gradTex = new GradientTexture1D();
        gradTex.Gradient = grad;
        mat.ColorRamp = gradTex;

        // StandardMaterial3D (DrawPass)
        if (DrawPass1 == null)
        {
            DrawPass1 = new QuadMesh();
        }

        var quadMesh = (QuadMesh)DrawPass1;
        quadMesh.Size = new Vector2(1f, 1f); // Base size 1, scale controlled by process material

        if (interpolateScale)
        {
            var scaleCurve = new Curve();
            scaleCurve.AddPoint(new Vector2(0f, data.StartScaleX)); // Assume uniform X/Y for simplicity in basic curve
            scaleCurve.AddPoint(new Vector2(1f, data.EndScaleX));
            var scaleTex = new CurveTexture();
            scaleTex.Curve = scaleCurve;
            mat.ScaleCurve = scaleTex;
            // Note: Godot GPUParticles3D scale curve is a single float multiplier.
            // If Start/End scales differ in aspect ratio, it gets complex. We use X as an approximation.
        }
        else
        {
            mat.ScaleMin = data.StartScaleX;
            mat.ScaleMax = data.StartScaleX;
        }

        StandardMaterial3D drawMat = quadMesh.Material as StandardMaterial3D;
        if (drawMat == null)
        {
            drawMat = new StandardMaterial3D();
            drawMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            drawMat.BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles;
            drawMat.VertexColorUseAsAlbedo = true;
            quadMesh.Material = drawMat;
        }

        drawMat.ShadingMode = emissive ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel;

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
                gradCircle.AddPoint(0.0f, new Color(1, 1, 1, 1));
                gradCircle.AddPoint(0.4f, new Color(1, 1, 1, 1)); // solid core
                gradCircle.AddPoint(1.0f, new Color(1, 1, 1, 0)); // soft edge
                circleTex.Gradient = gradCircle;
                circleTex.Fill = GradientTexture2D.FillEnum.Radial;
                circleTex.FillFrom = new Vector2(0.5f, 0.5f);
                circleTex.FillTo = new Vector2(1.0f, 0.5f);
                circleTex.Width = 32;
                circleTex.Height = 32;
                drawMat.AlbedoTexture = circleTex;
            }
        }
    }

    private void ApplyTextureDeferred(Texture2D tex)
    {
        var quadMesh = (QuadMesh)DrawPass1;
        if (quadMesh.Material is StandardMaterial3D drawMat)
        {
            drawMat.AlbedoTexture = tex;
        }
    }
}
