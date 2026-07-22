using System;
using System.IO;
using SLNG.Core;

class Program {
    static void Main() {
        var skel = AvatarSkeleton.Parse(File.ReadAllText("app/assets/avatar/avatar_skeleton.xml"));
        foreach (var name in new[] { "mHip", "mKnee", "mAnkle", "mFoot", "mToe" }) {
            var left = skel.GetBone(name + "Left");
            var right = skel.GetBone(name + "Right");
            Console.WriteLine($"{name}: Left {left.Position}, Right {right.Position}");
        }
    }
}
