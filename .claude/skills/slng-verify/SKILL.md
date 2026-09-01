---
name: slng-verify
description: Run SLNG's full pre-commit verification — build both projects (the solution does NOT build app/), run the tests, check the shader globals, run the Godot selftest, and check the traps that make a "clean" build a lie. Use before committing, when asked to verify, or when a change appears to have no effect.
argument-hint: [--quick]
---

# Verify an SLNG change

`AGENTS.md` **Definition of done**: it builds, tests pass, the acceptance criteria in the
roadmap entry are met, `dotnet format` is clean, and it is committed on its own branch with
a Conventional-Commit message.

This skill is the mechanical half of that. Work the steps in order and **report the real
result** — if something fails, say so with the output; never report a skipped step as passed.

## 0. Environment

The .NET 8 SDK is installed **per-user** at `%USERPROFILE%\.dotnet`. A runtime-only `dotnet`
in `C:\Program Files\dotnet` wins PATH precedence, so a bare `dotnet` may report "no SDK".
If that happens: `. tools/dev-env.ps1`, or call `%USERPROFILE%\.dotnet\dotnet.exe` directly.

Verified toolchain: **.NET SDK 8** and **Godot 4.7-stable (.NET/mono)**. Godot is on PATH
as `godot`.

## 1. Build the engine-agnostic half

```bash
dotnet build SLNG.sln
```

## 2. Build the client — separately, always

```bash
dotnet build app/SLNG.App.csproj
```

**This is the step that gets skipped, and it is the one that hides bugs.**
`app/SLNG.App.csproj` sits outside `SLNG.sln` on purpose, so step 1 compiles **none** of
`app/`. A renderer or UI change builds "clean" in step 1 without ever being compiled, and
the client then runs the previous assembly. If a change appears to have no effect in-world,
suspect a stale assembly before suspecting the logic.

## 3. Tests

```bash
dotnet test
```

## 4. Format

```bash
dotnet format SLNG.sln
```

Must be clean before a commit.

## 5. Shader globals

```bash
python tools/check_shader_globals.py
```

Every global uniform must be declared in **both** the shader and `app/project.godot`.

## 6. Selftest (skip only with `--quick`)

```bash
godot --headless --path app -- --selftest
```

Loads every shader, locale file and the Bento skeleton through the engine and exits non-zero
on the first one that fails (`app/scripts/SelfTest.cs`). It does not log in, so it needs no
credentials and no reachable grid.

It is the **only** check that actually compiles the shaders — `godot --headless --editor
--quit` reports success on a shader that cannot compile.

## 7. Undo what the selftest broke

`godot --headless` **rewrites `app/project.godot`**. Every run silently drops
`lights_and_shadows/directional_shadow/size=4096`.

```bash
git diff app/project.godot
```

Revert that hunk before staging. Do not commit it.

## 8. Version

If the change is user-visible, `AppVersion` in `app/scripts/Boot.cs` must be bumped —
patch for a fix, minor only at a genuinely testable milestone.

## Reporting

State plainly which steps passed and which did not. If step 6 was skipped because of
`--quick`, say so. Then suggest the Conventional-Commit message with the feature id
(`feat(area): [FEAT-XXX-NN] …`) — but do not commit unless asked.
