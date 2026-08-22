using System;
using System.IO;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// Guards the terrain blend ramp asset's Godot import settings.
///
/// The ramp is a lookup table, not a picture: the renderer indexes it to reproduce the viewer's
/// terrain texture transitions, so its values have to survive import bit-for-bit. Godot's default
/// <c>detect_3d/compress_to=1</c> silently re-imports a texture as VRAM-compressed the first time
/// it is used in a 3D material — which is exactly how this one is used. That would corrupt the
/// ramp *after* it had already been verified as correct once, with no error and no visible cause.
///
/// This is a repository-layout test rather than a unit test, and it is deliberate: the failure it
/// catches is a silent edit to a generated file that no C# code path touches.
/// </summary>
public class TerrainRampAssetTests
{
    private const string ImportPath = "app/textures/sl_alpha_gradient_2d.png.import";
    private const string PngPath = "app/textures/sl_alpha_gradient_2d.png";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SLNG.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "could not locate SLNG.sln above the test assembly");
        return dir!.FullName;
    }

    [Theory]
    // Lossless. Any VRAM or lossy mode re-encodes the table.
    [InlineData("compress/mode=0")]
    // Indexed directly, never minified; mip levels would average unrelated ramp rows together.
    [InlineData("mipmaps/generate=false")]
    // The one that matters most — see the class comment.
    [InlineData("detect_3d/compress_to=0")]
    public void RampImportSettings_KeepTheTableExact(string expectedSetting)
    {
        string path = Path.Combine(RepoRoot(), ImportPath);
        Assert.True(File.Exists(path), $"{ImportPath} is missing");

        string[] lines = File.ReadAllLines(path);
        Assert.Contains(expectedSetting, lines);
    }

    [Fact]
    public void RampAsset_IsPresentAndGreyscale8Bit()
    {
        // Not a full decode — just enough of the PNG header to catch the asset being replaced by a
        // re-encoded or resized version, which would shift every terrain texture transition.
        string path = Path.Combine(RepoRoot(), PngPath);
        Assert.True(File.Exists(path), $"{PngPath} is missing");

        byte[] png = File.ReadAllBytes(path);
        Assert.True(png.Length > 33, "file is too short to be a PNG");

        byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Equal(signature, png[..8]);

        // IHDR payload starts at byte 16: width, height (big-endian), bit depth, colour type.
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        int bitDepth = png[24];
        int colourType = png[25];

        Assert.Equal(256, width);
        Assert.Equal(256, height);
        Assert.Equal(8, bitDepth);
        Assert.Equal(0, colourType); // 0 = greyscale, matching the viewer's single-channel original
    }

    [Fact]
    public void Notice_ShipsInsideTheGodotProjectAndNamesTheAsset()
    {
        // The CC BY-SA licence on the Second Life viewer artwork requires the notice to travel with
        // the program, so it has to sit inside app/ to be exported and readable at res://. A notice
        // that only exists at the repository root would not reach a user.
        string notice = Path.Combine(RepoRoot(), "app/THIRD-PARTY-NOTICES.md");
        Assert.True(File.Exists(notice), "app/THIRD-PARTY-NOTICES.md is missing");

        string text = File.ReadAllText(notice);
        Assert.Contains("Creative Commons Attribution-Share Alike 3.0", text);
        Assert.Contains("sl_alpha_gradient_2d.png", text);
        Assert.Contains("clouds2.tga", text);
    }
}
