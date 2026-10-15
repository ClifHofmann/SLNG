using System;
using System.IO;
using Xunit;

namespace SLNG.Core.Tests;

public class PbrShaderTextTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SLNG.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SLNG.sln not found above the test binaries");
    }

    [Fact]
    public void PrimCommon_Contains_Pbr_Transform_Uniforms_And_Formula()
    {
        string shaderPath = Path.Combine(RepoRoot(), "app", "materials", "prim", "prim_common.gdshaderinc");
        Assert.True(File.Exists(shaderPath), $"Missing {shaderPath}");

        string text = File.ReadAllText(shaderPath);
        Assert.Contains("uniform vec4 pbr_uv_a", text);
        Assert.Contains("uniform vec4 pbr_uv_b", text);
        Assert.Contains("vec2 slng_khr_uv(", text);
        Assert.Contains("c * sc.x + s * sc.y", text);
        Assert.Contains("-s * sc.x + c * sc.y", text);
    }
}
