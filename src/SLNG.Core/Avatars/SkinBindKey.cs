using System;
using System.Runtime.InteropServices;

namespace SLNG.Core.Avatars;

/// <summary>FEAT-PERF-24: the content of a rigged mesh's skin -- per slot, the skeleton bone it
/// follows and its bind matrix -- so two worn meshes with the same content can share one engine
/// skin.
///
/// <para>Godot's skeleton writes every bind of every skin bound to it to the RenderingServer on
/// each re-pose, one command per bind, and it shares that work only between meshes that hold the
/// SAME skin object (<c>Skeleton3D::register_skin</c> compares the resource, not its content). A
/// mesh body or head arrives as dozens of meshes from one upload, all with identical joint lists
/// and inverse-bind matrices, and the renderer built a skin for each: ~158,000 bind writes per
/// frame for 17 avatars at Sirens Beach, ~100 ns each on a separate render thread.</para>
///
/// <para><b>Exact, not a hash.</b> Equal keys mean the engine computes a bit-identical skinning
/// palette, so sharing changes no pixel. A collision would pose one mesh with another's binds, so
/// equality compares every bone index and every float's bits. The bind count and slot order are
/// part of the identity: a mesh's vertex weights index its skin by slot.</para></summary>
public sealed class SkinBindKey : IEquatable<SkinBindKey>
{
    /// <summary>A bind matrix as the engine stores it: a 3x4 affine transform (basis + origin).</summary>
    public const int FloatsPerBind = 12;

    private readonly int[] _bones;
    private readonly float[] _values;
    private readonly int _hash;

    /// <param name="bones">The skeleton bone of each slot, in slot order.</param>
    /// <param name="values"><see cref="FloatsPerBind"/> floats per slot, in slot order.</param>
    public SkinBindKey(ReadOnlySpan<int> bones, ReadOnlySpan<float> values)
    {
        if (values.Length != bones.Length * FloatsPerBind)
            throw new ArgumentException($"expected {bones.Length * FloatsPerBind} floats for {bones.Length} binds, got {values.Length}", nameof(values));
        _bones = bones.ToArray();
        _values = values.ToArray();

        var hash = new HashCode();
        hash.Add(_bones.Length);
        foreach (int bone in _bones) hash.Add(bone);
        foreach (int bits in MemoryMarshal.Cast<float, int>(_values)) hash.Add(bits);
        _hash = hash.ToHashCode();
    }

    public int BindCount => _bones.Length;

    public bool Equals(SkinBindKey? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (_hash == other._hash
                && _bones.AsSpan().SequenceEqual(other._bones)
                && MemoryMarshal.Cast<float, int>(_values).SequenceEqual(MemoryMarshal.Cast<float, int>(other._values))));

    public override bool Equals(object? obj) => Equals(obj as SkinBindKey);

    public override int GetHashCode() => _hash;
}
