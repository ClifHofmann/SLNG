namespace SLNG.Assets;

/// <summary>
/// BUG-PERF-09: the arithmetic of a VRAM budget that holds under a burst of arrivals.
///
/// <para>FEAT-PERF-04's back-pressure reacts to what is already resident (a global LOD bias that
/// rises one step per cooldown, and a shrink pass over what was uploaded too large). That cannot
/// keep up with a decoded-texture cache that delivers thousands of textures within seconds: all of
/// them are uploaded at bias 0 before the first reaction. Admission control moves the decision to
/// the moment each texture is prepared. The caller knows what is resident plus what is already
/// admitted but not uploaded yet (the PROJECTED size) and asks, for the texture in hand, how many
/// extra discard levels it needs so that projected + itself stays inside the budget.</para>
///
/// <para>Pure functions, no engine types: the renderer applies the answer with the same local
/// downsample as every other discard level (<see cref="TextureLod.DimensionAfterDiscard"/>).</para>
/// </summary>
public static class TextureAdmission
{
    /// <summary>Video memory of an RGBA8 texture, with its mip chain when it has one. A full chain
    /// adds a third on top of the base level; counting the base alone left the budget a quarter
    /// below what the card actually holds.</summary>
    public static long TextureBytes(int width, int height, bool mipmaps)
    {
        if (width <= 0 || height <= 0) return 0;
        long baseBytes = (long)width * height * 4;
        return mipmaps ? baseBytes + baseBytes / 3 : baseBytes;
    }

    /// <summary>
    /// The extra discard levels (0..<paramref name="maxExtra"/>) a texture of
    /// <paramref name="width"/> x <paramref name="height"/> (already at the level the screen asks for)
    /// is given so that <paramref name="projectedBytes"/> plus its own size fits inside
    /// <paramref name="budgetBytes"/>: the smallest level that fits, or <paramref name="maxExtra"/>
    /// when none does -- an over-full cache takes the cheapest upload it can get and leaves the rest
    /// to the shrink pass.
    ///
    /// <para>Monotonic: a larger projected size never yields fewer levels, and a larger texture never
    /// yields fewer than a smaller one at the same projected size. Each level is a 4x cut in bytes
    /// (down to <see cref="TextureLod.MinReducedDimension"/>), which is why a small cap is enough.</para>
    /// </summary>
    /// <param name="projectedBytes">Resident bytes plus admitted-but-not-yet-resident bytes, without
    /// this texture. For a re-upload of a texture that is already resident, pass the total without
    /// that texture's current bytes.</param>
    public static int ExtraDiscardFor(
        long projectedBytes, long budgetBytes, int width, int height, bool mipmaps, int maxExtra)
    {
        if (maxExtra <= 0 || width <= 0 || height <= 0) return 0;
        if (budgetBytes <= 0) return maxExtra;

        for (int extra = 0; extra < maxExtra; extra++)
        {
            long bytes = TextureBytes(
                TextureLod.DimensionAfterDiscard(width, extra),
                TextureLod.DimensionAfterDiscard(height, extra),
                mipmaps);
            if (projectedBytes + bytes <= budgetBytes) return extra;
        }
        return maxExtra;
    }

    /// <summary>The screen area a texture uploaded <paramref name="extraDiscard"/> levels smaller than
    /// its area asked for actually serves. Recorded as the area it was "built for", so the existing
    /// re-sharpen rule (a request at 4x the built-for area) fires for it as soon as the cache has
    /// room again, instead of treating it as correctly sized for the area it was too small for.</summary>
    public static float BuiltForArea(float screenPixelArea, int extraDiscard)
        => extraDiscard <= 0 ? screenPixelArea : screenPixelArea / (float)System.Math.Pow(4.0, extraDiscard);
}
