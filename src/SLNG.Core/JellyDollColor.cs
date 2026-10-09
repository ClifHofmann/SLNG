using System;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-08: the flat colour a reduced ("jelly doll") avatar is drawn in, ported from the
/// reference viewer's <c>LLVOAvatar::calcMutedAVColor</c>
/// (<c>indra/newview/llvoavatar.cpp:11948-11997</c>, vendored under <c>scratch/slviewer</c>).
///
/// <para>The viewer has two answers. The one that ships is the final <c>else</c> (:11990-11994):
/// every over-limit avatar is <c>LLColor4::grey4</c> = (0.3, 0.3, 0.3)
/// (<c>indra/llmath/v4color.cpp:61</c>). The other, behind <c>#ifdef COLORIZE_JELLYDOLLS</c>
/// (:11971-11989, never defined anywhere in the tree), picks a colour from the FIRST BYTE of the
/// agent's UUID so a given avatar always gets the same hue: that byte / 256 is scaled over the
/// seven-stop ring red, magenta, blue, cyan, green, yellow, red, linearly interpolated between the
/// two neighbouring stops, normalised to unit RGB length and toned down by 0.28.</para>
///
/// <para>We port the colourised branch: in a crowd of identical dark grey dolls nobody can be told
/// apart, and the per-avatar hue costs nothing. <see cref="Colorize"/> switches back to the grey
/// the shipped viewer uses.</para>
/// </summary>
public static class JellyDollColor
{
    /// <summary>The viewer's <c>#ifdef COLORIZE_JELLYDOLLS</c>. False gives grey4 for everyone.</summary>
    public static readonly bool Colorize = true;

    /// <summary>The "tone it down" factor of the colourised branch (llvoavatar.cpp:11986).</summary>
    public const float Brightness = 0.28f;

    /// <summary><c>LLColor4::grey4</c> (v4color.cpp:61): what the shipped viewer paints every jelly doll.</summary>
    public const float Grey4 = 0.3f;

    // LLColor4::red, magenta, blue, cyan, green, yellow, red (v4color.cpp:41-47); llvoavatar.cpp:11978.
    private static readonly (float R, float G, float B)[] Spectrum =
    {
        (1f, 0f, 0f), (1f, 0f, 1f), (0f, 0f, 1f), (0f, 1f, 1f), (0f, 1f, 0f), (1f, 1f, 0f), (1f, 0f, 0f),
    };

    /// <summary>The first byte of the UUID as the viewer reads it (<c>LLUUID::mData[0]</c>): the first
    /// two hex digits of the textual form. .NET's <see cref="Guid.ToByteArray()"/> is mixed-endian and
    /// would hand back the fourth byte instead.</summary>
    public static byte FirstByte(Guid agentId)
    {
        Span<byte> bytes = stackalloc byte[16];
        agentId.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes[0];
    }

    /// <summary>The colour of this avatar's jelly doll, linear components in 0..1.</summary>
    public static (float R, float G, float B) ForAgent(Guid agentId) => ForFirstByte(FirstByte(agentId));

    /// <summary>The colour for a UUID whose first byte is <paramref name="firstByte"/>. At most 256
    /// distinct values, which is what lets the renderer share one material per colour.</summary>
    public static (float R, float G, float B) ForFirstByte(byte firstByte)
    {
        if (!Colorize) return (Grey4, Grey4, Grey4);

        // llvoavatar.cpp:11974-11987
        float spectrum = firstByte / 256f * (Spectrum.Length - 1);   // between 0 and 6
        int first = (int)MathF.Floor(spectrum);                      // desired colour lies after this stop
        int second = first + 1;                                      // ...and before this one
        float between = spectrum - first;

        var a = Spectrum[first];
        var b = Spectrum[second];
        float r = a.R + (b.R - a.R) * between;
        float g = a.G + (b.G - a.G) * between;
        float bl = a.B + (b.B - a.B) * between;

        // LLColor4::normalize(): unit RGB length (v4color.h:417-429), then `new_color *= 0.28f`.
        float length = MathF.Sqrt(r * r + g * g + bl * bl);
        if (length > 0f)
        {
            r /= length;
            g /= length;
            bl /= length;
        }
        return (r * Brightness, g * Brightness, bl * Brightness);
    }
}
