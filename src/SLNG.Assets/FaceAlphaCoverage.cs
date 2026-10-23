namespace SLNG.Assets;

/// <summary>What a face's own UVs find in a texture's alpha channel.</summary>
/// <param name="Samples">Points sampled: every vertex and every triangle centre of the face.</param>
/// <param name="Below">How many of them have an alpha below the threshold, i.e. are cut away.</param>
/// <param name="MinAlpha">Lowest alpha sampled, 0..255.</param>
/// <param name="MeanAlpha">Mean alpha sampled, 0..255.</param>
public readonly record struct FaceAlphaCoverage(int Samples, int Below, int MinAlpha, float MeanAlpha)
{
    /// <summary>The share of the face that is cut away, 0..1.</summary>
    public float CutFraction => Samples == 0 ? 0f : (float)Below / Samples;
}
