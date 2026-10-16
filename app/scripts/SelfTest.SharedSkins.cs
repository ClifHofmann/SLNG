using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>FEAT-PERF-24: identical rigged skins on one avatar share one Skin and one engine
    /// skeleton binding (see <c>AvatarRenderer.SelfTestSharedSkins</c>).</summary>
    private static Check CheckSharedRiggedSkins(SceneTree tree)
    {
        const string Name = "identical rigged skins share one Skin";
        var renderer = new AvatarRenderer();
        try
        {
            var (passed, detail) = renderer.SelfTestSharedSkins(tree);
            return new Check(Name, passed, detail);
        }
        finally
        {
            renderer.Free();
        }
    }
}
