using Godot;
using System;
using System.Collections.Generic;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-34: the region-restart popup — the region's name, a live countdown, and a way out.
///
/// <para>A region restart is the one alert with a deadline: miss it and the session ends where
/// you stand. Until now it was a single line among others in the chat and the notification
/// window, which is why it was asked for in-world ("im FS ploppt ein signalfarbenes Popup auf").
/// This is the popup; the notification entry stays as the record.</para>
///
/// <para>Modelled on the reference viewer's <c>LLFloaterRegionRestarting</c>: a repeat notice moves
/// the deadline of the window that is already open instead of stacking a second one
/// (<see cref="Update"/>), and the owner closes it on a region change. The part Firestorm adds and
/// the request was actually about is the destination: one of the resident's own landmarks and a
/// Teleport button — deliberately not a free destination picker.</para>
///
/// <para><b>Home is always the first entry, and the default.</b> It needs nothing from the
/// inventory, so the Teleport button works the moment the window opens; the landmarks are appended
/// when they have loaded. With no landmarks at all (or none loaded yet) there is still a way out,
/// and what happens when home itself is unset or unusable is the grid's decision, not ours.</para>
///
/// <para>The countdown is a deadline held by <see cref="RegionRestartCountdown"/> and read against
/// a monotonic clock every frame, so a stalled frame cannot make it drift. The camera shake of the
/// reference viewer is not reproduced: it is optional there for a reason, and UI that moves the
/// camera is something some people cannot stand.</para>
/// </summary>
public partial class RegionRestartWindow : SLNGWindow
{
    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it.</summary>
    public event Action? Closed;

    private static readonly Color Warning = new(0.98f, 0.62f, 0.25f);
    private static readonly Color Urgent = new(0.95f, 0.32f, 0.30f);

    /// <summary>Below this the clock turns red.</summary>
    private const int UrgentSeconds = 10;

    private readonly RegionRestartCountdown _countdown = new();
    private GridSession _session = null!;

    private Label _headline = null!;
    private Label _clock = null!;
    private OptionButton _landmarks = null!;
    private Button _teleport = null!;
    private Label _status = null!;

    /// <summary>The landmarks, in dropdown order after the Home entry: dropdown index N is
    /// <c>_entries[N - 1]</c>; index 0 is Home.</summary>
    private IReadOnlyList<InventoryEntry> _entries = Array.Empty<InventoryEntry>();
    private int _shownSeconds = -1;
    private bool _teleporting;
    private bool _closed;

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(400, 0); // height 0 -> shrink-wraps its contents
        Size = CustomMinimumSize;
        OnCloseRequested = Close;

        Title = L10n.Tr("ui.region_restart.title");

        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        _headline = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _headline.AddThemeFontSizeOverride("font_size", 14);
        _headline.AddThemeColorOverride("font_color", Warning);
        box.AddChild(_headline);

        _clock = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _clock.AddThemeFontSizeOverride("font_size", 40);
        _clock.AddThemeColorOverride("font_color", Warning);
        box.AddChild(_clock);

        var warning = new Label
        {
            Text = L10n.Tr("ui.region_restart.warning"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        warning.AddThemeFontSizeOverride("font_size", 12);
        warning.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        box.AddChild(warning);

        box.AddChild(new HSeparator());

        var destination = new Label { Text = L10n.Tr("ui.region_restart.destination") };
        destination.AddThemeFontSizeOverride("font_size", 11);
        destination.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        box.AddChild(destination);

        _landmarks = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FitToLongestItem = false,
        };
        _landmarks.AddItem(L10n.Tr("ui.region_restart.home"));
        _landmarks.Selected = 0;
        box.AddChild(_landmarks);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);

