namespace SLNG.Net;

/// <summary>
/// FEAT-AVATAR-01: brings LibreMetaverse's built-in bake layers up to the size its own baker
/// composites at, because the baker assumes they already match and corrupts the result when they
/// do not.
///
/// <para><b>The defect.</b> <c>Baker</c> allocates 1024x1024 for every channel except Eyes, but the
/// resource TGAs shipped with LibreMetaverse are 512x512. <c>DrawLayer</c> walks one flat index over
/// <c>bakeWidth * bakeHeight</c> = 1,048,576 and reads the source array at the same index; against a
/// 262,144-entry source it lays 512 source pixels across each 1024-pixel bake row, then its bounds
/// check quietly stops writing a quarter of the way down. Measured in-world 2026-08-31: the head
/// bake came back holding the face twice, sheared, with a diagonal seam. <c>AddAlpha</c> and
/// <c>MultiplyLayerFromAlpha</c> make the same assumption; <c>ApplyAlpha</c> and the per-wearable
/// layer path do resize, which is why only these three misbehave.</para>
///
/// <para>Only the head bake draws resource layers unconditionally (head_color, head_alpha,
/// head_skingrain, head_hair). upperbody_color and lowerbody_color are drawn only when no skin
/// texture is worn — which is exactly why the upper body composited perfectly while the head did
/// not.</para>
///
/// <para><b>Why the TGA handling is here.</b> The obvious route, reading through
/// <c>Baker.LoadResourceLayer</c>, is a trap: that loader caches, so it both returns the stale
/// 512x512 image after the file has been rewritten and leaves the wrong size in the cache for the
/// baker to use. Measured — the file on disk was 4,194,322 bytes and the loader still reported
/// 512x512. So the files are read and written directly. The shipped layers are RLE TGAs (type 10
/// 32-bit BGRA, type 11 8-bit grey), which is a small enough format to handle exactly rather than
/// pull an image library across the layering boundary into <c>SLNG.Net</c>.</para>
/// </summary>
internal static class BakeResourceLayers
{
    /// <summary>What <c>Baker</c> allocates for head, upper body, lower body and hair. Eyes bakes at
    /// 128x128 but draws none of these layers, so one target size is correct.</summary>
    internal const int BakeSize = 1024;

    /// <summary>The layers reached through the three code paths that do not resize.</summary>
    internal static readonly string[] Layers =
    {
        "head_color.tga", "head_alpha.tga", "head_skingrain.tga", "head_hair.tga",
        "upperbody_color.tga", "lowerbody_color.tga",
    };

