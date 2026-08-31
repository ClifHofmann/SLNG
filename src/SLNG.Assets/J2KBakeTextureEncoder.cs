using System.Runtime.InteropServices;
using CoreJ2K.Configuration;
using SkiaSharp;
using SLNG.Net;

namespace SLNG.Assets;

/// <summary>
/// FEAT-AVATAR-01: the working JPEG2000 encoder for avatar bakes — see
/// <see cref="IBakeTextureEncoder"/> for why LibreMetaverse's own one cannot be used.
///
/// <para>Uses <c>ForLossless()</c>, not because avatar bakes need to be lossless but because in the
/// pinned CoreJ2K 2.3.3.91 it is the only encoder path that works: measured offline in
/// <c>BakeEncodeTests</c>, every lossy preset collapses a 512x512 gradient to a few hundred bytes,
/// and <c>WithBitrate</c> / <c>WithQuality</c> change nothing. The lossless path reproduces the
/// source with a channel error of zero.</para>
///
/// <para>Cost: a 1024x1024 head bake weighs a few hundred KB rather than the ~50 KB a viewer
/// uploads. That is worth paying for a bake that is actually visible, and it is an upload that
/// happens on a wearable change, not per frame. If a later CoreJ2K fixes rate control, swap the
/// preset here — nothing else needs to know.</para>
/// </summary>
public sealed class J2KBakeTextureEncoder : IBakeTextureEncoder
{
    /// <inheritdoc/>
    public byte[] EncodeBake(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return System.Array.Empty<byte>();

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);

        nint pixels = bitmap.GetPixels();
        if (pixels == nint.Zero) return System.Array.Empty<byte>();
        Marshal.Copy(bgra, 0, pixels, width * height * 4);

        return CompleteConfigurationPresets.Streaming.ForLossless().Encode(bitmap);
    }
}
