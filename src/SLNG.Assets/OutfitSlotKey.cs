using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SLNG.Assets;

/// <summary>FEAT-PERF-23: one slot of an avatar's outfit skin -- the skeleton bone it follows and the
/// asset's own inverse-bind matrix it was bound with. The renderer computes the bind from exactly
/// these two (plus the avatar's rest and own scale, which every slot of the avatar shares), so two
/// items with equal keys get a bit-identical palette entry and can share the slot.</summary>
/// <remarks>
/// <para><b>Bit for bit.</b> The matrix is compared on its raw float bits, not with <c>==</c>: equal
/// keys must mean an equal bind, and a key that is told apart only costs a duplicate slot.</para>
/// <para><b>The rest marker.</b> An identity inverse-bind matrix is not bound to itself: the renderer
/// binds it to the live inverse rest of the bone (<c>AvatarRenderer.BindRiggedSkin</c>), which moves
/// with the shape and the joint overrides. So it gets its own marker, equal for every identity matrix
/// on the same bone.</para>
/// </remarks>
public readonly struct OutfitSlotKey : IEquatable<OutfitSlotKey>
{
    private OutfitSlotKey(int bone, Matrix4x4 inverseBind, bool isRest)
    {
        Bone = bone;
        InverseBind = inverseBind;
        IsRest = isRest;
    }

    /// <summary>The skeleton bone index the slot follows.</summary>
    public int Bone { get; }

    /// <summary>The asset's inverse-bind matrix (SL axes, row vectors); identity for the rest marker.</summary>
    public Matrix4x4 InverseBind { get; }

    /// <summary>True when the slot binds to the bone's live inverse rest (an identity matrix in the asset).</summary>
    public bool IsRest { get; }

    /// <summary>The key for one joint of an asset: the rest marker when its matrix is the identity.</summary>
    public static OutfitSlotKey For(int bone, Matrix4x4 inverseBind) =>
        inverseBind == Matrix4x4.Identity ? Rest(bone) : new OutfitSlotKey(bone, inverseBind, false);

    public static OutfitSlotKey Rest(int bone) => new(bone, Matrix4x4.Identity, true);

    public bool Equals(OutfitSlotKey other)
    {
        if (Bone != other.Bone || IsRest != other.IsRest) return false;
        if (IsRest) return true;
        var a = InverseBind;
        var b = other.InverseBind;
        return MemoryMarshal.Cast<Matrix4x4, int>(MemoryMarshal.CreateReadOnlySpan(ref a, 1))
            .SequenceEqual(MemoryMarshal.Cast<Matrix4x4, int>(MemoryMarshal.CreateReadOnlySpan(ref b, 1)));
    }

    public override bool Equals(object? obj) => obj is OutfitSlotKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Bone);
        hash.Add(IsRest);
        if (!IsRest)
        {
            var m = InverseBind;
            foreach (int bits in MemoryMarshal.Cast<Matrix4x4, int>(MemoryMarshal.CreateReadOnlySpan(ref m, 1))) hash.Add(bits);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(OutfitSlotKey left, OutfitSlotKey right) => left.Equals(right);
    public static bool operator !=(OutfitSlotKey left, OutfitSlotKey right) => !left.Equals(right);

    public override string ToString() => IsRest ? $"(bone {Bone}, rest)" : $"(bone {Bone}, ibm)";
}
