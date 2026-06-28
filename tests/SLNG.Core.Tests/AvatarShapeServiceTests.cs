using System.Numerics;
using SLNG.Assets;
using Xunit;

namespace SLNG.Core.Tests;

public class AvatarShapeServiceTests
{
    [Fact]
    public void ComputeDistortions_null_params_does_not_throw()
    {
        var result = AvatarShapeService.ComputeDistortions(null);
        Assert.NotNull(result);
    }

    [Fact]
    public void ComputeDistortions_empty_params_does_not_throw()
    {
        var result = AvatarShapeService.ComputeDistortions(System.Array.Empty<byte>());
        Assert.NotNull(result);
    }

    [Fact]
    public void ComputeDistortions_nonzero_param_populates_dict()
    {
        // Height is param ID 33, at index 25 in Group0 (VisualParams.Group0ParamIds).
        // It has 35 skeletal distortions.  With byte=255 (max value = 2.0) any bone that
        // Height affects will appear in the result.
        var visualParams = new byte[253];
        visualParams[25] = 255;

        var result = AvatarShapeService.ComputeDistortions(visualParams);

        Assert.True(result.Count > 0, "Expected at least one bone distortion for non-zero height param");
        Assert.True(result.ContainsKey("mNeck"), "Height param should distort mNeck");
    }

    [Fact]
    public void ComputeDistortions_result_values_are_finite()
    {
        // All params at mid-range (byte 128) — no NaN or Infinity in the output.
        var visualParams = new byte[253];
        System.Array.Fill(visualParams, (byte)128);

        var result = AvatarShapeService.ComputeDistortions(visualParams);

        foreach (var (bone, (scale, pos)) in result)
        {
            Assert.True(float.IsFinite(scale.X) && float.IsFinite(scale.Y) && float.IsFinite(scale.Z),
                $"Non-finite scale for bone {bone}");
            Assert.True(float.IsFinite(pos.X) && float.IsFinite(pos.Y) && float.IsFinite(pos.Z),
                $"Non-finite position for bone {bone}");
        }
    }
}
