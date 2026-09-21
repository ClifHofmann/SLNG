using Godot;
using System;
using System.Collections.Generic;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// "This object wants permission to…" prompt for an in-world script's
/// <c>llRequestPermissions</c>.
///
/// <para>Nothing is ever granted without a click here, and the window says which permissions are
/// being asked for rather than a single yes/no — the flags run from the harmless (animate you) to
/// the expensive (<b>spend your money</b>), and a prompt that hides that difference is worse than
/// no prompt at all. Debit is called out separately and in a warning colour for exactly that
/// reason.</para>
///
/// <para>Closing the window with the title-bar × <b>refuses</b>, and says so to the simulator,
/// rather than sending nothing. That matches the reference viewer, which always replies and
/// simply zeroes the granted bits (llviewermessage.cpp:5600-5632) — silence leaves the script
/// waiting forever, which is the state SLNG was in before this window existed.</para>
/// </summary>
public partial class ScriptPermissionWindow : SLNGWindow
{
    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it.</summary>
    public event Action? Closed;

    /// <summary>Set by the owner before Initialize so several prompts cascade instead of stacking.</summary>
    public int CascadeIndex { get; set; }

    private GridSession _session = null!;
    private ScriptPermissionRequestEvent _request = null!;
    private bool _answered;
    private VBoxContainer _contentVBox = null!;

    /// <summary>The permissions worth naming, in the order they are shown. Anything the simulator
    /// asks for that is not in this list still appears, as its raw flag — an unknown permission
    /// must never be silently hidden from the person granting it.</summary>
    private static readonly (ScriptPermissionFlags Flag, string Key)[] Described =
    {
        (ScriptPermissionFlags.Debit, "ui.script_permission.debit"),
        (ScriptPermissionFlags.TakeControls, "ui.script_permission.take_controls"),
        (ScriptPermissionFlags.TriggerAnimation, "ui.script_permission.trigger_animation"),
        (ScriptPermissionFlags.Attach, "ui.script_permission.attach"),
        (ScriptPermissionFlags.Teleport, "ui.script_permission.teleport"),
        (ScriptPermissionFlags.TrackCamera, "ui.script_permission.track_camera"),
        (ScriptPermissionFlags.ControlCamera, "ui.script_permission.control_camera"),
        (ScriptPermissionFlags.ChangeLinks, "ui.script_permission.change_links"),
        (ScriptPermissionFlags.ChangePermissions, "ui.script_permission.change_permissions"),
        (ScriptPermissionFlags.ReleaseOwnership, "ui.script_permission.release_ownership"),
        (ScriptPermissionFlags.RemapControls, "ui.script_permission.remap_controls"),
        (ScriptPermissionFlags.ChangeJoints, "ui.script_permission.change_joints"),
    };

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(400, 0); // height 0 -> shrink-wraps its contents
        Size = CustomMinimumSize;

        // Dismissing is a refusal, and a refusal is still an answer -- see the class doc.
        OnCloseRequested = () => Close(respond: true, grant: false);

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        _contentVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentVBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_contentVBox);
    }

    public void Initialize(GridSession session, ScriptPermissionRequestEvent request)
    {
        _session = session;
        _request = request;

        Title = L10n.Tr("ui.script_permission.title");

        var who = new Label
        {
            Text = L10n.TrFormat("ui.script_permission.from", request.ObjectName, request.ObjectOwner),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        who.AddThemeFontSizeOverride("font_size", 12);
        _contentVBox.AddChild(who);

        var asks = new Label { Text = L10n.Tr("ui.script_permission.asks_for") };
        asks.AddThemeFontSizeOverride("font_size", 11);
        asks.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        _contentVBox.AddChild(asks);

        foreach (var line in DescribePermissions(request.Permissions))
        {
            var row = new Label
            {
                Text = "• " + line.Text,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            row.AddThemeFontSizeOverride("font_size", 12);
            // Money is not one bullet among others.
            if (line.IsMoney) row.AddThemeColorOverride("font_color", new Color(0.98f, 0.55f, 0.35f));
            _contentVBox.AddChild(row);
        }

        var buttons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddThemeConstantOverride("separation", 8);

        // Deny first, and focused: the safe answer should be the easy one.
        var deny = new Button
        {
            Text = L10n.Tr("ui.script_permission.deny"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        deny.Pressed += () => Close(respond: true, grant: false);
        buttons.AddChild(deny);

        var grant = new Button
        {
            Text = L10n.Tr("ui.script_permission.grant"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        grant.Pressed += () => Close(respond: true, grant: true);
        buttons.AddChild(grant);

        _contentVBox.AddChild(buttons);
        deny.GrabFocus();

        PositionWindow();
    }

    /// <summary>One readable line per requested permission. An unnamed flag is reported as its raw
    /// bit rather than dropped — granting something the prompt did not mention is the one outcome
    /// this window exists to prevent.</summary>
    internal static List<(string Text, bool IsMoney)> DescribePermissions(int permissions)
    {
        var lines = new List<(string, bool)>();
        int remaining = permissions;

        foreach (var (flag, key) in Described)
        {
            if ((permissions & (int)flag) == 0) continue;
            lines.Add((L10n.Tr(key), flag == ScriptPermissionFlags.Debit));
            remaining &= ~(int)flag;
        }

        if (remaining != 0)
            lines.Add((L10n.TrFormat("ui.script_permission.unknown", remaining), false));

        if (lines.Count == 0)
            lines.Add((L10n.Tr("ui.script_permission.nothing"), false));

        return lines;
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2) + CascadeIndex * 28,
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 3) + CascadeIndex * 28);
    }

    private void Close(bool respond, bool grant)
    {
        if (_answered) return;
        _answered = true;

        if (respond)
        {
            // Grant exactly what was asked for, or nothing. Never more.
            _session.RespondToScriptPermissionRequest(
                _request.TaskId, _request.ItemId, grant ? _request.Permissions : 0);
        }

        Closed?.Invoke();
        QueueFree();
    }
}
