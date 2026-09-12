using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-26: the one shared <see cref="Theme"/> every <see cref="SLNGWindow"/> hands to its
/// children, currently carrying the check-control indicators.
///
/// Why this exists: Godot's default check icons are a thin grey glyph on a dark panel, and the
/// <c>CheckButton</c> switch is worse -- its off state is a dim dot whose position, not colour,
/// is the entire signal. At the sizes this UI uses, "is that on?" needed a second look every
/// time, which is what the user reported. The indicators below are drawn instead of themed:
/// checked is a filled accent box with a white tick, unchecked a hollow outline, and the switch
/// is a filled accent capsule versus a hollow grey one -- so state reads from COLOUR and FILL
/// first and shape second, at a glance and in peripheral vision.
///
/// Drawn procedurally rather than shipped as PNGs so they follow the accent colour and stay
/// crisp at whatever size the UI-scale preference lands on, with no assets to keep in sync.
/// Built once, lazily, and shared by every window -- a Theme is a Resource, so handing the same
/// instance to fifty Controls costs one of them.
///
/// A Control only inherits a Theme from Control ANCESTORS, and this app's windows hang off
/// CanvasLayers (which break that chain), so the theme is applied per window in
/// <see cref="SLNGWindow._Ready"/> rather than once at the root. Per-control
/// <c>AddThemeStyleboxOverride</c> calls still win over it, so nothing already styled by hand
/// changes.
/// </summary>
public static class UiTheme
{
    // The app's accent, matching the top bar's border and version label.
    private static readonly Color Accent = new(0.24f, 0.66f, 0.94f);
    private static readonly Color OffOutline = new(0.62f, 0.67f, 0.74f);
    private static readonly Color OffFill = new(1f, 1f, 1f, 0.06f);
    private static readonly Color Mark = new(0.98f, 0.99f, 1f);

    private const int BoxSize = 20;
    private const int SwitchWidth = 40;
    private const int SwitchHeight = 22;

    private static Theme? _shared;

    /// <summary>The shared theme instance, built on first use.</summary>
    public static Theme Shared => _shared ??= Build();

    private static Theme Build()
    {
        var theme = new Theme();

        theme.SetIcon("checked", "CheckBox", BuildCheckBox(true));
        theme.SetIcon("unchecked", "CheckBox", BuildCheckBox(false));
        theme.SetIcon("checked_disabled", "CheckBox", BuildCheckBox(true, dim: true));
        theme.SetIcon("unchecked_disabled", "CheckBox", BuildCheckBox(false, dim: true));
        theme.SetConstant("h_separation", "CheckBox", 8);

        theme.SetIcon("checked", "CheckButton", BuildSwitch(true));
        theme.SetIcon("unchecked", "CheckButton", BuildSwitch(false));
        theme.SetIcon("checked_disabled", "CheckButton", BuildSwitch(true, dim: true));
        theme.SetIcon("unchecked_disabled", "CheckButton", BuildSwitch(false, dim: true));
        // Godot picks the *_mirrored icons under a right-to-left layout; without them a mirrored
        // locale would silently fall back to the default switch and lose this whole fix.
        theme.SetIcon("checked_mirrored", "CheckButton", BuildSwitch(true, mirrored: true));
        theme.SetIcon("unchecked_mirrored", "CheckButton", BuildSwitch(false, mirrored: true));
        theme.SetConstant("h_separation", "CheckButton", 8);

        return theme;
    }

    /// <summary>A square check: filled accent with a white tick when on, a hollow outline when
    /// off.</summary>
    private static ImageTexture BuildCheckBox(bool on, bool dim = false)
    {
        var img = Image.CreateEmpty(BoxSize, BoxSize, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));

        var half = new Vector2(BoxSize * 0.5f - 1.5f, BoxSize * 0.5f - 1.5f);
        var centre = new Vector2(BoxSize * 0.5f, BoxSize * 0.5f);
        const float radius = 5f;
        const float outline = 1.8f;

        var fill = on ? Accent : OffFill;
        var edge = on ? Accent : OffOutline;

