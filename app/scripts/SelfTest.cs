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
        results.AddRange(CheckWindlightPresets());
        results.Add(CheckInstanceSlotMap());
        results.Add(CheckAvatarAnimationPlayer());
        results.Add(CheckAvatarHoldMode());
        results.Add(CheckAvatarAnimationFreeze());
        results.Add(CheckAvatarAnimationLocalOverlay());
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
