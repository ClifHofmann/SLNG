using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Xml;

namespace SLNG.Core;

/// <summary>
/// Represents a single bone definition from the SL Bento skeleton.
/// </summary>
public record BoneDefinition(
    string Name,
    Vector3 Position,
    Vector3 Rotation,
    Vector3 Scale,
    Vector3 End,
    string? ParentName,
    string Support,  // "base" or "extended"
    string Group,
    bool IsCollisionVolume
);

/// <summary>
/// Parses and holds the Second Life Bento skeleton hierarchy from avatar_skeleton.xml.
/// Engine-agnostic — lives in SLNG.Core so it can be unit-tested without Godot.
/// </summary>
public class AvatarSkeleton
{
    private readonly List<BoneDefinition> _bones = new();
    private readonly Dictionary<string, BoneDefinition> _bonesByName = new();

    public IReadOnlyList<BoneDefinition> Bones => _bones;

    /// <summary>
    /// Returns just the real bones (not collision volumes).
    /// </summary>
    public IEnumerable<BoneDefinition> RealBones
    {
        get
        {
            foreach (var b in _bones)
                if (!b.IsCollisionVolume)
                    yield return b;
        }
    }

    public BoneDefinition? GetBone(string name) =>
        _bonesByName.TryGetValue(name, out var b) ? b : null;

    /// <summary>
    /// Computes a bone's global rest position (SL space) by summing local positions up the
    /// parent chain. Parent rest rotations are ~identity for the SL rest pose, so this is a
    /// good approximation for placing a blocky placeholder figure. Returns zero if unknown.
    /// </summary>
    public Vector3 GetGlobalRestPosition(string boneName)
    {
        var position = Vector3.Zero;
        for (var bone = GetBone(boneName); bone != null;
             bone = bone.ParentName != null ? GetBone(bone.ParentName) : null)
        {
            position += bone.Position;
        }
        return position;
    }

    /// <summary>
    /// Loads the skeleton from an avatar_skeleton.xml file.
    /// </summary>
    public static AvatarSkeleton LoadFromFile(string path)
    {
        var xml = File.ReadAllText(path);
        return LoadFromXml(xml);
    }

    /// <summary>
    /// Loads the skeleton from an XML string.
    /// </summary>
    public static AvatarSkeleton LoadFromXml(string xml)
    {
        var skeleton = new AvatarSkeleton();

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var root = doc.DocumentElement;
        if (root == null || root.Name != "linden_skeleton")
            throw new InvalidDataException("Not a valid avatar_skeleton.xml");

        // Recursively parse bone elements
        foreach (XmlNode child in root.ChildNodes)
        {
            if (child is XmlElement elem)
            {
                ParseBoneElement(skeleton, elem, null);
            }
        }

        return skeleton;
    }

    private static void ParseBoneElement(AvatarSkeleton skeleton, XmlElement elem, string? parentName)
    {
        bool isCollisionVolume = elem.Name == "collision_volume";
        bool isBone = elem.Name == "bone";

        if (!isBone && !isCollisionVolume)
            return;

        string name = elem.GetAttribute("name");
        if (string.IsNullOrEmpty(name))
            return;

        var pos = ParseVector3(elem.GetAttribute("pos"));
        var rot = ParseVector3(elem.GetAttribute("rot"));
        var scale = ParseVector3(elem.GetAttribute("scale"), Vector3.One);
        var end = ParseVector3(elem.GetAttribute("end"));
        string support = elem.GetAttribute("support") ?? "base";
        string group = elem.GetAttribute("group") ?? "";

        var bone = new BoneDefinition(
            name, pos, rot, scale, end, parentName, support, group, isCollisionVolume
        );

        skeleton._bones.Add(bone);
        skeleton._bonesByName[name] = bone;

        // Recurse into child elements
        if (isBone)
        {
            foreach (XmlNode child in elem.ChildNodes)
            {
                if (child is XmlElement childElem)
                {
                    ParseBoneElement(skeleton, childElem, name);
                }
            }
        }
    }

    private static Vector3 ParseVector3(string? s, Vector3? defaultValue = null)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue ?? Vector3.Zero;

        // SL uses space-separated floats, e.g. "0.000 0.000 1.067"
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return defaultValue ?? Vector3.Zero;

        if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
        {
            return new Vector3(x, y, z);
        }

        return defaultValue ?? Vector3.Zero;
    }
}
