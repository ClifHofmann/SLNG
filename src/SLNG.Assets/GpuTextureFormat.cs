namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-17 Part B: how a texture is held in video memory. The values are stored in the
/// decoded-texture disk cache, so never renumber them.
/// </summary>
public enum GpuTextureFormat : byte
{
    /// <summary>Uncompressed, 4 bytes per texel.</summary>
    Rgba8 = 0,

    /// <summary>BC1 (DXT1), RGB only: 8 bytes per 4x4 block, half a byte per texel. Godot samples it
    /// as <c>BC1_RGB</c> with alpha forced to 1, so it is only ever used for a texture whose alpha is
    /// 255 everywhere.</summary>
    Bc1 = 1,

    /// <summary>BC3 (DXT5): a BC1 colour block plus an 8-level interpolated alpha block, 16 bytes per
    /// 4x4 block, one byte per texel.</summary>
    Bc3 = 3,
}
