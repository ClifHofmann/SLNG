using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RenderBufferSwitchTests
{
    private static string Ultra => RenderBufferSwitch.Signature(3, true, true, true, true, 4096);
    private static string High => RenderBufferSwitch.Signature(2, true, false, true, true, 4096);
    private static string Low => RenderBufferSwitch.Signature(0, false, false, false, false, 0);

    private static void RunOut(RenderBufferSwitch s)
    {
        for (int i = 0; i < RenderBufferSwitch.ReleaseFrames - 1; i++)
            Assert.False(s.OnFrame());
        Assert.True(s.OnFrame());
    }

    [Fact]
    public void The_first_apply_goes_straight_in()
    {
        var s = new RenderBufferSwitch();
        Assert.Equal(RenderBufferSwitch.Step.ApplyNow, s.Request(Ultra));
        Assert.False(s.Pending);
        Assert.Equal(Ultra, s.Applied);
    }

    [Fact]
    public void A_change_that_leaves_the_buffers_alone_applies_at_once()
    {
        var s = new RenderBufferSwitch();
        s.Request(Ultra);
        // a draw-distance slider, a shadow-distance slider...: the signature is the same
        Assert.Equal(RenderBufferSwitch.Step.ApplyNow, s.Request(Ultra));
        Assert.False(s.Pending);
    }

    [Fact]
    public void A_preset_switch_releases_the_buffers_first_and_applies_after_the_wait()
    {
        var s = new RenderBufferSwitch();
        s.Request(Ultra);

        Assert.Equal(RenderBufferSwitch.Step.ReleaseThenApply, s.Request(High));
        Assert.True(s.Pending);
        Assert.Equal(Ultra, s.Applied); // the caller has not put High in yet

        RunOut(s);
        Assert.False(s.Pending);

        s.SetApplied(High);
        Assert.Equal(High, s.Applied);
        Assert.Equal(RenderBufferSwitch.Step.ApplyNow, s.Request(High));
    }

    [Fact]
    public void Another_change_during_the_wait_restarts_it_instead_of_stacking_a_second_release()
    {
        var s = new RenderBufferSwitch();
        s.Request(Ultra);
        s.Request(High);
        for (int i = 0; i < RenderBufferSwitch.ReleaseFrames - 2; i++) s.OnFrame();

        // the user picks Low while High is still waiting: no second release, the wait starts over
        Assert.Equal(RenderBufferSwitch.Step.Pending, s.Request(Low));
        for (int i = 0; i < RenderBufferSwitch.ReleaseFrames - 1; i++)
            Assert.False(s.OnFrame());
        Assert.True(s.OnFrame());
        Assert.False(s.OnFrame()); // fires once
    }

    [Fact]
    public void The_wait_ends_exactly_once_and_idle_frames_do_nothing()
    {
        var s = new RenderBufferSwitch();
        Assert.False(s.OnFrame());
        s.Request(Ultra);
        Assert.False(s.OnFrame());
        s.Request(Low);
        RunOut(s);
        Assert.False(s.OnFrame());
        Assert.False(s.OnFrame());
    }

    [Fact]
    public void The_signature_tells_apart_exactly_what_changes_the_buffers()
    {
        Assert.NotEqual(Ultra, High);
        Assert.NotEqual(RenderBufferSwitch.Signature(2, true, false, true, true, 4096), RenderBufferSwitch.Signature(2, true, false, true, true, 2048));
        Assert.NotEqual(RenderBufferSwitch.Signature(2, true, false, true, true, 4096), RenderBufferSwitch.Signature(2, true, false, true, false, 4096));
        Assert.Equal(RenderBufferSwitch.Signature(2, true, false, true, true, 4096), RenderBufferSwitch.Signature(2, true, false, true, true, 4096));
        Assert.Contains("shadows off", Low);
        Assert.Contains("msaa 8x", Ultra);
    }
}
