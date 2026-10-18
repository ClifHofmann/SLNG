using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace SLNG.Core;

/// <summary>
/// Reads binary LLSD -- the format the viewer stores glTF material assets in
/// (<c>LLSDSerialize::serialize(asset, str, LLSD_BINARY)</c>, llmaterialeditor.cpp:1315), and that
/// BUG-RENDER-49 has to open before it can see a material at all.
///
/// <para><b>Layout</b> (<c>LLSDBinaryParser::doParse</c>, llsdserialize.cpp:952-1160), all numbers
/// big-endian: an optional header line <c>&lt;? LLSD/Binary ?&gt;</c> (case-insensitive, then
/// whitespace), then one value. <c>{</c> U32 count, count x (key, value), <c>}</c> -- a key is
/// <c>k</c> U32 length + bytes, or a quoted string; <c>[</c> U32 count, count x value, <c>]</c>;
/// <c>!</c> undefined; <c>1</c>/<c>0</c> booleans; <c>i</c> S32; <c>r</c> F64; <c>u</c> 16-byte
/// UUID; <c>s</c> and <c>l</c> (URI) U32 length + UTF-8; <c>d</c> F64 date; <c>b</c> U32 length +
/// bytes; <c>'</c> or <c>"</c> a quoted string.</para>
///
/// <para>Never throws: anything that does not add up -- a length past the end, an unknown marker, a
/// missing closer -- is a parse failure with the offset, not an exception.</para>
/// </summary>
public static class LlsdBinary
{
    public const int MaxDepth = 32;

    /// <summary>The header the viewer writes in front of binary LLSD (llsdserialize.cpp:60, :98).</summary>
    public const string Header = "<? LLSD/Binary ?>";

    /// <summary>True when <paramref name="data"/> starts with the binary header.</summary>
    public static bool HasHeader(ReadOnlySpan<byte> data)
    {
        var header = Encoding.ASCII.GetBytes(Header);
        if (data.Length < header.Length) return false;
        for (int i = 0; i < header.Length; i++)
        {
            if (char.ToUpperInvariant((char)data[i]) != char.ToUpperInvariant((char)header[i])) return false;
        }
        return true;
    }

    /// <summary>Parses one binary LLSD value, with or without the header line.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out LlsdValue value, out string error)
    {
        value = LlsdValue.Undefined;
        error = string.Empty;
        int i = 0;
        if (HasHeader(data))
        {
            i = Header.Length;
            while (i < data.Length && (data[i] == (byte)'\n' || data[i] == (byte)'\r' || data[i] == (byte)' ' || data[i] == (byte)'\t')) i++;
        }

        try
        {
            value = ParseValue(data, ref i, 0);
            return true;
        }
        catch (FormatException ex)
        {
            value = LlsdValue.Undefined;
            error = ex.Message;
            return false;
        }
    }

