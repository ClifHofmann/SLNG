using System;
using System.Text;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class LlsdNotationTests
{
    [Theory]
    [InlineData("!", LlsdKind.Undefined)]
    [InlineData("0", LlsdKind.Boolean)]
    [InlineData("1", LlsdKind.Boolean)]
    [InlineData("true", LlsdKind.Boolean)]
    [InlineData("TRUE", LlsdKind.Boolean)]
    [InlineData("t", LlsdKind.Boolean)]
    [InlineData("T", LlsdKind.Boolean)]
    [InlineData("false", LlsdKind.Boolean)]
    [InlineData("FALSE", LlsdKind.Boolean)]
    [InlineData("f", LlsdKind.Boolean)]
    [InlineData("F", LlsdKind.Boolean)]
    [InlineData("i1", LlsdKind.Integer)]
    [InlineData("i-7", LlsdKind.Integer)]
    [InlineData("r1e-05", LlsdKind.Real)]
    [InlineData("r-0.5", LlsdKind.Real)]
    [InlineData("rnan", LlsdKind.Real)]
    [InlineData("r-inf", LlsdKind.Real)]
    [InlineData("rinf", LlsdKind.Real)]
    [InlineData("u00112233-4455-6677-8899-aabbccddeeff", LlsdKind.Uuid)]
    [InlineData("'it\\'s'", LlsdKind.String)]
    [InlineData("\"x\"", LlsdKind.String)]
    [InlineData("s(3)\"abc\"", LlsdKind.String)]
    public void Parse_Scalars_Table(string notation, LlsdKind expectedKind)
    {
        bool ok = LlsdNotation.TryParse(notation, out var val, out var err);
        Assert.True(ok, $"Failed to parse {notation}: {err}");
        Assert.Equal(expectedKind, val.Kind);
    }

    [Fact]
    public void Parse_Scalar_Values_Correct()
    {
        Assert.True(LlsdNotation.TryParse("0", out var b0, out _));
        Assert.False(b0.AsBoolean());

        Assert.True(LlsdNotation.TryParse("1", out var b1, out _));
        Assert.True(b1.AsBoolean());

        Assert.True(LlsdNotation.TryParse("true", out var bt, out _));
        Assert.True(bt.AsBoolean());

        Assert.True(LlsdNotation.TryParse("f", out var bf, out _));
        Assert.False(bf.AsBoolean());

        Assert.True(LlsdNotation.TryParse("i-7", out var vi, out _));
        Assert.Equal(-7, vi.AsInteger());

        Assert.True(LlsdNotation.TryParse("r1e-05", out var vr, out _));
        Assert.Equal(1e-05, vr.AsReal(), 6);

        Assert.True(LlsdNotation.TryParse("rnan", out var vnan, out _));
        Assert.True(double.IsNaN(vnan.AsReal()));

        Assert.True(LlsdNotation.TryParse("r-inf", out var vninf, out _));
        Assert.True(double.IsNegativeInfinity(vninf.AsReal()));

        var guidStr = "00112233-4455-6677-8899-aabbccddeeff";
        Assert.True(LlsdNotation.TryParse($"u{guidStr}", out var vu, out _));
        Assert.Equal(Guid.Parse(guidStr), vu.AsUuid());

        Assert.True(LlsdNotation.TryParse("'it\\'s'", out var vs1, out _));
        Assert.Equal("it's", vs1.AsString());

        Assert.True(LlsdNotation.TryParse("\"x\"", out var vs2, out _));
        Assert.Equal("x", vs2.AsString());

        Assert.True(LlsdNotation.TryParse("s(3)\"abc\"", out var vs3, out _));
        Assert.Equal("abc", vs3.AsString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[[]]")]
    [InlineData("[{},{}]")]
    [InlineData("{'a':[],'b':{}, 'c':i1}")]
    [InlineData("[[],i5]")]
    public void Parse_EmptyContainers_Succeed(string input)
    {
        bool ok = LlsdNotation.TryParse(input, out var val, out var err);
        Assert.True(ok, $"Failed to parse empty container {input}: {err}");
        if (input == "{'a':[],'b':{}, 'c':i1}")
        {
            Assert.Equal(1, val["c"].AsInteger());
            Assert.Equal(0, val["a"].Count);
            Assert.Equal(0, val["b"].Count);
        }
        else if (input == "[[],i5]")
        {
            Assert.Equal(5, val[1].AsInteger());
            Assert.Equal(0, val[0].Count);
        }
    }

    [Theory]
    [InlineData("{'id':i4242,'od':[],'te':[]}")]
    [InlineData("{'id':i4242,'te':[],'od':[]}")]
    [InlineData("{'id':i4242,'od':[{}],'te':[i0]}")]
    [InlineData("{'id':i4242,'od':[{},{'bc':[r1,r0,r0,r1]}],'te':[i0,i1]}")]
    public void Parse_LmvRejectedPayloads_Succeed(string input)
    {
        bool ok = LlsdNotation.TryParse(input, out var val, out var err);
        Assert.True(ok, $"Payload rejected: {err}");
        Assert.Equal(4242, val["id"].AsInteger());
    }

    [Fact]
    public void Parse_Tolerance_Whitespace_TrailingComma_NullByte_Bom()
    {
        string pretty = @"
        {
            'id' : i42,
            'od' : [
                { 'bc' : [ r1.0, r0.5, r0.0, r1.0, ], },
            ],
            'te' : [ i0, ],
        }
        " + "\0";

        byte[] utf8WithBom = Encoding.UTF8.GetPreamble();
        byte[] payloadBytes = Encoding.UTF8.GetBytes(pretty);
        byte[] combined = new byte[utf8WithBom.Length + payloadBytes.Length];
        utf8WithBom.CopyTo(combined, 0);
        payloadBytes.CopyTo(combined, utf8WithBom.Length);

        bool ok = LlsdNotation.TryParse(combined, out var val, out var err);
        Assert.True(ok, $"Tolerance check failed: {err}");
        Assert.Equal(42, val["id"].AsInteger());
        Assert.Equal(1.0, val["od"][0]["bc"][0].AsReal());
    }

    [Fact]
    public void Parse_Tolerance_MissingCommaBetweenEntries()
    {
        string input = "{'a':i1 'b':i2}";
        bool ok = LlsdNotation.TryParse(input, out var val, out var err);
        Assert.True(ok, $"Missing comma check failed: {err}");
        Assert.Equal(1, val["a"].AsInteger());
        Assert.Equal(2, val["b"].AsInteger());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[i1")]
    [InlineData("{'a'}")]
    [InlineData("{i1:i2}")]
    [InlineData("{'a':?}")]
    [InlineData("u1234")]
    [InlineData("i999999999999999999999999999")]
    [InlineData("r bad")]
    public void Parse_Failures_ReturnFalseWithMessage(string badInput)
    {
        bool ok = LlsdNotation.TryParse(badInput, out _, out var err);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void Parse_NestingTooDeep_Fails()
    {
        string deeplyNested = new string('[', 40) + new string(']', 40);
        bool ok = LlsdNotation.TryParse(deeplyNested, out _, out var err);
        Assert.False(ok);
        Assert.Contains("depth", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InputExceedingMaxBytes_Fails()
    {
        byte[] huge = new byte[20_000];
        Array.Fill(huge, (byte)' ');
        huge[0] = (byte)'{';
        huge[1] = (byte)'}';
        bool ok = LlsdNotation.TryParse(huge, out _, out var err);
        Assert.False(ok);
        Assert.Contains("maximum", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LlsdValue_Semantics()
    {
        var undef = LlsdValue.Undefined;
        Assert.Equal(LlsdValue.Undefined, undef["missing"]);
        Assert.Equal(LlsdValue.Undefined, undef[0]);
        Assert.Equal(0, undef.Count);
        Assert.False(undef.IsDefined);

        Assert.True(LlsdNotation.TryParse("i1", out var intVal, out _));
        Assert.True(intVal.IsInteger);
        Assert.False(intVal.IsReal);
        Assert.Equal(1, intVal.AsInteger());

        Assert.True(LlsdNotation.TryParse("r2.9", out var realVal, out _));
        Assert.True(realVal.IsReal);
        Assert.False(realVal.IsInteger);
        Assert.Equal(2, realVal.AsInteger());
    }
}