        var dismiss = new Button
        {
            Text = L10n.Tr("ui.region_restart.dismiss"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        dismiss.Pressed += Close;
        buttons.AddChild(dismiss);

        _teleport = new Button
        {
            Text = L10n.Tr("ui.region_restart.teleport"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _teleport.Pressed += OnTeleportPressed;
        buttons.AddChild(_teleport);

        _status = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = false,
        };
        _status.AddThemeFontSizeOverride("font_size", 11);
        box.AddChild(_status);
    }

    /// <summary>Opens the window on a fresh restart notice.</summary>
    public void Initialize(GridSession session, RegionRestartEvent restart)
    {
        _session = session;
        Update(restart);
        PositionWindow();
        _ = LoadLandmarksAsync();
    }

    /// <summary>A repeat notice for the restart already on screen: moves the deadline, opens
    /// nothing. The reference viewer's <c>updateTime</c>.</summary>
    public void Update(RegionRestartEvent restart)
    {
        _countdown.Start(restart.RegionName, restart.Seconds, Now());
        _headline.Text = string.IsNullOrEmpty(restart.RegionName)
            ? L10n.Tr("ui.region_restart.headline_unnamed")
            : L10n.TrFormat("ui.region_restart.headline", restart.RegionName);
        _shownSeconds = -1; // force the clock to redraw this frame

        // The taskbar flash of Firestorm: a restart notice is worth interrupting a game for, and
        // a window that is already focused is unaffected.
        DisplayServer.WindowRequestAttention();
        MoveToFront();
    }

    public override void _Process(double delta)
    {
        if (!_countdown.IsRunning) return;

        var now = Now();
        int left = _countdown.SecondsLeft(now);
        if (left == _shownSeconds) return;
        _shownSeconds = left;

        bool elapsed = _countdown.HasElapsed(now);
        _clock.Text = elapsed ? L10n.Tr("ui.region_restart.restarting") : RegionRestartCountdown.Format(left);
        // "Restarting…" is a sentence, not a clock: smaller so it stays on one line.
        _clock.AddThemeFontSizeOverride("font_size", elapsed ? 24 : 40);
        _clock.AddThemeColorOverride("font_color", left <= UrgentSeconds ? Urgent : Warning);
    }

    /// <summary>What the clock currently shows. Exists for the selftest, which cannot read a
    /// private label.</summary>
    internal string ClockText => _clock.Text;

    /// <summary>Destinations offered right now (Home plus any loaded landmarks) and whether the
    /// Teleport button can be pressed. Selftest only, like <see cref="ClockText"/>.</summary>
    internal int DestinationCount => _landmarks.ItemCount;
    internal bool TeleportEnabled => !_teleport.Disabled;

    private static TimeSpan Now() => TimeSpan.FromMilliseconds(Time.GetTicksMsec());

    private async System.Threading.Tasks.Task LoadLandmarksAsync()
    {
        IReadOnlyList<InventoryEntry> landmarks;
        try
        {
            landmarks = await _session.GetLandmarksAsync();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RegionRestart] could not list landmarks: {ex.Message}");
            landmarks = Array.Empty<InventoryEntry>();
        }

        // Continues on the main thread (Godot's synchronization context), but the window may have
        // been closed or freed while the folders were being fetched.
        if (!IsInstanceValid(this) || _closed) return;

        // Appended after Home: whatever the user has already picked (or not) is left alone.
        _entries = landmarks;
        foreach (var entry in _entries) _landmarks.AddItem(entry.Name);
    }

    private async void OnTeleportPressed()
    {
        int index = _landmarks.Selected;
        if (_teleporting || index < 0 || index > _entries.Count) return;

        // Index 0 is Home; the landmarks follow it.
        bool home = index == 0;
        InventoryEntry? target = home ? null : _entries[index - 1];
        // A landmark is never sent with an empty id: the protocol reads that as "teleport home",
        // which would send the resident somewhere they did not choose.
        if (target != null && target.AssetId == Guid.Empty) return;

        _teleporting = true;
        _teleport.Disabled = true;
        ShowStatus(L10n.Tr("ui.region_restart.teleporting"), UiTheme.SecondaryText);

        string failure = string.Empty;
        bool success = false;
        try
        {
            var result = home
                ? await _session.TeleportHomeAsync()
                : await _session.TeleportToLandmarkAsync(target!.AssetId);
            success = result.Success;
            failure = result.Message;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (!IsInstanceValid(this) || _closed) return;
        _teleporting = false;

        if (success)
        {
            Close(); // the region change would close it too; this just does not wait for it
            return;
        }

        _teleport.Disabled = false;
        ShowStatus(L10n.TrFormat("ui.region_restart.teleport_failed", failure), Urgent);
    }

    private void ShowStatus(string text, Color colour)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", colour);
        _status.Visible = true;
    }

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2),
            Mathf.Max(0, (viewportSize.Y - Size.Y) / 4));
    }

    /// <summary>Closes and frees the window. Also what the owner calls on a region change.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _countdown.Stop();
        Closed?.Invoke();
        QueueFree();
    }
}
