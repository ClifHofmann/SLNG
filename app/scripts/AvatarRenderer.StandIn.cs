using Godot;
using System;
using System.Linq;
using SLNG.Core;

namespace SLNG.App;

public partial class AvatarRenderer
{
    // BUG-AVATAR-19: a reduced avatar draws its system body with its bakes. Each part stays on the jelly colour until its bake
    // has been applied (to the part's own stashed material, as before); then the part shows it as described here.
    // docs/specs/BUG-AVATAR-19-reduced-avatar-bakes.md.

    /// <summary>The jelly colour of an avatar, the one <see cref="GetJellyMaterial"/> paints it with.</summary>
    private static Color JellyColorFor(Guid agentId)
    {
        byte key = JellyDollColor.Colorize ? JellyDollColor.FirstByte(agentId) : (byte)0;
        var (r, g, b) = JellyDollColor.ForFirstByte(key);
        return new Color(r, g, b);
    }

    private static string PartNameOf(AvatarVisual visual, MeshInstance3D mi)
    {
        foreach (var (name, part) in visual.Parts)
            if (ReferenceEquals(part, mi)) return name;
        return "";
    }

    /// <summary>
    /// Puts the bake of a stashed part on display, if the part's own material has one. A body surface gets the opaque stand-in material
    /// (the jelly colour where the bake's alpha hides the body, so no hole and no black); hair and eyelashes get their own alpha-cut
    /// material back, because their alpha is their shape. A part whose material has no texture yet stays as it is (the jelly colour).
    /// </summary>
    private void ShowStandInFromOriginal(AvatarVisual visual, string partName, MeshInstance3D mi)
    {
        if (!IsInstanceValid(mi)) return;
        if (!visual.StashedPartMaterials.TryGetValue(mi, out var original) || original is not ShaderMaterial own) return;
        if (!own.GetShaderParameter(PrimShaderFamily.HasAlbedoTexture).AsBool()) return;

        if (!StandInBakePolicy.IsBodySurface(partName))
        {
            mi.MaterialOverride = own;
            return;
        }

        var texture = own.GetShaderParameter(PrimShaderFamily.AlbedoTexture).As<Texture2D>();
        if (texture == null || !IsInstanceValid(texture)) return;

        if (!visual.StandInMaterials.TryGetValue(mi, out var standIn) || !IsInstanceValid(standIn))
        {
            standIn = new ShaderMaterial { Shader = PrimShaderFamily.Select(PrimShaderFamily.Kind.Opaque, PrimShaderFamily.Surface.Avatar) };
            visual.StandInMaterials[mi] = standIn;
        }
        standIn.SetShaderParameter(PrimShaderFamily.AlbedoTexture, texture);
        standIn.SetShaderParameter(PrimShaderFamily.HasAlbedoTexture, true);
        standIn.SetShaderParameter(PrimShaderFamily.AlbedoColor, Colors.White);
        standIn.SetShaderParameter(PrimShaderFamily.StandInAlphaCutoff, StandInBakePolicy.HiddenAlphaCutoff);
        standIn.SetShaderParameter(PrimShaderFamily.StandInColor, JellyColorFor(visual.AgentId));
        mi.MaterialOverride = standIn;
    }
}
