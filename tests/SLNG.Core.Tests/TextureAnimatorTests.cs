using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// Parity tests for the llSetTextureAnim frame maths against LLViewerTextureAnim::animateTextures
/// (newview/llviewertextureanim.cpp). Each expectation is derived by hand from that function's
/// arithmetic rather than from what our port happens to produce.
/// </summary>
public class TextureAnimatorTests
{
    private const float Tol = 1e-5f;

    private static TextureAnimation Flipbook(TextureAnimFlags extra, byte sizeX, byte sizeY, float rate, float start = 0f, float length = 0f)
        => new(TextureAnimFlags.On | extra, -1, sizeX, sizeY, start, length, rate);

    [Fact]
    public void AnimationThatIsOff_DrivesNothing()
    {
        var anim = new TextureAnimation(TextureAnimFlags.Off, -1, 4, 4, 0f, 0f, 10f);

        var frame = TextureAnimator.Evaluate(anim, 3.5f);

        Assert.Equal(TextureAnimResult.None, frame.Driven);
    }

    [Theory]
    // A 4x4 sheet at 4 fps. frame_counter = floor(t*4 + 0.01), then x = f % 4, y = f / 4.
    // scale = 1/4; off_s = -0.5 + 0.5*0.25 + x*0.25; off_t = 0.5 - 0.5*0.25 - y*0.25.
    [InlineData(0.0f, 0, 0)]
    [InlineData(0.30f, 1, 0)]
    [InlineData(1.30f, 1, 1)]
    [InlineData(3.80f, 3, 3)]
    public void FrameGrid_StepsAcrossTheSheet(float elapsed, int expectedX, int expectedY)
    {
        var anim = Flipbook(TextureAnimFlags.Loop, sizeX: 4, sizeY: 4, rate: 4f);

        var frame = TextureAnimator.Evaluate(anim, elapsed);

        Assert.Equal(TextureAnimResult.Translate | TextureAnimResult.Scale, frame.Driven);
        Assert.Equal(0.25f, frame.ScaleS, Tol);
        Assert.Equal(0.25f, frame.ScaleT, Tol);
        Assert.Equal(-0.5f + 0.125f + expectedX * 0.25f, frame.OffsetS, Tol);
        Assert.Equal(0.5f - 0.125f - expectedY * 0.25f, frame.OffsetT, Tol);
    }

    [Fact]
    public void Loop_WrapsBackToTheFirstFrame()
    {
        var anim = Flipbook(TextureAnimFlags.Loop, sizeX: 4, sizeY: 4, rate: 4f);

        // 16 frames at 4 fps: t=4.0 is exactly one full cycle, so it must equal t=0.
        Assert.Equal(TextureAnimator.Evaluate(anim, 0f), TextureAnimator.Evaluate(anim, 4f));
    }

    [Fact]
    public void WithoutLoop_HoldsOnTheLastFrame()
    {
        var anim = Flipbook(TextureAnimFlags.Off, sizeX: 4, sizeY: 4, rate: 4f);

        var atEnd = TextureAnimator.Evaluate(anim, 3.75f);   // frame 15, the last of 16
        var wayPast = TextureAnimator.Evaluate(anim, 900f);

        Assert.Equal(atEnd, wayPast);
        Assert.Equal(-0.5f + 0.125f + 3 * 0.25f, wayPast.OffsetS, Tol);
        Assert.Equal(0.5f - 0.125f - 3 * 0.25f, wayPast.OffsetT, Tol);
    }

    [Fact]
    public void Length_OverridesTheGridAsTheFrameCount()
    {
        // A 4x4 sheet but only the first 3 frames play, so it wraps after frame 2, not frame 15.
        var anim = Flipbook(TextureAnimFlags.Loop, sizeX: 4, sizeY: 4, rate: 1f, length: 3f);

        Assert.Equal(TextureAnimator.Evaluate(anim, 0f), TextureAnimator.Evaluate(anim, 3f));
        Assert.NotEqual(TextureAnimator.Evaluate(anim, 0f), TextureAnimator.Evaluate(anim, 2f));
    }

    [Fact]
    public void Start_OffsetsTheFrameNumber()
    {
        var plain = Flipbook(TextureAnimFlags.Loop, 4, 4, rate: 4f);
        var started = Flipbook(TextureAnimFlags.Loop, 4, 4, rate: 4f, start: 5f);

        Assert.Equal(TextureAnimator.Evaluate(plain, 5f / 4f), TextureAnimator.Evaluate(started, 0f));
    }

