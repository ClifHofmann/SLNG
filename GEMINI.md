# GEMINI.md

@AGENTS.md

The shared rules above are authoritative. This file only adds Gemini-CLI-specific
guidance so that Gemini and Claude Code can develop SLNG in parallel.

## Working agreement (same as Claude)

- Pick a task from `docs/ROADMAP.md`, set its `Owner: gemini`, and work the matching
  branch in your **own git worktree** — never share a working tree with another agent.
  See `docs/AI_WORKFLOW.md`.
- Respect the layering rule: no `using Godot;` in `src/`.
- Conventional Commits with the task id, e.g. `feat(assets): j2c decoder (M2-3)`.
- Run `dotnet build` and `dotnet test` before committing.

## Using the role definitions

Gemini CLI has no native subagents, but the role prompts in `.claude/agents/` are
plain Markdown and tool-agnostic. To adopt a role for a session, load it as context,
e.g.:

```
@.claude/agents/protocol-re.md   help me map the ObjectUpdate packet to our ECS
@.claude/agents/graphics-engineer.md   wire J2K textures into a Godot PBR material
```

This keeps one shared set of role definitions for both tools.

## Updating the progress dashboard

`docs/PROGRESS_UPDATE.md` is the procedure, written for both tools. Gemini owns steps 0-4
in full (roadmap status flags, spec status lines, `dotnet test`, regenerate
`docs/dashboard.html`) and stops before step 5 — publishing the Artifact is a Claude Code
capability. Hand over after step 4; the generated HTML opens fine in a browser meanwhile.

## Coordination

- Gemini and Claude pick **different workstreams** by default (e.g. Gemini on
  `assets`, Claude on `net`) so they rarely touch the same files.
- Integrate through small PRs into `main`; rebase often.
- The roadmap's `Suggested tool` column is a hint, not a rule — whoever is free
  takes the next unblocked task and marks ownership.

## Review learnings (M0–M2 — read before continuing)

A code review of the early milestones surfaced recurring issues. They are now codified in
`AGENTS.md` under **Layering & boundaries** and **Threading model** — follow them. Short
version:

- **Don't invert the layers.** `SLNG.Core` must not reference `SLNG.Net` / `SLNG.Assets`
  (it picked up a `Core -> Net` reference, which pulls LibreMetaverse into the domain).
  Use an interface defined in `Core`, or put the bridge in `app`.
- **Don't leak LibreMetaverse types** (`FacetedMesh`, `AssetMesh`, …) past `Net` / `Assets`
  into the renderer — emit engine-neutral DTOs.
- **Don't mutate `World` from network-thread callbacks.** Buffer events and apply them on
  a single thread, once per frame.
- **Keep asset codecs (CoreJ2K) in `SLNG.Assets`, not `SLNG.Net`.**
- **Repo hygiene:** never commit runtime caches (`app/linden/`) or throwaway probes
  (`tools/Probe/`) — both are now git-ignored.
