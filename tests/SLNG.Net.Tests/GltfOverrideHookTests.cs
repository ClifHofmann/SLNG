using System;
using System.Text;
using LibreMetaverse.StructuredData;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

public class GltfOverrideHookTests
{
    [Fact]
    public void Process_ValidPayload_Produces_Expected_Event()
    {
        const string payload = "{'id':i4242,'od':[{'bc':[r0.8,r0.1,r0.1,r1]}],'te':[i1]}";
        byte[] bytes = Encoding.UTF8.GetBytes(payload);

        bool ok = GltfOverrideEvents.TryProcess(12345ul, bytes, out var evt, out var err);
        Assert.True(ok, err);
        Assert.NotNull(evt);
        Assert.Equal(12345ul, evt!.RegionHandle);
        Assert.Equal(4242u, evt.LocalId);
        Assert.Single(evt.Faces);
        Assert.True(evt.Faces.ContainsKey(1));
    }

    [Fact]
    public void Process_RejectedPayload_Returns_False()
    {
        byte[] bad = Encoding.UTF8.GetBytes("{'id':r1,'te':[]}");
        bool ok = GltfOverrideEvents.TryProcess(12345ul, bad, out var evt, out var err);
        Assert.False(ok);
        Assert.Null(evt);
        Assert.NotNull(err);
    }

    [Fact]
    public void Preview_Replaces_NonPrintable_And_Caps_At_240()
    {
        byte[] binary = new byte[300];
        for (int i = 0; i < binary.Length; i++)
        {
            binary[i] = (byte)(i % 256);
        }

        string preview = GltfOverrideEvents.Preview(binary);
        Assert.Equal(240, preview.Length);
        // byte 0 is non-printable -> '.'
        Assert.Equal('.', preview[0]);
        // byte 65 is 'A' (printable) -> 'A'
        Assert.Equal('A', preview[65]);
    }

    [Theory]
    [InlineData("{'id':i4242,'od':[],'te':[]}")]
    [InlineData("{'id':i4242,'te':[],'od':[]}")]
    [InlineData("{'id':i4242,'od':[{}],'te':[i0]}")]
    [InlineData("{'id':i4242,'od':[{},{'bc':[r1,r0,r0,r1]}],'te':[i0,i1]}")]
    public void Regression_LmvFailsOnEmptyContainers_While_SlngParses(string payload)
    {
        // If an LMV upgrade makes this test fail, LMV fixed its parser;
        // keep SLNG's own (replace semantics, no LMV type) and delete only this test.
        Assert.Throws<OSDException>(() => OSDParser.DeserializeLLSDNotation(payload));

        bool ok = GltfOverrideParser.TryParse(Encoding.UTF8.GetBytes(payload), out var msg, out var err);
        Assert.True(ok, err);
        Assert.NotNull(msg);
        Assert.Equal(4242u, msg!.LocalId);
    }
}
