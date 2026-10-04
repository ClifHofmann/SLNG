using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Core;

/// <summary>The colours and opacities the radar's object layer is painted with.</summary>
public readonly record struct RadarObjectPalette(
    Vector3 Other,
    Vector3 OtherBelowWater,
    Vector3 Yours,
    Vector3 YoursBelowWater,
    float OtherFillAlpha,
    float YoursFillAlpha,
    float OutlineAlpha);

/// <summary>
/// The radar's object layer as ONE picture. Drawn square by square it cost two draw calls per prim on every
/// redraw -- measured at 52 ms per second of wall clock with 600 prims, nine tenths of what an open radar
/// cost -- while the objects only change when the layer is scanned again, every half second at the soonest.
/// So the squares are painted into a pixel buffer once per scan and the canvas draws that buffer as a single
/// texture, turned and scaled with the map exactly like a region's map tile.
///
/// The buffer covers <see cref="SizeMetres"/> metres in each direction at one pixel per metre, starting at
/// (<see cref="OriginX"/>, <see cref="OriginY"/>) in the CURRENT region's metres: the region itself with a
/// full region of neighbours on every side. Row 0 is the northern edge, as a map tile's is. Pure: bytes in,
/// bytes out; the app turns them into a texture.
/// </summary>
public static class RadarObjectRaster
{
    /// <summary>Pixels (= metres) per side of the layer.</summary>
    public const int SizeMetres = 768;

    /// <summary>The layer's south-west corner, in the current region's metres: one region west and south of it.</summary>
    public const float OriginX = -256f;
    public const float OriginY = -256f;

    public const int BufferLength = SizeMetres * SizeMetres * 4;

    /// <summary>Paints <paramref name="objects"/> into <paramref name="rgba"/> (straight alpha, 4 bytes per
    /// pixel, <see cref="BufferLength"/> long), clearing it first. Other people's prims go first and yours over
    /// them, so a small prim of yours is never hidden under a neighbour's wall; yours also get a one-pixel
    /// outline. Whatever lies outside the layer is skipped.</summary>
    /// <param name="phantomOpacity">How opaque a phantom prim is, 0..1.</param>
    public static void Render(IReadOnlyList<RadarObject> objects, byte[] rgba, in RadarObjectPalette palette, float phantomOpacity)
    {
        if (rgba.Length < BufferLength) throw new ArgumentException("buffer too small", nameof(rgba));
        Array.Clear(rgba, 0, BufferLength);

        for (int pass = 0; pass < 2; pass++)
        {
            bool yoursPass = pass == 1;
            foreach (var obj in objects)
            {
                if (obj.IsYours != yoursPass) continue;

                var colour = obj.IsYours
                    ? (obj.BelowWater ? palette.YoursBelowWater : palette.Yours)
                    : (obj.BelowWater ? palette.OtherBelowWater : palette.Other);
                float phantom = obj.Phantom ? phantomOpacity : 1f;

                // Metres to pixels: east is +column, north is -row (row 0 is the northern edge).
                int left = (int)MathF.Floor(obj.Position.X - obj.Radius - OriginX);
                int right = (int)MathF.Ceiling(obj.Position.X + obj.Radius - OriginX);   // exclusive
                int top = (int)MathF.Floor(OriginY + SizeMetres - (obj.Position.Y + obj.Radius));
                int bottom = (int)MathF.Ceiling(OriginY + SizeMetres - (obj.Position.Y - obj.Radius)); // exclusive

                if (right <= 0 || bottom <= 0 || left >= SizeMetres || top >= SizeMetres) continue;

                float fill = (obj.IsYours ? palette.YoursFillAlpha : palette.OtherFillAlpha) * phantom;
                int x0 = Math.Max(left, 0), x1 = Math.Min(right, SizeMetres);
                int y0 = Math.Max(top, 0), y1 = Math.Min(bottom, SizeMetres);

                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        Blend(rgba, x, y, colour, fill);

                if (!obj.IsYours) continue;

                float outline = palette.OutlineAlpha * phantom;
                for (int x = x0; x < x1; x++)
                {
                    if (top >= 0 && top < SizeMetres) Blend(rgba, x, top, colour, outline);
                    if (bottom - 1 >= 0 && bottom - 1 < SizeMetres && bottom - 1 != top) Blend(rgba, x, bottom - 1, colour, outline);
                }
                for (int y = y0; y < y1; y++)
                {
                    if (left >= 0 && left < SizeMetres) Blend(rgba, left, y, colour, outline);
                    if (right - 1 >= 0 && right - 1 < SizeMetres && right - 1 != left) Blend(rgba, right - 1, y, colour, outline);
                }
            }
        }
    }

    // Source-over, straight alpha: what drawing a translucent rectangle over the previous ones gives.
    private static void Blend(byte[] rgba, int x, int y, Vector3 colour, float alpha)
    {
        if (alpha <= 0f) return;
        alpha = Math.Min(alpha, 1f);

        int i = (y * SizeMetres + x) * 4;
        float dstA = rgba[i + 3] / 255f;
        float outA = alpha + dstA * (1f - alpha);
        if (outA <= 0f) return;

        float dstWeight = dstA * (1f - alpha);
        rgba[i] = ToByte((colour.X * alpha + (rgba[i] / 255f) * dstWeight) / outA);
        rgba[i + 1] = ToByte((colour.Y * alpha + (rgba[i + 1] / 255f) * dstWeight) / outA);
        rgba[i + 2] = ToByte((colour.Z * alpha + (rgba[i + 2] / 255f) * dstWeight) / outA);
        rgba[i + 3] = ToByte(outA);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
