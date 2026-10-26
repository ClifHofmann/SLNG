using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// FEAT-UI-74: the Camera Controls window. The maths is tested in SLNG.Core.Tests; this is the engine half -
    /// the window opens compact, with its pads at 80 px and its icon buttons at 18 px or more (scale 1.0), cannot be
    /// made smaller than 0.8 (pads at 64 px or more), the pad turns presses into per-axis rates the way the
    /// viewer's joystick does (side, centre dot, drag while held), and the controller maps the zoom slider onto its
    /// distance. Everything is built off the running client, driven by synthetic events, and freed again.
    /// </summary>
    private static Check CheckCameraControls(SceneTree tree)
    {
        const string Name = "camera controls (FEAT-UI-74)";
        var problems = new List<string>();
        UI.CameraHUD? hud = null;
        UI.CameraJoystick? round = null, square = null;
        AvatarController? controller = null;
        try
        {
            // ---- the window ----
            hud = new UI.CameraHUD();
            tree.Root.AddChild(hud);
            hud.Visible = true;
            static void Layout(Control c) { if (c is Container) c.Notification((int)Container.NotificationSortChildren); foreach (var k in c.GetChildren()) if (k is Control kc) Layout(kc); }
            static IEnumerable<T> All<T>(Node n) where T : Node => n.GetChildren().SelectMany(k => (k is T t ? new[] { t } : Array.Empty<T>()).Concat(All<T>(k)));

            var want = hud.Size;
            if (want.X > 260f || want.Y > 200f) problems.Add($"the window opens {want.X:0} x {want.Y:0} px, want a compact one (at most 260 x 200)");
            for (int pass = 0; pass < 3; pass++) { hud.Size = want; Layout(hud); }
            var pads = All<UI.CameraJoystick>(hud).ToList();
            if (pads.Count != 2) problems.Add($"{pads.Count} pads, want the trackball and the pan pad");
            foreach (var pad in pads)
            {
                float size = pad.Size.X * pad.GetGlobalTransform().Scale.X;
                if (Math.Abs(size - 80f) > 3f) problems.Add($"a pad is {size:0} px at the default size, want 80");
            }

            foreach (var button in All<Button>(hud.ContentContainer).Where(b => b.GetType() == typeof(Button)))
            {
                float height = button.Size.Y * button.GetGlobalTransform().Scale.Y;
                if (height < 18f - 0.5f) problems.Add($"a button is {height:0.#} px high at the default size, want at least 18");
            }

            hud.Size = hud.CustomMinimumSize;
            for (int pass = 0; pass < 3; pass++) { Layout(hud); }
            foreach (var pad in All<UI.CameraJoystick>(hud))
            {
                float size = pad.Size.X * pad.GetGlobalTransform().Scale.X;
                if (size < 64f - 3f) problems.Add($"a pad is {size:0} px at the minimum size, want at least 64 (scale 0.8)");
            }

            hud.Size = hud.CustomMinimumSize - new Vector2(60, 40);
            if (hud.Size.X < hud.CustomMinimumSize.X - 0.5f || hud.Size.Y < hud.CustomMinimumSize.Y - 0.5f)
                problems.Add($"the window shrank to {hud.Size} below its minimum {hud.CustomMinimumSize}");

            // ---- the pad ----
            round = new UI.CameraJoystick { Round = true, Size = new Vector2(80, 80) };
            square = new UI.CameraJoystick { Round = false, Size = new Vector2(80, 80) };
            tree.Root.AddChild(round);
            tree.Root.AddChild(square);

            static void Click(UI.CameraJoystick pad, bool pressed, Vector2 at)
                => pad._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at });
            static void Move(UI.CameraJoystick pad, Vector2 at) => pad._GuiInput(new InputEventMouseMotion { Position = at });
            void Expect(string what, Vector2 got, float x, float y)
            {
                // Signs only: the magnitude is the ramp, which depends on how long the press has been held.
                if (Math.Sign(got.X) != x || Math.Sign(got.Y) != y) problems.Add($"{what}: rate ({got.X:0.##}, {got.Y:0.##}), want signs ({x}, {y})");
            }

            // Screen y is down; the pad's Rate is y UP.
            Click(round, true, new Vector2(40, 8)); Expect("press on top", round.Rate, 0, 1);
            Click(round, false, new Vector2(40, 8)); Expect("release", round.Rate, 0, 0);
            Click(round, true, new Vector2(8, 40)); Expect("press on the left", round.Rate, -1, 0);
            Click(round, false, new Vector2(8, 40));
            Click(round, true, new Vector2(40, 72)); Expect("press at the bottom", round.Rate, 0, -1);
            Click(round, false, new Vector2(40, 72));

            Click(round, true, new Vector2(40, 8)); // up, then drag right 40 px: past the 16 px slop on X as well
            Move(round, new Vector2(80, 8)); Expect("up, dragged right", round.Rate, 1, 1);
            Move(round, new Vector2(40, 68)); Expect("up, dragged down 60 px", round.Rate, 0, -1); // 24 - 60 = -36
            Click(round, false, new Vector2(40, 68));

            Click(round, true, new Vector2(72, 40)); // right, then drag back left 41 px: reversed
            Move(round, new Vector2(31, 40)); Expect("right, dragged back past the start", round.Rate, -1, 0);
            Click(round, false, new Vector2(31, 40));

            int resets = 0;
            round.ResetRequested += () => resets++;
            Click(round, true, new Vector2(42, 39));
            Expect("press on the centre dot", round.Rate, 0, 0);
            Click(round, false, new Vector2(42, 39));
            if (resets != 1) problems.Add($"the centre dot raised {resets} resets, want 1");
            Click(round, true, new Vector2(40, 8)); Click(round, false, new Vector2(40, 8));
            if (resets != 1) problems.Add("a press on the side raised a reset");

            if (round._HasPoint(new Vector2(3, 3))) problems.Add("the round pad takes a press in its square corner");
            if (!round._HasPoint(new Vector2(40, 3))) problems.Add("the round pad ignores a press at its top");
            if (!square._HasPoint(new Vector2(3, 3))) problems.Add("the square pad ignores a press in its corner");

            // A pad drawn twice as large keeps the feel: 40 px of travel is 20 px at half the size.
            square.Size = new Vector2(160, 160);
            Click(square, true, new Vector2(80, 16)); // up
            Move(square, new Vector2(80 + 33, 16)); Expect("scaled pad, 33 px of 160 is past its slop", square.Rate, 1, 1);
            Move(square, new Vector2(80 + 31, 16)); Expect("scaled pad, 31 px of 160 is not", square.Rate, 0, 1);
            Click(square, false, new Vector2(80, 16));

            // ---- the controller's end of the slider ----
            controller = new AvatarController();
            controller.ZoomFraction = 1f;
            if (Math.Abs(controller.ZoomDistance - 0.5f) > 1e-4f) problems.Add($"slider top: {controller.ZoomDistance} m, want the person floor 0.5");
            controller.ZoomFraction = 0f;
            if (Math.Abs(controller.ZoomDistance - SLNG.Core.Camera.CameraZoom.MaxZoom) > 1e-3f) problems.Add($"slider bottom: {controller.ZoomDistance} m, want {SLNG.Core.Camera.CameraZoom.MaxZoom}");
            controller.ZoomFraction = 0.5f;
            if (Math.Abs(controller.ZoomFraction - 0.5f) > 1e-4f) problems.Add($"slider middle reads back {controller.ZoomFraction}");
            controller.ZoomCamera(-1000f);
            if (Math.Abs(controller.ZoomFraction - 1f) > 1e-4f) problems.Add("zoomed all the way in, the slider is not at the top");
            controller.PanCamera(new Vector2(1, 1));
            controller.ResetPan(); // nothing to read back; it must at least not throw

            return problems.Count == 0
                ? new Check(Name, true, "pads 80 px and icon buttons >= 18 px at the default size, pads >= 64 px at the minimum and no smaller; press, drag, reversal, centre reset and size scaling; slider 1 = 0.5 m ... 0 = 200 m")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (Node? n in new Node?[] { hud, round, square })
            {
                if (n != null && GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
            }

            if (controller != null && GodotObject.IsInstanceValid(controller)) controller.Free(); // never in the tree
        }
    }
}
