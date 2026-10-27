using System;
using System.Threading.Tasks;
using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Decodes one texture at its full size for a window that shows a picture (the profile, the
/// image viewer) -- and deliberately not through <see cref="GpuCache"/>. That cache is the
/// world's texture budget: it hands back whatever resolution the scene last needed (a picture
/// first seen as a thumbnail stays a thumbnail, and the shrink pass can cut it again) and may
/// hold a block-compressed copy. A window that exists to show the picture owns its texture
/// instead, so it never shows less than the asset has.
/// </summary>
public static class FullResImage
{
    /// <summary>Fetches and decodes <paramref name="textureId"/> in full, builds the mip chain, and
    /// returns the <see cref="Image"/> (null when it could not be loaded). Runs on worker threads;
    /// the caller does the GPU upload (<c>ImageTexture.CreateFromImage</c>) on the main thread.</summary>
    public static async Task<Image?> LoadAsync(SLNG.Assets.AssetService assetService, Guid textureId)
    {
        // High priority: someone is looking at this one, ahead of the scene's background fetches.
        var data = await assetService.GetTextureAsync(textureId, priority: 1000f).ConfigureAwait(false);

        // The world may have a reduced decode of the same id in flight (the fetch is
        // single-flighted per id, first caller's level wins). The .j2c is on disk by now, so
        // ask for the full-size decode of it.
        if (data != null && data.SourceWidth > data.Width && data.SourceHeight > 0)
        {
            var full = await assetService.GetShrinkSourceAsync(
                textureId, data.SourceWidth, data.SourceHeight, data.SourceWidth, data.SourceHeight, priority: 1000f)
                .ConfigureAwait(false);
            if (full != null) data = full;
        }

        if (data == null || data.Width <= 0 || data.Height <= 0 || GpuCache.ShuttingDown) return null;

        var image = Image.CreateFromData(data.Width, data.Height, false, Image.Format.Rgba8, data.Rgba);
        if (image == null || image.IsEmpty()) return null;
        image.GenerateMipmaps();
        return image;
    }
}
