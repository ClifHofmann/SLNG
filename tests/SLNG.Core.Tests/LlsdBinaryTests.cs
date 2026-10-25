using System;
using System.Collections.Generic;
using System.Text;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-RENDER-49: binary LLSD, the format the viewer stores glTF material assets in
/// (llmaterialeditor.cpp:1315). Encoded here by hand, big-endian, the way
/// <c>LLSDBinaryFormatter</c> writes it, so the parser is checked against the layout and not against
/// itself.
/// </summary>
public class LlsdBinaryTests
{
    internal static class Encode
    {
        public static byte[] Header => Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n");

        public static byte[] U32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        public static byte[] Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            return Concat(new[] { (byte)'s' }, U32(b.Length), b);
        }

        public static byte[] Map(params (string Key, byte[] Value)[] entries)
        {
            var parts = new List<byte[]> { new[] { (byte)'{' }, U32(entries.Length) };
            foreach (var (key, value) in entries)
            {
                var k = Encoding.UTF8.GetBytes(key);
                parts.Add(new[] { (byte)'k' });
                parts.Add(U32(k.Length));
                parts.Add(k);
                parts.Add(value);
            }
            parts.Add(new[] { (byte)'}' });
            return Concat(parts.ToArray());
        }

        public static byte[] Concat(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (var p in parts) all.AddRange(p);
            return all.ToArray();
        }
    }

    [Fact]
    public void AMaterialEnvelopeWithItsHeaderReadsBack()
    {
        var bytes = Encode.Concat(Encode.Header, Encode.Map(
            ("version", Encode.Str("1.1")),
            ("type", Encode.Str("GLTF 2.0")),
            ("data", Encode.Str("{\"materials\":[{}]}"))));

        Assert.True(LlsdBinary.TryParse(bytes, out var value, out string error), error);
        Assert.True(value.IsMap);
        Assert.Equal("1.1", value["version"].AsString());
        Assert.Equal("GLTF 2.0", value["type"].AsString());
        Assert.Equal("{\"materials\":[{}]}", value["data"].AsString());
    }

    [Fact]
    public void TheHeaderIsOptionalAndCaseInsensitive()
    {
        var map = Encode.Map(("a", new byte[] { (byte)'1' }));

        Assert.True(LlsdBinary.TryParse(map, out var bare, out _));
        Assert.True(bare["a"].AsBoolean());
        Assert.True(LlsdBinary.TryParse(Encode.Concat(Encoding.ASCII.GetBytes("<? llsd/binary ?>\r\n"), map), out var lower, out _));
        Assert.True(lower["a"].AsBoolean());
    }

    [Fact]
    public void NumbersUuidsAndArraysAreBigEndian()
    {
        var id = new Guid("e69dde89-50f8-b85f-9a26-da60314acba6");
        var real = BitConverter.GetBytes(0.35);
        if (BitConverter.IsLittleEndian) Array.Reverse(real);
        var bytes = Encode.Map(
            ("i", Encode.Concat(new[] { (byte)'i' }, new byte[] { 0xFF, 0xFF, 0xFF, 0xFE })),
            ("r", Encode.Concat(new[] { (byte)'r' }, real)),
            ("u", Encode.Concat(new[] { (byte)'u' }, id.ToByteArray(bigEndian: true))),
            ("a", Encode.Concat(new[] { (byte)'[' }, Encode.U32(2), new[] { (byte)'0', (byte)'!' }, new[] { (byte)']' })));

        Assert.True(LlsdBinary.TryParse(bytes, out var value, out string error), error);
        Assert.Equal(-2, value["i"].AsInteger());
        Assert.Equal(0.35, value["r"].AsReal(), 12);
        Assert.Equal(id, value["u"].AsUuid());
        Assert.Equal(2, value["a"].Count);
        Assert.False(value["a"][0].AsBoolean());
        Assert.False(value["a"][1].IsDefined);
    }

    [Fact]
    public void QuotedKeysAndStringsHonourTheirEscapes()
    {
        var bytes = Encode.Concat(new[] { (byte)'{' }, Encode.U32(1),
            Encoding.ASCII.GetBytes("'ke\\'y'"), Encoding.ASCII.GetBytes("\"a\\nb\\x41\""), new[] { (byte)'}' });

        Assert.True(LlsdBinary.TryParse(bytes, out var value, out string error), error);
        Assert.Equal("a\nbA", value["ke'y"].AsString());
    }

    [Theory]
    [InlineData(new byte[] { (byte)'s', 0, 0, 0, 10, (byte)'a' }, "bytes needed")]           // length past the end
    [InlineData(new byte[] { (byte)'{', 0, 0, 0, 1, (byte)'k', 0, 0, 0, 1, (byte)'a', (byte)'1' }, "'}'")] // no closer
    [InlineData(new byte[] { (byte)'Z' }, "unknown marker")]
    [InlineData(new byte[] { (byte)'s', 0xFF, 0xFF, 0xFF, 0xFF }, "out of range")]
    public void AnythingThatDoesNotAddUpIsAFailureNotAnException(byte[] bytes, string why)
    {
        Assert.False(LlsdBinary.TryParse(bytes, out var value, out string error));
        Assert.False(value.IsDefined);
        Assert.Contains(why, error);
    }
}
