using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SLNG.Core.Landmarks;
using SLNG.Core.Login;
using SLNG.Net;

namespace SLNG.App;

public static partial class SelfTest
{
    private const string LoginStartSection = "Test Resident @ https://grid.invalid/login.cgi";

    private static LoginFavorite LoginStartFavorite(string name, string region, int x, int y, int z)
    {
        StartTarget.TryCreate(region, x, y, z, StartTarget.OpenMaxXY, out var target);
        return new LoginFavorite(Guid.NewGuid(), Guid.NewGuid(), name, target!);
    }

    /// <summary>
    /// FEAT-UI-72: the profile's "Start at" choice and favorites cache round-trip through logins.cfg's text form on an IN-MEMORY
    /// <see cref="ConfigFile"/> (never a path -- the real file holds the developer's password hashes); an old file without the
    /// keys reads as "last / never populated"; erasing removes both and is a no-op on a missing section or key; and values that
    /// are not ones we would have written (wrong Variant type, a megabyte of text, not JSON) read as last / empty.
    /// </summary>
    private static Check CheckLoginStartStore()
    {
        const string Name = "login start store";
        try
        {
            var failures = new List<string>();

            // A file written before this feature existed.
            const string Old = "[Test Resident @ https://grid.invalid/login.cgi]\n\ngrid=\"https://grid.invalid/login.cgi\"\nfirst=\"Test\"\nlast=\"Resident\"\npass_hash=\"$1$00000000000000000000000000000000\"\n\n[Settings]\n\nlast_profile=\"x\"\n";
            var cfg = new ConfigFile();
            if (cfg.Parse(Old) != Error.Ok) failures.Add("old file did not parse");
            if (LoginStartStore.ReadChoice(cfg, LoginStartSection) != StartChoice.Last) failures.Add("old file does not read as last");
            if (LoginStartStore.FavoritesPopulated(cfg, LoginStartSection)) failures.Add("old file reads as populated");
            if (LoginStartStore.ReadFavorites(cfg, LoginStartSection).Count != 0) failures.Add("old file has favorites");
            if (LoginStartStore.ReadChoice(cfg, "Missing") != StartChoice.Last) failures.Add("missing section does not read as last");

            // Choice: every kind survives serialisation to text and back.
            var fav = LoginStartFavorite("Puris Town", "Ahern", 128, 64, 30);
            var other = LoginStartFavorite("Café \"Sunset\"", "Sunset Beach", 10, 20, 5);
            foreach (var choice in new[] { StartChoice.Home, StartChoice.Favorite(fav.AssetId), StartChoice.Region("Ahern/1/2/3"), StartChoice.Last })
            {
                LoginStartStore.WriteChoice(cfg, LoginStartSection, choice);
                var again = new ConfigFile();
                if (again.Parse(cfg.EncodeToText()) != Error.Ok) { failures.Add($"{choice.Serialize()}: written file did not parse"); continue; }
                if (LoginStartStore.ReadChoice(again, LoginStartSection) != choice) failures.Add($"{choice.Serialize()}: did not survive the round trip");
            }
            if (LoginStartStore.WriteChoice(cfg, LoginStartSection, StartChoice.Last)) failures.Add("writing the same choice again reported a change");

            // Favorites: names, regions and positions survive; the profile's other keys are undisturbed; "populated" follows the key.
            var list = new[] { fav, other };
            if (!LoginStartStore.WriteFavorites(cfg, LoginStartSection, list)) failures.Add("writing favorites reported no change");
            if (LoginStartStore.WriteFavorites(cfg, LoginStartSection, list)) failures.Add("writing the same favorites again reported a change");
            var back = new ConfigFile();
            if (back.Parse(cfg.EncodeToText()) != Error.Ok) failures.Add("written file did not parse");
            var read = LoginStartStore.ReadFavorites(back, LoginStartSection);
            if (!read.SequenceEqual(list)) failures.Add($"favorites did not survive the round trip ({read.Count} of 2)");
            if (!LoginStartStore.FavoritesPopulated(back, LoginStartSection)) failures.Add("populated key not seen");
            if (back.GetValue(LoginStartSection, "pass_hash", "").AsString() != "$1$00000000000000000000000000000000") failures.Add("password hash disturbed");
            if (back.GetValue(LoginStartSection, "first", "").AsString() != "Test") failures.Add("profile name disturbed");

            // A confirmed EMPTY folder is recorded too: the key is there, the list is empty.
            var empty = new ConfigFile();
            LoginStartStore.WriteFavorites(empty, LoginStartSection, Array.Empty<LoginFavorite>());
            if (!LoginStartStore.FavoritesPopulated(empty, LoginStartSection)) failures.Add("an empty list is not recorded as populated");
            if (LoginStartStore.ReadFavorites(empty, LoginStartSection).Count != 0) failures.Add("an empty list read back with entries");

            // Erase: both keys go, the rest stays; again, and on a section that never existed, is a harmless no-op.
            if (!LoginStartStore.Erase(back, LoginStartSection)) failures.Add("erase reported no change");
            if (back.HasSectionKey(LoginStartSection, LoginStartStore.ChoiceKey) || back.HasSectionKey(LoginStartSection, LoginStartStore.FavoritesKey))
                failures.Add("erase left a key behind");
            if (!back.HasSectionKey(LoginStartSection, "pass_hash")) failures.Add("erase took the password hash with it");
            if (LoginStartStore.Erase(back, LoginStartSection)) failures.Add("second erase reported a change");
            if (LoginStartStore.Erase(back, "Never existed")) failures.Add("erase on a missing section reported a change");
            if (LoginStartStore.Erase(back, "")) failures.Add("erase on an empty section name reported a change");

            // Hostile values: reading never throws and never believes them.
            var odd = new ConfigFile();
            odd.SetValue(LoginStartSection, LoginStartStore.ChoiceKey, 42);
            odd.SetValue(LoginStartSection, LoginStartStore.FavoritesKey, 42);
            if (LoginStartStore.ReadChoice(odd, LoginStartSection) != StartChoice.Last) failures.Add("a non-string choice did not read as last");
            if (LoginStartStore.ReadFavorites(odd, LoginStartSection).Count != 0) failures.Add("a non-string favorites value read as entries");
            odd.SetValue(LoginStartSection, LoginStartStore.ChoiceKey, "region:" + new string('a', 1_000_000));
            odd.SetValue(LoginStartSection, LoginStartStore.FavoritesKey, new string('x', 1_000_000));
            if (LoginStartStore.ReadChoice(odd, LoginStartSection) != StartChoice.Last) failures.Add("a megabyte choice did not read as last");
            if (LoginStartStore.ReadFavorites(odd, LoginStartSection).Count != 0) failures.Add("a megabyte favorites value read as entries");
            odd.SetValue(LoginStartSection, LoginStartStore.FavoritesKey, "not json");
            odd.SetValue(LoginStartSection, LoginStartStore.ChoiceKey, "fav:not-a-guid");
            if (LoginStartStore.ReadFavorites(odd, LoginStartSection).Count != 0) failures.Add("non-JSON favorites read as entries");
            if (LoginStartStore.ReadChoice(odd, LoginStartSection) != StartChoice.Last) failures.Add("a bad favorite id did not read as last");

            return failures.Count == 0
                ? new Check(Name, true, "old file reads as last/unpopulated; choice and favorites round-trip in memory; erase and hostile values handled; the real logins.cfg is never opened")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// FEAT-UI-72: the "Start at" control. Options are Last, Home, a Favorites heading, the favorites, a separator and Region...;
    /// a favorite resolves to its <c>uri:</c> string; a typed region is valid or not (and an invalid one shows the red line and
    /// reports <c>IsValid</c> false, which is what keeps Login disabled); a remembered favorite that is not in the list shows My
    /// last location; a profile with no cache yet shows the disabled "after your next login" line. Pure UI: no file, no network.
    /// </summary>
    private static Check CheckLoginStartPicker(SceneTree tree)
    {
        const string Name = "login start picker";
        UI.LoginStartPicker? picker = null;
        try
        {
            var failures = new List<string>();
            var first = LoginStartFavorite("Puris Town", "Ahern", 128, 64, 30);
            var second = LoginStartFavorite("Sunset Beach", "Sunset", 10, 20, 5);

            picker = new UI.LoginStartPicker();
            tree.Root.AddChild(picker);
            int changes = 0, submits = 0;
            picker.ChoiceChanged += () => changes++;
            picker.Submitted += () => submits++;

            // The default, before anything is set.
            if (picker.Choice != StartChoice.Last || !picker.IsValid) failures.Add("the default is not a valid My last location");
            if (picker.OptionCount != 4) failures.Add($"default has {picker.OptionCount} items, wanted Last, Home, separator, Region...");

            // Two favorites: Last, Home, heading, fav 1, fav 2, separator, Region...
            picker.SetFavorites(new[] { first, second }, neverPopulated: false);
            if (picker.OptionCount != 7) failures.Add($"{picker.OptionCount} items with two favorites, wanted 7");
            else
            {
                if (!picker.OptionIsSeparator(2)) failures.Add("item 2 is not the Favorites heading");
                if (picker.OptionText(3) != "Puris Town" || picker.OptionText(4) != "Sunset Beach") failures.Add("favorites are not items 3 and 4 in order");
                if (!picker.OptionIsSeparator(5)) failures.Add("item 5 is not a separator");
                if (picker.OptionIsSeparator(0) || picker.OptionIsSeparator(1) || picker.OptionIsSeparator(3) || picker.OptionIsSeparator(6))
                    failures.Add("a selectable item is a separator");
            }

            // Picking a favorite resolves to its uri:.
            int before = changes;
            picker.PickOption(4);
            var r = picker.Resolve(StartTarget.LindenMaxXY);
            if (r.LoginString != "uri:Sunset&10&20&5") failures.Add($"favorite 2 resolved to '{r.LoginString}'");
            if (picker.SelectedFavorite?.AssetId != second.AssetId) failures.Add("SelectedFavorite is not favorite 2");
            if (changes == before) failures.Add("picking a favorite did not raise ChoiceChanged");

            picker.PickOption(1);
            if (picker.Resolve(StartTarget.LindenMaxXY).LoginString != "home") failures.Add("My home did not resolve to 'home'");
            picker.PickOption(0);
            if (picker.Resolve(StartTarget.LindenMaxXY).LoginString != "last") failures.Add("My last location did not resolve to 'last'");

            // Region...: empty is invalid but quiet; a valid location is accepted; nonsense is invalid and says so.
            picker.PickOption(6);
            if (!picker.RegionFieldVisible) failures.Add("the region field is hidden while Region... is picked");
            if (picker.IsValid) failures.Add("an empty region counts as valid");
            if (picker.HintVisible) failures.Add("the red line shows before anything was typed");
            picker.TypeRegion("Ahern/1/2/3");
            if (!picker.IsValid) failures.Add("'Ahern/1/2/3' is not valid");
            if (picker.HintVisible) failures.Add("the red line shows for a valid location");
            if (picker.Resolve(StartTarget.LindenMaxXY).LoginString != "uri:Ahern&1&2&3") failures.Add("a typed region did not resolve to its uri:");
            picker.TypeRegion("nope/x");
            if (picker.IsValid) failures.Add("'nope/x' is valid");
            if (!picker.HintVisible) failures.Add("no red line for an invalid location");
            picker.TypeRegion("Ahern/300/1/1");
            picker.RegionMaxXY = StartTarget.LindenMaxXY;
            if (picker.IsValid) failures.Add("x=300 is valid on a Linden grid");
            picker.RegionMaxXY = StartTarget.OpenMaxXY;
            if (!picker.IsValid || picker.HintVisible) failures.Add("x=300 is not valid on an open grid");
            picker.RegionMaxXY = StartTarget.LindenMaxXY;

            // Typed text survives a trip to another item, and Enter in the field is reported.
            picker.TypeRegion("Ahern/1/2/3");
            picker.PickOption(1);
            if (picker.RegionFieldVisible) failures.Add("the region field stays visible after leaving Region...");
            if (!picker.IsValid) failures.Add("My home is not valid after an invalid region was typed");
            picker.PickOption(6);
            if (picker.Choice != StartChoice.Region("Ahern/1/2/3")) failures.Add("the typed text was lost on the way back to Region...");
            picker.PressEnterInRegionField();
            if (submits != 1) failures.Add($"Enter reported {submits} times");

            // A remembered favorite that is not in the list shows My last location; one that is, is selected.
            picker.SetChoice(StartChoice.Favorite(Guid.NewGuid()));
            if (picker.Choice != StartChoice.Last || picker.SelectedOption != 0) failures.Add("a missing favorite does not show My last location");
            if (picker.FallbackReason != StartFallback.FavoriteMissing) failures.Add("a missing favorite is not reported as such");
            picker.SetChoice(StartChoice.Favorite(first.AssetId));
            if (picker.SelectedOption != 3 || picker.FallbackReason != StartFallback.None) failures.Add("a present favorite is not selected");

            // A new list without the picked favorite drops it quietly to Last.
            picker.SetFavorites(new[] { second }, neverPopulated: false);
            if (picker.Choice != StartChoice.Last) failures.Add("a favorite that left the list is still picked");

            // A profile whose cache was never written: one disabled line instead of the favorites.
            picker.SetFavorites(Array.Empty<LoginFavorite>(), neverPopulated: true);
            if (picker.OptionCount != 5) failures.Add($"{picker.OptionCount} items for a never-populated profile, wanted 5");
            else if (!picker.OptionIsDisabled(2)) failures.Add("the 'favorites appear after your next login' line is selectable");

            // An unknown account: Last, Home, Region... only.
            picker.SetFavorites(Array.Empty<LoginFavorite>(), neverPopulated: false);
            if (picker.OptionCount != 4) failures.Add($"{picker.OptionCount} items for an unknown account, wanted 4");

            // The row stays a sane size at its narrowest: it must not widen the login panel for a long favorite name.
            picker.SetFavorites(new[] { LoginStartFavorite(new string('W', 200), "Ahern", 1, 2, 3) }, neverPopulated: false);
            picker.PickOption(3);
            float width = picker.GetCombinedMinimumSize().X;
            if (width > 360f) failures.Add($"a long favorite name makes the row {width:0} px wide");

            return failures.Count == 0
                ? new Check(Name, true, "options, favorite and typed-region resolution, invalid-region hint, missing-favorite fallback, pending line and row width behave")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (picker != null && GodotObject.IsInstanceValid(picker)) picker.QueueFree();
        }
    }

    /// <summary>A scripted stand-in for a session: the folder (as the grid answers and as the local store holds it) and what each
    /// landmark resolves to.</summary>
    private sealed class FakeLoginStartSource : ILoginStartSource
    {
        private readonly object _gate = new();
        public FavoritesSnapshot Remote = FavoritesSnapshot.Unconfirmed;
        public FavoritesSnapshot Local = FavoritesSnapshot.Unconfirmed;
        public readonly Dictionary<Guid, StartTarget> Targets = new();
        public readonly HashSet<Guid> OfflineRegions = new();   // landmarks whose region does not answer
        public readonly List<Guid> Looked = new();

        public Task<FavoritesSnapshot> GetFavoritesAsync(CancellationToken ct)
        {
            lock (_gate) return Task.FromResult(Remote);
        }

        public FavoritesSnapshot ReadFavoritesFromStore()
        {
            lock (_gate) return Local;
        }

        public Task<LandmarkTargetLookup> LookupLandmarkTargetAsync(Guid landmarkAssetId, CancellationToken ct)
        {
            lock (_gate)
            {
                Looked.Add(landmarkAssetId);
                if (OfflineRegions.Contains(landmarkAssetId))
                    return Task.FromResult(new LandmarkTargetLookup(null, "selftest: region down", RegionOffline: true));
                return Task.FromResult(Targets.TryGetValue(landmarkAssetId, out var t)
                    ? new LandmarkTargetLookup(t, "")
                    : new LandmarkTargetLookup(null, "selftest: no such landmark"));
            }
        }

        public int LookedCount() { lock (_gate) return Looked.Count; }
    }

    /// <summary>FEAT-UI-72: the flush before a logout resolves the landmarks nobody has resolved yet, so a favorite added shortly
    /// before logging out still reaches the login list; one that cannot be resolved is left out.</summary>
    private static Check CheckLoginStartFlushResolves()
    {
        const string Name = "login start flush resolves new favorites";
        try
        {
            var known = Guid.NewGuid(); var fresh = Guid.NewGuid(); var broken = Guid.NewGuid();
            LandmarkInventoryItem Item(string name, Guid asset, int age) =>
                new(Guid.NewGuid(), Guid.NewGuid(), asset, name, "Favorites", "Favorites", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-age));

            StartTarget.TryCreate("Ahern", 1, 2, 3, StartTarget.OpenMaxXY, out var ahern);
            StartTarget.TryCreate("Sunset", 4, 5, 6, StartTarget.OpenMaxXY, out var sunset);

            var source = new FakeLoginStartSource();
            source.Local = new FavoritesSnapshot(true, new[] { Item("Known", known, 3), Item("Fresh", fresh, 1), Item("Broken", broken, 2) });
            source.Targets[fresh] = sunset!;

            var cached = new[] { new LoginFavorite(known, Guid.Empty, "Known", ahern!) };
            using var favorites = new LoginStartFavorites(source, cached, populated: true);

            var update = favorites.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var failures = new List<string>();
            if (update == null) return new Check(Name, false, "the flush published nothing");

            var names = update.Favorites.Select(f => f.Name).ToArray();
            if (!names.SequenceEqual(new[] { "Fresh", "Known" }))
                failures.Add("flush published [" + string.Join(", ", names) + "], wanted [Fresh, Known]");
            if (source.LookedCount() != 2) failures.Add($"{source.LookedCount()} lookups, wanted 2 (the new one and the broken one; the known one is never asked again)");

            return failures.Count == 0
                ? new Check(Name, true, "a new favorite is resolved at flush time, a known one is not asked again, an unresolvable one is left out")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// FEAT-UI-77: a favorite whose region does not answer is published as offline (not dropped, not retried at once), the bar is
    /// told, it is asked again once the recheck time has passed, and when the region is back it becomes a normal favorite. The
    /// login picker lists an offline favorite as a disabled line.
    /// </summary>
    private static Check CheckLoginStartOfflineRegions()
    {
        const string Name = "login start offline regions";
        var fast = new LoginStartFavorites.Timing(
            Debounce: TimeSpan.FromMilliseconds(20), ResolveSpacing: TimeSpan.FromMilliseconds(2),
            Reread: TimeSpan.FromHours(1), PublishEvery: TimeSpan.FromMilliseconds(5), OfflineRecheck: TimeSpan.FromMilliseconds(150));

        static LandmarkInventoryItem Item(string name, Guid asset, int ageDays) =>
            new(Guid.NewGuid(), Guid.NewGuid(), asset, name, "Favorites", "Favorites", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-ageDays));

        static bool WaitFor(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                if (done()) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        SLNG.App.UI.LoginStartPicker? picker = null;
        try
        {
            var failures = new List<string>();
            var up = Guid.NewGuid(); var down = Guid.NewGuid();
            var itemUp = Item("Up", up, 1); var itemDown = Item("Down", down, 2);
            StartTarget.TryCreate("Ahern", 1, 2, 3, StartTarget.OpenMaxXY, out var ahern);
            StartTarget.TryCreate("Sunset", 4, 5, 6, StartTarget.OpenMaxXY, out var sunset);

            var source = new FakeLoginStartSource { Remote = new FavoritesSnapshot(true, new[] { itemUp, itemDown }) };
            source.Local = source.Remote;
            source.Targets[up] = ahern!;
            source.OfflineRegions.Add(down);

            using var resolver = new LoginStartFavorites(source, Array.Empty<LoginFavorite>(), populated: false, fast);
            resolver.Poke();

            LoginStartFavoritesUpdate? latest = null;
            bool published = WaitFor(() =>
            {
                if (resolver.TryTakeUpdate(out var u)) latest = u;
                return latest != null && latest.Favorites.Count == 1 && latest.Offline.Count == 1;
            });
            if (!published) return new Check(Name, false, $"the resolver did not publish one favorite and one offline (got {latest?.Favorites.Count}/{latest?.Offline.Count})");
            if (latest!.Favorites[0].Name != "Up") failures.Add("the resolved favorite is not 'Up'");
            if (latest.Offline[0].Name != "Down") failures.Add("the offline favorite is not 'Down'");

            if (!resolver.TryTakeOfflineChange(out var offlineSet) || !offlineSet.Contains(down) || offlineSet.Count != 1)
                failures.Add("the bar was not told that 'Down' is offline");
            if (resolver.TryTakeOfflineChange(out _)) failures.Add("the bar was told twice about the same change");

            // Within the recheck time a repeat pass does not ask again; after it, it does.
            int looked = source.LookedCount();
            resolver.Poke();
            Thread.Sleep(80);
            if (source.LookedCount() != looked) failures.Add("an offline region was asked again before the recheck time");
            Thread.Sleep(200);
            resolver.Poke();
            if (!WaitFor(() => source.LookedCount() > looked)) failures.Add("an offline region was never asked again after the recheck time");

            // The region comes back: a normal favorite, no longer offline, and the bar is told.
            source.OfflineRegions.Remove(down);
            source.Targets[down] = sunset!;
            Thread.Sleep(200);
            resolver.Poke();
            LoginStartFavoritesUpdate? back = null;
            bool recovered = WaitFor(() =>
            {
                if (resolver.TryTakeUpdate(out var u)) back = u;
                return back != null && back.Favorites.Count == 2 && back.Offline.Count == 0;
            });
            if (!recovered) failures.Add("a region that came back did not become a normal favorite");
            if (!WaitFor(() => resolver.TryTakeOfflineChange(out var now) && now.Count == 0)) failures.Add("the bar was not told that the region is back");

            // The login picker: an offline favorite is a disabled line between the favorites and "Region...".
            picker = new SLNG.App.UI.LoginStartPicker();
            var fav = LoginStartFavorite("Up", "Ahern", 1, 2, 3);
            var gone = new LoginOfflineFavorite(Guid.NewGuid(), Guid.NewGuid(), "Down");
            var lp = picker;
            lp.SetFavorites(new[] { fav }, neverPopulated: false, offline: new[] { gone });
            int offlineLine = -1;
            for (int i = 0; i < lp.OptionCount; i++)
                if (!lp.OptionIsSeparator(i) && lp.OptionText(i).Contains("Down", StringComparison.Ordinal)) offlineLine = i;
            if (offlineLine < 0) failures.Add("the picker does not list the offline favorite");
            else if (!lp.OptionIsDisabled(offlineLine)) failures.Add("the offline favorite can be picked");

            lp.SetFavorites(Array.Empty<LoginFavorite>(), neverPopulated: true, offline: new[] { gone });
            bool pendingLine = false;
            for (int i = 0; i < lp.OptionCount; i++)
                if (!lp.OptionIsSeparator(i) && lp.OptionIsDisabled(i) && !lp.OptionText(i).Contains("Down", StringComparison.Ordinal)) pendingLine = true;
            if (pendingLine) failures.Add("the 'favorites appear after the next login' line shows although an offline favorite is known");

            return failures.Count == 0
                ? new Check(Name, true, "offline favorites are published and flagged, asked again after the recheck time, recovered when the region is back, and shown disabled on the login screen")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (picker != null && GodotObject.IsInstanceValid(picker)) picker.QueueFree();
        }
    }

    /// <summary>
    /// FEAT-UI-72: the background resolver behind the login screen's favorites. An unconfirmed folder read never publishes (a refused
    /// fetch reads as an empty folder); landmarks are resolved once and what cannot be resolved is left out; a repeat pass resolves
    /// nothing again; a confirmed empty folder is recorded; a removed favorite leaves the list; the final flush follows the local
    /// store without asking anything. Timings are shortened to milliseconds; no session, no file, no network.
    /// </summary>
    private static Check CheckLoginStartResolver()
    {
        const string Name = "login start favorites resolver";
        var fast = new LoginStartFavorites.Timing(
            Debounce: TimeSpan.FromMilliseconds(20), ResolveSpacing: TimeSpan.FromMilliseconds(2),
            Reread: TimeSpan.FromHours(1), PublishEvery: TimeSpan.FromMilliseconds(5));

        static LandmarkInventoryItem Item(string name, Guid asset, int ageDays) =>
            new(Guid.NewGuid(), Guid.NewGuid(), asset, name, "Favorites", "Favorites", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-ageDays));

        // Waits for the worker; the checks are about WHAT it publishes, so each poll is bounded and loud on timeout.
        static bool WaitFor(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                if (done()) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        try
        {
            var failures = new List<string>();
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
            var itemA = Item("A", a, 3); var itemB = Item("B", b, 2); var itemC = Item("C", c, 1);

            var source = new FakeLoginStartSource();
            StartTarget.TryCreate("Ahern", 1, 2, 3, StartTarget.OpenMaxXY, out var ahern);
            StartTarget.TryCreate("Sunset", 4, 5, 6, StartTarget.OpenMaxXY, out var sunset);
            source.Targets[a] = ahern!;
            source.Targets[b] = sunset!;
            // c has no target: it cannot be resolved.

            // 1. An unconfirmed read changes nothing, however empty it looks.
            source.Remote = FavoritesSnapshot.Unconfirmed;
            using (var resolver = new LoginStartFavorites(source, Array.Empty<LoginFavorite>(), populated: true, fast))
            {
                resolver.Poke();
                Thread.Sleep(150);
                if (resolver.TryTakeUpdate(out _)) failures.Add("an unconfirmed read published something");
                if (source.LookedCount() != 0) failures.Add("an unconfirmed read resolved landmarks");
            }

            // 2. A confirmed folder of three: A and B resolve, C is left out; newest first (the viewer's default order).
            source.Remote = new FavoritesSnapshot(true, new[] { itemA, itemB, itemC });
            source.Local = source.Remote;
            using (var resolver = new LoginStartFavorites(source, Array.Empty<LoginFavorite>(), populated: false, fast))
            {
                resolver.Poke();
                LoginStartFavoritesUpdate? latest = null;
                bool finished = WaitFor(() =>
                {
                    if (resolver.TryTakeUpdate(out var u)) latest = u;
                    return latest != null && latest.Favorites.Count == 2;
                });
                if (!finished) failures.Add($"the resolver did not publish two favorites (got {latest?.Favorites.Count.ToString() ?? "nothing"})");
                else
                {
                    var names = latest!.Favorites.Select(f => f.Name).ToArray();
                    if (!names.SequenceEqual(new[] { "B", "A" })) failures.Add($"order/entries were [{string.Join(",", names)}], wanted [B,A]");
                    if (latest.Favorites.Any(f => f.Name == "C")) failures.Add("an unresolvable landmark was offered");
                    var lookedAfterFirst = source.LookedCount();
                    if (lookedAfterFirst != 3) failures.Add($"{lookedAfterFirst} lookups for 3 new landmarks");

                    // 3. Another pass resolves nothing again -- not even the one that failed -- and publishes nothing new.
                    resolver.Poke();
                    Thread.Sleep(200);
                    if (source.LookedCount() != lookedAfterFirst) failures.Add($"a repeat pass looked up {source.LookedCount() - lookedAfterFirst} landmarks again");
                    if (resolver.TryTakeUpdate(out _)) failures.Add("a repeat pass republished an unchanged list");

                    // 4. B is removed from the folder: the flush (local store only) drops it.
                    source.Local = new FavoritesSnapshot(true, new[] { itemA, itemC });
                    if (!resolver.TryFlush(out var flushed)) failures.Add("the flush published nothing after a removal");
                    else if (!flushed.Favorites.Select(f => f.Name).SequenceEqual(new[] { "A" })) failures.Add("the flush did not drop the removed favorite");

                    // 5. A store that cannot vouch for the folder changes nothing.
                    source.Local = FavoritesSnapshot.Unconfirmed;
                    if (resolver.TryFlush(out _)) failures.Add("an unconfirmed store flushed something");
                }
            }

            // 6. Targets already on disk are reused: nothing is looked up for them. A first confirmed EMPTY read of an
            //    unpopulated profile is recorded; the same read of a populated one is not rewritten.
            var cached = new[] { new LoginFavorite(a, itemA.Id, "A", ahern!) };
            var source2 = new FakeLoginStartSource { Remote = new FavoritesSnapshot(true, new[] { itemA }) };
            using (var resolver = new LoginStartFavorites(source2, cached, populated: true, fast))
            {
                resolver.Poke();
                Thread.Sleep(150);
                if (source2.LookedCount() != 0) failures.Add("a cached landmark was looked up again");
                if (resolver.TryTakeUpdate(out _)) failures.Add("an unchanged cache was rewritten");
            }

            var source3 = new FakeLoginStartSource { Remote = new FavoritesSnapshot(true, Array.Empty<LandmarkInventoryItem>()) };
            using (var resolver = new LoginStartFavorites(source3, Array.Empty<LoginFavorite>(), populated: false, fast))
            {
                resolver.Poke();
                LoginStartFavoritesUpdate? recorded = null;
                if (!WaitFor(() => resolver.TryTakeUpdate(out recorded))) failures.Add("a confirmed empty folder was not recorded");
                else if (recorded!.Favorites.Count != 0) failures.Add("a confirmed empty folder published entries");
            }
            using (var resolver = new LoginStartFavorites(source3, Array.Empty<LoginFavorite>(), populated: true, fast))
            {
                resolver.Poke();
                Thread.Sleep(150);
                if (resolver.TryTakeUpdate(out _)) failures.Add("an empty populated cache was rewritten");
            }

            // 7. A disposed resolver does nothing more.
            var source4 = new FakeLoginStartSource { Remote = new FavoritesSnapshot(true, new[] { itemA }) };
            source4.Targets[a] = ahern!;
            var disposed = new LoginStartFavorites(source4, Array.Empty<LoginFavorite>(), populated: true, fast);
            disposed.Dispose();
            disposed.Poke();
            Thread.Sleep(100);
            if (source4.LookedCount() != 0 || disposed.TryTakeUpdate(out _)) failures.Add("a disposed resolver kept working");

            return failures.Count == 0
                ? new Check(Name, true, "unconfirmed reads publish nothing; landmarks resolve once, failures are left out; empty folders are recorded; the flush follows the store")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
