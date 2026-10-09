using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// BUG-PERF-09: a burst of textures against a small budget. Every texture asks for full size
    /// (the screen area equals its texel count), the budget holds five of them, and forty arrive. The
    /// upload path itself (the worker half, then the main-thread commit) is what runs, so this holds
    /// the real bookkeeping to the arithmetic: while the extra-discard cap still has room the resident
    /// size never crosses the budget, sizes only ever step down, a cut texture is recorded as built
    /// for the area it actually serves, an avatar-style texture (no screen area, or flagged) is
    /// counted but never reduced, a re-upload that admission would not make larger uploads nothing,
    /// and the in-flight total is back to zero once every upload is committed.
    /// </summary>
    private static Check CheckGpuCacheAdmission()
    {
        const string Name = "texture admission holds the budget under a burst";
        var problems = new List<string>();
        const long Budget = 7_864_320; // 7.5 MB: five 512x512 textures with mips, then room for a few smaller ones
        var cache = new GpuCache(Budget);
        try
        {
            const int S = 512;
            var rgba = new byte[S * S * 4];
            for (int i = 0; i < S * S; i++)
            {
                rgba[i * 4] = (byte)(i * 7);
                rgba[i * 4 + 1] = (byte)(i * 3);
                rgba[i * 4 + 2] = (byte)(i * 5);
                rgba[i * 4 + 3] = 255;
            }
            var data = new TextureData(S, S, rgba, false, S, S);
            float area = S * S;

            var widths = new List<int>();
            for (int i = 0; i < 40; i++)
            {
                var (tex, builtFor, extra) = cache.SelfTestUpload(Guid.NewGuid(), data, area, generateMipmaps: true, initialRefCount: 1);
                if (tex == null) { problems.Add($"texture {i} was not uploaded"); break; }
                widths.Add(tex.GetWidth());

                if (extra < 2 && cache.CurrentSizeBytes > Budget)
                    problems.Add($"texture {i} (extra {extra}) left {cache.CurrentSizeBytes} bytes resident, budget {Budget}");

                float expectedBuiltFor = extra > 0 ? area / MathF.Pow(4f, extra) : 0f;
                if (MathF.Abs(builtFor - expectedBuiltFor) > 1f)
                    problems.Add($"texture {i} (extra {extra}) built for {builtFor}, expected {expectedBuiltFor}");
                if (tex.GetWidth() != S >> extra)
                    problems.Add($"texture {i}: {tex.GetWidth()} px wide with extra {extra}");
            }

            for (int i = 1; i < widths.Count; i++)
                if (widths[i] > widths[i - 1]) problems.Add($"size stepped UP at texture {i}: {widths[i - 1]} -> {widths[i]}");
            if (widths.Count > 0 && widths[0] != S) problems.Add($"the first texture was cut to {widths[0]}");
            if (!widths.Contains(S / 2)) problems.Add("no texture took one extra level");
            if (!widths.Contains(S / 4)) problems.Add("no texture took the capped two levels");
            if (widths.Exists(w => w < S / 4)) problems.Add("a texture went past the cap");
            if (cache.SelfTestReservedBytes != 0) problems.Add($"{cache.SelfTestReservedBytes} bytes still reserved after every commit");

            // No screen area: counted, never reduced -- even with the cache over budget.
            var exempt = cache.SelfTestUpload(Guid.NewGuid(), data, 0f, true, 1);
            if (exempt.Texture?.GetWidth() != S) problems.Add($"a texture with no screen area was uploaded at {exempt.Texture?.GetWidth()}");
            // Flagged never-shrink (an avatar face that a world caller also asked for).
            var flagged = Guid.NewGuid();
            cache.SelfTestMarkNoShrink(flagged);
            var pinned = cache.SelfTestUpload(flagged, data, area, true, 1);
            if (pinned.Texture?.GetWidth() != S) problems.Add($"a never-shrink texture was uploaded at {pinned.Texture?.GetWidth()}");

            // A sharpen into a full cache: admission cuts it to the cap, which is not larger than
            // the 256 px texture it would replace, so nothing is uploaded and nothing stays reserved.
            long resident256 = TextureAdmission.TextureBytes(S / 2, S / 2, true);
            var noRoom = cache.SelfTestPrepareReUpload(Guid.NewGuid(), data, area, resident256);
            if (noRoom.HasImage) problems.Add($"a re-upload with no room produced a {noRoom.Width} px image");
            if (noRoom.NothingSharper) problems.Add("a re-upload with no room was reported as 'nothing sharper exists'");
            if (cache.SelfTestReservedBytes != 0) problems.Add($"{cache.SelfTestReservedBytes} bytes still reserved after the re-upload");

            // A sharpen whose area is below the level floor asks for nothing larger than is there,
            // however much room there is. That must be told apart from "no room": the first stands
            // (the area has to grow 4x), the second is retried later. Mixing them up re-decoded the same
            // 1,700 textures 42,000 times in the first in-world run.
            var roomy = new GpuCache(1L << 30);
            try
            {
                long resident16 = TextureAdmission.TextureBytes(16, 16, true); // 512 px at the 5-level floor
                var atFloor = roomy.SelfTestPrepareReUpload(Guid.NewGuid(), data, 11f, resident16);
                if (atFloor.HasImage) problems.Add($"a floor-level re-upload produced a {atFloor.Width} px image");
                if (!atFloor.NothingSharper) problems.Add("a re-upload at the level floor was not reported as 'nothing sharper exists'");
                var sharper = roomy.SelfTestPrepareReUpload(Guid.NewGuid(), data, area, resident16);
                if (!sharper.HasImage || sharper.Width != S) problems.Add($"a re-upload into a roomy cache gave {sharper.Width} px (has image: {sharper.HasImage})");
            }
            finally { roomy.DisposeAll(); }

            return problems.Count == 0
                ? new Check(Name, true, $"{widths.Count} textures: {string.Join(",", widths.GetRange(0, Math.Min(widths.Count, 12)))}... resident {cache.CurrentSizeBytes >> 10} KB of a {Budget >> 10} KB budget")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cache.DisposeAll();
        }
    }

    /// <summary>
    /// BUG-PERF-09: a shrink now rebuilds the smaller image on a worker from the decoded pixels
    /// instead of reading the texture back out of VRAM. For a texture uploaded at full size the two
    /// have to come out the same: same pixels in every mip level, same alpha numbers.
    /// </summary>
    private static Check CheckShrinkWithoutReadBack()
    {
        const string Name = "shrink from decoded pixels equals the read-back shrink";
        try
        {
            const int W = 64, H = 64, NW = 32, NH = 32;
            var rgba = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++)
            {
                rgba[i * 4] = (byte)(i * 7);
                rgba[i * 4 + 1] = (byte)(i * 3);
                rgba[i * 4 + 2] = (byte)(i * 5);
                rgba[i * 4 + 3] = (i % 9) switch { 0 => 0, 1 => 10, 2 => 17, 3 => 128, 4 => 238, 5 => 239, 6 => 254, _ => 255 };
            }
            var decoded = new TextureData(W, H, rgba, false, W, H);

            // The old way, as ShrinkOne does it: upload as GpuCache uploads, read back, halve.
            using var source = Image.CreateFromData(W, H, false, Image.Format.Rgba8, rgba);
            source.FixAlphaEdges();
            source.GenerateMipmaps();
            using var texture = ImageTexture.CreateFromImage(source);
            using var back = texture.GetImage();
            if (back == null) return new Check(Name, false, "the texture could not be read back");
            back.ClearMipmaps();
            back.Resize(NW, NH, Image.Interpolation.Lanczos);
            var oldStats = GpuCache.MeasureAlphaStats(back.GetData(), NW, NH);
            back.GenerateMipmaps();
            var oldData = back.GetData();

            var (image, stats) = GpuCache.SelfTestPrepareShrink(decoded, NW, NH, mips: true);
            if (image == null) return new Check(Name, false, "the worker path produced no image");
            using (image)
            {
                if (image.GetWidth() != NW || image.GetHeight() != NH)
                    return new Check(Name, false, $"{image.GetWidth()}x{image.GetHeight()}, expected {NW}x{NH}");
                if (!image.HasMipmaps()) return new Check(Name, false, "the mip chain is missing");
                if (!image.GetData().AsSpan().SequenceEqual(oldData))
                    return new Check(Name, false, "pixels differ from the read-back shrink");
                if (stats != oldStats) return new Check(Name, false, $"alpha numbers {stats} vs {oldStats}");
            }
            return new Check(Name, true, $"{W}x{H} -> {NW}x{NH}, {oldData.Length} bytes identical, alpha numbers equal");
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// BUG-PERF-13: Verifies that remote avatar bake textures are not added to _noShrink,
    /// and that when an avatar visual is removed, its texture ids are no longer pinned or in _noShrink
    /// and are evicted from GpuCache.
    /// </summary>
    private static Check CheckAvatarTextureRemoval()
    {
        const string Name = "remote avatar texture removal unpins and unregisters from GpuCache";
        var problems = new List<string>();
        var cache = new GpuCache(10_000_000);
        try
        {
            var renderer = new AvatarRenderer();
            var (passed, detail) = renderer.SelfTestAvatarTextureRemoval(cache);
            if (!passed) problems.Add(detail);

            // Also test own avatar vs remote avatar bake texture upload logic
            var remoteBake = Guid.NewGuid();
            var selfBake = Guid.NewGuid();
            cache.SelfTestMarkAvatarTexture(remoteBake, isBake: true, isSelf: false);
            cache.SelfTestMarkAvatarTexture(selfBake, isBake: true, isSelf: true);

            if (cache.SelfTestIsNoShrink(remoteBake))
                problems.Add("remote bake texture was added to _noShrink");
            if (!cache.SelfTestIsNoShrink(selfBake))
                problems.Add("own avatar bake texture was NOT added to _noShrink");

            // UpdateProtectedAvatarTextures should never add remote bakes to _noShrink
            var protectedSet = new HashSet<Guid> { remoteBake, selfBake };
            cache.UpdateProtectedAvatarTextures(protectedSet);
            if (cache.SelfTestIsNoShrink(remoteBake))
                problems.Add("UpdateProtectedAvatarTextures added remote bake to _noShrink");
            if (!cache.SelfTestIsNoShrink(selfBake))
                problems.Add("UpdateProtectedAvatarTextures removed own bake from _noShrink");
        }
        finally
        {
            cache.DisposeAll();
        }

        return problems.Count == 0
            ? new Check(Name, true, "remote avatar textures unpinned, unregistered, and evicted")
            : new Check(Name, false, string.Join("; ", problems));
    }

    /// <summary>
    /// FEAT-PERF-08: Verifies that reduced avatars have rigged and rigid attachments freed,
    /// skins detached from Skeleton3D, animations stopped, and are restored when set full.
    /// </summary>
    private static Check CheckAvatarReduction()
    {
        const string Name = "reduced avatars free attachments, detach skins, and stop animation";
        var problems = new List<string>();
        var renderer = new AvatarRenderer();
        var (passed, detail) = renderer.SelfTestAvatarReductionLifecycle();
        if (!passed) problems.Add(detail);

        return problems.Count == 0
            ? new Check(Name, true, "avatar reduction frees attachments and detaches skins")
            : new Check(Name, false, string.Join("; ", problems));
    }
}
