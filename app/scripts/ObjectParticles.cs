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
        Amount = data.BurstPartCount > 0 ? (int)(data.BurstRate * data.BurstPartCount * data.MaxAge) + 1 : 100;
        Amount = Math.Clamp(Amount, 1, 4096);
        Lifetime = data.MaxAge > 0f ? data.MaxAge : 1.0f;
        OneShot = false;

        ParticleProcessMaterial mat = ProcessMaterial as ParticleProcessMaterial;
        if (mat == null)
        {
            mat = new ParticleProcessMaterial();
            ProcessMaterial = mat;
        }

        // SL Particles emit along the Z axis of the prim, spreading by OuterAngle
        mat.Direction = new Vector3(0, 0, 1);
        mat.Spread = data.OuterAngle * (180f / (float)Math.PI);
        mat.InitialVelocityMin = data.BurstSpeedMin;
        mat.InitialVelocityMax = data.BurstSpeedMax;
        
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
        quadMesh.Size = new Vector2(data.StartScaleX, data.StartScaleY);

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
