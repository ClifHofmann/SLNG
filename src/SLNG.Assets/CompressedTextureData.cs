namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-17 Part B: a texture as the GPU holds it -- block-compressed, with its mip chain in
/// Godot's layout (<see cref="BcLayout"/>) -- plus what the renderer needs to know about it without
/// the RGBA pixels. Engine-neutral; the app turns it into an <c>Image</c>.
/// </summary>
/// <param name="Data">The whole chain, <see cref="BcLayout.ChainBytes"/> long.</param>
/// <param name="SourceWidth">The asset's own size (the codestream's), which is larger than
/// <paramref name="Width"/> when this copy was encoded at a reduced level.</param>
public sealed record CompressedTextureData(
    GpuTextureFormat Format, int Width, int Height, bool Mipmaps, byte[] Data,
    int SourceWidth, int SourceHeight, TextureAlphaInfo Alpha)
{
    /// <summary>
    /// The same texture at <paramref name="targetWidth"/> x <paramref name="targetHeight"/>: the tail of
    /// the mip chain from that level, or null when no level has that size (or there is no chain).
    /// </summary>
    public CompressedTextureData? Slice(int targetWidth, int targetHeight)
    {
        if (targetWidth == Width && targetHeight == Height) return this;
        if (!Mipmaps) return null;
        int level = BcLayout.FindLevel(Width, Height, Mipmaps, targetWidth, targetHeight);
        if (level < 0) return null;
        long offset = BcLayout.LevelOffset(Format, Width, Height, level);
        long length = BcLayout.ChainBytes(Format, targetWidth, targetHeight, mipmaps: true);
        if (offset + length > Data.Length) return null;
        var data = new byte[length];
        Array.Copy(Data, offset, data, 0, length);
        return this with { Width = targetWidth, Height = targetHeight, Data = data };
    }
}
