using Godot;
using SLNG.Net;

namespace SLNG.App;

/// <summary>
/// FEAT-SL-02: where a login profile keeps the grid's <c>mfa_hash</c> ("remember this computer") in
/// <c>logins.cfg</c> -- one extra key, <see cref="Key"/>, in the profile's own <c>[first last @ grid]</c>
/// section, next to <c>pass_hash</c>. Operates on a <see cref="ConfigFile"/> it is handed and never
/// reads or writes a path, so a test can run it on an in-memory file; saving is the caller's job.
///
/// <para><b>Compatibility.</b> A <c>logins.cfg</c> written before this existed has no such key: reading
/// it yields "" and the login is exactly what it was. A build older than this one never looks the key
/// up, so a file that carries it still loads and still logs in; the section is rewritten whole on the
/// next save, with the key preserved by Godot's ConfigFile like any other.</para>
///
/// <para><b>Protection.</b> The same as the password hash next to it, no more: plain text in the same
/// file, readable by whoever can read the user's profile folder. It is login-equivalent only for the
/// MFA step -- the account password is still needed -- and the grid, not the viewer, decides how long
/// it is good for. Neither it nor the code is logged, printed, or put in a notification or a
/// diagnostics line; nothing in this class writes anywhere but the ConfigFile it is given.</para>
/// </summary>
internal static class MfaHashStore
{
    /// <summary>The key inside a profile section.</summary>
    public const string Key = "mfa_hash";

    /// <summary>The stored hash for <paramref name="section"/>, or "" when there is none, the section
    /// does not exist, or the value is not one we would have written (wrong type, control characters,
    /// absurd length).</summary>
    public static string Read(ConfigFile config, string section)
    {
        if (string.IsNullOrEmpty(section) || !config.HasSection(section) || !config.HasSectionKey(section, Key))
            return "";
        var value = config.GetValue(section, Key, "");
        if (value.VariantType != Variant.Type.String) return "";
        return MfaLogin.StorableHashOrNull(value.AsString()) ?? "";
    }

    /// <summary>Carries out <paramref name="action"/> on <paramref name="section"/>. Returns true when
    /// the file's contents changed (so the caller knows whether a save is needed). Erasing from a
    /// section or key that does not exist is a no-op -- in Godot it would be an error.</summary>
    public static bool Apply(ConfigFile config, string section, MfaHashAction action, string? hash)
    {
        switch (action)
        {
            case MfaHashAction.Store:
                if (!MfaLogin.IsStorableHash(hash)) return false;
                if (config.HasSectionKey(section, Key) && config.GetValue(section, Key, "").AsString() == hash)
                    return false;
                config.SetValue(section, Key, hash!);
                return true;

            case MfaHashAction.Erase:
                if (string.IsNullOrEmpty(section) || !config.HasSection(section) || !config.HasSectionKey(section, Key))
                    return false;
                config.EraseSectionKey(section, Key);
                return true;

            default:
                return false;
        }
    }
}