    [Fact]
    public void Smooth_SlidesContinuouslyRatherThanStepping()
    {
        // The classic scrolling setup: SMOOTH with no frame grid. The viewer leaves scale at 1
        // and slides U by the raw frame counter, so a rate of 1 moves exactly one texture width
        // per second. Also the one case where SizeX/SizeY may legitimately be 0 -- see
        // TextureAnimation.FromWire.
        var anim = new TextureAnimation(TextureAnimFlags.On | TextureAnimFlags.Smooth | TextureAnimFlags.Loop,
            -1, 0, 0, 0f, 0f, 1f);

        var quarter = TextureAnimator.Evaluate(anim, 0.25f);
        var half = TextureAnimator.Evaluate(anim, 0.5f);

        Assert.Equal(TextureAnimResult.Translate, quarter.Driven);
        Assert.Equal(1f, quarter.ScaleS, Tol);
        Assert.Equal(0.25f, quarter.OffsetS, Tol);
        Assert.Equal(0.5f, half.OffsetS, Tol);
        Assert.Equal(0f, half.OffsetT, Tol);
    }

    [Fact]
    public void Rotate_DrivesTheAngleOnly()
    {
        var anim = new TextureAnimation(TextureAnimFlags.On | TextureAnimFlags.Rotate | TextureAnimFlags.Smooth | TextureAnimFlags.Loop,
            -1, 1, 1, Start: 0f, Length: 6.2831855f, Rate: 1f);

        var frame = TextureAnimator.Evaluate(anim, 2f);

        Assert.Equal(TextureAnimResult.Rotate, frame.Driven);
        Assert.Equal(2f, frame.Rotation, Tol);
    }

    [Fact]
    public void Scale_DrivesBothAxesOnly()
    {
        var anim = new TextureAnimation(TextureAnimFlags.On | TextureAnimFlags.Scale | TextureAnimFlags.Smooth | TextureAnimFlags.Loop,
            -1, 1, 1, Start: 0f, Length: 4f, Rate: 1f);

        var frame = TextureAnimator.Evaluate(anim, 1.5f);

        Assert.Equal(TextureAnimResult.Scale, frame.Driven);
        Assert.Equal(1.5f, frame.ScaleS, Tol);
        Assert.Equal(1.5f, frame.ScaleT, Tol);
    }

    [Fact]
    public void Reverse_PlaysTheSheetBackwards()
    {
        var anim = Flipbook(TextureAnimFlags.Loop | TextureAnimFlags.Reverse, sizeX: 4, sizeY: 1, rate: 1f);

        // Non-smooth REVERSE is (num_frames - 0.99) - counter, rounded: at t=0 that is
        // round(3.01) = 3, the last frame of the row.
        var first = TextureAnimator.Evaluate(anim, 0f);
        var second = TextureAnimator.Evaluate(anim, 1f);

        Assert.Equal(-0.5f + 0.125f + 3 * 0.25f, first.OffsetS, Tol);
        Assert.Equal(-0.5f + 0.125f + 2 * 0.25f, second.OffsetS, Tol);
    }

    [Fact]
    public void PingPong_TurnsAroundAtTheEnd()
    {
        // 4 frames, LOOP|PING_PONG: full_length = 2*4-2 = 6, so t=0..3 runs forward and t=4,5
        // come back through frames 2 and 1 before repeating.
        var anim = Flipbook(TextureAnimFlags.Loop | TextureAnimFlags.PingPong, sizeX: 4, sizeY: 1, rate: 1f);

        float[] xs = new float[7];
        for (int t = 0; t < 7; t++)
        {
            var f = TextureAnimator.Evaluate(anim, t);
            xs[t] = (f.OffsetS - (-0.5f + 0.125f)) / 0.25f;
        }

        Assert.Equal(new[] { 0f, 1f, 2f, 3f, 2f, 1f, 0f }, xs.Select(x => MathF.Round(x)).ToArray());
    }

    [Fact]
    public void FromWire_ReadsFace255AsEveryFace()
    {
        var anim = TextureAnimation.FromWire(mode: 0x03, face: 255, sizeX: 4, sizeY: 4, start: 0f, length: 0f, rate: 4f);

        Assert.Equal(-1, anim.Face);
    }

    [Fact]
    public void FromWire_ClampsSizeUpForSteppedAnimations()
    {
        var stepped = TextureAnimation.FromWire(mode: 0x03, face: 0, sizeX: 0, sizeY: 0, start: 0f, length: 0f, rate: 4f);
        var smooth = TextureAnimation.FromWire(mode: 0x13, face: 0, sizeX: 0, sizeY: 0, start: 0f, length: 0f, rate: 4f);

        Assert.Equal(1, stepped.SizeX);
        Assert.Equal(1, stepped.SizeY);
        // SMOOTH keeps the zero: it selects the sizeless "slide U continuously" branch.
        Assert.Equal(0, smooth.SizeX);
        Assert.Equal(0, smooth.SizeY);
    }
}
