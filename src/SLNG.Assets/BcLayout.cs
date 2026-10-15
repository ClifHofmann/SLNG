namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-17 Part B: the byte layout of a texture and its mip chain, exactly as Godot 4.7 lays it
/// out in an <c>Image</c> (<c>Image::_get_dst_image_size</c>, core/io/image.cpp, 4.7-stable): the levels
/// follow each other, each half the size of the one before (never below 1), down to 1x1; a
/// block-compressed level is padded to whole 4x4 blocks, so the 2x2 and 1x1 levels are one block each.
/// <c>Image.CreateFromData</c> rejects any other length, and the budget counts these bytes.
/// </summary>
public static class BcLayout
{
    /// <summary>Bytes per 4x4 block, or 0 for <see cref="GpuTextureFormat.Rgba8"/>.</summary>
    public static int BlockBytes(GpuTextureFormat format) => format switch
    {
        GpuTextureFormat.Bc1 => 8,
        GpuTextureFormat.Bc3 => 16,
        _ => 0,
    };

    /// <summary>Bytes of one level of <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static long LevelBytes(GpuTextureFormat format, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        if (format == GpuTextureFormat.Rgba8) return (long)width * height * 4;
        return (long)((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(format);
    }

    /// <summary>Levels in the chain: 1 without mipmaps, else halvings until both sides are 1.</summary>
    public static int LevelCount(int width, int height, bool mipmaps)
    {
        if (!mipmaps || width <= 0 || height <= 0) return 1;
        int levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
            levels++;
        }
        return levels;
    }

    /// <summary>The size of level <paramref name="level"/> of a chain whose top is
    /// <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static (int Width, int Height) LevelSize(int width, int height, int level)
    {
        for (int i = 0; i < level; i++)
        {
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return (width, height);
    }

    /// <summary>Bytes of the whole chain (just the top level without mipmaps).</summary>
    public static long ChainBytes(GpuTextureFormat format, int width, int height, bool mipmaps)
    {
        long total = 0;
        int levels = LevelCount(width, height, mipmaps);
        for (int i = 0; i < levels; i++)
        {
            total += LevelBytes(format, width, height);
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return total;
    }

    /// <summary>Byte offset of level <paramref name="level"/> inside the chain.</summary>
    public static long LevelOffset(GpuTextureFormat format, int width, int height, int level)
    {
        long offset = 0;
        for (int i = 0; i < level; i++)
        {
            offset += LevelBytes(format, width, height);
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return offset;
    }

    /// <summary>
    /// The level of a chain whose top is <paramref name="width"/> x <paramref name="height"/> that is
    /// exactly <paramref name="targetWidth"/> x <paramref name="targetHeight"/>, or -1. The tail of a
    /// mip chain from that level IS the chain of a texture of the target size, so a smaller upload or a
    /// GPU-side shrink of a compressed texture is a slice, with no decode and no encode.
    /// </summary>
    public static int FindLevel(int width, int height, bool mipmaps, int targetWidth, int targetHeight)
    {
        int levels = LevelCount(width, height, mipmaps);
        for (int i = 0; i < levels; i++)
        {
            if (width == targetWidth && height == targetHeight) return i;
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        return -1;
    }
}
