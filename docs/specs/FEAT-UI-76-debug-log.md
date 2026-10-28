# [FEAT-UI-76] Debug log, privacy-safe, with a way to its file

- **Feature ID:** `FEAT-UI-76`
- **Track:** `ui` / `core`
- **Status:** ✅ Done
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal
Requested 2026-10-28: a checkbox in the settings that writes a debug log in which everything is logged in a way that
respects privacy, and a way to find the file easily (a button).

## Built
- **Preferences > Debug log:** one checkbox (effective at once, remembered in `preferences.cfg`, section `debug_log`),
  a plain statement of what is and is not in the file, the log folder in a selectable field, and the buttons
  *Open log folder*, *Show current log* (selects the file in the file manager) and *Copy path*; a status line shows the
  file name and its size while it grows.
- **`DebugLog`** (app): while on, every line the client prints goes into `logs/debug/slng-debug.log`: `GD.Print`,
  `GD.PrintErr`, the libraries' `Console` output (already routed into the engine log by `ConsoleToGodotLog`) and engine
  warnings and errors, through one engine logger (`DebugLogTap`, `OS.AddLogger`). It also switches `Diag.Verbose` on, so the
  detailed per-object diagnostics that normally need `--diag` are in the file. Bounded queue and one writer task: a render or
  network thread never waits for the disk. Rotated like the other logs (10 sessions), capped at 64 MB per file.
- **`LogSanitizer`** (`SLNG.Core.Logging`), applied to every line before it is written:
  - UUIDs, avatar names and region names become short tags (`<id-3fa91c>`, `<name-b04e11>`, `<region-7c2d90>`): SHA-256 of the
    value with a salt that exists only in this process, so the same thing reads the same inside one file, but nothing can be
    turned back and tags do not match between sessions.
  - Passwords, tokens, session ids, circuit codes, `Authorization`/`Bearer` values -> `<secret>`.
  - URLs keep scheme, host and port; path and query (capability tokens) -> `/<redacted>`; address hosts -> `<ip>`.
  - IPv4/IPv6 addresses -> `<ip>`; e-mail addresses -> `<email>`; region handles -> `<handle>`; the user name in a home-folder
    path -> `<user>`.
  - Avatar and region names are known because the session registers what it learns (`GridSession.RegisterLogPrivacySources`:
    name cache, Display Names, own name, simulators, re-read once a second) and the login registers the account name before its
    first line. A line the rules cannot finish within 100 ms is withheld rather than passed on.
- The client never writes chat or instant-message text to a log; the sanitizer is the second line. The line that said who was
  answered automatically (`[Presence]`) no longer names anyone.

## Limits (stated in the UI)
- Free text typed into the world (object and parcel names) has no shape to recognise.
- `godot.log`, the engine's own file, is unchanged and not sanitized; only the debug log is meant to be handed over.
- A name learned within the last second can still appear in a line (the account name cannot: it is registered at login).
- LibreMetaverse's own logger stays at Warning; raising it floods the log.

## Tests
`LogSanitizerTests` (33), selftest "debug log sanitizes" (writes through `GD.Print` and `Console`, reads the file back).
