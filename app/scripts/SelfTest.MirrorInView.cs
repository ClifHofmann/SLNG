using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>BUG-PERF-23: the planar mirror's second scene render is skipped for a mirror the camera
    /// is not looking at. <see cref="MirrorReflection.InView"/> is the sphere-against-frustum test.</summary>
    private static Check CheckMirrorInView()
    {
        const string Name = "planar mirror: only rendered while in view";
        const float Tan = 1f; // 90 degrees both ways

        var cases = new (Vector3 Local, float Radius, bool Expected, string What)[]
        {
            (new Vector3(0, 0, -10), 1f, true, "ahead"),
            (new Vector3(0, 0, 10), 1f, false, "behind"),
            (new Vector3(50, 0, -10), 1f, false, "far to the side"),
            (new Vector3(0, -50, -10), 1f, false, "far below"),
            (new Vector3(11, 0, -10), 1f, true, "edge touches the frustum"),
            (new Vector3(30, 0, -10), 1f, false, "clear of the frustum"),
            (new Vector3(0, 0, 0.5f), 1f, true, "radius reaches in front of the camera"),
            (new Vector3(0, 0, 2f), 1f, false, "wholly behind"),
        };

        foreach (var c in cases)
        {
            bool got = MirrorReflection.InView(c.Local, Tan, Tan, c.Radius);
            if (got != c.Expected) return new Check(Name, false, $"{c.What}: expected {c.Expected}, got {got}");
        }

        // The Sirens case: a 1.82 m mirror 84 m away whose plane lies 4.2 m from the eye, dead ahead but edge-on.
        const float Min = 0.01f;
        var sizes = new (float PlaneDistance, float LineOfSight, float Radius, bool Renders, string What)[]
        {
            (4.2f, 84f, 1.82f, false, "84 m away, edge-on"),
            (84f, 84f, 1.82f, true, "84 m away, head-on (still rendered)"),
            (5f, 5f, 1f, true, "5 m, head-on"),
            (3.5f, 5f, 1f, true, "5 m at 45 degrees"),
            (0.5f, 3f, 1f, true, "3 m, grazing but close"),
            (0.1f, 60f, 2f, false, "60 m, nearly flat"),
        };
        foreach (var s in sizes)
        {
            bool renders = MirrorReflection.ScreenFraction(s.PlaneDistance, s.LineOfSight, s.Radius) >= Min;
            if (renders != s.Renders) return new Check(Name, false, $"{s.What}: expected renders={s.Renders}, got {renders}");
        }
        return new Check(Name, true, $"{cases.Length} placements, {sizes.Length} sizes");
    }
}
