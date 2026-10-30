using System.Xml.Linq;
using LibreMetaverse;
using SLNG.Assets;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace SLNG.Core.Tests;

/// <summary>BUG-AVATAR-15: the shape's <c>volume_morph</c> entries (avatar_lad.xml) scale and move the collision-volume
/// joints (LEFT_PEC, RIGHT_PEC, BELLY, BUTT, ...) that fitted mesh bodies and clothes are rigged to. The viewer applies
/// them in <c>LLPolyMorphTarget::apply</c> (llpolymorph.cpp:642-650): <c>setScale(scale + volume.scale * delta_weight)</c>
/// and <c>setPosition(pos + volume.pos * delta_weight)</c>. LibreMetaverse carries none of that data.</summary>
public class CollisionVolumeMorphTests
{
    private const int BreastSize = 105;
    private const int BreastGravity = 507;
    private const int MaleParam = 80;

    private static string CharDir => System.IO.Path.Combine(AppContext.BaseDirectory, "linden", "character");

    /// <summary>Every Group0 param at weight 0 except <paramref name="values"/>, and Breast Size at its neutral default,
    /// so only what a test names contributes.</summary>
    private static byte[] Params(params (int Id, float Value)[] values)
    {
        int[] group0 = VisualParams.Group0ParamIds!;
        var bytes = new byte[group0.Length];
        float Encode(VisualParam vp, float v) => vp.MaxValue == vp.MinValue ? 0 : (v - vp.MinValue) / (vp.MaxValue - vp.MinValue) * 255f;
        for (int i = 0; i < group0.Length; i++)
        {
            if (!VisualParams.Params.TryGetValue(group0[i], out var vp)) continue;
            float v = group0[i] == BreastSize ? vp.DefaultValue : 0f;
            bytes[i] = (byte)System.Math.Round(Encode(vp, v));
        }
        foreach (var (id, value) in values)
        {
            int index = System.Array.IndexOf(group0, id);
            Assert.True(index >= 0, $"param {id} is not in Group0");
            bytes[index] = (byte)System.Math.Round(Encode(VisualParams.Params[id], value));
        }
        return bytes;
    }

    [Fact]
    public void Breast_size_at_maximum_scales_and_moves_the_pec_volumes_like_Big_Chest()
    {
        // 105 Breast Size drives 626 Big_Chest (avatar_lad.xml: min1=.5 max1=1 -> 0..1); Big_Chest's volume_morph is
        // scale 0.0273 on all axes and pos (0.038, +-0.024, -0.016) for LEFT_PEC / RIGHT_PEC.
        var shape = AvatarShapeService.ComputeDistortions(Params((BreastSize, 1f)), CharDir);

        Assert.True(shape.BoneMods.TryGetValue("LEFT_PEC", out var left), "LEFT_PEC got no distortion");
        Assert.True(shape.BoneMods.TryGetValue("RIGHT_PEC", out var right), "RIGHT_PEC got no distortion");
        Assert.True((left.Scale - new Vector3(0.0273f)).Length() < 0.003f, $"LEFT_PEC scale {left.Scale}");
        Assert.True((left.Position - new Vector3(0.038f, 0.024f, -0.016f)).Length() < 0.003f, $"LEFT_PEC position {left.Position}");
        Assert.True((right.Position - new Vector3(0.038f, -0.024f, -0.016f)).Length() < 0.003f, $"RIGHT_PEC position {right.Position}");
    }

    [Fact]
    public void Neutral_breast_size_leaves_the_pec_volumes_alone()
    {
        var shape = AvatarShapeService.ComputeDistortions(Params(), CharDir);

        if (shape.BoneMods.TryGetValue("LEFT_PEC", out var left))
        {
            Assert.True(left.Scale.Length() < 0.002f, $"LEFT_PEC scale {left.Scale}");
            Assert.True(left.Position.Length() < 0.002f, $"LEFT_PEC position {left.Position}");
        }
    }

    [Fact]
    public void Params_that_morph_the_same_volume_add_up()
    {
        var size = AvatarShapeService.ComputeDistortions(Params((BreastSize, 1f)), CharDir);
        var both = AvatarShapeService.ComputeDistortions(Params((BreastSize, 1f), (BreastGravity, 1f)), CharDir);

        // Breast_Gravity's volume_morph moves the PEC volumes by (0.004, 0, -0.01) per unit of weight, no scale.
        var delta = both.BoneMods["LEFT_PEC"].Position - size.BoneMods["LEFT_PEC"].Position;
        Assert.True((delta - new Vector3(0.004f, 0f, -0.01f)).Length() < 0.0015f, $"gravity added {delta}");
        Assert.True((both.BoneMods["LEFT_PEC"].Scale - size.BoneMods["LEFT_PEC"].Scale).Length() < 0.0005f);
    }

    [Fact]
    public void Without_the_character_directory_the_morphs_are_skipped_not_guessed()
    {
        var shape = AvatarShapeService.ComputeDistortions(Params((BreastSize, 1f)));

        Assert.False(shape.BoneMods.ContainsKey("LEFT_PEC"));
    }

    [Fact]
    public void The_table_reads_every_volume_morph_of_a_param()
    {
        var doc = XDocument.Parse("""
            <linden_avatar>
              <param id="626" name="Big_Chest"><param_morph>
                <volume_morph name="LEFT_PEC" scale="0.0273 0.0273 0.0273" pos="0.038 0.024 -0.016"/>
                <volume_morph name="RIGHT_PEC" scale="0.0273 0.0273 0.0273" pos="0.038 -0.024 -0.016"/>
              </param_morph></param>
              <param id="5" name="Plain"><param_morph/></param>
              <param id="6" name="NoPos"><param_morph><volume_morph name="BELLY" scale="0.1 0 0"/></param_morph></param>
            </linden_avatar>
            """);

        var table = VolumeMorphTable.Parse(doc);

        Assert.Equal(2, table[626].Length);
        Assert.Equal("RIGHT_PEC", table[626][1].Volume);
        Assert.Equal(new Vector3(0.038f, -0.024f, -0.016f), table[626][1].Position);
        Assert.False(table.ContainsKey(5));
        Assert.Equal(Vector3.Zero, table[6][0].Position); // a missing attribute is the viewer's zero default
    }
}
