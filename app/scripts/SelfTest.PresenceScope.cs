using System;
using System.IO;
using Godot;
using SLNG.Core.Presence;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// BUG-UI-43: Away &amp; auto-response is the setup of one login. Drives the real controller and the real Godot
    /// <c>ConfigFile</c> adapter against a throwaway file (never the maintainer's <c>preferences.cfg</c>): two avatars on one
    /// grid keep their own texts and switches, the second one is not shown the first one's values, the old shared section is
    /// copied once and never written again, the same avatar on another grid is another tenant, and a controller with no login
    /// writes nothing. The rules themselves are unit-tested in <c>PresenceSettingsStoreTests</c>.
    /// </summary>
    private static Check CheckPresencePerAccount()
    {
        const string Name = "presence per account";
        const string A = "11111111-1111-1111-1111-111111111111";
        const string B = "22222222-2222-2222-2222-222222222222";
        string temp = Path.Combine(Path.GetTempPath(), "slng-selftest-presence-" + Guid.NewGuid().ToString("N") + ".cfg");
        string savedPath = PresenceSettings.ConfigPath;
        var presence = new PresenceController();
        try
        {
            var failures = new System.Collections.Generic.List<string>();
            PresenceSettings.ConfigPath = temp;

            var legacy = new ConfigFile();
            legacy.SetValue("presence", "away_message", "shared text");
            legacy.SetValue("presence", "autorespond", true);
            legacy.Save(temp);

            int accountChanges = 0;
            presence.AccountChanged += () => accountChanges++;

            presence.BindAccount("agni", A);
            if (presence.Config.AwayMessage != "shared text" || !presence.Config.Autorespond)
                failures.Add($"first login of A was not seeded from the shared section ({presence.Config.AwayMessage})");
            presence.SetTexts("A away", "A busy", "A auto", "A strangers");
            presence.SetAutorespond(false);
            presence.SetTimeoutSeconds(1800);

            presence.Detach();
            if (presence.Config.AwayMessage != PresenceConfig.DefaultAwayMessage || presence.AwayTimeoutSeconds != PresenceSettings.DefaultAwayTimeoutSeconds)
                failures.Add("Detach left the previous avatar's setup in memory");

            presence.BindAccount("agni", B);
            if (presence.Config.AwayMessage != "shared text")
                failures.Add($"B was shown {presence.Config.AwayMessage} instead of the shared seed");
            if (presence.AwayTimeoutSeconds != PresenceSettings.DefaultAwayTimeoutSeconds)
                failures.Add($"B got A's timeout {presence.AwayTimeoutSeconds}");
            presence.SetTexts("B away", "B busy", "B auto", "B strangers");

            presence.BindAccount("agni", A); // a re-login without restarting the viewer
            if (presence.Config.AwayMessage != "A away" || presence.Config.Autorespond || presence.AwayTimeoutSeconds != 1800)
                failures.Add($"A did not get its own setup back ({presence.Config.AwayMessage}, {presence.AwayTimeoutSeconds} s)");

            presence.BindAccount("hg.osgrid.org", A);
            if (presence.Config.AwayMessage != "shared text")
                failures.Add($"the same avatar on another grid was shown {presence.Config.AwayMessage}");

            presence.Detach();
            presence.SetAutorespondNonFriends(true); // no login: applied in memory, written nowhere
            presence.SetTexts("nobody away", "nobody busy", "nobody auto", "nobody strangers");

            var file = new ConfigFile();
            file.Load(temp);
            if ((string)file.GetValue("presence", "away_message", "") != "shared text")
                failures.Add("the shared section was written");
            if (file.GetSections().Length != 4) // presence, agni/A, agni/B, osgrid/A
                failures.Add($"unexpected sections: {string.Join(", ", file.GetSections())}");
            if ((string)file.GetValue($"presence_agni_{A}", "away_message", "") != "A away")
                failures.Add("A's section does not hold A's text");
            if ((string)file.GetValue($"presence_agni_{B}", "away_message", "") != "B away")
                failures.Add("B's section does not hold B's text");
            if (accountChanges < 5) failures.Add($"AccountChanged fired {accountChanges} times");

            return failures.Count == 0
                ? new Check(Name, true, "two avatars and a second grid keep separate setups, the shared section is read once and never written")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PresenceSettings.ConfigPath = savedPath;
            presence.Free();
            try { File.Delete(temp); } catch (IOException) { }
        }
    }
}
