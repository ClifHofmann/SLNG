using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RollingMinimumTests
{
    [Fact]
    public void FirstReading_IsTheMinimum()
    {
        var m = new RollingMinimum(30);
        Assert.Equal(9000, m.Add(0, 9000));
    }

    [Fact]
    public void FallsAtOnce_RisesOnlyAfterTheLowReadingAgesOut()
    {
        var m = new RollingMinimum(30);
        m.Add(0, 9700);
        Assert.Equal(8800, m.Add(2, 8800));     // a lower reading wins at once
        Assert.Equal(8800, m.Add(4, 9500));     // a higher one does not lift it
        Assert.Equal(8800, m.Add(31, 9600));    // the 8800 at t=2 is still inside the window
        Assert.Equal(9500, m.Add(33, 9600));    // aged out: the minimum of what is left
    }

    [Fact]
    public void WobblingReadings_GiveAStableValue()
    {
        var m = new RollingMinimum(30);
        long[] readings = { 8810, 9507, 9718, 9659, 9002, 9230, 9624, 9559, 9581, 9354, 8763, 9292, 9577, 8936 };
        long last = 0;
        for (int i = 0; i < readings.Length; i++) last = m.Add(i * 2, readings[i]);
        Assert.Equal(8763, last);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var m = new RollingMinimum(30);
        m.Add(0, 100);
        m.Clear();
        Assert.Equal(500, m.Add(1, 500));
    }
}
