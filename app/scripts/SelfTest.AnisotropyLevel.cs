using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>BUG-RENDER-52: <c>textures/default_filters/anisotropic_filtering_level</c> is an index (0 off, 1 2x, 2 4x,
    /// 3 8x, 4 16x), not a sample count. Godot makes the sampler's <c>MaxAnisotropy</c> <c>1 &lt;&lt; level</c>, so the 16 this
    /// project once carried asked for 65,536. Vulkan clamps that; D3D12 rejects the sampler and removes the device.</summary>
    private static Check CheckAnisotropyLevel()
    {
        const string Name = "project: anisotropic filtering level is an index (0-4)";
        const string Setting = "rendering/textures/default_filters/anisotropic_filtering_level";
        int level = ProjectSettings.HasSetting(Setting) ? (int)ProjectSettings.GetSetting(Setting) : 2;
        return level is >= 0 and <= 4
            ? new Check(Name, true, $"level {level} = {1 << level}x")
            : new Check(Name, false, $"level {level} would be MaxAnisotropy {1L << level}; the range is 0-4");
    }
}
