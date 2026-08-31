using LibreMetaverse;

namespace SLNG.Net;

/// <summary>
/// FEAT-AVATAR-01: generates known-answer skin textures for testing the bake.
///
/// <para>Grid content cannot answer "did the bake work". A real skin is a photograph of a person:
/// if the head channel came out flipped, half-scale, or fed from the wrong layer, it still looks
/// broadly like a face, and every defect found in this task was invisible that way until the
/// composite was measured against a reference. A texture whose correct appearance is known in
/// advance turns each of those into something visible at a glance.</para>
///
/// <para>Each channel gets its own base colour — head green, upper body blue, lower body red — so
/// a layer landing in the wrong channel is obvious. On top of that:
/// <list type="bullet">
/// <item>a grid, so scaling and shearing show up;</item>
/// <item>a top-to-bottom brightness ramp, so a vertical flip is visible even where the grid is
/// symmetric — the defect that hid longest in the head bake;</item>
/// <item>four differently sized corner squares, so orientation is unambiguous;</item>
/// <item>a diagonal, which no stride or row-order error survives looking straight.</item>
/// </list></para>
///
/// <para>Deliberately plain byte arithmetic: this lives in <c>SLNG.Net</c>, which may not reference
/// the image stack in <c>SLNG.Assets</c>, and the pixels go out through the bake encoder that is
/// already injected for exactly that reason.</para>
/// </summary>
internal static class TestSkinTextures
{
    /// <summary>Bake resolution, so a test texture never exercises the resize paths — the point is
    /// to test the bake, not the scaler.</summary>
    internal const int Size = 1024;

    /// <summary>Base colours as (B, G, R). Chosen far apart in all three channels so a channel mix-up
    /// cannot be mistaken for a lighting difference.</summary>
    internal static (byte B, byte G, byte R) BaseColor(AvatarTextureIndex slot) => slot switch
    {
        AvatarTextureIndex.HeadBodypaint => (40, 190, 40),    // green
        AvatarTextureIndex.UpperBodypaint => (200, 90, 40),   // blue
        AvatarTextureIndex.LowerBodypaint => (40, 60, 210),   // red
        _ => (160, 160, 160),
    };

    /// <summary>Builds one channel's texture as tightly packed 8-bit BGRA, top-left origin — the
    /// layout <see cref="IBakeTextureEncoder.EncodeBake"/> expects.</summary>
    internal static byte[] Build(AvatarTextureIndex slot)
    {
        var (baseB, baseG, baseR) = BaseColor(slot);
        var pixels = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            // Bright at the top, dark at the bottom. A vertically flipped bake reads as a body that
            // is lit from below, which is unmistakable on an avatar.
            float ramp = 0.45f + 0.55f * (1f - y / (float)Size);

            for (int x = 0; x < Size; x++)
            {
                byte b = (byte)(baseB * ramp), g = (byte)(baseG * ramp), r = (byte)(baseR * ramp);

                // Grid every 64 px: scaling changes the spacing, shearing bends the lines.
                if (x % 64 == 0 || y % 64 == 0) { b = g = r = 20; }

                // Diagonal: survives no row-stride or row-order mistake.
                if (System.Math.Abs(x - y) < 3) { b = g = r = 245; }

                // Corner markers, each a different size, so which corner is which is never in doubt.
                if (InCorner(x, y, 0, 0, 140) || InCorner(x, y, Size - 100, 0, 100)
                    || InCorner(x, y, 0, Size - 60, 60) || InCorner(x, y, Size - 30, Size - 30, 30))
                {
                    b = g = r = 255;
                }

                int i = (y * Size + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    private static bool InCorner(int x, int y, int left, int top, int size)
        => x >= left && x < left + size && y >= top && y < top + size;
}
