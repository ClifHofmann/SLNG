using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The "Licences" section of the Help &amp; info category (FEAT-UI-79): shows THIRD-PARTY-NOTICES.md inside the client.
///
/// This is not decoration. We ship Second Life viewer artwork (the terrain blend ramp and the
/// Windlight cloud texture) under Creative Commons Attribution-Share Alike 3.0, and that licence
/// requires the notice to reach the user — "in a text file distributed with your program, in your
/// application's About window, or on a credits page". This section is that credits page, and the
/// notice file ships inside the project directory so an export includes it.
///
/// It renders the file as-is rather than a curated summary, so adding a dependency to the notice
/// file is all anyone has to remember. One long text, so it is a single block rather than rows.
/// </summary>
public sealed class LicensesPreferencesPage
{
    /// <summary>Path is res://-relative because the notice must come from the exported project,
    /// not from a repository checkout that will not exist on a user's machine.</summary>
    public const string NoticePath = "res://THIRD-PARTY-NOTICES.md";

    /// <summary>Fills <paramref name="section"/> (Help &amp; info, "licences"), which Boot has already put in its place.</summary>
    public void Build(PrefsSection section)
    {
        var body = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            SelectionEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 320),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        body.Text = ReadNotice();
        section.AddBlock($"{section.CategoryId}.{section.Id}.notices", body, "ui.preferences.licenses_heading");
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
