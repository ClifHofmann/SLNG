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
    /// FEAT-PERF-25: the shrink pass ranks what it gives back. In the 3.5 GB run it gave back nothing at
    /// all; the ranking is what decides it now, so it is held to the rules here: unseen and far textures
    /// (more texels than the screen shows) first, the longest unseen before the merely far; never what is
    /// protected, smaller than its screen area, without LOD information, unreferenced or blocked after a
    /// failed shrink; right-sized textures only when the cache is far over and nothing is free. Then the
    /// two halves that pay for it: a NEAR texture is admitted whole into a full cache that has bytes to
    /// give back (a far one still takes the cut), and a shrink leaves the bookkeeping that stops it from
    /// coming straight back.
    /// </summary>
    private static Check CheckShrinkCandidateRanking()
    {
        const string Name = "shrink pass ranks far/unseen first, spares near and protected, admits near whole";
        var problems = new List<string>();
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
        float full = S * S;
        long size = TextureAdmission.TextureBytes(S, S, true);
        long saving = size - size / 4;

        var cache = new GpuCache(1L << 30);
        var small = new GpuCache(3 * size);
        var onlyNear = new GpuCache(1L << 30);
        try
        {
            Guid Up(GpuCache c, float area, int refs = 1, bool protect = false)
            {
                var id = Guid.NewGuid();
                if (protect) c.SelfTestMarkNoShrink(id);
                var up = c.SelfTestUpload(id, data, area, generateMipmaps: true, initialRefCount: refs);
                if (up.Texture?.GetWidth() != S) problems.Add($"setup: a texture came out {up.Texture?.GetWidth()} px wide");
                return id;
            }

            var near = Up(cache, full);
            var far = Up(cache, full);
            var unseen = Up(cache, full);
            var guarded = Up(cache, full, protect: true);
            var undersized = Up(cache, full);
            var blocked = Up(cache, full);
            var noLod = Up(cache, 0f);
            var unreferenced = Up(cache, full, refs: 0);
            cache.SelfTestSetSeen(near, 0, full);            // right-sized and large on screen
            cache.SelfTestSetSeen(far, 0, 4096f);            // 64 texels per pixel
            cache.SelfTestSetSeen(unseen, 30_000, 0f);       // not asked for in 30 s
            cache.SelfTestSetSeen(guarded, 30_000, 0f);
            cache.SelfTestSetSeen(undersized, 0, 4 * full);  // the screen wants more than it has
            cache.SelfTestSetSeen(blocked, 30_000, 0f);
            cache.SelfTestBlockShrink(blocked);
            cache.SelfTestSetSeen(unreferenced, 30_000, 0f);

            var picked = cache.SelfTestShrinkCandidates(allowVisible: false);
            if (picked.Count != 2 || picked[0] != unseen || picked[1] != far)
                problems.Add($"candidates {Describe(picked, near, far, unseen, guarded, undersized, blocked, noLod, unreferenced)}, expected unseen, far");
            if (cache.SelfTestReclaimableBytes != 2 * saving)
                problems.Add($"reclaimable {cache.SelfTestReclaimableBytes} bytes, expected {2 * saving}");
            var withVisible = cache.SelfTestShrinkCandidates(allowVisible: true);
            if (withVisible.Contains(near))
                problems.Add("a right-sized near texture was offered while free ones exist");

            // Nothing free: the right-sized one is offered only when the cache is far over.
            var onlyNearId = Up(onlyNear, full);
            onlyNear.SelfTestSetSeen(onlyNearId, 0, full);
            if (onlyNear.SelfTestShrinkCandidates(allowVisible: false).Count != 0)
                problems.Add("a right-sized texture was offered without the far-over condition");
            var last = onlyNear.SelfTestShrinkCandidates(allowVisible: true);
            if (last.Count != 1 || last[0] != onlyNearId)
                problems.Add($"far over with nothing free, {last.Count} candidates instead of the right-sized one");

            // Admission: a full cache (3 textures, budget 3) with two unseen ones to give back.
            var a = Up(small, full);
            var b = Up(small, full);
            Up(small, full);
            small.SelfTestSetSeen(a, 30_000, 0f);
            small.SelfTestSetSeen(b, 30_000, 0f);
            small.SelfTestShrinkCandidates(allowVisible: false);
            var nearArrival = small.SelfTestUpload(Guid.NewGuid(), data, full, true, 1);
            if (nearArrival.Extra != 0 || nearArrival.Texture?.GetWidth() != S)
                problems.Add($"a near texture arriving in a full cache with bytes to give back took {nearArrival.Extra} extra levels");
            var farArrival = small.SelfTestUpload(Guid.NewGuid(), data, 16_000f, true, 1);
            if (farArrival.Extra == 0)
                problems.Add("a far texture arriving in a full cache took no extra level");
            if (small.SelfTestReservedBytes != 0) problems.Add($"{small.SelfTestReservedBytes} bytes still reserved");

            // A shrink: half the size, built for a quarter of its texels, requested for all of them.
            var (nw, nh) = cache.SelfTestShrinkNow(far, data);
            if (nw != S / 2 || nh != S / 2) problems.Add($"far texture shrank to {nw}x{nh}, expected {S / 2}x{S / 2}");
            var farState = cache.SelfTestSharpenState(far);
            if (Math.Abs(farState.BuiltFor - (S / 2) * (S / 2) / 4f) > 1f || Math.Abs(farState.RequestedFor - (S / 2) * (S / 2)) > 1f)
                problems.Add($"after the shrink built for {farState.BuiltFor} / requested for {farState.RequestedFor}");
            if (farState.Guarded) problems.Add("a free-tier shrink raised the sharpen guard");
            var visibleShrink = cache.SelfTestShrinkNow(near, data);
            if (visibleShrink.Width != S / 2 || !cache.SelfTestSharpenState(near).Guarded)
                problems.Add("a visible-tier shrink did not raise the sharpen guard");

            return problems.Count == 0
                ? new Check(Name, true, $"ranked unseen > far, reclaimable {(2 * saving) >> 10} KB; near admitted whole, far cut {farArrival.Extra}; shrink {S}->{S / 2}")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cache.DisposeAll();
            small.DisposeAll();
            onlyNear.DisposeAll();
        }
    }

    private static string Describe(List<Guid> ids, params Guid[] named)
    {
        string[] names = { "near", "far", "unseen", "protected", "undersized", "blocked", "noLod", "unreferenced" };
        var parts = new List<string>();
        foreach (var id in ids)
        {
            int i = Array.IndexOf(named, id);
            parts.Add(i >= 0 && i < names.Length ? names[i] : id.ToString()[..8]);
        }
        return "[" + string.Join(", ", parts) + "]";
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

            // The old way, as the read-back shrink did it: upload as GpuCache uploads, read back, halve.
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
    /// skins detached from Skeleton3D, are drawn in the jelly-doll colour, keep their pose, and are
    /// restored (with their bake materials) when set full.
    /// </summary>
    private static Check CheckAvatarReduction()
    {
        const string Name = "reduced avatars park attachments, show the jelly doll, keep their pose";
        var problems = new List<string>();
        var renderer = new AvatarRenderer();
        var (passed, detail) = renderer.SelfTestAvatarReductionLifecycle();
        if (!passed) problems.Add(detail);

        return problems.Count == 0
            ? new Check(Name, true, detail)
            : new Check(Name, false, string.Join("; ", problems));
    }

    /// <summary>FEAT-PERF-08: the avatar cap neither flaps nor holds back a newcomer.</summary>
    private static Check CheckAvatarLimitStability()
    {
        const string Name = "avatar cap holds still (absolute margin, dwell time, first promotion)";
        var renderer = new AvatarRenderer();
        try
        {
            var (passed, detail) = renderer.SelfTestAvatarLimitStability();
            return new Check(Name, passed, detail);
        }
        finally
        {
            renderer.Free();
        }
    }

    /// <summary>FEAT-PERF-08: a prim or sculpt attachment is rebuilt only when its geometry or materials change.</summary>
    private static Check CheckPrimAttachmentSignature()
    {
        const string Name = "prim attachment dedupe compares geometry and material inputs only";
        var renderer = new AvatarRenderer();
        try
        {
            var (passed, detail) = renderer.SelfTestPrimAttachmentSignature();
            return new Check(Name, passed, detail);
        }
        finally
        {
            renderer.Free();
        }
    }

    /// <summary>BUG-AVATAR-11: worn items that arrive before their wearer's visual are not dropped.</summary>
    private static Check CheckPendingWornItems()
    {
        const string Name = "worn items that arrive before their avatar are remembered and built later";
        var renderer = new AvatarRenderer();
        try
        {
            var (passed, detail) = renderer.SelfTestPendingWornItems();
            return new Check(Name, passed, detail);
        }
        finally
        {
            renderer.Free();
        }
    }
}
