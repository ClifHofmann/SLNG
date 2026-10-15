namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-17 Part B: what the renderer decides a face's transparency on, measured on the
/// UNCOMPRESSED pixels of a texture's top level before it was block-compressed, and stored with the
/// compressed copy so a warm start does not need the RGBA pixels to answer it.
/// </summary>
/// <param name="Mode">Godot's <c>Image.AlphaMode</c> as a number: 0 none, 1 bit, 2 blend.</param>
/// <param name="Maskable">BUG-RENDER-11: the viewer's <c>analyzeAlphaData</c> verdict (a 1-bit cutout).</param>
/// <param name="MinAlpha">BUG-PERF-07: the smallest alpha.</param>
/// <param name="FracMid">BUG-PERF-07: the fraction of texels with 16 &lt; a &lt; 239.</param>
/// <param name="FracClear">BUG-PERF-07: the fraction of texels with a &lt;= 16.</param>
public readonly record struct TextureAlphaInfo(byte Mode, bool Maskable, int MinAlpha, float FracMid, float FracClear);
