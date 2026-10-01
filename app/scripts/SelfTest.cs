using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Assets;
using SLNG.Core;

namespace SLNG.App;

/// <summary>
/// The <c>--selftest</c> smoke test that <c>AGENTS.md</c> has documented since the first commit and
/// that never existed: <c>godot --headless --path app -- --selftest</c> simply sat at the login
/// screen forever. That gap is not cosmetic -- it is why the FEAT-RENDER-04 shader changes were
/// handed over unverified, and why a sky shader that failed to compile (v0.7.36-alpha) shipped as a
/// black dome with no other symptom.
///
/// What it deliberately does NOT do: log in. A smoke test that needs credentials and a reachable
/// grid is a test nobody can run in CI, and every failure it finds is ambiguous between "our bug"
/// and "the grid is down". Everything checked here is a resource the client loads before the login
/// button is even pressed, which is exactly the class of failure that has actually bitten this
/// project: a resource that reads fine from source and not from a .pck, a shader whose
/// <c>#include</c> stopped resolving, a locale file that lost half its keys.
///
/// Exit code is 0 on pass and 1 on failure, so it can go straight into CI next to
/// <c>tools/check_shader_globals.py</c> -- the two are complementary: the python checker reads the
/// shader *text* and catches a global uniform missing from project.godot, this one loads the
/// resources through the engine.
/// </summary>
public static class SelfTest
{
    private const string Flag = "--selftest";

    /// <summary>True when the client was started with <c>--selftest</c>. Mirrors
    /// <see cref="Diagnostics"/>: Godot puts arguments after a bare <c>--</c> into
    /// GetCmdlineUserArgs and the rest into GetCmdlineArgs, and both are checked so the flag works
    /// whether or not it is passed after the separator.</summary>
    public static bool Requested =>
        HasFlag(OS.GetCmdlineArgs()) || HasFlag(OS.GetCmdlineUserArgs());