    private static FormatException Fail(string message, int at) => new($"{message} at offset {at}");

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int i, int count)
    {
        if (count < 0 || count > data.Length - i) throw Fail($"{count} bytes needed, {data.Length - i} left", i);
        var span = data.Slice(i, count);
        i += count;
        return span;
    }

    private static int ReadLength(ReadOnlySpan<byte> data, ref int i)
    {
        uint length = BinaryPrimitives.ReadUInt32BigEndian(Take(data, ref i, 4));
        if (length > int.MaxValue) throw Fail("length out of range", i - 4);
        return (int)length;
    }

    private static string ReadSizedString(ReadOnlySpan<byte> data, ref int i)
    {
        int length = ReadLength(data, ref i);
        return Encoding.UTF8.GetString(Take(data, ref i, length));
    }

    /// <summary><c>deserialize_string_delim</c>: up to the closing quote, a backslash escaping the next
    /// character (\n, \t, \r, \xHH and the quote itself).</summary>
    private static string ReadQuotedString(ReadOnlySpan<byte> data, ref int i, byte quote)
    {
        var bytes = new List<byte>();
        while (true)
        {
            if (i >= data.Length) throw Fail("unterminated string", i);
            byte b = data[i++];
            if (b == quote) break;
            if (b != (byte)'\\')
            {
                bytes.Add(b);
                continue;
            }
            if (i >= data.Length) throw Fail("unterminated escape", i);
            byte e = data[i++];
            switch (e)
            {
                case (byte)'n': bytes.Add((byte)'\n'); break;
                case (byte)'t': bytes.Add((byte)'\t'); break;
                case (byte)'r': bytes.Add((byte)'\r'); break;
                case (byte)'a': bytes.Add(7); break;
                case (byte)'b': bytes.Add(8); break;
                case (byte)'f': bytes.Add(12); break;
                case (byte)'v': bytes.Add(11); break;
                case (byte)'x':
                    var hex = Take(data, ref i, 2);
                    bytes.Add(Convert.ToByte(Encoding.ASCII.GetString(hex), 16));
                    break;
                default: bytes.Add(e); break;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static LlsdValue ParseValue(ReadOnlySpan<byte> data, ref int i, int depth)
    {
        if (depth > MaxDepth) throw Fail("nested too deep", i);
        if (i >= data.Length) throw Fail("unexpected end of input", i);

        int at = i;
        byte marker = data[i++];
        switch (marker)
        {
            case (byte)'{':
                {
                    int count = ReadLength(data, ref i);
                    var map = new Dictionary<string, LlsdValue>(Math.Min(count, 256));
                    for (int n = 0; n < count; n++)
                    {
                        if (i >= data.Length) throw Fail("map ends early", i);
                        byte keyMarker = data[i++];
                        string key = keyMarker switch
                        {
                            (byte)'k' => ReadSizedString(data, ref i),
                            (byte)'\'' or (byte)'"' => ReadQuotedString(data, ref i, keyMarker),
                            _ => throw Fail($"map key marker 0x{keyMarker:X2}", i - 1),
                        };
                        map[key] = ParseValue(data, ref i, depth + 1);
                    }
                    if (i >= data.Length || data[i++] != (byte)'}') throw Fail("map without '}'", i);
                    return LlsdValue.FromMap(map);
                }
            case (byte)'[':
                {
                    int count = ReadLength(data, ref i);
                    var array = new List<LlsdValue>(Math.Min(count, 256));
                    for (int n = 0; n < count; n++) array.Add(ParseValue(data, ref i, depth + 1));
                    if (i >= data.Length || data[i++] != (byte)']') throw Fail("array without ']'", i);
                    return LlsdValue.FromArray(array);
                }
            case (byte)'!':
                return LlsdValue.Undefined;
            case (byte)'1':
                return LlsdValue.FromBoolean(true);
            case (byte)'0':
                return LlsdValue.FromBoolean(false);
            case (byte)'i':
                return LlsdValue.FromInteger(BinaryPrimitives.ReadInt32BigEndian(Take(data, ref i, 4)));
            case (byte)'r':
                return LlsdValue.FromReal(BinaryPrimitives.ReadDoubleBigEndian(Take(data, ref i, 8)));
            case (byte)'d':
                BinaryPrimitives.ReadDoubleBigEndian(Take(data, ref i, 8));
                return LlsdValue.Other();
            case (byte)'u':
                return LlsdValue.FromUuid(new Guid(Take(data, ref i, 16), bigEndian: true));
            case (byte)'s':
            case (byte)'l':
                return LlsdValue.FromString(ReadSizedString(data, ref i));
            case (byte)'\'':
            case (byte)'"':
                return LlsdValue.FromString(ReadQuotedString(data, ref i, marker));
            case (byte)'b':
                Take(data, ref i, ReadLength(data, ref i));
                return LlsdValue.Other();
            default:
                throw Fail($"unknown marker 0x{marker:X2}", at);
        }
    }
}
