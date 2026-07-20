using System.Collections.Generic;
using SLNG.Assets;
using Xunit;

namespace SLNG.Core.Tests;

public class MeshSkinWeightDecoderTests
{
    private static void WriteInfluence(List<byte> buf, byte joint, float weight)
    {
        buf.Add(joint);
        ushort raw = (ushort)(weight * 65535f);
        buf.Add((byte)(raw & 0xFF));
        buf.Add((byte)(raw >> 8));
    }

    [Fact]
    public void Vertex_with_exactly_four_influences_has_no_sentinel_and_does_not_shift_the_stream()
    {
        // The critical SL format rule (llmodel.cpp writer / llvolume.cpp reader): a vertex with
        // exactly 4 influences is NOT followed by 0xFF — the reader must stop on its own. A parser
        // that keeps scanning for 0xFF (LibreMetaverse's bug) consumes the next vertex's bytes
        // and corrupts the rest of the stream.
        var buf = new List<byte>();
        // v0: exactly 4 influences — joints 1..4 — NO sentinel afterwards.
        WriteInfluence(buf, 1, 0.4f);
        WriteInfluence(buf, 2, 0.3f);
        WriteInfluence(buf, 3, 0.2f);
        WriteInfluence(buf, 4, 0.1f);
        // v1: single influence — joint 5 — with sentinel.
        WriteInfluence(buf, 5, 0.9f);
        buf.Add(0xFF);
        // v2: two influences — joints 6,7 — with sentinel.
        WriteInfluence(buf, 6, 0.6f);
        WriteInfluence(buf, 7, 0.4f);
        buf.Add(0xFF);

        var weights = MeshSkinWeightDecoder.Decode(buf.ToArray(), 3);

        Assert.Equal(3, weights.Length);
        Assert.Equal((1, 2, 3, 4), (weights[0].Joint0, weights[0].Joint1, weights[0].Joint2, weights[0].Joint3));
        Assert.Equal(5, weights[1].Joint0);
        Assert.True(weights[1].Weight1 == 0f, "v1 has exactly one influence");
        Assert.Equal((6, 7), (weights[2].Joint0, weights[2].Joint1));
        Assert.True(System.Math.Abs(weights[2].Weight0 - 0.6f) < 0.01f);
        Assert.True(System.Math.Abs(weights[2].Weight1 - 0.4f) < 0.01f);
    }

    [Fact]
    public void Weights_are_clamped_to_the_viewer_range()
    {
        // llvolume.cpp: w = llclamp(influence/65535, 0.001, 0.999).
        var buf = new List<byte>();
        WriteInfluence(buf, 1, 0.0f);   // raw 0 → clamped up to 0.001
        buf.Add(0xFF);
        WriteInfluence(buf, 2, 1.0f);   // raw 65535 → clamped down to 0.999
        buf.Add(0xFF);

        var weights = MeshSkinWeightDecoder.Decode(buf.ToArray(), 2);

        Assert.Equal(0.001f, weights[0].Weight0, 3);
        Assert.Equal(0.999f, weights[1].Weight0, 3);
    }

    [Fact]
    public void Missing_data_pads_remaining_vertices_pinned_to_joint_zero()
    {
        var buf = new List<byte>();
        WriteInfluence(buf, 3, 0.5f);
        buf.Add(0xFF);

        var weights = MeshSkinWeightDecoder.Decode(buf.ToArray(), 3); // data only covers v0

        Assert.Equal(3, weights[0].Joint0);
        Assert.Equal(0, weights[1].Joint0);
        Assert.Equal(1f, weights[1].Weight0);
        Assert.Equal(0, weights[2].Joint0);
        Assert.Equal(1f, weights[2].Weight0);
    }
}