        for (int y = 0; y < BoxSize; y++)
        {
            for (int x = 0; x < BoxSize; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f) - centre;
                float d = RoundedBoxDistance(p, half, radius);

                // Interior first, then the ring, then the tick on top -- painter's order, so the
                // tick is never eaten by the fill it sits on.
                var colour = new Color(0, 0, 0, 0);
                colour = Blend(colour, fill, Coverage(d));
                colour = Blend(colour, edge, Coverage(Mathf.Abs(d + outline * 0.5f) - outline * 0.5f));

                if (on)
                {
                    float tick = Mathf.Min(
                        SegmentDistance(p, new Vector2(-4.4f, 0.2f), new Vector2(-1.2f, 3.6f)),
                        SegmentDistance(p, new Vector2(-1.2f, 3.6f), new Vector2(4.6f, -3.8f)));
                    colour = Blend(colour, Mark, Coverage(tick - 1.35f));
                }

                img.SetPixel(x, y, dim ? Dim(colour) : colour);
            }
        }

        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>A capsule switch: filled accent with the knob right when on, hollow grey with the
    /// knob left when off. Colour carries the state as much as knob position does -- reading it
    /// must not depend on remembering which side means yes.</summary>
    private static ImageTexture BuildSwitch(bool on, bool dim = false, bool mirrored = false)
    {
        var img = Image.CreateEmpty(SwitchWidth, SwitchHeight, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));

        var centre = new Vector2(SwitchWidth * 0.5f, SwitchHeight * 0.5f);
        var half = new Vector2(SwitchWidth * 0.5f - 1.5f, SwitchHeight * 0.5f - 1.5f);
        float radius = half.Y;
        const float outline = 1.8f;

        // Inset by a pixel past the knob radius so the knob sits INSIDE the capsule rather than
        // flush with its outline -- flush reads as the knob eating the border.
        float knobOffset = half.X - radius * 0.58f - 2f;
        bool knobRight = on != mirrored;
        var knobCentre = new Vector2(knobRight ? knobOffset : -knobOffset, 0f);
        float knobRadius = radius * 0.58f;

        var fill = on ? Accent : OffFill;
        var edge = on ? Accent : OffOutline;
        var knob = on ? Mark : OffOutline;

        for (int y = 0; y < SwitchHeight; y++)
        {
            for (int x = 0; x < SwitchWidth; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f) - centre;
                float d = RoundedBoxDistance(p, half, radius);

                var colour = new Color(0, 0, 0, 0);
                colour = Blend(colour, fill, Coverage(d));
                colour = Blend(colour, edge, Coverage(Mathf.Abs(d + outline * 0.5f) - outline * 0.5f));
                colour = Blend(colour, knob, Coverage((p - knobCentre).Length() - knobRadius));

                img.SetPixel(x, y, dim ? Dim(colour) : colour);
            }
        }

        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Signed distance to a rounded box centred on the origin -- negative inside.</summary>
    private static float RoundedBoxDistance(Vector2 p, Vector2 half, float radius)
    {
        var q = new Vector2(Mathf.Abs(p.X) - half.X + radius, Mathf.Abs(p.Y) - half.Y + radius);
        float outside = new Vector2(Mathf.Max(q.X, 0f), Mathf.Max(q.Y, 0f)).Length();
        return Mathf.Min(Mathf.Max(q.X, q.Y), 0f) + outside - radius;
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return (p - (a + ab * t)).Length();
    }

    /// <summary>One-pixel antialiasing: a distance of 0 is the edge, so coverage crosses 0.5
    /// exactly there and reaches full within half a pixel either side.</summary>
    private static float Coverage(float distance) => Mathf.Clamp(0.5f - distance, 0f, 1f);

    private static Color Blend(Color under, Color over, float coverage)
    {
        float a = over.A * coverage;
        if (a <= 0f) return under;

        float outA = a + under.A * (1f - a);
        if (outA <= 0f) return new Color(0, 0, 0, 0);

        return new Color(
            (over.R * a + under.R * under.A * (1f - a)) / outA,
            (over.G * a + under.G * under.A * (1f - a)) / outA,
            (over.B * a + under.B * under.A * (1f - a)) / outA,
            outA);
    }

    private static Color Dim(Color c) => new(c.R, c.G, c.B, c.A * 0.4f);
}
