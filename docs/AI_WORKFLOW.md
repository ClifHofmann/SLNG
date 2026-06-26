# AI workflow — two assistants, one repo, in parallel

SLNG is built by AI agents. This document defines how **Claude Code** and **Gemini
CLI** work the same repository at the same time without stepping on each other.

## Core idea: one worktree per agent

Never run two agents in the same working directory. Use **git worktrees** so each
assistant has its own checkout of its own branch, sharing one `.git`:

```bash
# from the main checkout (E:/Git/SLNG)
git worktree add ../SLNG-claude  -b feat/m0-2-login
git worktree add ../SLNG-gemini  -b feat/m0-4-login-ui
```

Run Claude Code in `../SLNG-claude`, Gemini CLI in `../SLNG-gemini`. They build and
test independently; they integrate through `main`.

```bash
git worktree list          # see all active worktrees
git worktree remove ../SLNG-gemini   # clean up when a branch is merged
```

## Branch & commit conventions

- Branch name: `feat/<task-id>-<slug>` e.g. `feat/m2-2-j2c-decoder`.
- Conventional Commits with the task id: `feat(assets): off-thread j2c decode (M2-2)`.
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
