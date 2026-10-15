using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>FEAT-PERF-20: the identity of a prim face's MATERIAL, as opposed to the face itself.
/// Two faces with equal keys get a material with the same shader and the same value in every
/// uniform, so they could share one material object -- which is what lets Godot's Forward+ merge
/// their draws into one instanced call (it merges only consecutive draws of the same mesh surface
/// AND the same material, see the spec), and what a per-cell merged mesh groups faces by.
///
/// <para><b>Built from the inputs, not read back from the material.</b> Every uniform the
/// renderer writes on a prim material is a function of the face's <see cref="FaceTexture"/>, the
/// prim's scale and per-asset data reached through ids in that record (texture, legacy material,
/// PBR material -- all immutable per id). <see cref="FaceSurfaceMerge"/> already relies on exactly
/// this to merge faces within one object; this key carries the same rule across objects. Reading
/// the uniforms back costs one interop call each and a string per material
/// (<c>MaterialFingerprint</c> in the app); the key costs a struct.</para>
///
/// <para><b>Exact, not a hash.</b> A key that collided would hand one face another face's
/// material, so equality is full value equality over the normalised inputs. Use it as a
/// dictionary key directly.</para>
///
/// <para><b>Normalised where the renderer cannot tell two inputs apart</b>, so the key does not
/// split groups over values with no effect on the pixels:</para>
/// <list type="bullet">
/// <item>the prim scale is only read by the PLANAR projection; for any other mapping it is
/// dropped (FEAT-PERF-06 measured the cost of keeping it: one group per slightly different
/// prop size);</item>
/// <item>texgen is reduced to planar or not -- spherical and cylindrical are not implemented and
/// fall back to the mesh's own UVs (<c>uv_texgen</c> is 0 or 1);</item>
/// <item>shiny outside 1-3 reads as none, as <see cref="FaceTexture.ShinyGlossiness"/> does;</item>
/// <item>-0 and +0 compare equal (the float's own equality already does, and its hash agrees).</item>
/// </list>
///
/// <para><see cref="Variant"/> is what the renderer decided on top of the inputs and cannot be
/// derived from them here: the shader variant in use (opaque, mask, blend, their double-sided and
/// viewer-specular twins), plus any pass chained onto it. Equal inputs with different variants
/// are different materials.</para></summary>
public readonly record struct PrimMaterialKey
{
    /// <summary>The face record after normalisation (see the type's remarks).</summary>
    public FaceTexture Face { get; }

    /// <summary>The prim's scale when the face is planar-mapped, otherwise zero.</summary>
    public Vector3 PlanarScale { get; }

    /// <summary>The renderer's choice of shader and chained passes, as an opaque number. Only
    /// compared, never interpreted.</summary>
    public ulong Variant { get; }

    // The hash, computed once: a key is hashed many times (every set the tally keeps, and later a
    // material cache), and the generated one walks every field each time. Zero on a
    // default-constructed key, which then hashes the slow way, so equality and hash still agree.
    private readonly int _hash;

    private PrimMaterialKey(FaceTexture face, Vector3 planarScale, ulong variant)
    {
        Face = face;
        PlanarScale = planarScale;
        Variant = variant;
        _hash = ComputeHash(face, planarScale, variant);
    }

    /// <summary>Value equality over the normalised inputs and the variant.</summary>
    public bool Equals(PrimMaterialKey other) =>
        Variant == other.Variant && PlanarScale.Equals(other.PlanarScale) && Face.Equals(other.Face);

    /// <inheritdoc/>
    public override int GetHashCode() => _hash != 0 ? _hash : ComputeHash(Face, PlanarScale, Variant);

    private static int ComputeHash(in FaceTexture face, Vector3 planarScale, ulong variant)
    {
        int h = HashCode.Combine(face, planarScale, variant);
        return h != 0 ? h : 1;
    }

    /// <summary>The key of a face drawn with <paramref name="variant"/> on a prim of
    /// <paramref name="primScale"/>.</summary>
    public static PrimMaterialKey Create(in FaceTexture face, Vector3 primScale, ulong variant)
    {
        bool planar = face.IsPlanar;
        var normalised = face with
        {
            TexGen = planar ? FaceTexture.TexGenPlanar : FaceTexture.TexGenDefault,
            Shiny = face.Shiny is >= 1 and <= 3 ? face.Shiny : (byte)0,
        };
        return new PrimMaterialKey(normalised, planar ? primScale : Vector3.Zero, variant);
    }
}
