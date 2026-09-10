---
name: progress-update
description: Update the "Puris Viewer — Fortschritt" progress dashboard — refresh the status flags in docs/ROADMAP.md (and the matching docs/specs/<ID>-*.md Status line), commit and push. The dashboard on GitHub Pages then rebuilds itself. Use when asked to update the progress, the roadmap status, the dashboard, or "den Fortschritt aktualisieren".
---

# Fortschritt aktualisieren

The full procedure lives in **[docs/PROGRESS_UPDATE.md](../../../docs/PROGRESS_UPDATE.md)**
and is the single source of truth — it is written tool-agnostically so Gemini CLI follows
the same steps. Read it and work through it in order.

The dashboard is **auto-published**: `.github/workflows/dashboard.yml` regenerates
`docs/dashboard.html` and deploys it to <https://clifhofmann.github.io/SLNG/> on every
push to `main` that touches `docs/ROADMAP.md`, `docs/specs/**` or
`tools/roadmap-dashboard.py`. There is no Artifact to publish and no script to run by
hand — the only work is editing the roadmap and pushing.

One thing is load-bearing and easy to get wrong:

- **Setting a flag is the only thinking step.** The dashboard reports whatever
  `docs/ROADMAP.md` says. A flag only goes to `✅` with evidence — acceptance criteria in
  `docs/specs/<ID>-*.md` all checked, code on `main`, `dotnet build` + `dotnet test`
  green. Otherwise `🧪`. When moving to `✅`, also update the spec's `**Status:**` line
  and add one "Done when" sentence with the commit hash / live-verification.

`docs/dashboard.html` is git-ignored and is never edited by hand; the workflow builds it
fresh in `_site/`.
