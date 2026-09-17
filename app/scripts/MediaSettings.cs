using System;
using Godot;

namespace SLNG.App;

/// <summary>
/// MVP3-3: the global media toggle in the top bar (Firestorm parity — its own media/audio
/// icon cluster). Static and read directly from <c>ObjectRenderer</c>, the same way
/// <see cref="RenderConfig"/>'s toggles are, rather than threaded through a constructor --
/// unlike RenderConfig's diagnostic flags, this one IS a real, persisted user preference
/// (<c>preferences.cfg</c>, same <c>ConfigFile</c> pattern as <c>DofSettings</c>/
/// <c>CameraSettings</c>), so it needs its own Load/Persist rather than living in RenderConfig.
///
/// <para><see cref="AutoLoadEnabled"/> gates <c>ObjectRenderer.ApplyMediaImageAsync</c> outright:
/// a MOAP face's <c>AUTO_PLAY</c> flag is the CREATOR's declared intent, but MOAP is also a known
/// IP-disclosure vector (any face can point at a server the creator controls, which then logs
/// every visitor who loads it) -- this is the resident's own kill switch for that, matching real
/// SL's "Play Media" preference. Defaults to on: that is what every reference viewer ships, and
/// what MVP3-3 Phase 3 was itself confirmed against.</para>
///
/// <para><see cref="AudioMuted"/> has no audio source to affect yet -- SLNG plays no in-world
/// sound or media audio at all. Kept here, off by default, purely so the top bar can show the
/// same two-icon cluster Firestorm does without wiring a second settings class in later; nothing
/// currently reads it.</para>
/// </summary>
public static class MediaSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "media";

    public static bool AutoLoadEnabled { get; private set; } = true;
    public static bool AudioMuted { get; private set; } = false;

    public static event Action? Changed;

    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        AutoLoadEnabled = (bool)cfg.GetValue(Section, "auto_load_enabled", true);
        AudioMuted = (bool)cfg.GetValue(Section, "audio_muted", false);
    }

    public static void SetAutoLoadEnabled(bool value) => Persist("auto_load_enabled", value, () => AutoLoadEnabled = value);
    public static void SetAudioMuted(bool value) => Persist("audio_muted", value, () => AudioMuted = value);

    private static void Persist(string key, Variant value, Action assign)
    {
        assign();
        Changed?.Invoke();

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other settings classes
        cfg.SetValue(Section, key, value);
        cfg.Save(ConfigPath);
    }
}
