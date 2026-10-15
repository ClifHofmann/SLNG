using System;
using System.IO;
using System.Numerics;
using Xunit;

namespace SLNG.Core.Tests;

public class TextureAnimShaderTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SLNG.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("SLNG.sln not found above the test binaries");
    }

    [Fact]
    public void ParityWithTextureAnimatorAcrossAll64Modes()
    {
        TextureAnimFlags[] flagBits =
        [
            TextureAnimFlags.Loop,
            TextureAnimFlags.Reverse,
            TextureAnimFlags.PingPong,
            TextureAnimFlags.Smooth,
            TextureAnimFlags.Rotate,
            TextureAnimFlags.Scale
        ];

        (byte sx, byte sy)[] grids = [(1, 1), (4, 4), (8, 2)];
        float[] lengths = [0f, 5f];
        float[] starts = [0f, 2f];
        float[] rates = [2.5f, -3f, 0.5f];
        float[] times = [0f, 0.013f, 0.5f, 1f, 3.7f, 10f, 59.9f, 600f];

        for (int mask = 0; mask < 64; mask++)
        {
            var mode = TextureAnimFlags.On;
            for (int b = 0; b < 6; b++)
            {
                if ((mask & (1 << b)) != 0)
                    mode |= flagBits[b];
            }

            bool isSmooth = (mode & TextureAnimFlags.Smooth) != 0;
            var currentGrids = isSmooth ? [.. grids, ((byte)0, (byte)0)] : grids;

            foreach (var (sx, sy) in currentGrids)
            {
                foreach (float len in lengths)
                {
                    foreach (float start in starts)
                    {
                        foreach (float rate in rates)
                        {
                            var anim = new TextureAnimation(mode, -1, sx, sy, start, len, rate);
                            var (a, b) = TextureAnimShader.Pack(anim, 100f);

                            foreach (float t in times)
                            {
                                bool isLoop = (mode & TextureAnimFlags.Loop) != 0;
                                float elapsedForRef = isLoop ? 100f + t : t;
                                var expected = TextureAnimator.Evaluate(anim, elapsedForRef);
                                var actual = TextureAnimShader.Evaluate(a, b, 100f + t);

                                Assert.Equal(expected.Driven, actual.Driven);
                                Assert.True(MathF.Abs(expected.OffsetS - actual.OffsetS) < 1e-3f,
                                    $"OffsetS mismatch for mode {mode}, grid ({sx},{sy}), len {len}, start {start}, rate {rate}, t {t}: expected {expected.OffsetS}, actual {actual.OffsetS}");
                                Assert.True(MathF.Abs(expected.OffsetT - actual.OffsetT) < 1e-3f,
                                    $"OffsetT mismatch for mode {mode}, grid ({sx},{sy}), len {len}, start {start}, rate {rate}, t {t}: expected {expected.OffsetT}, actual {actual.OffsetT}");
                                Assert.True(MathF.Abs(expected.ScaleS - actual.ScaleS) < 1e-3f,
                                    $"ScaleS mismatch for mode {mode}, grid ({sx},{sy}), len {len}, start {start}, rate {rate}, t {t}: expected {expected.ScaleS}, actual {actual.ScaleS}");
                                Assert.True(MathF.Abs(expected.ScaleT - actual.ScaleT) < 1e-3f,
                                    $"ScaleT mismatch for mode {mode}, grid ({sx},{sy}), len {len}, start {start}, rate {rate}, t {t}: expected {expected.ScaleT}, actual {actual.ScaleT}");
                                Assert.True(MathF.Abs(expected.Rotation - actual.Rotation) < 1e-3f,
                                    $"Rotation mismatch for mode {mode}, grid ({sx},{sy}), len {len}, start {start}, rate {rate}, t {t}: expected {expected.Rotation}, actual {actual.Rotation}");
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void PackSetsStartTimeToZeroForLoopAndNowForNonLoop()
    {
        var loopAnim = new TextureAnimation(TextureAnimFlags.On | TextureAnimFlags.Loop, -1, 1, 1, 0f, 0f, 1f);
        var (aLoop, bLoop) = TextureAnimShader.Pack(loopAnim, 42.5f);
        Assert.Equal(0f, bLoop.Z);
        Assert.Equal(1f, bLoop.W);

        var nonLoopAnim = new TextureAnimation(TextureAnimFlags.On, -1, 1, 1, 0f, 0f, 1f);
        var (aNonLoop, bNonLoop) = TextureAnimShader.Pack(nonLoopAnim, 42.5f);
        Assert.Equal(42.5f, bNonLoop.Z);
        Assert.Equal(1f, bNonLoop.W);
    }

    [Fact]
    public void OffReturnsTextureAnimFrameIdle()
    {
        var result = TextureAnimShader.Evaluate(Vector4.Zero, TextureAnimShader.Off, 123.4f);
        Assert.Equal(TextureAnimFrame.Idle, result);
    }

    [Fact]
    public void ShaderTextContainsRequiredUniformsAndFunctions()
    {
        string shader = File.ReadAllText(Path.Combine(RepoRoot(), "app", "materials", "prim", "prim_common.gdshaderinc"));
        Assert.Contains("uniform vec4 tex_anim_a", shader);
        Assert.Contains("uniform vec4 tex_anim_b", shader);
        Assert.Contains("global uniform float slng_anim_time;", shader);
        Assert.Contains("float slng_fmod(", shader);
    }
}
