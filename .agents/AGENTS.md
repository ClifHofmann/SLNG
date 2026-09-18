
# RTK - Rust Token Killer

See **[rules/antigravity-rtk-rules.md](rules/antigravity-rtk-rules.md)** — the single
copy, including the Windows wrapping rule. Do not restate it here.

# Communication Style Rule

- Keep replies short and plain. No preamble, no sign-off, no restating what was asked.
- A finding or a fix gets 1–3 sentences: what's wrong/what changed, why, and the file.
  Skip the rest unless asked.

# Mandatory Versioning Rule

- **Always update the version number**: when implementing a feature, a fix, or a significant UI change, bump the `AppVersion` constant in `app/scripts/Boot.cs` — patch for a fix, minor only at a genuinely testable milestone. Read the current value and increment it; never copy a version out of a rules file. It is shown on the login screen and in the title bar.


# UI Localization Rule

- **Always localize new and existing UI strings**: When creating or modifying UI elements (windows, panels, buttons, etc.), never hardcode user-facing strings. If you are modifying a window or UI component that still contains hardcoded (untranslated) strings, you MUST translate those existing strings as well. Always use `SLNG.App.UI.L10n.Tr("ui.component.key")` in C# or translation keys in `.tscn` files, and immediately add the corresponding keys to both `app/i18n/en-US.json` and `app/i18n/de-DE.json`.
