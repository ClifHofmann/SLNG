using System;
using SLNG.Core.Avatars;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-PERF-24: the rigged-skin content key. Two meshes whose keys are equal share one
/// engine skin, so a key that called two different skins equal would pose one mesh with the
/// other's bind matrices. The tests that matter are "every bone and every matrix element
/// separates two keys"; the hash tests guard the dictionary use.</summary>
public class SkinBindKeyTests
{
    private static float[] Matrices(int binds, float seed = 0f)
    {
        var values = new float[binds * SkinBindKey.FloatsPerBind];
        for (int i = 0; i < values.Length; i++) values[i] = seed + i * 0.25f;
        return values;
    }

    [Fact]
    public void SameBonesAndMatrices_AreEqual_WithEqualHashes()
    {
        var a = new SkinBindKey(new[] { 3, 7, 9 }, Matrices(3));
        var b = new SkinBindKey(new[] { 3, 7, 9 }, Matrices(3));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(3, a.BindCount);
    }

    [Fact]
    public void ADifferentBone_SeparatesTheKeys()
    {
        var a = new SkinBindKey(new[] { 3, 7, 9 }, Matrices(3));
        var b = new SkinBindKey(new[] { 3, 8, 9 }, Matrices(3));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void EveryMatrixElement_SeparatesTheKeys()
    {
        var reference = new SkinBindKey(new[] { 1, 2 }, Matrices(2));
        for (int i = 0; i < 2 * SkinBindKey.FloatsPerBind; i++)
        {
            var values = Matrices(2);
            values[i] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(values[i]) + 1); // one ulp
            Assert.NotEqual(reference, new SkinBindKey(new[] { 1, 2 }, values));
        }
    }

    [Fact]
    public void TheSameBindsInAnotherOrder_AreDifferentSkins()
    {
        // Slot order is what the geometry's bone indices point at, so it is part of the identity.
        var values = Matrices(2);
        var swapped = new float[values.Length];
        Array.Copy(values, SkinBindKey.FloatsPerBind, swapped, 0, SkinBindKey.FloatsPerBind);
        Array.Copy(values, 0, swapped, SkinBindKey.FloatsPerBind, SkinBindKey.FloatsPerBind);

        Assert.NotEqual(new SkinBindKey(new[] { 1, 2 }, values), new SkinBindKey(new[] { 2, 1 }, swapped));
    }

    [Fact]
    public void ALongerSkin_IsNotEqualToItsPrefix()
    {
        var shorter = new SkinBindKey(new[] { 1, 2 }, Matrices(2));
        var longer = new SkinBindKey(new[] { 1, 2, 3 }, Matrices(3));

        Assert.NotEqual(shorter, longer);
    }

    [Fact]
    public void TheKey_CopiesItsInputs()
    {
        var bones = new[] { 1, 2 };
        var values = Matrices(2);
        var key = new SkinBindKey(bones, values);
        bones[0] = 5;
        values[0] = 99f;

        Assert.Equal(new SkinBindKey(new[] { 1, 2 }, Matrices(2)), key);
    }

    [Fact]
    public void MismatchedLengths_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new SkinBindKey(new[] { 1, 2 }, Matrices(1)));
    }
}
