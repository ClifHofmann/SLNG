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
        using var bitmap = ToBitmap(bgra, width, height);
        if (bitmap == null) return System.Array.Empty<byte>();

        // WithFileFormat(false) is not a detail. CoreJ2K wraps its output in JP2 file-format boxes
        // by default -- the stream starts 00 00 00 0C 6A 50 20 20 -- and Second Life textures are
        // raw JPEG2000 codestreams starting FF 4F FF 51. Verified against a texture off the grid.
        // A JP2-wrapped upload is accepted, stored and served, and then renders grey in a real
        // viewer: measured 2026-09-01, three uploaded test textures showed as flat grey in
        // Firestorm while decoding perfectly here, because CoreJ2K reads back its own container.
        return CompleteConfigurationPresets.Streaming.ForLossless().WithFileFormat(false).Encode(bitmap);
    }

    /// <inheritdoc/>
    public byte[] EncodePreviewPng(byte[] bgra, int width, int height)
    {
        using var bitmap = ToBitmap(bgra, width, height);
        if (bitmap == null) return System.Array.Empty<byte>();

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        return data?.ToArray() ?? System.Array.Empty<byte>();
    }

    private static SKBitmap? ToBitmap(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        nint pixels = bitmap.GetPixels();
        if (pixels == nint.Zero) { bitmap.Dispose(); return null; }

        Marshal.Copy(bgra, 0, pixels, width * height * 4);
        return bitmap;
    }
}
