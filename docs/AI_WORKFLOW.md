# AI workflow — two assistants, one repo, in parallel

SLNG is built by AI agents. This document defines how **Claude Code** and **Gemini
CLI** work the same repository at the same time without stepping on each other.

## Core idea: one checkout, one branch per work item

Everything happens in the single checkout at `E:/Git/SLNG`. Branch in place:

```bash
git switch -c feature/FEAT-NET-01-script-permissions
```

This project does **not** use `git worktree` — an earlier attempt left stale
registrations behind and was cleaned up in `FEAT-INFRA-01`.

The tree is shared, so the real constraint is uncommitted work, not the directory: a
second session's commit sweeps up whatever the first left unstaged. Commit small and
early, and re-check that your own changes are still present before reporting a task
done. Agents integrate through `main`.

## Branch & commit conventions

- Branch name: `feature/<ID>-<slug>` e.g. `feature/FEAT-ANIM-06-inventory-animations`;
  `fix/<ID>-<slug>` for a bug, `chore/<slug>` where no ID applies.
- Conventional Commits in the shape `AGENTS.md` → **Feature Tracking & ID Convention**
  defines: `feat(anim): [FEAT-ANIM-06] play inventory animations (v0.22.179-alpha)`.
- Small PRs into `main`. Rebase onto `main` before opening a PR.

## Claiming a task (avoid double work)

1. Pick the next **unblocked** task in `docs/ROADMAP.md` (deps satisfied, `Owner: —`).
2. On your branch, set its `Owner` to `claude` or `gemini` and commit that one-line
   change first (`chore: claim M2-2 (gemini)`). This is the lock.
3. Prefer a task in **your default track** (Claude → `net`/`core`, Gemini →
   `assets`/`render`/`ui`) so you naturally avoid the other agent's files.

If two agents ever claim the same task, the earlier commit timestamp on `main` wins;
the other agent picks a different task.

## Staying out of each other's way

- The architecture keeps `src/` (protocol/world) and `app/` (engine) separate on
  purpose. Net/assets vs. render/ui is the natural parallel cut.
- **Shared files** — `AGENTS.md`, `SLNG.sln`, `docs/ROADMAP.md`, CI config — are
  edited in tiny, dedicated commits and merged quickly to minimize conflicts.
- Rebase often. If you've been on a branch more than a few tasks, you waited too long.

## Roles for both tools

The role prompts in `.claude/agents/` are tool-agnostic Markdown.

- **Claude Code** uses them natively as subagents (auto-delegated by description).
- **Gemini CLI** has no subagents; load a role as context instead, e.g.
  `@.claude/agents/asset-pipeline.md`. Same definitions, one source of truth.

## Definition of done (recap)

Builds (`dotnet build`) · tests pass (`dotnet test`) · acceptance criteria met ·
`dotnet format` clean · committed on its own branch with the task id · reviewed.

## Suggested cadence

- Sync `main` at the start of every task.
- Open a PR per task; keep them small enough to review in one pass.
- Update the task's status/owner in `docs/ROADMAP.md` as part of the same PR.
