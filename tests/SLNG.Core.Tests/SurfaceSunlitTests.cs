using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The Millenium terrace sky of the v0.25.9 session (SLT 12:15): sunlight_color (0.911, 0.618, 0.529),
/// hazeDensity 4, density_multiplier 0.0002223, max_y 347, blue_density.r 0.16 (G and B taken as
/// uniform -- the logged SunDiffuse G and B are consistent with it to 0.1%), sun elevation 34.86 deg.
/// </summary>
public class SurfaceSunlitTests
{
    private static SkyLighting Terrace()
    {
        var sky = new SkySettings
        {
            SunlightColor = new Vector3(0.911f, 0.618f, 0.529f),
            AmbientColor = new Vector3(0.291f, 0.42f, 0.506f),
            BlueDensity = new Vector3(0.16f, 0.16f, 0.16f),
            HazeDensity = 4f,
            DensityMultiplier = 0.0002223f,
            MaxY = 347f,
            CloudShadow = 0.52f,
        };
        return SkyLighting.Calculate(sky, MathF.Sin(34.86f * MathF.PI / 180f));
    }

    [Fact]
    public void SurfaceSunlitIsTheRawSunlightTimesTheAtmosphericAttenuationOnly()
    {
        var l = Terrace();
        // atmosphericsFuncs.glsl:60-73: sunlit = sunlight * exp(-light_atten / lightnorm.y)
        Assert.Equal(0.911f * 0.8551f, l.SurfaceSunlit.X, 3);
        Assert.Equal(0.618f * 0.8551f, l.SurfaceSunlit.Y, 3);
        Assert.Equal(0.529f * 0.8551f, l.SurfaceSunlit.Z, 3);
    }

    [Fact]
    public void SunDiffuseIsTheSameTimesBeersLawTransmittanceWhichOnlyTheSunDiscUses()
    {
        var l = Terrace();
        // the logged SunDiffuse: 0.911 x 0.8551 x 0.725 = 0.565 (R); G and B from the same factor
        Assert.Equal(0.565f, l.SunDiffuse.X, 3);
        Assert.Equal(0.384f, l.SunDiffuse.Y, 2);
        Assert.Equal(0.328f, l.SunDiffuse.Z, 2);
        Assert.Equal(0.7255f, l.SunDiffuse.X / l.SurfaceSunlit.X, 3);
    }
}
