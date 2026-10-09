using System;
using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// BUG-AVATAR-10: the joint-position overrides and joint-scale locks that the rigged meshes worn on
/// ONE skeleton impose, kept PER MESH so that taking a mesh off takes exactly its share back.
/// </summary>
/// <remarks>
/// <para>Before this, the renderer wrote every worn mesh's overrides into one flat
/// joint-to-position map, last writer winning, and nothing ever removed an entry. A pet or a
/// mesh body that carried an unusual skeleton therefore left the avatar deformed after it was
/// detached, until a relog rebuilt the map from what was still worn.</para>
///
/// <para>The reference viewer does it per mesh: every joint holds an <c>LLVector3OverrideMap</c>
/// (mesh id to value, scratch/slviewer/indra/llcharacter/lljoint.h), the override a joint actually
/// uses is the entry whose mesh id compares GREATEST (<c>findActiveOverride</c>,
/// lljoint.cpp:46 -- <c>std::max_element</c> over the key, an arbitrary but deterministic
/// tie-break, not a sum or "most recent"), and <c>removeAttachmentOverridesForObject</c>
/// (llvoavatar.cpp:6996) erases the mesh from every joint on detach. A joint's scale is locked
/// while ANY mesh holds a scale override for it (<c>addAttachmentScaleOverride</c>,
/// lljoint.cpp:619).</para>
///
/// <para><see cref="Positions"/> and <see cref="ScaleLocks"/> are the EFFECTIVE view the shape code
/// consumes. They include the implicit left/right mirror: a mesh that overrides only
/// <c>mFootLeft</c> is applied to <c>mFootRight</c> too (Y negated), unless some mesh overrides
/// that side itself. The mirror is derived from the per-mesh entries on every change, so it
/// disappears with the mesh that caused it rather than becoming a permanent entry.</para>
///
/// <para>Not thread-safe; the owner (the renderer's main thread) is the only caller.</para>
/// </remarks>
public sealed class JointOverrideSet
{
    private sealed record Contribution(Dictionary<string, Vector3> Positions, HashSet<string> ScaleLocks);

    private readonly Dictionary<Guid, Contribution> _meshes = new();
    private readonly Dictionary<string, Guid> _positionOwner = new();
    private Dictionary<string, Vector3> _positions = new();
    private HashSet<string> _scaleLocks = new();

    /// <summary>The overridden LOCAL position of each joint (SL space, relative to its parent), after
    /// picking each joint's winning mesh and adding the left/right mirror.</summary>
    public IReadOnlyDictionary<string, Vector3> Positions => _positions;

    /// <summary>Joints whose scale is pinned to the skeleton default, by any worn mesh, plus the
    /// mirrored sibling of a pinned joint whose position override is mirrored.</summary>
    public IReadOnlySet<string> ScaleLocks => _scaleLocks;

    /// <summary>Bumped whenever <see cref="Positions"/> or <see cref="ScaleLocks"/> actually
    /// changed, so a caller can tell whether the skeleton needs rebuilding without diffing.</summary>
    public int Version { get; private set; }

    /// <summary>The number of meshes currently contributing.</summary>
    public int MeshCount => _meshes.Count;

    public bool Contains(Guid meshId) => _meshes.ContainsKey(meshId);

    /// <summary>The mesh whose override a joint actually uses (the greatest id among those that
    /// override it), or false for a joint that is not overridden or whose entry is a mirror.</summary>
    public bool TryGetActiveMesh(string joint, out Guid meshId) => _positionOwner.TryGetValue(joint, out meshId);

    /// <summary>
    /// Sets what <paramref name="meshId"/> contributes, replacing anything it contributed before.
    /// Applying the same mesh again with the same data changes nothing, which is what a rig that is
    /// rebuilt at another detail level does.
    /// </summary>
    /// <returns>True when the effective view changed.</returns>
    public bool Apply(Guid meshId, IEnumerable<KeyValuePair<string, Vector3>> positions, IEnumerable<string> scaleLocks)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(scaleLocks);

        var contribution = new Contribution(new Dictionary<string, Vector3>(positions), new HashSet<string>(scaleLocks));
        if (contribution.Positions.Count == 0 && contribution.ScaleLocks.Count == 0)
        {
            // A mesh with nothing to say is not a contributor (the viewer never adds an entry for
            // it either), and must not linger as one after it stopped saying something.
            return Remove(meshId);
        }

        _meshes[meshId] = contribution;
        return Rebuild();
    }

    /// <summary>Takes everything <paramref name="meshId"/> contributed back (the viewer's
    /// <c>removeAttachmentOverridesForObject</c>).</summary>
    /// <returns>True when the effective view changed.</returns>
    public bool Remove(Guid meshId) => _meshes.Remove(meshId) && Rebuild();

    /// <summary>Forgets every mesh.</summary>
    /// <returns>True when the effective view changed.</returns>
    public bool Clear()
    {
        if (_meshes.Count == 0) return false;
        _meshes.Clear();
        return Rebuild();
    }

    private bool Rebuild()
    {
        var positions = new Dictionary<string, Vector3>();
        var scaleLocks = new HashSet<string>();
        _positionOwner.Clear();

        foreach (var (meshId, contribution) in _meshes)
        {
            foreach (var (joint, position) in contribution.Positions)
            {
                // Greatest mesh id wins, whatever order the meshes arrived in.
                if (_positionOwner.TryGetValue(joint, out var owner) && owner.CompareTo(meshId) >= 0) continue;
                _positionOwner[joint] = meshId;
                positions[joint] = position;
            }
            scaleLocks.UnionWith(contribution.ScaleLocks);
        }

        // Viewer parity (applyAttachmentOverrides): a mesh that overrides only one side of a
        // left/right pair is applied to the other side too, mirrored on Y. A side some mesh
        // overrides itself is left alone.
        var mirrored = new Dictionary<string, Vector3>();
        foreach (var (joint, position) in positions)
        {
            if (!TryMirrorName(joint, out var sibling) || positions.ContainsKey(sibling)) continue;
            mirrored[sibling] = new Vector3(position.X, -position.Y, position.Z);
        }
        // The mirrored sibling inherits the lock too: its position override is synthetic, so
        // leaving its scale slider-driven while the real side is frozen would make the two
        // asymmetric, which is the one thing the mirroring exists to prevent.
        foreach (var joint in new List<string>(scaleLocks))
        {
            if (TryMirrorName(joint, out var sibling) && mirrored.ContainsKey(sibling)) scaleLocks.Add(sibling);
        }
        foreach (var (joint, position) in mirrored) positions[joint] = position;

        if (SameContent(positions, _positions) && scaleLocks.SetEquals(_scaleLocks)) return false;

        _positions = positions;
        _scaleLocks = scaleLocks;
        Version++;
        return true;
    }

    private static bool SameContent(Dictionary<string, Vector3> a, Dictionary<string, Vector3> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (joint, position) in a)
            if (!b.TryGetValue(joint, out var other) || other != position) return false;
        return true;
    }

    private static bool TryMirrorName(string joint, out string sibling)
    {
        if (joint.EndsWith("Left", StringComparison.Ordinal))
        {
            sibling = string.Concat(joint.AsSpan(0, joint.Length - 4), "Right");
            return true;
        }
        if (joint.EndsWith("Right", StringComparison.Ordinal))
        {
            sibling = string.Concat(joint.AsSpan(0, joint.Length - 5), "Left");
            return true;
        }
        sibling = "";
        return false;
    }
}