    private static bool HasFlag(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg == Flag) return true;
        }
        return false;
    }

    private readonly record struct Check(string Name, bool Passed, string Detail);

    /// <summary>Directories under <c>user://</c> the engine and the client own outright and write
    /// to as a matter of course. Excluding them is not a loophole: <c>logs/</c> receives this very
    /// run's <c>godot.log</c>, and the caches exist to be rewritten. What the check is for is
    /// CONFIGURATION and user content -- <c>logins.cfg</c>, <c>preferences.cfg</c>, saved
    /// snapshots -- none of which a smoke test has any business touching.</summary>
    private static readonly string[] IgnoredUserDirs =
    {
        "logs", "cache", "shader_cache", "vulkan", "objectdb_snapshots",
    };

    /// <summary>Every file under <c>user://</c> as it stood before the boot path ran, keyed by
    /// path, valued by size and modification time. Null outside a selftest run.</summary>
    private static Dictionary<string, (long Size, ulong Modified)>? _userDataBefore;

    /// <summary>Records the state of <c>user://</c> before <c>Boot._Ready</c> touches it.
    ///
    /// <para>Must be called as the FIRST thing in <c>_Ready</c>, ahead of anything that reads or
    /// writes there. The snapshot is the evidence half of the isolation: <c>_Ready</c> skipping
    /// its <c>user://</c> work is what keeps the developer's data intact, and
    /// <see cref="CheckUserDataUntouched"/> is what keeps that true after the next person adds a
    /// write without knowing the rule. A comment would not have survived; this fails the
    /// build.</para>
    ///
    /// <para>The reason it exists: <c>--selftest</c> does not run the checks in isolation, it runs
    /// the whole client and then the checks. On 2026-09-17 a one-way password migration added to
    /// <c>LoadProfiles</c> therefore rewrote four real saved logins during what everyone involved
    /// believed was a read-only smoke test. It was the wanted migration and a backup existed, but
    /// nothing about "run the smoke test" implies "and rewrite my credentials".</para></summary>
    public static void SnapshotUserData() => _userDataBefore = ScanUserData();

    private static Dictionary<string, (long Size, ulong Modified)> ScanUserData()
    {
        var found = new Dictionary<string, (long, ulong)>();
        Walk("user://", found, depth: 0);
        return found;
    }

    private static void Walk(string dirPath, Dictionary<string, (long, ulong)> into, int depth)
    {
        // Deep enough for anything this project writes; a guard against a symlink loop rather
        // than a real structural limit.
        if (depth > 8) return;

        using var dir = DirAccess.Open(dirPath);
        if (dir == null) return;

        foreach (string file in dir.GetFiles())
        {
            string path = dirPath.EndsWith('/') ? dirPath + file : dirPath + "/" + file;
            long size = -1;
            using (var f = FileAccess.Open(path, FileAccess.ModeFlags.Read))
            {
                if (f != null) size = (long)f.GetLength();
            }
            into[path] = (size, FileAccess.GetModifiedTime(path));
        }

        foreach (string sub in dir.GetDirectories())
        {
            if (depth == 0 && System.Array.IndexOf(IgnoredUserDirs, sub) >= 0) continue;
            string path = dirPath.EndsWith('/') ? dirPath + sub : dirPath + "/" + sub;
            Walk(path, into, depth + 1);
        }
    }

    /// <summary>Fails when the boot path wrote anything under <c>user://</c> outside the
    /// engine-owned directories. Names the offending paths, because "something wrote" is not
    /// actionable and the whole point is that the write was invisible the first time.</summary>
    private static Check CheckUserDataUntouched()
    {
        const string Name = "user data untouched";

        if (_userDataBefore == null)
        {
            return new Check(Name, false,
                "no snapshot was taken -- SelfTest.SnapshotUserData() must be the first thing " +
                "Boot._Ready does, before anything reads or writes user://");
        }

        var after = ScanUserData();
        var changed = new List<string>();

        foreach (var (path, before) in _userDataBefore)
        {
            if (!after.TryGetValue(path, out var now)) { changed.Add($"removed {path}"); continue; }
            if (now.Size != before.Size) changed.Add($"resized {path} ({before.Size} -> {now.Size} bytes)");
            else if (now.Modified != before.Modified) changed.Add($"rewritten {path}");
        }
        foreach (var path in after.Keys)
        {
            if (!_userDataBefore.ContainsKey(path)) changed.Add($"created {path}");
        }

        return changed.Count == 0
            ? new Check(Name, true, $"{after.Count} file(s) under user:// unchanged by the boot path")
            : new Check(Name, false,
                $"the boot path wrote to user:// during a smoke test: {string.Join("; ", changed)}");
    }

    /// <summary>
    /// Runs every check, prints one line each plus a summary, and quits the tree with 0 or 1.
    /// Call it from <c>Boot._Ready</c> deferred: the checks load resources, and doing that inside
    /// <c>_Ready</c> would interleave with the rest of the boot sequence still setting itself up.
    /// </summary>
    public static void Run(SceneTree tree)
    {
        var results = new List<Check>();
        results.AddRange(CheckShaders());
        results.Add(CheckShaderIncludes());
        results.AddRange(CheckShaderVariants());
        results.AddRange(CheckLocales());
        results.Add(CheckAvatarSkeleton());
        results.Add(CheckControlAvatar(tree));
        results.Add(CheckAnimeshHandOver(tree));
        results.Add(CheckControlAvatarAnimation(tree));
        results.Add(CheckAnimeshAnimation(tree));
        results.AddRange(CheckWindlightPresets());
        results.Add(CheckInstanceSlotMap());
        results.Add(CheckWorkQueueOnceThePumpIsGone());
        results.Add(CheckLoginScreenCoversTheWorld());
        results.Add(CheckAvatarAnimationPlayer());
        results.Add(CheckAvatarHoldMode());
        results.Add(CheckAvatarAnimationFreeze());
        results.Add(CheckAvatarAnimationLocalOverlay());
        results.Add(CheckRegionRestartWindow(tree));
        results.Add(CheckInventoryTrashMenus(tree));
        results.Add(CheckWornListKeepsSelection(tree));
        results.Add(CheckWindowInsets(tree));
        results.Add(CheckTooltipStyle());
        // Last, so it sees everything the run did.
        results.Add(CheckUserDataUntouched());

        foreach (var r in results)
        {
            GD.Print($"[SelfTest] {(r.Passed ? "ok  " : "FAIL")} {r.Name}: {r.Detail}");
        }

        int failed = results.Count(r => !r.Passed);
        GD.Print($"[SelfTest] {results.Count - failed}/{results.Count} checks passed");
        GD.Print(failed == 0 ? "[SelfTest] PASS" : "[SelfTest] FAIL");

        tree.Quit(failed == 0 ? 0 : 1);
    }

    /// <summary>
    /// FEAT-UI-34: the region-restart popup builds inside a real scene tree, shows a countdown, and
    /// a repeat notice moves the deadline of the same window instead of needing a second one.
    ///
    /// <para>This is the part of the feature a unit test cannot reach: a Control tree that throws
    /// in <c>_Ready</c> (a missing theme item, a bad container flag) would otherwise only show up
    /// when a real restart notice arrives in-world — the one moment the window has to work.</para>
    /// </summary>
    private static Check CheckRegionRestartWindow(SceneTree tree)
    {
        const string Name = "region restart window";
        var win = new SLNG.App.UI.RegionRestartWindow();
        try
        {
            int closed = 0;
            win.Closed += () => closed++;
            tree.Root.AddChild(win);

            win.Update(new SLNG.Core.RegionRestartEvent("Testland", 65));
            win._Process(0);
            string first = win.ClockText;

            // A repeat notice with less time left: same window, new deadline.
            win.Update(new SLNG.Core.RegionRestartEvent("Testland", 30));
            win._Process(0);
            string second = win.ClockText;

            // Before any landmark has loaded there is still a way out: Home, and Teleport works.
            int destinations = win.DestinationCount;
            bool canTeleport = win.TeleportEnabled;

            win.Close();
            win.Close(); // idempotent: the owner and the × can both ask

            bool ok = first == "1:05" && second == "0:30" && closed == 1 && destinations == 1 && canTeleport;
            return new Check(Name, ok,
                ok ? "opens at 1:05, a repeat notice moves it to 0:30, Home is offered at once, closes once"
                   : $"clock '{first}' then '{second}' (want '1:05' then '0:30'), Closed fired {closed}x (want 1), " +
                     $"{destinations} destination(s) (want 1 = Home), Teleport enabled={canTeleport} (want True)");
        }
        catch (System.Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(win)) win.QueueFree();
        }
    }

    /// <summary>
    /// FEAT-INV-10: the inventory panel builds in a real tree with its two Trash menus, and every
    /// label on them — and the counted question the Empty Trash prompt asks — is real text.
    ///
    /// <para>A missing string does not throw; it renders as <c>[ui.inventory_trash.purge]</c> on the
    /// menu of the one action that cannot be undone. The locale check compares the language files
    /// with each other only, so a key misspelt in the code, or missing from both, gets past it.</para>
    /// </summary>
    private static Check CheckInventoryTrashMenus(SceneTree tree)
    {
        const string Name = "inventory trash menus";
        var panel = new SLNG.App.UI.InventoryPanel();
        try
        {
            tree.Root.AddChild(panel);

            var labels = panel.TrashMenuLabels().ToList();
            int menuEntries = labels.Count;
            labels.Add(SLNG.App.UI.InventoryPanel.CountText(1, 0));
            labels.Add(SLNG.App.UI.InventoryPanel.CountText(3, 2));
            labels.Add(SLNG.App.UI.L10n.TrFormat("ui.inventory_trash.restore", "Objects"));

            var unresolved = labels.Where(l => string.IsNullOrWhiteSpace(l) || l.StartsWith('[')).ToList();
            // Empty Trash; Restore and Delete permanently.
            bool ok = menuEntries == 3 && unresolved.Count == 0;
            return new Check(Name, ok, ok
                ? string.Join(" | ", labels)
                : $"{menuEntries} menu entr(y/ies) (want 3), unresolved: {string.Join(", ", unresolved)}");
        }
        catch (System.Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(panel)) panel.QueueFree();
        }
    }

    /// <summary>
    /// BUG-INV-11: the worn list is refreshed every 2.5 s while its tab is open. A refresh that finds
    /// nothing changed must leave the rows alone, and one that does rebuild must keep the selected
    /// row selected. The old clear-and-rebuild dropped the selection under an open context menu, so
    /// „Ablegen" went nowhere — reported in-world, invisible to every unit test.
    /// </summary>
    private static Check CheckWornListKeepsSelection(SceneTree tree)
    {
        const string Name = "worn list keeps its selection";
        var panel = new SLNG.App.UI.InventoryPanel();
        try
        {
            tree.Root.AddChild(panel);

            static SLNG.Core.WornItem Worn(string name, string point) => new(
                System.Guid.NewGuid(), name, SLNG.Core.WornCategory.Attachment, point, AssetType: 6, Live: true);
            var hat = Worn("Hat", "Skull");
            var shoe = Worn("Shoe", "Left Foot");
            var ring = Worn("Ring", "Left Hand");

            panel.ShowWornItems(new[] { hat, shoe });
            bool selected = panel.SelectWorn(shoe.ItemId);
            ulong rowBefore = panel.WornRowFor(shoe.ItemId)?.GetInstanceId() ?? 0;

            // The same set in another order: nothing to rebuild.
            panel.ShowWornItems(new[] { shoe, hat });
            bool untouched = rowBefore != 0
                && panel.WornRowFor(shoe.ItemId)?.GetInstanceId() == rowBefore
                && panel.SelectedWornId() == shoe.ItemId;

            // Something new arrived: rebuilt, and the selection survives it.
            panel.ShowWornItems(new[] { hat, shoe, ring });
            bool rebuilt = panel.WornRowFor(ring.ItemId) != null;
            bool kept = panel.SelectedWornId() == shoe.ItemId;

            bool ok = selected && untouched && rebuilt && kept;
            return new Check(Name, ok, ok
                ? "an unchanged refresh keeps the rows, a changed one keeps the selected row selected"
                : $"selected={selected}, unchanged refresh left rows and selection alone={untouched}, " +
                  $"change rebuilt the list={rebuilt}, selection survived the rebuild={kept}");
        }
        catch (System.Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(panel)) panel.QueueFree();
        }
    }

    /// <summary>Tooltips get the app's dark, near-opaque box (Boot applies UiTheme.ApplyTooltipStyle to the
    /// engine's default theme); the stock one is 50 % black and unreadable over a bright world.</summary>
    private static Check CheckTooltipStyle()
    {
        var box = ThemeDB.GetDefaultTheme().GetStylebox("panel", "TooltipPanel") as StyleBoxFlat;
        return box is { BgColor.A: >= 0.95f }
            ? new Check("tooltip style", true, $"opaque tooltip box (alpha {box.BgColor.A:0.00})")
            : new Check("tooltip style", false, "the default theme's TooltipPanel is not the opaque one -- UiTheme.ApplyTooltipStyle was not applied");
    }

    /// <summary>
    /// Every SLNGWindow buildable without a login keeps the standard inset (SLNGWindow.DefaultContentMarginH/V,
    /// 14 px left/right, 12 px top/bottom) between its frame and the nearest content control -- a Label,
    /// Button, Tree... or painted box (styled panel, scroll area, tab container) -- whatever the nesting.
    /// An extra wrapper margin, or an opt-out via SetContentMargin(0, 0), fails here. Layout is forced
    /// synchronously (one call, no frames to wait for), at the default size and in the default tab; the
    /// radar's full-bleed map is the one exception.
    /// </summary>
    private static Check CheckWindowInsets(SceneTree tree)
    {
        var session = new SLNG.Net.GridSession();
        var log = new SLNG.Core.Services.ChatLogger(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slng-selftest-no-chat"));
        static (UI.SLNGWindow, Action) W<T>(Action<T>? init = null) where T : UI.SLNGWindow, new() { var w = new T(); return (w, () => init?.Invoke(w)); }
        Guid id() => Guid.NewGuid();
        var windows = new[]
        {
            W<UI.AboutWindow>(), W<UI.ActiveAnimationsWindow>(), W<UI.AvatarHoverWindow>(), W<UI.CameraHUD>(), W<UI.CreateLandmarkWindow>(),
            W<UI.EnvironmentWindow>(), W<UI.InventoryPanel>(), W<UI.ItemPropertiesWindow>(), W<UI.MinimapOverlay>(), W<UI.SnapshotWindow>(), W<UI.WorldMapWindow>(),
            W<UI.BuyObjectWindow>(w => w.Initialize(session, 0, 1, "Chair", PrimSaleType.Copy, 250)),
            W<UI.ChatHistoryWindow>(w => w.Open(log, SLNG.Core.Services.ChatLogKind.Local, "", "Local chat")), W<UI.ChatWindow>(w => w.Initialize(log)),
            W<UI.ConfirmWindow>(w => w.Initialize("Title", "Really do that?", "Do it")), W<UI.TextPromptWindow>(w => w.Initialize("Rename", "Name:", "Old", "OK")),
            W<UI.GroupInvitationWindow>(w => w.Initialize(session, new GroupInvitationEvent(id(), id(), "Someone", "Join us", 0))),
            W<UI.InventoryOfferWindow>(w => w.Initialize(session, new InventoryOfferEvent(id(), id(), "Someone", "Hat", id(), 6, false))),
            W<UI.NotificationWindow>(w => w.Initialize(new NotificationStore())), W<UI.ObjectEditWindow>(w => w.Initialize(session, new SLNG.Core.ECS.World())),
            W<UI.PayAvatarWindow>(w => w.Initialize(session, id(), "Someone")), W<UI.PayObjectWindow>(w => w.Initialize(session, 0, 1, id(), "Vendor")),
            W<UI.PreferencesWindow>(w => w.AddTab("Test", new Label { Text = "Test" })), W<UI.RegionRestartWindow>(w => w.Initialize(session, new RegionRestartEvent("Testland", 120))),
            W<UI.ScriptDialogWindow>(w => w.Initialize(session, new ScriptDialogEvent(id(), "Object", id(), "Owner", "Pick", 1, new[] { "Yes", "No" }))),
            W<UI.ScriptPermissionWindow>(w => w.Initialize(session, new ScriptPermissionRequestEvent(id(), id(), "Object", "Owner", 4))),
            W<UI.TermsOfServiceWindow>(w => w.Initialize("https://grid.invalid/login", "Accept the terms.", false)),
            W<UI.UserProfileWindow>(w => w.Initialize(id(), "Someone", session, null, null)),
        };
        static void Layout(Control c) { if (c is Container) c.Notification((int)Container.NotificationSortChildren); foreach (var k in c.GetChildren()) if (k is Control kc) Layout(kc); }
        var failures = new List<string>();
        foreach (var (win, init) in windows)
        {
            try
            {
                tree.Root.AddChild(win); init(); win.Visible = true;
                var want = win.Size; // the default the window gave itself
                for (int pass = 0; pass < 3; pass++) { win.Size = want; Layout(win); } // 3: wrapped text settles once it has a width
                var frame = win.ContentContainer.GetGlobalRect();
                float[] got = { 1e9f, 1e9f, 1e9f, 1e9f }; // nearest content: left, right, top, bottom
                void Walk(Node n)
                {
                    foreach (var c in n.GetChildren().OfType<Control>().Where(x => x.Visible && x is not Separator))
                    {
                        bool box = c is ScrollContainer or TabContainer || (c is PanelContainer or Panel && c.HasThemeStyleboxOverride("panel"));
                        if (!box && (c is Container || c.GetType() == typeof(Control))) { Walk(c); continue; } // pure layout
                        var g = c.GetGlobalTransform() * new Rect2(Vector2.Zero, c.Size);
                        if (g.Size.X < 1 || g.Size.Y < 1) continue;
                        got[0] = Math.Min(got[0], g.Position.X - frame.Position.X); got[1] = Math.Min(got[1], frame.End.X - g.End.X);
                        got[2] = Math.Min(got[2], g.Position.Y - frame.Position.Y); got[3] = Math.Min(got[3], frame.End.Y - g.End.Y);
                    }
                }
                Walk(win.ContentContainer);
                float h = win is UI.MinimapOverlay ? 0 : UI.SLNGWindow.DefaultContentMarginH, v = UI.SLNGWindow.DefaultContentMarginV; // radar: map is full-bleed
                if (new[] { h, h, v, v }.Zip(got).Any(p => Math.Abs(p.First - p.Second) > 2))
                    failures.Add($"{win.GetType().Name} L/R/T/B = {got[0]:0.#}/{got[1]:0.#}/{got[2]:0.#}/{got[3]:0.#}");
            }
            catch (Exception ex) { failures.Add($"{win.GetType().Name} threw {ex.GetType().Name}: {ex.Message}"); }
            finally { if (GodotObject.IsInstanceValid(win)) win.QueueFree(); }
        }
        return failures.Count == 0
            ? new Check("window insets", true, $"{windows.Length} windows: 14/14/12/12 px from frame to content (radar map excepted)")
            : new Check("window insets", false, $"off the standard 14/14/12/12 by more than 2 px: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// Every <c>.gdshader</c> under <c>res://materials</c> loads and reports uniforms.
    ///
    /// The uniform list is the signal, not the load itself: <c>ResourceLoader.Load</c> hands back a
    /// Shader object for a file that does not parse, but a shader the language frontend rejected
    /// exposes no uniforms. Every shader in this project declares some, so an empty list means the
    /// shader is broken -- including a broken <c>#include</c>, which is otherwise invisible until a
    /// surface using it renders wrong.
    /// </summary>
    private static IEnumerable<Check> CheckShaders()
    {
        var paths = EnumerateResources("res://materials", ".gdshader").OrderBy(p => p).ToList();
        if (paths.Count == 0)
        {
            yield return new Check("shaders", false, "no .gdshader files found under res://materials");
            yield break;
        }

        foreach (string path in paths)
        {
            Shader? shader = ResourceLoader.Load<Shader>(path);
            if (shader == null)
            {
                yield return new Check($"shader {ShortName(path)}", false, "failed to load");
                continue;
            }

            int uniforms = shader.GetShaderUniformList().Count;
            yield return new Check(
                $"shader {ShortName(path)}",
                uniforms > 0,
                uniforms > 0 ? $"{uniforms} uniforms" : "0 uniforms -- shader did not compile");
        }
    }

    /// <summary>
    /// The <c>.gdshaderinc</c> files exist and are non-empty.
    ///
    /// They are checked by presence rather than by parsing because an include is not a standalone
    /// translation unit -- there is nothing to load it as. Their real verification is the uniform
    /// count of the shaders that include them, above; this check exists to turn "every shader
    /// reports 0 uniforms" into an obvious cause instead of a mystery.
    /// </summary>
    private static Check CheckShaderIncludes()
    {
        var paths = EnumerateResources("res://materials", ".gdshaderinc").ToList();
        var empty = new List<string>();
        foreach (string path in paths)
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null || file.GetLength() == 0) empty.Add(ShortName(path));
        }

        return new Check(
            "shader includes",
            paths.Count > 0 && empty.Count == 0,
            empty.Count == 0 ? $"{paths.Count} readable" : $"empty/unreadable: {string.Join(", ", empty)}");
    }

    /// <summary>
    /// Every surface variant exposes exactly the uniforms its base shader does.
    ///
    /// The family compiles one shader per (transparency, surface) pair because Godot makes cull
    /// mode and shading mode compile-time (FEAT-RENDER-01 Phase 3, see PrimShaderFamily.Surface).
    /// The <c>_avatar</c> and <c>_hud</c> files are therefore hand-kept copies differing from their
    /// base in one render_mode line, which makes them a standing drift risk: add a uniform to
    /// <c>prim_scissor.gdshader</c>, forget its two siblings, and avatar or HUD faces silently stop
    /// receiving whatever it carries -- the exact failure the family's own docs warn about for
    /// texture flags, with no error anywhere.
    ///
    /// Comparing the uniform SETS rather than the counts, so a rename shows up as the two names
    /// that differ instead of "6 vs 6".
    /// </summary>
    private static IEnumerable<Check> CheckShaderVariants()
    {
        string[] suffixes = { "_avatar.gdshader", "_hud.gdshader" };

        foreach (string path in EnumerateResources("res://materials", ".gdshader").OrderBy(p => p))
        {
            string? suffix = suffixes.FirstOrDefault(sfx => path.EndsWith(sfx, StringComparison.Ordinal));
            if (suffix == null) continue;

            string basePath = path.Substring(0, path.Length - suffix.Length) + ".gdshader";
            string name = ShortName(path);

            var variant = ResourceLoader.Load<Shader>(path);
            var original = ResourceLoader.Load<Shader>(basePath);
            if (variant == null || original == null)
            {
                yield return new Check($"variant {name}", false, $"missing base shader {ShortName(basePath)}");
                continue;
            }

            var variantNames = UniformNames(variant);
            var baseNames = UniformNames(original);
            var onlyVariant = variantNames.Except(baseNames).OrderBy(s => s).ToList();
            var onlyBase = baseNames.Except(variantNames).OrderBy(s => s).ToList();

            if (onlyVariant.Count == 0 && onlyBase.Count == 0)
            {
                yield return new Check($"variant {name}", true, $"{variantNames.Count} uniforms match {ShortName(basePath)}");
            }
            else
            {
                var parts = new List<string>();
                if (onlyBase.Count > 0) parts.Add("missing " + string.Join(", ", onlyBase));
                if (onlyVariant.Count > 0) parts.Add("extra " + string.Join(", ", onlyVariant));
                yield return new Check($"variant {name}", false, string.Join("; ", parts));
            }
        }
    }

    private static HashSet<string> UniformNames(Shader shader)
    {
        var names = new HashSet<string>();
        foreach (var entry in shader.GetShaderUniformList())
        {
            var dict = entry.AsGodotDictionary();
            if (dict.TryGetValue("name", out var value)) names.Add(value.AsString());
        }
        return names;
    }

    /// <summary>
    /// Each <c>res://i18n/*.json</c> parses and carries keys, and every non-default locale covers
    /// the full <c>en-US</c> key set.
    ///
    /// The coverage half is what makes this worth running: a missing key does not throw, it renders
    /// as the raw <c>[key]</c> fallback in the UI, which is only ever noticed by someone running
    /// that language. The whole i18n subsystem shipped once showing nothing but those fallbacks
    /// (FEAT-UI-02, fixed by reading through FileAccess so the .pck works).
    /// </summary>
    private static IEnumerable<Check> CheckLocales()
    {
        var manager = new SLNG.Core.Services.LocalizationManager();
        var loaded = new Dictionary<string, HashSet<string>>();

        var paths = EnumerateResources("res://i18n", ".json").OrderBy(p => p).ToList();
        foreach (string path in paths)
        {
            string locale = ShortName(path);
            locale = locale.Substring(0, locale.Length - ".json".Length);

            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null)
            {
                yield return new Check($"locale {locale}", false, $"cannot open: {FileAccess.GetOpenError()}");
                continue;
            }

            // Assigned in the try and inspected after it: C# forbids `yield return` inside a catch,
            // so the failure has to leave the block as data.
            HashSet<string>? keys = null;
            string? error = null;
            try
            {
                manager.LoadLocaleFromJson(locale, file.GetAsText());
                keys = manager.GetDictionaryForLocale(locale).Keys.ToHashSet();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (keys == null)
            {
                yield return new Check($"locale {locale}", false, error ?? "failed to load");
                continue;
            }

            loaded[locale] = keys;
            yield return new Check($"locale {locale}", keys.Count > 0, keys.Count > 0 ? $"{keys.Count} keys" : "no keys");
        }

        // en-US is the fallback locale, so it defines the key set every other locale is measured
        // against. Reported as a named list rather than a count: "3 missing" sends someone
        // diffing two JSON files by hand, the names do not.
        if (loaded.TryGetValue("en-US", out var reference) && reference.Count > 0)
        {
            foreach (var (locale, keys) in loaded.Where(kv => kv.Key != "en-US").OrderBy(kv => kv.Key))
            {
                var missing = reference.Except(keys).OrderBy(k => k).ToList();
                yield return new Check(
                    $"locale {locale} coverage",
                    missing.Count == 0,
                    missing.Count == 0
                        ? $"{keys.Count}/{reference.Count}"
                        : $"missing {missing.Count}: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", ..." : "")}");
            }
        }
    }

    /// <summary>
    /// <c>avatar_skeleton.xml</c> loads through the real parser and yields the Bento bone set.
    ///
    /// This is the check with a live precedent: the loader used System.IO + GlobalizePath, which
    /// reads nothing out of an exported .pck, and the failure path in AvatarRenderer swallows the
    /// exception and falls back to a capsule -- so an entire broken avatar pipeline produced no log
    /// line at all. The threshold is deliberately loose (a Bento skeleton has well over 100 joints);
    /// the point is to separate "parsed" from "silently empty", not to pin a count that legitimately
    /// changes.
    /// </summary>
    private static Check CheckAvatarSkeleton()
    {
        const string path = "res://assets/avatar/avatar_skeleton.xml";
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) return new Check("avatar skeleton", false, $"cannot open {path}: {FileAccess.GetOpenError()}");

            var skeleton = AvatarSkeleton.LoadFromXml(file.GetAsText());
            int bones = skeleton.Bones.Count;
            return new Check("avatar skeleton", bones >= 100, $"{bones} bones");
        }
        catch (Exception ex)
        {
            return new Check("avatar skeleton", false, ex.Message);
        }
    }

    /// <summary>
    /// FEAT-ANIMESH-01: an animated-mesh object's control avatar stands a rigged mesh upright.
    ///
    /// <para>The unit tests cover the arithmetic of the placement rotation; this runs the rest of
    /// the chain through the real renderer, headless -- skeleton, shape, bind matrices, SL-to-Godot
    /// conversions, the node it is placed with -- and compares the skinned result with a known
    /// answer. The failure it exists for does not throw and does not log: a robot that is built
    /// perfectly well and lies on its side.</para>
    /// </summary>
    private static Check CheckControlAvatar(SceneTree tree)
    {
        const string Name = "control avatar (animesh)";
        var renderer = new AvatarRenderer();
        try
        {
            tree.Root.AddChild(renderer);
            var (passed, detail) = renderer.SelfTestControlAvatar();
            return new Check(Name, passed, detail);
        }
        catch (System.Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(renderer)) renderer.QueueFree();
        }
    }

    /// <summary>
    /// FEAT-ANIMESH-01: the object renderer gives a rigged mesh to the control avatar exactly when
    /// the object's root says "animated mesh", whichever of the two arrives first.
    ///
    /// <para>Runs the real renderers on a real <see cref="World"/> with no asset service, so
    /// nothing is fetched and a decoded mesh is delivered by hand. The cases it exists for are the
    /// silent ones: a flag that turns up after the mesh was already drawn statically, a child that
    /// has to follow its root without any update of its own, and a skeleton left alive after its
    /// object was derezzed or went out of range.</para>
    /// </summary>
    private static Check CheckAnimeshHandOver(SceneTree tree)
    {
        const string Name = "animesh hand-over";
        var objects = new ObjectRenderer();
        var avatars = new AvatarRenderer();
        try
        {
            tree.Root.AddChild(objects);
            tree.Root.AddChild(avatars);
            var world = new SLNG.Core.ECS.World();
            avatars.Initialize(world, null!, null!);
            objects.Initialize(world, null!, null!);
            objects.ControlAvatars = avatars;

            var (passed, detail) = objects.SelfTestAnimeshOrders(world, avatars);
            return new Check(Name, passed, detail);
        }
        catch (System.Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(objects)) objects.QueueFree();
            if (GodotObject.IsInstanceValid(avatars)) avatars.QueueFree();
        }
    }

    /// <summary>
    /// FEAT-ANIMESH-02: a control avatar plays what is signalled for its object, and nothing else.
    ///
    /// <para>Driven with a synthetic animation per id through the loader seam -- the fetch itself
    /// needs a grid. The failure it exists for is silent: an animesh that stays in its T-pose looks
    /// exactly like one nobody told to move.</para>
    /// </summary>
    private static Check CheckControlAvatarAnimation(SceneTree tree)
    {
        const string Name = "control avatar animation (animesh)";
        var renderer = new AvatarRenderer();
        try
        {
            tree.Root.AddChild(renderer);
            var (passed, detail) = renderer.SelfTestControlAvatarAnimation();
            return new Check(Name, passed, detail);
        }
        catch (System.Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(renderer)) renderer.QueueFree();
        }
    }

    /// <summary>
    /// FEAT-ANIMESH-02: the animations a linkset's prims signal reach the object's control avatar
    /// through the real renderers and a real <see cref="World"/> -- the union over the root and every
    /// child, plain ones included, following links, unlinks and derezzes.
    /// </summary>
    private static Check CheckAnimeshAnimation(SceneTree tree)
    {
        const string Name = "animesh animation hand-off";
        var objects = new ObjectRenderer();
        var avatars = new AvatarRenderer();
        try
        {
            tree.Root.AddChild(objects);
            tree.Root.AddChild(avatars);
            var world = new SLNG.Core.ECS.World();
            avatars.Initialize(world, null!, null!);
            objects.Initialize(world, null!, null!);
            objects.ControlAvatars = avatars;

            var (passed, detail) = objects.SelfTestAnimeshAnimations(world, avatars);
            return new Check(Name, passed, detail);
        }
        catch (System.Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(objects)) objects.QueueFree();
            if (GodotObject.IsInstanceValid(avatars)) avatars.QueueFree();
        }
    }

    /// <summary>
    /// FEAT-ENV-02: the shipped Windlight library is listed AND every preset in it parses.
    ///
    /// Both halves are load-bearing and neither is covered by the unit tests, which read the same
    /// files off disk with System.IO. Here they go through DirAccess/FileAccess, which is the only
    /// way to find out that an exported build packed them at all -- a preset picker whose list is
    /// empty is exactly what a missing export include_filter looks like.
    /// </summary>
    private static IEnumerable<Check> CheckWindlightPresets()
    {
        var library = new WindlightPresetLibrary();
        library.Load();

        int skyFailures = library.SkyNames.Count(n => library.LoadSky(n) == null);
        int waterFailures = library.WaterNames.Count(n => library.LoadWater(n) == null);

        yield return new Check(
            "windlight sky presets",
            library.SkyNames.Count >= 30 && skyFailures == 0,
            skyFailures == 0 ? $"{library.SkyNames.Count} presets parse" : $"{skyFailures} of {library.SkyNames.Count} failed to parse");

        yield return new Check(
            "windlight water presets",
            library.WaterNames.Count >= 5 && waterFailures == 0,
            waterFailures == 0 ? $"{library.WaterNames.Count} presets parse" : $"{waterFailures} of {library.WaterNames.Count} failed to parse");
    }

    /// <summary>
    /// BUG-UI-16: the login screen counts as covering the world, so the avatar nametags — drawn in
    /// a canvas layer of their own, above every screen of the boot UI — stay hidden while it is up.
    /// Left out, the local avatar's tag sat across the login dialog after a session ended.
    ///
    /// <para>The selftest boots to the real login screen, so this checks the actual node, not a
    /// flag someone remembered to set: a <c>%LoginScreen</c> lookup that stopped resolving would
    /// read as "not covered" here.</para>
    /// </summary>
    private static Check CheckLoginScreenCoversTheWorld()
    {
        const string Name = "login screen covers the world";
        bool covered = global::Boot.IsWorldCovered;
        return new Check(Name, covered, covered
            ? "nametags stay hidden while the login screen is up"
            : "Boot.IsWorldCovered is false at the login screen -- nametags would draw over it");
    }

    /// <summary>
    /// BUG-RENDER-37: once the pump has left the tree — the client quitting — work handed to
    /// <see cref="MainThreadWorkQueue"/> from a worker thread is dropped, and its coalescing key is
    /// not left held. The old fallback passed it to Godot through <c>Callable.From(...)
    /// .CallDeferred()</c> from that worker thread, and texture workers finishing on the way out
    /// died there with a fatal <c>AccessViolationException</c> — seen closing the viewer right
    /// after the grid ended the session.
    ///
    /// <para>Under the old code this fails deterministically as well as crashing now and then: the
    /// deferred item still holds its key when the same key is queued again, so the second enqueue
    /// is coalesced away.</para>
    /// </summary>
    private static Check CheckWorkQueueOnceThePumpIsGone()
    {
        const string Name = "work queue once the pump is gone";
        const string Key = "selftest.pump-gone";
        int ran = 0;
        try
        {
            int before = MainThreadWorkQueue.Depth;

            MainThreadWorkQueue.SetPumpActive(false);
            System.Threading.Tasks.Task.Run(() =>
                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => ran++, Key, "selftest")).Wait();
            bool dropped = MainThreadWorkQueue.Depth == before;

            // The pump back, and the same key queued again: it must be free.
            MainThreadWorkQueue.SetPumpActive(true);
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => ran++, Key, "selftest");
            bool keyFree = MainThreadWorkQueue.Depth == before + 1;
            MainThreadWorkQueue.Pump(1000);
            bool ranOnce = ran == 1;

            bool ok = dropped && keyFree && ranOnce;
            return new Check(Name, ok, ok
                ? "work from a worker thread is dropped, not deferred through Godot; its key is not held"
                : $"dropped={dropped}, key free again={keyFree}, ran={ran} (want 1)");
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            MainThreadWorkQueue.SetPumpActive(true);
        }
    }

    /// <summary>
    /// FEAT-PERF-06: the swap-remove bookkeeping in <see cref="InstanceSlotMap"/> — the fiddly
    /// bit of the MultiMesh instancing. app/ has no unit-test project, and getting a middle
    /// removal wrong here silently draws a prim at another prim's transform, so it is checked
    /// through the one harness app/ code does run: <c>--selftest</c>.
    /// </summary>
    private static Check CheckInstanceSlotMap()
    {
        var map = new InstanceSlotMap();
        var ids = new Guid[6];
        for (int i = 0; i < ids.Length; i++) ids[i] = Guid.NewGuid();

        var problems = new List<string>();

        for (int i = 0; i < ids.Length; i++)
            if (map.Add(ids[i]) != i) problems.Add($"Add returned wrong slot at {i}");
        if (map.Add(ids[2]) != 2) problems.Add("re-Add of an existing id did not return its slot");
        if (map.Count != 6) problems.Add($"Count {map.Count} != 6 after adds");

        // Remove a middle entry: the last id (ids[5]) must move into slot 2, every index stays dense.
        int freed = map.Remove(ids[2], out Guid moved);
        if (freed != 2) problems.Add($"Remove freed slot {freed}, expected 2");
        if (moved != ids[5]) problems.Add("Remove did not report the last id as moved");
        if (map.Count != 5) problems.Add($"Count {map.Count} != 5 after middle remove");
        if (!map.TryIndex(ids[5], out int m5) || m5 != 2) problems.Add($"moved id not re-indexed to slot 2 (got {m5})");
        if (map.TryIndex(ids[2], out _)) problems.Add("removed id still present");
        for (int i = 0; i < map.Count; i++)
            if (!map.TryIndex(map.Order[i], out int back) || back != i)
                problems.Add($"index/order disagree at slot {i}");

        // Remove the last entry: no move should be reported.
        map.Remove(map.Order[map.Count - 1], out Guid movedLast);
        if (movedLast != Guid.Empty) problems.Add("removing the last entry reported a moved id");

        // Removing an absent id is a -1 no-op.
        if (map.Remove(Guid.NewGuid(), out _) != -1) problems.Add("Remove of an absent id did not return -1");

        return new Check("instance slot map", problems.Count == 0,
            problems.Count == 0 ? "swap-remove keeps indices dense" : string.Join("; ", problems));
    }

    private static Check CheckAvatarAnimationPlayer()
    {
        var problems = new List<string>();
        var player = new AvatarAnimationPlayer();
        var animId = Guid.NewGuid();
        var data = new SLNG.Assets.AnimationData
        {
            Length = 2.0f,
            InPoint = 0.5f,
            OutPoint = 1.8f,
            Loop = true,
            Priority = 3,
            Joints = Array.Empty<SLNG.Assets.AnimationJointData>()
        };

        player.SetActiveAnimations(new[] { (animId, data) });
        if (!player.IsPlaying) problems.Add("IsPlaying should be true after SetActiveAnimations");

        player.Advance(0.5f);
        player.Resync();

        player.Stop();
        if (player.IsPlaying) problems.Add("IsPlaying should be false after Stop");

        return new Check("animation player reset & resync", problems.Count == 0,
            problems.Count == 0 ? "Stop clears active and Resync handles time" : string.Join("; ", problems));
    }

    private static Check CheckAvatarHoldMode()
    {
        var problems = new List<string>();
        var player = new AvatarAnimationPlayer();
        var skeleton = new Skeleton3D();
        skeleton.AddBone("mPelvis");
        skeleton.SetBoneRest(0, new Transform3D(Basis.Identity, new Vector3(0, 1, 0)));
        skeleton.ResetBonePoses();
        player.SetSkeleton(skeleton);

        var animId = Guid.NewGuid();
        var rotKeys = new[] { new RotationKeyframe { Time = 0f, Rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(0, 0, 1.5f) } };
        var joint = new AnimationJointData
        {
            JointName = "mPelvis",
            Priority = 3,
            RotationKeys = rotKeys,
            PositionKeys = Array.Empty<PositionKeyframe>()
        };
        var activeData = new AnimationData
        {
            Length = 2.0f,
            InPoint = 0f,
            OutPoint = 2.0f,
            Loop = true,
            Priority = 3,
            Joints = new[] { joint }
        };
        player.SetActiveAnimations(new[] { (animId, activeData) });

        // Normal mode: Advance applies rotation
        player.Advance(0.1f);
        if (skeleton.GetBonePoseRotation(0) == Quaternion.Identity)
            problems.Add("Normal mode did not pose bone");

        // BindPose mode: Advance resets to rest pose
        player.HoldMode = SLNG.Core.Avatars.AvatarHoldMode.BindPose;
        player.Advance(0.1f);
        if (skeleton.GetBonePoseRotation(0) != Quaternion.Identity)
            problems.Add("BindPose did not reset bone to identity rest");

        // PoseStand mode with StandAnimation
        var standJoint = new AnimationJointData
        {
            JointName = "mPelvis",
            Priority = 1,
            RotationKeys = new[] { new RotationKeyframe { Time = 0f, Rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(0.5f, 0, 0) } },
            PositionKeys = Array.Empty<PositionKeyframe>()
        };
        player.StandAnimation = new AnimationData
        {
            Length = 1.0f,
            InPoint = 0f,
            OutPoint = 1.0f,
            Loop = true,
            Priority = 1,
            Joints = new[] { standJoint }
        };
        player.HoldMode = SLNG.Core.Avatars.AvatarHoldMode.PoseStand;
        player.Advance(0.1f);
        if (skeleton.GetBonePoseRotation(0) == Quaternion.Identity)
            problems.Add("PoseStand did not apply StandAnimation");

        // Back to None
        player.HoldMode = SLNG.Core.Avatars.AvatarHoldMode.None;
        player.Advance(0.1f);
        if (skeleton.GetBonePoseRotation(0) == Quaternion.Identity)
            problems.Add("None mode did not resume active animation");

        return new Check("avatar hold mode", problems.Count == 0,
            problems.Count == 0 ? "BindPose and PoseStand override active animations" : string.Join("; ", problems));
    }

    private static Check CheckAvatarAnimationFreeze()
    {
        var problems = new List<string>();
        var player = new AvatarAnimationPlayer();
        var skeleton = new Skeleton3D();
        skeleton.AddBone("mPelvis");
        skeleton.SetBoneRest(0, new Transform3D(Basis.Identity, new Vector3(0, 1, 0)));
        skeleton.ResetBonePoses();
        player.SetSkeleton(skeleton);

        var animId1 = Guid.NewGuid();
        var rotKeys1 = new[] { new RotationKeyframe { Time = 0f, Rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(0, 0, 1.0f) } };
        var joint1 = new AnimationJointData
        {
            JointName = "mPelvis",
            Priority = 3,
            RotationKeys = rotKeys1,
            PositionKeys = Array.Empty<PositionKeyframe>()
        };
        var data1 = new AnimationData
        {
            Length = 2.0f,
            InPoint = 0f,
            OutPoint = 2.0f,
            Loop = true,
            Priority = 3,
            Joints = new[] { joint1 }
        };

        player.SetActiveAnimations(new[] { (animId1, data1) });
        player.Advance(0.2f);
        float? tNormal = player.GetAnimationTime(animId1);
        if (!tNormal.HasValue || Math.Abs(tNormal.Value - 0.2f) > 0.001f)
            problems.Add($"Normal Advance did not advance time to 0.2s (got {tNormal})");

        // Freeze playback
        player.IsFrozen = true;
        player.Advance(0.5f);
        float? tFrozen = player.GetAnimationTime(animId1);
        if (!tFrozen.HasValue || Math.Abs(tFrozen.Value - 0.2f) > 0.001f)
            problems.Add($"Frozen Advance advanced time from {tNormal} to {tFrozen}");

        // Step forward 1 frame (+1/30 s)
        player.StepFrame(1, 1f / 30f);
        float? tSteppedFwd = player.GetAnimationTime(animId1);
        float expectedFwd = 0.2f + (1f / 30f);
        if (!tSteppedFwd.HasValue || Math.Abs(tSteppedFwd.Value - expectedFwd) > 0.002f)
            problems.Add($"StepFrame(1) did not advance by 1/30s (expected {expectedFwd}, got {tSteppedFwd})");

        // Step back 1 frame (-1/30 s)
        player.StepFrame(-1, 1f / 30f);
        float? tSteppedBack = player.GetAnimationTime(animId1);
        if (!tSteppedBack.HasValue || Math.Abs(tSteppedBack.Value - 0.2f) > 0.002f)
            problems.Add($"StepFrame(-1) did not step back to 0.2s (got {tSteppedBack})");

        // Network update arriving while frozen: buffered, not applied
        var animId2 = Guid.NewGuid();
        var rotKeys2 = new[] { new RotationKeyframe { Time = 0f, Rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(0, 0, 2.0f) } };
        var joint2 = new AnimationJointData
        {
            JointName = "mPelvis",
            Priority = 4,
            RotationKeys = rotKeys2,
            PositionKeys = Array.Empty<PositionKeyframe>()
        };
        var data2 = new AnimationData
        {
            Length = 1.0f,
            InPoint = 0f,
            OutPoint = 1.0f,
            Loop = true,
            Priority = 4,
            Joints = new[] { joint2 }
        };
        player.SetActiveAnimations(new[] { (animId2, data2) });
        if (player.GetAnimationTime(animId2).HasValue)
            problems.Add("SetActiveAnimations swapped active clip while frozen (should be buffered)");
        if (!player.GetAnimationTime(animId1).HasValue)
            problems.Add("Old clip animId1 was removed while frozen");

        // Unfreeze: buffered update applied cleanly
        player.IsFrozen = false;
        if (!player.GetAnimationTime(animId2).HasValue)
            problems.Add("Unfreeze did not apply buffered animId2");
        if (player.GetAnimationTime(animId1).HasValue)
            problems.Add("Unfreeze did not remove stale animId1");

        return new Check("avatar animation freeze", problems.Count == 0,
            problems.Count == 0 ? "Freeze halts time, stepFrame nudges time, updates buffer cleanly" : string.Join("; ", problems));
    }

    private static Check CheckAvatarAnimationLocalOverlay()
    {
        var problems = new List<string>();
        var player = new AvatarAnimationPlayer();

        var localId = Guid.NewGuid();
        var rotKeys = new[] { new RotationKeyframe { Time = 0f, Rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(0, 0, 1.5f) } };
        var localJoint = new AnimationJointData
        {
            JointName = "mHead",
            Priority = 5,
            RotationKeys = rotKeys,
            PositionKeys = Array.Empty<PositionKeyframe>()
        };
        var localData = new AnimationData
        {
            Length = 2.0f,
            InPoint = 0f,
            OutPoint = 2.0f,
            Loop = true,
            Priority = 5,
            Joints = new[] { localJoint }
        };

        // 1. PlayLocal marks IsPlaying true
        player.PlayLocal(localId, localData, "PreviewDance");
        if (!player.IsPlaying)
            problems.Add("IsPlaying should be true after PlayLocal");

        var localInfos = player.GetLocalAnimationInfos();
        if (localInfos.Count != 1 || localInfos[0].id != localId || localInfos[0].name != "PreviewDance")
            problems.Add("GetLocalAnimationInfos did not report local overlay");

        // 2. SetActiveAnimations (e.g. sim echo) does not prune local overlay
        var simId = Guid.NewGuid();
        var simData = new AnimationData
        {
            Length = 1.0f,
            InPoint = 0f,
            OutPoint = 1.0f,
            Loop = true,
            Priority = 2,
            Joints = Array.Empty<AnimationJointData>()
        };
        player.SetActiveAnimations(new[] { (simId, simData) });
        if (player.GetLocalAnimationInfos().Count != 1)
            problems.Add("SetActiveAnimations cleared local overlay");

        // 3. StopLocal removes local overlay
        player.StopLocal(localId);
        if (player.GetLocalAnimationInfos().Count != 0)
            problems.Add("StopLocal did not remove local overlay");

        return new Check("avatar animation local overlay", problems.Count == 0,
            problems.Count == 0 ? "Local overlay plays, blends priority, survives sim echo, stops cleanly" : string.Join("; ", problems));
    }

    /// <summary>
    /// Recursive directory walk over a res:// tree. DirAccess rather than System.IO for the same
    /// reason the rest of the boot path uses it: it reads loose files and .pck contents alike, so
    /// the self-test covers an exported build instead of only a run from source.
    /// </summary>
    private static IEnumerable<string> EnumerateResources(string dirPath, string extension)
    {
        var subdirs = new List<string>();

        using (var dir = DirAccess.Open(dirPath))
        {
            if (dir == null) yield break;

            dir.ListDirBegin();
            for (string name = dir.GetNext(); name != ""; name = dir.GetNext())
            {
                if (dir.CurrentIsDir())
                {
                    if (!name.StartsWith(".")) subdirs.Add($"{dirPath}/{name}");
                }
                else if (name.EndsWith(extension, StringComparison.Ordinal))
                {
                    yield return $"{dirPath}/{name}";
                }
            }
            dir.ListDirEnd();
        }

        foreach (string sub in subdirs)
        {
            foreach (string found in EnumerateResources(sub, extension)) yield return found;
        }
    }

    private static string ShortName(string resPath) => resPath.Substring(resPath.LastIndexOf('/') + 1);
}
