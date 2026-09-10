using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
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
        results.AddRange(CheckWindlightPresets());
        results.Add(CheckInstanceSlotMap());

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
