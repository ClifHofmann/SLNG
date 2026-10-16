using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>BUG-AVATAR-12: a seated avatar's pelvis sits on the seat point under any seat rotation
    /// (see <c>AvatarRenderer.SelfTestSeatedPelvis</c>).</summary>
    private static Check CheckSeatedPelvisOnSeat(SceneTree tree)
    {
        var (passed, detail) = AvatarRenderer.SelfTestSeatedPelvis(tree);
        return new Check("a tilted seat keeps the pelvis on the seat point", passed, detail);
    }
}
