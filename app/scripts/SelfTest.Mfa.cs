using System;
using System.Collections.Generic;
using Godot;
using SLNG.Net;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// FEAT-SL-02: the authentication-code prompt builds, stays a sane size, accepts only six digits (spaces
    /// ignored), reports what was typed through <c>Submitted</c> and a cancel through <c>Cancelled</c>
    /// -- once, and never both -- and shows the "that code was refused" line and the "remember this
    /// computer" box exactly when asked. Pure UI: no session, no network, no file.
    /// </summary>
    private static Check CheckMfaPromptWindow(SceneTree tree)
    {
        const string Name = "mfa prompt window";
        var created = new List<UI.MfaPromptWindow>();
        try
        {
            UI.MfaPromptWindow Build(bool refused, bool remember, out List<string> events)
            {
                var log = new List<string>();
                var w = new UI.MfaPromptWindow();
                created.Add(w);
                tree.Root.AddChild(w);
                w.Submitted += (code, rem) => log.Add($"S:{code}:{rem}");
                w.Cancelled += () => log.Add("C");
                w.Initialize("The grid says: enter your code.", refused, remember);
                events = log;
                return w;
            }

            var failures = new List<string>();

            // Typing and pasting: six digits enable Login, anything else does not.
            var a = Build(refused: false, remember: true, out var aLog);
            if (a.LoginEnabled) failures.Add("Login enabled on an empty field");
            foreach (var (text, want) in new[] { ("12345", false), ("12345a", false), ("1234567", false), ("123 456", true), ("  000000 ", true) })
            {
                a.SetCodeText(text);
                if (a.LoginEnabled != want) failures.Add($"'{new string('*', text.Length)}' enabled={a.LoginEnabled}, wanted {want}");
            }
            if (a.ErrorVisible) failures.Add("refusal line shown on a first ask");
            if (!a.RememberOffered) failures.Add("remember box missing when offered");

            // A sane size at the default width (an autowrap label without a width floor is the usual cause).
            float needs = a.GetCombinedMinimumSize().Y;
            if (needs > MaxSaneWindowMinHeight) failures.Add($"needs {needs:0} px of height");

            // Confirm: normalised code and the remember choice, once; a second try does nothing.
            a.SetCodeText("123 456");
            a.SetRemember(true);
            a.Submit();
            a.Submit();
            a.Cancel();
            if (aLog.Count != 1 || aLog[0] != "S:123456:True") failures.Add($"confirm reported [{string.Join(",", aLog)}]");

            // An invalid field is refused without closing or reporting anything.
            var b = Build(refused: true, remember: false, out var bLog);
            if (!b.ErrorVisible) failures.Add("refusal line missing after a refused code");
            if (b.RememberOffered) failures.Add("remember box shown when not offered");
            b.SetCodeText("12");
            b.Submit();
            if (bLog.Count != 0 || b.IsQueuedForDeletion()) failures.Add("an invalid code was accepted or closed the window");

            // Not offered means false, whatever was set; then cancel.
            b.SetCodeText("654321");
            b.SetRemember(true);
            b.Submit();
            if (bLog.Count != 1 || bLog[0] != "S:654321:False") failures.Add($"no-remember confirm reported [{string.Join(",", bLog)}]");

            var c = Build(refused: false, remember: false, out var cLog);
            c.Cancel();
            c.SetCodeText("111111");
            c.Submit();
            if (cLog.Count != 1 || cLog[0] != "C") failures.Add($"cancel reported [{string.Join(",", cLog)}]");

            return failures.Count == 0
                ? new Check(Name, true, "six digits (spaces ignored) enable Login, Submit/Cancel fire once and never both, refusal line and remember box follow their flags")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var w in created)
                if (GodotObject.IsInstanceValid(w)) w.QueueFree();
        }
    }

    /// <summary>
    /// FEAT-SL-02: the profile's saved <c>mfa_hash</c> round-trips through logins.cfg's text form on an
    /// IN-MEMORY <see cref="ConfigFile"/> (never a path -- the real file holds the developer's password
    /// hashes), an old file without the key still reads as "no hash", a file with extra unknown keys is
    /// undisturbed, erasing a missing key is a no-op, and a value that is not one we would have written
    /// reads as none.
    /// </summary>
    private static Check CheckMfaHashStore()
    {
        const string Name = "mfa hash store";
        const string Section = "Test Resident @ https://grid.invalid/login.cgi";
        const string Fake = "FAKE-HASH-0123";
        try
        {
            var failures = new List<string>();

            // A file written before this feature existed.
            const string Old = "[Test Resident @ https://grid.invalid/login.cgi]\n\ngrid=\"https://grid.invalid/login.cgi\"\nfirst=\"Test\"\nlast=\"Resident\"\npass_hash=\"$1$00000000000000000000000000000000\"\n\n[Settings]\n\nlast_profile=\"x\"\n";
            var old = new ConfigFile();
            if (old.Parse(Old) != Error.Ok) failures.Add("old file did not parse");
            if (MfaHashStore.Read(old, Section) != "") failures.Add("old file reads a hash");
            if (MfaHashStore.Read(old, "Missing") != "") failures.Add("missing section reads a hash");

            // Store, serialise, parse again: the hash survives and nothing else in the profile moved.
            if (!MfaHashStore.Apply(old, Section, MfaHashAction.Store, Fake)) failures.Add("store reported no change");
            if (MfaHashStore.Apply(old, Section, MfaHashAction.Store, Fake)) failures.Add("storing the same value again reported a change");
            string text = old.EncodeToText();
            var again = new ConfigFile();
            if (again.Parse(text) != Error.Ok) failures.Add("written file did not parse");
            if (MfaHashStore.Read(again, Section) != Fake) failures.Add("hash did not survive the round trip");
            if (again.GetValue(Section, "pass_hash", "").AsString() != "$1$00000000000000000000000000000000") failures.Add("password hash disturbed");
            if (again.GetValue(Section, "first", "").AsString() != "Test") failures.Add("profile name disturbed");

            // An older build ignores the key it does not know; a newer file with extra keys reads fine here.
            var extra = new ConfigFile();
            extra.Parse(text.Replace("first=", "future_key=\"whatever\"\nfirst="));
            if (MfaHashStore.Read(extra, Section) != Fake) failures.Add("unknown key broke reading");

            // A value we would not have written reads as none.
            var odd = new ConfigFile();
            odd.SetValue(Section, MfaHashStore.Key, 42);
            if (MfaHashStore.Read(odd, Section) != "") failures.Add("a non-string value read as a hash");
            odd.SetValue(Section, MfaHashStore.Key, "bad\nvalue");
            if (MfaHashStore.Read(odd, Section) != "") failures.Add("a value with a line break read as a hash");
            if (MfaHashStore.Apply(odd, Section, MfaHashAction.Store, "bad\nvalue")) failures.Add("a value with a line break was stored");

            // Erase: removes it; again, and on a section that never existed, is a harmless no-op.
            if (!MfaHashStore.Apply(again, Section, MfaHashAction.Erase, null)) failures.Add("erase reported no change");
            if (MfaHashStore.Read(again, Section) != "") failures.Add("erase left the hash");
            if (MfaHashStore.Apply(again, Section, MfaHashAction.Erase, null)) failures.Add("second erase reported a change");
            if (MfaHashStore.Apply(again, "Never existed", MfaHashAction.Erase, null)) failures.Add("erase on a missing section reported a change");
            if (!again.HasSectionKey(Section, "pass_hash")) failures.Add("erase took the password hash with it");

            // Keep leaves everything.
            if (MfaHashStore.Apply(again, Section, MfaHashAction.Keep, Fake)) failures.Add("keep changed something");

            return failures.Count == 0
                ? new Check(Name, true, "old file reads as none; store/read/erase round-trip in memory; unknown keys and odd values tolerated; the real logins.cfg is never opened")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
