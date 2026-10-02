using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-LAND-02: the read-only "Sound" tab of the Land-Info window (Firestorm's About Land -> Sound).
/// Music URL, the gesture/object sound restriction, who may make avatar sounds, the voice settings and
/// the MOAP restriction. The voice rules (the estate override, the inverse "restrict to this parcel")
/// are <see cref="LandInfoFormat.VoiceStates"/>, after <c>LLPanelLandAudio::refresh</c>
/// (<c>llpanellandaudio.cpp</c>:108-166).
///
/// <para>"Restrict MOAP to this parcel" is a third state today: <see cref="ParcelInfo.ObscureMoap"/> is
/// always null (LibreMetaverse cannot read it), and null is drawn as "unknown", never as unticked.</para>
/// </summary>
public partial class LandSoundTab : ScrollContainer, ILandInfoTab
{
    private Control _musicUrl = null!;
    private LandReadOnlyCheck _soundLocal = null!;
    private LandReadOnlyCheck _avEveryone = null!;
    private LandReadOnlyCheck _avGroup = null!;
    private LandReadOnlyCheck _voiceEnable = null!;
    private LandReadOnlyCheck _voiceLocal = null!;
    private LandReadOnlyCheck _moap = null!;

    string ILandInfoTab.TabTitle => L10n.Tr("ui.land.tab_sound");

    public override void _Ready()
    {
        var column = LandTabPage.Prepare(this);

        var grid = LandTabPage.NewGrid(column);
        _musicUrl = LandTabPage.SelectableValue(); // a URL is something to copy
        LandTabPage.AddRow(grid, L10n.Tr("ui.land.snd_music"), _musicUrl);

        _soundLocal = new LandReadOnlyCheck(L10n.Tr("ui.land.snd_local"));
        LandTabPage.AddRow(grid, L10n.Tr("ui.land.snd_sounds"), _soundLocal);

        _avEveryone = new LandReadOnlyCheck(L10n.Tr("ui.land.opt_everyone"));
        _avGroup = new LandReadOnlyCheck(L10n.Tr("ui.land.opt_group"));
        LandTabPage.AddRow(grid, L10n.Tr("ui.land.snd_avatar"), LandTabPage.Pair(_avEveryone, _avGroup));

        _voiceEnable = new LandReadOnlyCheck(L10n.Tr("ui.land.snd_voice_enable"));
        _voiceLocal = new LandReadOnlyCheck(L10n.Tr("ui.land.snd_voice_local"));
        var voice = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        voice.AddThemeConstantOverride("separation", 4);
        voice.AddChild(_voiceEnable);
        voice.AddChild(_voiceLocal);
        LandTabPage.AddRow(grid, L10n.Tr("ui.land.snd_voice"), voice);

        _moap = new LandReadOnlyCheck(L10n.Tr("ui.land.snd_moap"));
        LandTabPage.AddRow(grid, L10n.Tr("ui.land.snd_media"), _moap);

        Clear();
    }

    /// <inheritdoc/>
    public void ShowParcel(ParcelInfo p)
    {
        var o = p.Options;
        LandTabPage.SetText(_musicUrl, LandInfoFormat.OrDash(p.MusicUrl));
        _soundLocal.Show(LandInfoFormat.Has(o, ParcelOptions.SoundLocal));
        _avEveryone.Show(LandInfoFormat.Has(o, ParcelOptions.AvatarSoundsEveryone));
        _avGroup.Show(LandInfoFormat.GroupTicked(o, ParcelOptions.AvatarSoundsEveryone, ParcelOptions.AvatarSoundsGroup));

        var (enable, restrict) = LandInfoFormat.VoiceStates(o, p.RegionVoiceEnabled);
        _voiceEnable.Show(enable);
        _voiceLocal.Show(restrict);

        _moap.Show(p.ObscureMoap); // null stays "unknown"
    }

    /// <inheritdoc/>
    public void RefreshNames() { } // nothing here is a name

    // --- for the selftest ----------------------------------------------------------------------------

    internal string MusicUrlText => LandTabPage.Text(_musicUrl);

    internal LandReadOnlyCheck SoundLocal => _soundLocal;

    internal LandReadOnlyCheck AvatarEveryone => _avEveryone;

    internal LandReadOnlyCheck AvatarGroup => _avGroup;

    internal LandReadOnlyCheck VoiceEnable => _voiceEnable;

    internal LandReadOnlyCheck VoiceLocal => _voiceLocal;

    internal LandReadOnlyCheck Moap => _moap;

    private void Clear()
    {
        LandTabPage.SetText(_musicUrl, LandInfoFormat.Unknown);
        foreach (var c in new[] { _soundLocal, _avEveryone, _avGroup, _voiceEnable, _voiceLocal })
            c.Show(false, false);
        _moap.Show(null);
    }
}
