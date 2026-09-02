using Godot;
using System.Text;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-SL-01 — "About Puris Viewer", the window the Third-Party Viewer Policy §1.g requires:
/// the viewer must carry an About window naming it and stating its version, so anyone (a resident,
/// a region owner reading a sim log, Linden Lab) can identify exactly what is connecting.
///
/// The name and version shown here are the same ones sent to the grid at login — <see
/// cref="Boot.AppVersion"/> feeds both the login screen and <c>LoginCredentials.Version</c>, and
/// the channel is the one in <c>LoginCredentials.Channel</c>. That is the point: an About window
/// reporting something other than what the grid was told would be worse than none at all.
///
/// Also carries the §1.c disclosures — what this viewer is, that it is not affiliated with or
/// supported by Linden Lab, and where its source is — plus the runtime information that makes a
/// bug report useful (Godot version, renderer, platform).
/// </summary>
public partial class AboutWindow : SLNGWindow
{
    /// <summary>Viewer channel reported to every grid at login. Must match
    /// <c>LoginCredentials.Channel</c>'s default -- see its doc comment: "SLNG" starts with "SL"
    /// and TPV Policy §5.b forbids that fragment in a Third-Party Viewer's identity, so both use
    /// the viewer's actual public name instead.</summary>
    public const string ViewerChannel = "Puris";

    /// <summary>Public name of the viewer, as shown to users.</summary>
    public const string ViewerName = "Puris Viewer";

    /// <summary>Where the source lives. TPV §1.c requires the viewer to identify itself
    /// honestly; a project link is the practical form of that.</summary>
    public const string ProjectUrl = "https://github.com/ClifHofmann/SLNG";

    private VBoxContainer _contentVBox = null!;

    public override void _Ready()
    {
        base._Ready();

        Title = L10n.Tr("ui.about.title");
        CustomMinimumSize = new Vector2(520, 420);
        Size = CustomMinimumSize;
        PersistId = "about_window";

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _contentVBox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(_contentVBox);

        BuildContent();
    }

    private void BuildContent()
    {
        var name = new Label { Text = $"{ViewerName} {Boot.AppVersion}" };
        name.AddThemeFontSizeOverride("font_size", 20);
        _contentVBox.AddChild(name);

        var channel = new Label { Text = L10n.TrFormat("ui.about.channel", ViewerChannel, Boot.AppVersion.TrimStart('v')) };
        channel.AddThemeFontSizeOverride("font_size", 11);
        channel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        _contentVBox.AddChild(channel);

        var blurb = new Label
        {
            Text = L10n.Tr("ui.about.blurb"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _contentVBox.AddChild(blurb);

        // TPV §1.c: say plainly that this is not Linden Lab's software and carries no support
        // from them. Not boilerplate -- it is the disclosure the policy asks for.
        var disclaimer = new Label
        {
            Text = L10n.Tr("ui.about.disclaimer"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        disclaimer.AddThemeFontSizeOverride("font_size", 11);
        disclaimer.AddThemeColorOverride("font_color", new Color(0.72f, 0.76f, 0.82f));
        _contentVBox.AddChild(disclaimer);

        var details = new RichTextLabel
        {
            BbcodeEnabled = false,
            SelectionEnabled = true,
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
            Text = BuildRuntimeDetails(),
        };
        _contentVBox.AddChild(details);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);

        var projectButton = new Button { Text = L10n.Tr("ui.about.open_project") };
        projectButton.Pressed += () => OS.ShellOpen(ProjectUrl);
        buttons.AddChild(projectButton);

        var copyButton = new Button { Text = L10n.Tr("ui.about.copy") };
        copyButton.Pressed += () => DisplayServer.ClipboardSet($"{ViewerName} {Boot.AppVersion}\n{BuildRuntimeDetails()}");
        buttons.AddChild(copyButton);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddChild(spacer);

        var closeButton = new Button { Text = L10n.Tr("ui.about.close"), CustomMinimumSize = new Vector2(100, 0) };
        closeButton.Pressed += () => Visible = false;
        buttons.AddChild(closeButton);

        _contentVBox.AddChild(buttons);
    }

    /// <summary>The lines a bug report needs. Deliberately the same values a sim log would show
    /// for this session, so "which build was that" is answerable from either end.</summary>
    private static string BuildRuntimeDetails()
    {
        var sb = new StringBuilder();
        sb.AppendLine(L10n.TrFormat("ui.about.detail_version", Boot.AppVersion));
        sb.AppendLine(L10n.TrFormat("ui.about.detail_channel", ViewerChannel));
        sb.AppendLine(L10n.TrFormat("ui.about.detail_engine", Engine.GetVersionInfo()["string"].AsString()));
        sb.AppendLine(L10n.TrFormat("ui.about.detail_renderer",
            ProjectSettings.GetSetting("rendering/renderer/rendering_method").AsString()));
        sb.AppendLine(L10n.TrFormat("ui.about.detail_platform", $"{OS.GetName()} ({System.Environment.OSVersion.Version})"));
        sb.AppendLine(L10n.TrFormat("ui.about.detail_gpu", RenderingServer.GetVideoAdapterName()));
        return sb.ToString();
    }
}
