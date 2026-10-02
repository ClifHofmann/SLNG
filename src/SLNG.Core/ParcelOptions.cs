namespace SLNG.Core;

/// <summary>The yes/no settings the Land-Info "Options" and "Sound" tabs show for one parcel
/// (FEAT-LAND-02). Each member is the RAW state the sim sent, one bit per control, so a UI can
/// display it and later write it back unchanged. Where the reference viewer's checkbox shows a
/// derived value, the member says so; the sources are <c>LLPanelLandOptions::refresh</c>
/// (<c>llfloaterland.cpp</c>:1996) and <c>LLPanelLandAudio::refresh</c> (<c>llpanellandaudio.cpp</c>:108).
///
/// "Everyone" / "Group" pairs are two independent bits on the wire, and the viewer shows the
/// "Group" box ticked (and greyed) whenever "Everyone" is on, so a UI must display
/// <c>Group || Everyone</c> for the group half of each pair.</summary>
[Flags]
public enum ParcelOptions
{
    None = 0,

    /// <summary>"Fly: Everyone" (<c>PF_ALLOW_FLY</c>, <c>llparcelflags.h</c>:32).</summary>
    AllowFly = 1 << 0,

    /// <summary>"Build: Everyone" (<c>PF_CREATE_OBJECTS</c>, :39; <c>getAllowModify</c>).</summary>
    BuildEveryone = 1 << 1,

    /// <summary>"Build: Group" (<c>PF_CREATE_GROUP_OBJECTS</c>, :59). Shown ticked when
    /// <see cref="BuildEveryone"/> is (<c>llfloaterland.cpp</c>:2055).</summary>
    BuildGroup = 1 << 2,

    /// <summary>"Object Entry: Everyone" (<c>PF_ALLOW_ALL_OBJECT_ENTRY</c>, :60).</summary>
    ObjectEntryEveryone = 1 << 3,

    /// <summary>"Object Entry: Group" (<c>PF_ALLOW_GROUP_OBJECT_ENTRY</c>, :61). Shown ticked when
    /// <see cref="ObjectEntryEveryone"/> is (<c>llfloaterland.cpp</c>:2061).</summary>
    ObjectEntryGroup = 1 << 4,

    /// <summary>"Run Scripts: Everyone" (<c>PF_ALLOW_OTHER_SCRIPTS</c>, :33).</summary>
    ScriptsEveryone = 1 << 5,

    /// <summary>"Run Scripts: Group" (<c>PF_ALLOW_GROUP_SCRIPTS</c>, :58). Shown ticked when
    /// <see cref="ScriptsEveryone"/> is (<c>llfloaterland.cpp</c>:2070).</summary>
    ScriptsGroup = 1 << 6,

    /// <summary>Damage is allowed (<c>PF_ALLOW_DAMAGE</c>, :38). The tab's "Safe (no damage)" box is the
    /// INVERSE: it is ticked when this bit is CLEAR (<c>llfloaterland.cpp</c>:2064).</summary>
    AllowDamage = 1 << 7,

    /// <summary>"No Pushing" (<c>PF_RESTRICT_PUSHOBJECT</c>, :54). When <see cref="RegionPushOverride"/>
    /// is set the viewer shows it ticked regardless (<c>llfloaterland.cpp</c>:2076).</summary>
    RestrictPush = 1 << 8,

    /// <summary>The REGION forces "No Pushing"; the tab relabels the box "No Pushing (Region Override)"
    /// and shows it ticked (<c>llfloaterland.cpp</c>:2077). Comes from <c>RegionPushOverride</c> in the
    /// parcel message, not from the parcel flags.</summary>
    RegionPushOverride = 1 << 9,

    /// <summary>"Show Place in Search" (<c>PF_SHOW_DIRECTORY</c>, :45).</summary>
    ShowInSearch = 1 << 10,

    /// <summary>The parcel's own "Moderate Content" setting (<c>PF_MATURE_PUBLISH</c>, :51). The box
    /// only reflects it in a Moderate region: in a General region it shows unticked, in an Adult region
    /// ticked and relabelled "Adult Content" (<c>llfloaterland.cpp</c>:2148-2158), so display it from
    /// <see cref="ParcelInfo.Rating"/> plus this bit.</summary>
    MaturePublish = 1 << 11,

    /// <summary>"Avatars on other parcels can see and chat with avatars on this parcel" (<c>SeeAVs</c>,
    /// <c>llparcel.cpp</c>:572). Not a parcel flag: a boolean in the parcel message.</summary>
    SeeAvatars = 1 << 12,

    /// <summary>Sound tab "Restrict gesture and object sounds to this parcel" (<c>PF_SOUND_LOCAL</c>, :48).</summary>
    SoundLocal = 1 << 13,

    /// <summary>Sound tab "Enable Voice" (<c>PF_ALLOW_VOICE_CHAT</c>, :62). Only meaningful when the
    /// region allows voice at all; see <see cref="ParcelInfo.RegionVoiceEnabled"/>.</summary>
    AllowVoice = 1 << 14,

    /// <summary>Voice uses the estate-wide channel (<c>PF_USE_ESTATE_VOICE_CHAN</c>, :63). The tab's
    /// "Restrict Voice to this parcel" box is the INVERSE: ticked when this bit is CLEAR
    /// (<c>llpanellandaudio.cpp</c>:152).</summary>
    UseEstateVoiceChannel = 1 << 15,

    /// <summary>"Avatar Sounds: Everyone" (<c>AnyAVSounds</c>, <c>llparcel.cpp</c>:573). A boolean in
    /// the parcel message, not a parcel flag.</summary>
    AvatarSoundsEveryone = 1 << 16,

    /// <summary>"Avatar Sounds: Group" (<c>GroupAVSounds</c>, <c>llparcel.cpp</c>:574). Shown ticked
    /// when <see cref="AvatarSoundsEveryone"/> is (<c>llpanellandaudio.cpp</c>:161).</summary>
    AvatarSoundsGroup = 1 << 17,
}
