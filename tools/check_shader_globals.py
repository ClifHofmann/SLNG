#!/usr/bin/env python3
"""Cross-check Godot shader globals against project.godot's [shader_globals] table.

Why this exists: a `global uniform` has to be declared in BOTH the shader and
project.godot, and nothing in the normal workflow catches a mismatch.

  * `dotnet build` never looks at .gdshader files.
  * `godot --headless --editor --quit` imports the project without compiling the
    shader -- it reports zero errors on a shader that cannot compile.

The failure only appears when a ShaderMaterial is actually loaded at runtime, as
`SHADER ERROR: Unknown identifier in expression: '<name>'`, and a sky shader that
fails to compile renders as a BLACK DOME with no other symptom. That happened once
(v0.7.36-alpha, slng_sun_disc_color registered in project.godot and used in sky() but
never declared) and cost a round trip, so it gets a checker.

The checker also compares TYPES, which it did not originally, and that omission cost a second
round trip: project.godot spelling a vec3 as "vector3" instead of "vec3" makes Godot ignore the
entry entirely, so the global is not registered at all -- while a names-only check happily
reports it as present. The symptom was 186,149 "Shader uses global parameter ... but it was not
found" warnings in one session (2026-09-04).

Run from anywhere:  python tools/check_shader_globals.py
Exits non-zero when something is wrong, so it can go straight into CI.
"""

import glob
import os
import re
import sys

APP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app")

# A `global uniform <type> <name>` declaration, capturing BOTH.
DECL = re.compile(r"\bglobal\s+uniform\s+(\w+)\s+(\w+)")
# project.godot spells the type on its own line inside the entry: "type": "vec3",
REGISTERED_TYPE = re.compile(r'^(\w+)=\{\s*\n"type":\s*"([^"]+)"', re.M)

# Shader type -> the string project.godot must carry. Godot silently ignores an entry whose
# type it does not recognise, so a plausible-looking wrong spelling registers NOTHING and the
# only symptom is a runtime warning -- 186 149 of them in one live session from a single
# "vector3" that should have been "vec3" (2026-09-04). The checker said "28 registered, 0
# stale" throughout, because it only ever compared names.
GODOT_TYPE = {
    "float": "float",
    "int": "int",
    "bool": "bool",
    "vec2": "vec2",
    "vec3": "vec3",
    "vec4": {"vec4", "color"},   # a vec4 may legitimately be registered as a color
    "mat3": "mat3",
    "mat4": "mat4",
    "sampler2D": "sampler2D",
}
# Any `uniform` declaration, global or not, with optional `: hint` and initialiser.
ANY_UNIFORM = re.compile(r"\buniform\s+[\w<>]+\s+(\w+)")
# An slng_* identifier NOT immediately followed by '(' -- i.e. a value, not a function
# call. The prim shaders define helpers like slng_shade() and slng_transform_uv(), and
# treating those as undeclared uniforms was the checker's own first bug.
USE = re.compile(r"\b(slng_\w+)\b(?!\s*\()")
REGISTERED = re.compile(r"^(\w+)=\{", re.M)


def strip_comments(src):
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return re.sub(r"//[^\n]*", "", src)


def main():
    with open(os.path.join(APP, "project.godot"), encoding="utf-8") as handle:
        project = handle.read()

    start = project.find("[shader_globals]")
    if start < 0:
        print("no [shader_globals] section in project.godot")
        return 1
    # Stop at the next section header so we do not scoop up unrelated keys.
    end = project.find("\n[", start + 1)
    section = project[start:end if end > 0 else len(project)]
    registered = set(REGISTERED.findall(section))
    registered_types = dict(REGISTERED_TYPE.findall(section))

    sources = sorted(glob.glob(os.path.join(APP, "**", "*.gdshader"), recursive=True)
                     + glob.glob(os.path.join(APP, "**", "*.gdshaderinc"), recursive=True))

    problems = []
    all_declared = set()

    for path in sources:
        with open(path, encoding="utf-8") as handle:
            code = strip_comments(handle.read())
        rel = os.path.relpath(path, APP).replace("\\", "/")

        decl_pairs = DECL.findall(code)
        declared = set(name for _, name in decl_pairs)
        all_declared |= declared

        # Type agreement, not just presence. See GODOT_TYPE.
        for shader_type, name in decl_pairs:
            if name not in registered_types:
                continue
            expected = GODOT_TYPE.get(shader_type)
            if expected is None:
                continue  # a type this checker does not know about; presence check still applies
            allowed = expected if isinstance(expected, set) else {expected}
            actual = registered_types[name]
            if actual not in allowed:
                problems.append(
                    "%s declares global %s as %s, but project.godot registers it as \"%s\" "
                    "(expected %s) -- Godot ignores an unrecognised type, so the global is NOT "
                    "registered at all"
                    % (rel, name, shader_type, actual, " or ".join(sorted(allowed))))
        # Any uniform at all counts as declared for use-checking: a plain (non-global)
        # uniform is legitimate and must not be reported.
        visible = set(ANY_UNIFORM.findall(code))

        # An .gdshaderinc is included INTO a shader, so a name it uses may be declared by
        # the including file. Only flag those in the shaders themselves.
        if path.endswith(".gdshader"):
            for name in sorted(USE.findall(code)):
                if name not in visible:
                    problems.append("%s uses %s but never declares it" % (rel, name))

        for name in sorted(declared - registered):
            problems.append("%s declares global %s, missing from project.godot" % (rel, name))

    stale = sorted(registered - all_declared)

    for line in problems:
        print("ERROR  " + line)
    for name in stale:
        print("WARN   project.godot registers %s, no shader declares it" % name)

    if problems:
        print("\n%d error(s)" % len(problems))
        return 1
    print("shader globals consistent (%d registered, %d typed, %d stale)"
          % (len(registered), len(registered_types), len(stale)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
