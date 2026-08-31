namespace SLNG.Net;

/// <summary>
/// FEAT-AVATAR-01: encodes a composited avatar bake to JPEG2000, because LibreMetaverse's own
/// encode path produces a blank texture.
///
/// <para><b>Why this exists.</b> <c>Baker.Bake()</c> finishes with
/// <c>AssetTexture.Encode()</c>, which is hardcoded to
/// <c>CompleteConfigurationPresets.Streaming.Encode(Image.ExportBitmap())</c>. Measured offline
/// against the pinned CoreJ2K 2.3.3.91 (<c>SLNG.Assets.Tests.BakeEncodeTests</c>): a 512x512
/// gradient encodes to <b>292 bytes</b> through that preset — and 244 bytes at <i>any</i> explicit
/// bitrate, because <c>WithBitrate</c> and <c>WithQuality</c> have no effect at all in that version.
/// Every lossy preset is broken the same way; only the lossless path survives, at 123 KB with a
/// round-trip error of exactly zero. That is the entire reason a correctly composited 1024x1024 bake
/// came back as 507 bytes of nothing.</para>
///
/// <para><b>Why an interface.</b> The project layering runs <c>SLNG.Assets -&gt; SLNG.Net</c>, and
/// AGENTS.md puts the JPEG2000 codec in <c>SLNG.Assets</c> — so <c>SLNG.Net</c> cannot simply call
/// it. It declares this contract instead and <c>SLNG.Assets</c> implements it, which is the
/// dependency inversion the layering rules call for. The signature is deliberately plain bytes: no
/// CoreJ2K, SkiaSharp or LibreMetaverse type crosses the boundary.</para>
/// </summary>
public interface IBakeTextureEncoder
{
    /// <summary>Encodes raw 8-bit BGRA pixels, top-left origin, tightly packed at
    /// <paramref name="width"/> * 4 bytes per row — the layout
    /// <c>ManagedImage.ExportBitmap</c> builds. Must be lossless: an avatar bake is uploaded once
    /// and then worn by everyone who sees the avatar.</summary>
    /// <returns>A JPEG2000 codestream ready to upload, or an empty array if encoding failed.</returns>
    byte[] EncodeBake(byte[] bgra, int width, int height);
}
