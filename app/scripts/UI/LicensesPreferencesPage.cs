using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Shows THIRD-PARTY-NOTICES.md inside the client.
///
/// This is not decoration. We ship Second Life viewer artwork (the terrain blend ramp and the
/// Windlight cloud texture) under Creative Commons Attribution-Share Alike 3.0, and that licence
/// requires the notice to reach the user — "in a text file distributed with your program, in your
/// application's About window, or on a credits page". This page is that credits page, and the
/// notice file ships inside the project directory so an export includes it.
///
/// It renders the file as-is rather than a curated summary, so adding a dependency to the notice
/// file is all anyone has to remember.
/// </summary>
public partial class LicensesPreferencesPage : VBoxContainer
{
    /// <summary>Path is res://-relative because the notice must come from the exported project,
    /// not from a repository checkout that will not exist on a user's machine.</summary>
    public const string NoticePath = "res://THIRD-PARTY-NOTICES.md";

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
    }

    public void Initialize()
    {
        var heading = new Label { Text = L10n.Tr("ui.preferences.licenses_heading") };
        heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        AddChild(heading);

        var body = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            SelectionEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 320),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        body.Text = ReadNotice();
        AddChild(body);
    }

    private static string ReadNotice()
    {
        using var file = FileAccess.Open(NoticePath, FileAccess.ModeFlags.Read);
        if (file != null) return file.GetAsText();

        // A missing notice is a licence-compliance problem, not a cosmetic one: it means an export
        // dropped the file. Say so plainly instead of showing an empty panel that looks intentional.
        Logger.Warn($"[Licenses] {NoticePath} is missing from this build — third-party attribution " +
                    "is not being shown. Check the export preset's include filter.");
        return L10n.Tr("ui.preferences.licenses_missing");
    }
}
