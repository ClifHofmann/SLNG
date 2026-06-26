namespace SLNG.Assets;

/// <summary>
/// Engine- and protocol-neutral decoded texture: tightly packed RGBA8 pixels, row-major.
/// This is what crosses the <c>SLNG.Assets</c> boundary — never a LibreMetaverse type.
/// </summary>
public sealed record TextureData(int Width, int Height, byte[] Rgba);
