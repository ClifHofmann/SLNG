---
name: progress-update
description: Update the "Puris Viewer — Fortschritt" progress dashboard — refresh the status flags in docs/ROADMAP.md, regenerate docs/dashboard.html from it, and republish the Artifact at its fixed URL. Use when asked to update the progress, the roadmap status, the dashboard, or "den Fortschritt aktualisieren".
---

# Fortschritt aktualisieren

The full procedure lives in **[docs/PROGRESS_UPDATE.md](../../../docs/PROGRESS_UPDATE.md)**
and is the single source of truth — it is written tool-agnostically so Gemini CLI follows
the same steps. Read it and work through steps 0–6 in order.

Do not restate the procedure from memory and do not shortcut it. Two things in particular
are load-bearing and easy to get wrong:

- **Step 1 is the only thinking step.** The dashboard is generated; it reports whatever
  `docs/ROADMAP.md` says. A flag only goes to `✅` with evidence — acceptance criteria in
  `docs/specs/<ID>-*.md` all checked, code on `main`, tests green. Otherwise `🧪`.
- **Step 5 publishes to a fixed Artifact URL.** Pass that URL as `url:` to the `Artifact`
  tool; publishing without it silently creates a second, competing artifact. The URL is in
  step 5 of the procedure — take it from there, never from memory. If the tool refuses
  because the live version has not been read this session, do the `action: "read"` on the
  same URL first and then publish; that refusal is expected, not an error.

`docs/dashboard.html` is git-ignored and is never edited by hand.
