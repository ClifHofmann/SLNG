#!/usr/bin/env python3
"""Fail when a change removes a task ID from the roadmap table in docs/ROADMAP.md.

Why this exists: a feature commit can rewrite the table and silently delete another task's
row. FEAT-NET-05's row vanished inside the FEAT-PERF-11 commit and nobody noticed until a
merge. Git's diff shows the removal, but nothing in CI asked whether an ID went missing.

The check compares the task IDs in the first cell of each roadmap row at the merge base
against HEAD. An ID present at the base but absent at the head is a failure. IDs that are
new at the head are not checked, because adding a task is always legitimate.

Run from anywhere:
  python tools/check_roadmap_ids.py                     # merge-base(origin/main, HEAD) vs HEAD
  python tools/check_roadmap_ids.py --base REF --head REF --file PATH

Exit codes: 0 all IDs present, 1 at least one ID dropped, 2 a git command failed
(unknown ref, no origin/main, ...). Exit 2 is printed to stderr with the failing command.
"""

import argparse
import os
import re
import subprocess
import sys

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DEFAULT_FILE = "docs/ROADMAP.md"

# The first cell of a roadmap row, when it holds a task ID: M4-7, M1.5-1, MVP6-1, FEAT-UI-02,
# BUG-RENDER-45. The header cells `ID` / `Track` and the `|---|` separator rows do not match,
# because every ID ends in `-<digits>`.
ID_CELL = re.compile(r"^\|\s*([A-Za-z][A-Za-z0-9.]*(?:-[A-Za-z0-9]+)*-\d+)\s*\|")


class GitError(Exception):
    pass


def run_git(args):
    """Run git from the repo root and return its stdout as text."""
    cmd = ["git"] + args
    proc = subprocess.run(cmd, cwd=REPO_ROOT, capture_output=True)
    if proc.returncode != 0:
        stderr = proc.stderr.decode("utf-8", errors="replace").strip().replace("\n", " ")
        raise GitError("`%s` exited %d: %s" % (" ".join(cmd), proc.returncode, stderr))
    return proc.stdout.decode("utf-8", errors="replace")


def roadmap_at(ref, path):
    """Return the roadmap text at ref, or None when the file does not exist at ref."""
    if not run_git(["ls-tree", "--name-only", ref, "--", path]).strip():
        return None
    return run_git(["show", "%s:%s" % (ref, path)])


def task_ids(text):
    ids = set()
    for line in text.splitlines():
        match = ID_CELL.match(line)
        if match:
            ids.add(match.group(1))
    return ids


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Fail when a change removes a task ID from the roadmap table.")
    parser.add_argument("--base",
                        help="ref to compare against (default: git merge-base origin/main <head>)")
    parser.add_argument("--head", default="HEAD", help="ref under review (default: HEAD)")
    parser.add_argument("--file", default=DEFAULT_FILE,
                        help="roadmap path inside the repo (default: %(default)s)")
    args = parser.parse_args(argv)

    path = os.path.normpath(args.file).replace(os.sep, "/")

    try:
        base = args.base or run_git(["merge-base", "origin/main", args.head]).strip()
        base_short = run_git(["rev-parse", "--short", base]).strip()
        head_short = run_git(["rev-parse", "--short", args.head]).strip()
        base_text = roadmap_at(base, path)
        head_text = roadmap_at(args.head, path)
    except GitError as error:
        print("error: %s" % error, file=sys.stderr)
        return 2

    base_ids = task_ids(base_text) if base_text is not None else set()
    head_ids = task_ids(head_text) if head_text is not None else set()
    missing = sorted(base_ids - head_ids)

    if missing:
        print("Task IDs in %s at %s that are missing at %s:" % (path, base_short, head_short))
        for task_id in missing:
            print(task_id)
        print("A feature commit replaced or dropped these rows. Restore them "
              "(git show %s:%s) before merging." % (base_short, path))
        return 1

    print("ok: %d task IDs at %s all still present at %s"
          % (len(base_ids), base_short, head_short))
    return 0


if __name__ == "__main__":
    sys.exit(main())