    /// <summary>Rewrites every mismatched layer under <paramref name="lindenDir"/> at the bake size.
    /// Idempotent, and best-effort per file: a layer left at the wrong size bakes badly, but
    /// throwing here would take out the session before login.</summary>
    internal static void UpscaleAll(string lindenDir, Action<string>? log = null)
    {
        // The pinned package resolves these under "character"; other layouts use "static_assets",
        // which is why GridSession mirrors between them. Rewrite both rather than depend on which
        // one wins — guessing wrong fails silently.
        foreach (var sub in new[] { "character", "static_assets" })
        {
            var dir = Path.Combine(lindenDir, sub);
            if (!Directory.Exists(dir)) continue;

            foreach (var name in Layers)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;

                try
                {
                    var tga = Upscale(File.ReadAllBytes(path), BakeSize);
                    if (tga != null) File.WriteAllBytes(path, tga);
                }
                catch (Exception ex) { log?.Invoke($"[Bake] could not resize {sub}/{name}: {ex.Message}"); }
            }
        }
    }

    /// <summary>Decodes a TGA, scales it to <paramref name="size"/> square with nearest-neighbour
    /// sampling — matching what the baker uses for the layers it does resize — and re-encodes it
    /// uncompressed at the same bit depth. Returns null when the image is already that size, so the
    /// caller can leave the file untouched.</summary>
    internal static byte[]? Upscale(byte[] tga, int size)
    {
        var (pixels, width, height, bytesPerPixel) = Decode(tga);
        if (width == size && height == size) return null;

        var scaled = new byte[size * size * bytesPerPixel];
        for (int y = 0; y < size; y++)
        {
            int srcRow = y * height / size;
            for (int x = 0; x < size; x++)
            {
                int src = (srcRow * width + x * width / size) * bytesPerPixel;
                int dst = (y * size + x) * bytesPerPixel;
                for (int b = 0; b < bytesPerPixel; b++) scaled[dst + b] = pixels[src + b];
            }
        }

        return Encode(scaled, size, bytesPerPixel);
    }

    /// <summary>Reads TGA types 2/3 (uncompressed) and 10/11 (run-length encoded), which is every
    /// form the shipped layers use. Row order is preserved rather than interpreted: the data goes
    /// back out the same way it came in, so the orientation cannot drift.</summary>
    private static (byte[] Pixels, int Width, int Height, int Bpp) Decode(byte[] tga)
    {
        if (tga.Length < 18) throw new InvalidDataException("truncated TGA header");

        int idLength = tga[0];
        int imageType = tga[2];
        int width = tga[12] | (tga[13] << 8);
        int height = tga[14] | (tga[15] << 8);
        int bpp = tga[16] / 8;

        if (bpp is not (1 or 3 or 4)) throw new InvalidDataException($"unsupported TGA depth {tga[16]}");
        if (width <= 0 || height <= 0) throw new InvalidDataException($"bad TGA size {width}x{height}");

        int offset = 18 + idLength;
        int count = width * height;
        var pixels = new byte[count * bpp];

        if (imageType is 2 or 3)
        {
            if (offset + pixels.Length > tga.Length) throw new InvalidDataException("truncated TGA data");
            Array.Copy(tga, offset, pixels, 0, pixels.Length);
            return (pixels, width, height, bpp);
        }

        if (imageType is not (10 or 11)) throw new InvalidDataException($"unsupported TGA type {imageType}");

        for (int written = 0; written < count;)
        {
            if (offset >= tga.Length) throw new InvalidDataException("truncated TGA packet");

            int packet = tga[offset++];
            int run = (packet & 0x7F) + 1;
            if (written + run > count) throw new InvalidDataException("TGA run overflows the image");

            if ((packet & 0x80) != 0)
            {
                // Run-length packet: one pixel repeated.
                if (offset + bpp > tga.Length) throw new InvalidDataException("truncated TGA run");
                for (int i = 0; i < run; i++)
                    Array.Copy(tga, offset, pixels, (written + i) * bpp, bpp);
                offset += bpp;
            }
            else
            {
                // Raw packet: `run` literal pixels.
                int bytes = run * bpp;
                if (offset + bytes > tga.Length) throw new InvalidDataException("truncated TGA literals");
                Array.Copy(tga, offset, pixels, written * bpp, bytes);
                offset += bytes;
            }

            written += run;
        }

        return (pixels, width, height, bpp);
    }

    /// <summary>Writes an uncompressed TGA at the source's own depth — 8-bit grey stays grey, so the
    /// channel set LibreMetaverse infers from the file does not change.</summary>
    private static byte[] Encode(byte[] pixels, int size, int bpp)
    {
        var tga = new byte[18 + pixels.Length];

        tga[2] = (byte)(bpp == 1 ? 3 : 2);   // uncompressed grey / true-colour
        tga[12] = (byte)(size & 0xFF); tga[13] = (byte)(size >> 8);
        tga[14] = (byte)(size & 0xFF); tga[15] = (byte)(size >> 8);
        tga[16] = (byte)(bpp * 8);
        tga[17] = (byte)(bpp == 4 ? 8 : 0);  // alpha-channel bits; origin bottom-left, as shipped

        Array.Copy(pixels, 0, tga, 18, pixels.Length);
        return tga;
    }
}
