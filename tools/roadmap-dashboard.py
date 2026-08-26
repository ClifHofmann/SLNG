#!/usr/bin/env python3
"""Render docs/ROADMAP.md as a visual status dashboard.

Why this exists: the roadmap already IS the single source of truth -- every task, its
status flag, its track and owner. What it lacks is a shape you can take in at a glance.
Its "Done when" column has grown into a per-task logbook (one entry runs past a full
screen), which is right for the detail and useless for "where am I".

So this reads the roadmap rather than duplicating it. Nothing here is maintained by
hand; there is no second list to keep in sync, which is the failure mode of every
tracker bolted next to a repo. Update ROADMAP.md as the working agreement already
requires, re-run this, and the picture follows.

    python tools/roadmap-dashboard.py [-o docs/dashboard.html] [--tests 291]

Adds two things the roadmap cannot know: the live repo header (version from Boot.cs,
branch, last commit) and commit momentum per week from git history, which is the one
progress signal that needs no bookkeeping at all.
"""

import argparse
import html
import os
import re
import subprocess
import sys
from collections import Counter, OrderedDict
from datetime import date, timedelta

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The four status flags fixed by AGENTS.md's "Feature Tracking & ID Convention".
STATUS = OrderedDict([
    ("progress", {"flag": "\U0001F6A7", "label": "In Arbeit", "css": "progress"}),
    ("review",   {"flag": "\U0001F9EA", "label": "Review",    "css": "review"}),
    ("pending",  {"flag": "⏸",     "label": "Offen",     "css": "pending"}),
    ("done",     {"flag": "✅",     "label": "Fertig",    "css": "done"}),
])
FLAG_TO_KEY = {v["flag"]: k for k, v in STATUS.items()}

# Order tasks are shown in: what is moving first, what is finished last.
BOARD_ORDER = ["progress", "review", "pending", "done"]


def run(*args):
    try:
        out = subprocess.run(args, cwd=REPO, capture_output=True, text=True,
                             encoding="utf-8", errors="replace")
        return out.stdout.strip() if out.returncode == 0 else ""
    except OSError:
        return ""


def strip_flag(text):
    """Split a task title into (title, status-key). The flag is the trailing emoji of
    the Task cell; variation selectors and stray whitespace are tolerated because they
    are invisible in an editor and would otherwise silently mis-bucket a row."""
    cleaned = text.replace("️", "").strip()
    for flag, key in FLAG_TO_KEY.items():
        if cleaned.endswith(flag):
            return cleaned[: -len(flag)].strip(), key
    return cleaned, None


def first_sentence(text, limit=260):
    """The lead of a 'Done when' cell, with the markdown links and bold markers taken
    out. Full stops inside version numbers and file names are not sentence ends."""
    text = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", text)
    text = re.sub(r"\*\*|`|\*", "", text)
    text = re.sub(r"\s+", " ", text).strip()
    parts = re.split(r"(?<=[a-z\)\"])\.\s+(?=[A-Z\*—])", text)
    lead = parts[0] if parts else text
    if len(lead) > limit:
        lead = lead[:limit].rsplit(" ", 1)[0] + "…"
    return lead


def parse_roadmap(path):
    """Both shapes the roadmap uses: MVP 1's checkbox lists and every later MVP's
    table. They carry the same four fields, so they collapse into one task record."""
    milestones = []
    current = None

    with open(path, encoding="utf-8") as fh:
        lines = fh.read().split("\n")

    heading = re.compile(r"^## (MVP [\d.]+)\s+—\s+(.*?)\s*$")
    checkbox = re.compile(r"^- \[[ x]\]\s+\*\*([\w.\-]+)\*\*\s+(.*?)\s*$")

    for line in lines:
        m = heading.match(line)
        if m:
            name, rest = m.group(1), m.group(2)
            title, _ = strip_flag(rest)
            current = {"name": name, "title": title, "tasks": []}
            milestones.append(current)
            continue

        if current is None:
            continue

        m = checkbox.match(line)
        if m:
            title, status = strip_flag(m.group(2))
            current["tasks"].append({
                "id": m.group(1), "title": title, "status": status or "done",
                "track": "", "owner": "", "detail": "",
            })
            continue

        if not line.startswith("|") or line.startswith("|---") or line.startswith("| ID "):
            continue

        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) < 8:
            continue
        # Anything after the 8th column belongs to "Done when" -- a stray pipe inside
        # that prose must not shift every field to its left.
        task_id, task, track, owner = cells[0], cells[1], cells[2], cells[3]
        detail = " | ".join(cells[7:])
        title, status = strip_flag(task)
        current["tasks"].append({
            "id": task_id, "title": title, "status": status or "pending",
            "track": track, "owner": owner, "detail": detail,
        })

    return [m for m in milestones if m["tasks"]]


def weekly_activity(weeks=18):
    """Commits per ISO week, split by Conventional-Commit type. Weeks with no commits
    are kept as empty columns -- a gap is information (this repo has three)."""
    raw = run("git", "log", "--date=format:%G-W%V", "--format=%ad\x1f%s")
    if not raw:
        return [], []

    buckets = {}
    types = Counter()
    for line in raw.split("\n"):
        if "\x1f" not in line:
            continue
        week, subject = line.split("\x1f", 1)
        m = re.match(r"^(\w+)(\([^)]*\))?!?:", subject)
        kind = (m.group(1).lower() if m else "other")
        if kind not in ("feat", "fix", "docs", "chore", "test", "perf", "refactor"):
            kind = "other"
        buckets.setdefault(week, Counter())[kind] += 1
        types[kind] += 1

    if not buckets:
        return [], []

    # Fill the gaps: build every ISO week from the first commit to the last.
    ordered = sorted(buckets)
    start = date.fromisocalendar(int(ordered[0][:4]), int(ordered[0][6:]), 1)
    end = date.fromisocalendar(int(ordered[-1][:4]), int(ordered[-1][6:]), 1)
    series = []
    cursor = start
    while cursor <= end:
        iso = cursor.isocalendar()
        key = "%d-W%02d" % (iso[0], iso[1])
        series.append((key, buckets.get(key, Counter())))
        cursor += timedelta(days=7)

    return series[-weeks:], types.most_common()


def app_version():
    boot = os.path.join(REPO, "app", "scripts", "Boot.cs")
    try:
        with open(boot, encoding="utf-8") as fh:
            m = re.search(r'AppVersion\s*=\s*"([^"]+)"', fh.read())
            return m.group(1) if m else "?"
    except OSError:
        return "?"


# --------------------------------------------------------------------------------------
# Rendering
# --------------------------------------------------------------------------------------

KIND_ORDER = ["feat", "fix", "docs", "chore", "test", "perf", "refactor", "other"]

CSS = """
:root{
  --ground:#F2F4F3; --surface:#FBFCFB; --surface-2:#EAEDEC; --line:#D3D9D7;
  --ink:#171D1C; --ink-2:#4A5654; --ink-3:#77807E;
  --accent:#0E6F78; --accent-soft:#DCEAEA;
  --done:#4B8B3B; --progress:#C2610F; --review:#6B5BC4; --pending:#79828C;
  --done-soft:#E4EFDF; --progress-soft:#F8E6D6; --review-soft:#E7E4F7; --pending-soft:#E6E9EA;
  --shadow:0 1px 2px rgba(23,29,28,.06), 0 8px 24px -16px rgba(23,29,28,.24);
}
@media (prefers-color-scheme: dark){
  :root:not([data-theme="light"]){
    --ground:#0E1413; --surface:#161D1C; --surface-2:#1F2726; --line:#2C3736;
    --ink:#E2E8E6; --ink-2:#A3AEAC; --ink-3:#78837F;
    --accent:#4FBFC4; --accent-soft:#183234;
    --done:#7BB865; --progress:#E39445; --review:#9A8CE8; --pending:#8B959A;
    --done-soft:#1C2A1B; --progress-soft:#2E2015; --review-soft:#232043; --pending-soft:#212827;
    --shadow:0 1px 2px rgba(0,0,0,.4), 0 8px 24px -16px rgba(0,0,0,.8);
  }
}
:root[data-theme="dark"]{
  --ground:#0E1413; --surface:#161D1C; --surface-2:#1F2726; --line:#2C3736;
  --ink:#E2E8E6; --ink-2:#A3AEAC; --ink-3:#78837F;
  --accent:#4FBFC4; --accent-soft:#183234;
  --done:#7BB865; --progress:#E39445; --review:#9A8CE8; --pending:#8B959A;
  --done-soft:#1C2A1B; --progress-soft:#2E2015; --review-soft:#232043; --pending-soft:#212827;
  --shadow:0 1px 2px rgba(0,0,0,.4), 0 8px 24px -16px rgba(0,0,0,.8);
}

*{box-sizing:border-box;}
body{
  margin:0; background:var(--ground); color:var(--ink);
  font-family:"IBM Plex Sans","Segoe UI",system-ui,sans-serif;
  font-size:15px; line-height:1.55; -webkit-font-smoothing:antialiased;
}
.wrap{max-width:1120px; margin:0 auto; padding:40px 24px 80px; display:flex; flex-direction:column; gap:40px;}

h1,h2,h3{font-family:"IBM Plex Sans Condensed","IBM Plex Sans",sans-serif; margin:0; text-wrap:balance;}
h1{font-size:34px; font-weight:600; letter-spacing:-.01em; line-height:1.1;}
h2{font-size:13px; font-weight:600; text-transform:uppercase; letter-spacing:.12em; color:var(--ink-2);}
h3{font-size:17px; font-weight:600;}
.mono{font-family:"IBM Plex Mono",ui-monospace,monospace; font-variant-numeric:tabular-nums;}

/* header ---------------------------------------------------------------------------- */
.head{display:flex; flex-direction:column; gap:18px; border-bottom:1px solid var(--line); padding-bottom:26px;}
.head-top{display:flex; flex-wrap:wrap; align-items:baseline; gap:14px;}
.ver{
  font-family:"IBM Plex Mono",monospace; font-size:13px; font-weight:500;
  background:var(--accent-soft); color:var(--accent); border-radius:3px; padding:3px 8px;
}
.sub{color:var(--ink-2); max-width:66ch; margin:0;}
.facts{display:flex; flex-wrap:wrap; gap:0; border:1px solid var(--line); border-radius:4px; background:var(--surface); overflow:hidden;}
.fact{flex:1 1 150px; padding:11px 14px; border-right:1px solid var(--line);}
.fact:last-child{border-right:none;}
.fact dt{font-family:"IBM Plex Mono",monospace; font-size:10.5px; text-transform:uppercase; letter-spacing:.1em; color:var(--ink-3); margin:0 0 3px;}
.fact dd{margin:0; font-family:"IBM Plex Mono",monospace; font-size:14px; font-weight:500; overflow-wrap:anywhere;}

/* milestone rail -------------------------------------------------------------------- */
.rail{display:flex; flex-direction:column; gap:2px;}
.ms{display:grid; grid-template-columns:150px 1fr 64px; gap:16px; align-items:center; padding:9px 0; border-bottom:1px solid var(--line);}
.ms:last-child{border-bottom:none;}
.ms-name{display:flex; flex-direction:column; gap:1px; min-width:0;}
.ms-name b{font-family:"IBM Plex Sans Condensed",sans-serif; font-size:15px; font-weight:600;}
.ms-name span{font-size:11.5px; color:var(--ink-3); overflow:hidden; text-overflow:ellipsis; white-space:nowrap;}
.bar{display:flex; height:16px; border-radius:2px; overflow:hidden; background:var(--surface-2);}
.seg{min-width:2px;}
.seg.done{background:var(--done);} .seg.progress{background:var(--progress);}
.seg.review{background:var(--review);} .seg.pending{background:var(--pending); opacity:.4;}
.ms-pct{font-family:"IBM Plex Mono",monospace; font-size:14px; text-align:right; color:var(--ink-2);}
.legend{display:flex; flex-wrap:wrap; gap:16px; margin-top:12px;}
.legend span{display:flex; align-items:center; gap:6px; font-size:12px; color:var(--ink-2);}
/* A swatch has to read at full strength even when the bar segment it stands for is
   deliberately faded (pending, chore). Without this the legend keys look broken. */
.dot{width:9px; height:9px; border-radius:2px; display:inline-block; flex:none; opacity:1 !important;}

/* activity -------------------------------------------------------------------------- */
.chart{display:flex; align-items:flex-end; gap:5px; height:150px; padding-top:8px; overflow-x:auto;}
.wk{flex:1 1 0; min-width:16px; display:flex; flex-direction:column; justify-content:flex-end; gap:3px;}
.stack{display:flex; flex-direction:column-reverse; border-radius:2px 2px 0 0; overflow:hidden;}
.stack i{display:block;}
.wk-label{font-family:"IBM Plex Mono",monospace; font-size:9.5px; color:var(--ink-3); text-align:center; white-space:nowrap;}
.wk-total{font-family:"IBM Plex Mono",monospace; font-size:10px; color:var(--ink-2); text-align:center;}
.k-feat{background:var(--accent);} .k-fix{background:var(--progress);}
.k-docs{background:var(--review);} .k-chore{background:var(--pending); opacity:.55;}
.k-test{background:var(--done);} .k-perf{background:var(--accent); opacity:.55;}
.k-refactor{background:var(--review); opacity:.5;} .k-other{background:var(--ink-3); opacity:.4;}

/* board ----------------------------------------------------------------------------- */
.cols{display:grid; grid-template-columns:repeat(auto-fit,minmax(300px,1fr)); gap:20px; align-items:start;}
.col{display:flex; flex-direction:column; gap:10px;}
.col-head{display:flex; align-items:center; gap:8px; padding-bottom:6px; border-bottom:2px solid var(--line);}
.col-head .dot{width:10px; height:10px;}
.col-head b{font-family:"IBM Plex Sans Condensed",sans-serif; font-size:14px; text-transform:uppercase; letter-spacing:.08em;}
.col-head .n{margin-left:auto; font-family:"IBM Plex Mono",monospace; font-size:13px; color:var(--ink-3);}
.card{background:var(--surface); border:1px solid var(--line); border-left:3px solid var(--line); border-radius:4px; padding:12px 14px; display:flex; flex-direction:column; gap:6px; box-shadow:var(--shadow);}
.card.progress{border-left-color:var(--progress);} .card.review{border-left-color:var(--review);}
.card.pending{border-left-color:var(--pending);} .card.done{border-left-color:var(--done);}
.card-id{font-family:"IBM Plex Mono",monospace; font-size:11px; letter-spacing:.02em; color:var(--accent); font-weight:500;}
.card h3{font-size:15px; line-height:1.3;}
.card p{margin:0; font-size:12.5px; line-height:1.5; color:var(--ink-2);}
.tags{display:flex; flex-wrap:wrap; gap:5px;}
.tag{font-family:"IBM Plex Mono",monospace; font-size:10px; padding:2px 6px; border-radius:2px; background:var(--surface-2); color:var(--ink-2);}
.tag.owner{background:var(--accent-soft); color:var(--accent);}

/* inventory ------------------------------------------------------------------------- */
.tablewrap{overflow-x:auto; border:1px solid var(--line); border-radius:4px; background:var(--surface);}
table{border-collapse:collapse; width:100%; font-size:13px;}
th,td{text-align:left; padding:8px 12px; border-bottom:1px solid var(--line); vertical-align:top;}
th{font-family:"IBM Plex Mono",monospace; font-size:10.5px; text-transform:uppercase; letter-spacing:.09em; color:var(--ink-3); font-weight:500; background:var(--surface-2);}
tbody tr:last-child td{border-bottom:none;}
td.id{font-family:"IBM Plex Mono",monospace; font-size:12px; color:var(--accent); white-space:nowrap;}
td.st{white-space:nowrap;}
.pill{display:inline-block; font-family:"IBM Plex Mono",monospace; font-size:10.5px; padding:2px 7px; border-radius:9px;}
.pill.done{background:var(--done-soft); color:var(--done);}
.pill.progress{background:var(--progress-soft); color:var(--progress);}
.pill.review{background:var(--review-soft); color:var(--review);}
.pill.pending{background:var(--pending-soft); color:var(--pending);}
.ms-row td{background:var(--surface-2); font-family:"IBM Plex Sans Condensed",sans-serif; font-weight:600; font-size:13px;}

footer{border-top:1px solid var(--line); padding-top:18px; color:var(--ink-3); font-size:12px;}
footer code{font-family:"IBM Plex Mono",monospace; background:var(--surface-2); padding:1px 5px; border-radius:3px;}
@media (max-width:640px){
  .ms{grid-template-columns:1fr; gap:6px;}
  .ms-pct{text-align:left;}
  h1{font-size:27px;}
}
"""


def esc(s):
    return html.escape(s or "", quote=True)


def render(milestones, series, kinds, version, tests, out_path):
    branch = run("git", "rev-parse", "--abbrev-ref", "HEAD") or "?"
    commit = run("git", "log", "-1", "--format=%h") or "?"
    when = run("git", "log", "-1", "--format=%cd", "--date=format:%d.%m.%Y") or "?"
    total_commits = run("git", "rev-list", "--count", "HEAD") or "?"

    all_tasks = [t for m in milestones for t in m["tasks"]]
    counts = Counter(t["status"] for t in all_tasks)

    p = []
    p.append("<title>Puris Fortschritt</title>")
    p.append('<link rel="preconnect" href="https://fonts.googleapis.com">')
    p.append('<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>')
    p.append('<link rel="stylesheet" href="https://fonts.googleapis.com/css2?'
             'family=IBM+Plex+Mono:wght@400;500&'
             'family=IBM+Plex+Sans+Condensed:wght@600&'
             'family=IBM+Plex+Sans:wght@400;500;600&display=swap">')
    p.append("<style>%s</style>" % CSS)
    p.append('<div class="wrap">')

    # ---- header
    p.append('<header class="head">')
    p.append('<div class="head-top"><h1>Puris Viewer — Fortschritt</h1>'
             '<span class="ver mono">%s</span></div>' % esc(version))
    p.append('<p class="sub">Erzeugt aus <span class="mono">docs/ROADMAP.md</span>. '
             'Keine zweite Liste, die gepflegt werden will — Roadmap ändern, '
             'Skript neu laufen lassen.</p>')
    p.append('<dl class="facts">')
    facts = [
        ("Aufgaben", "%d" % len(all_tasks)),
        ("Fertig", "%d" % counts.get("done", 0)),
        ("In Arbeit", "%d" % (counts.get("progress", 0) + counts.get("review", 0))),
        ("Commits", total_commits),
        ("Branch", branch),
        ("Zuletzt", "%s · %s" % (commit, when)),
    ]
    if tests:
        facts.insert(3, ("Tests grün", str(tests)))
    for k, v in facts:
        p.append('<div class="fact"><dt>%s</dt><dd>%s</dd></div>' % (esc(k), esc(v)))
    p.append("</dl></header>")

    # ---- milestone rail
    p.append("<section><h2>Meilensteine</h2><div class=\"rail\">")
    for m in milestones:
        tasks = m["tasks"]
        c = Counter(t["status"] for t in tasks)
        n = len(tasks)
        pct = round(100 * c.get("done", 0) / n) if n else 0
        p.append('<div class="ms"><div class="ms-name"><b>%s</b><span>%s</span></div>'
                 % (esc(m["name"]), esc(m["title"])))
        p.append('<div class="bar">')
        for key in BOARD_ORDER:
            if c.get(key):
                p.append('<i class="seg %s" style="width:%.4f%%" title="%s: %d"></i>'
                         % (key, 100.0 * c[key] / n, STATUS[key]["label"], c[key]))
        p.append('</div><div class="ms-pct">%d%%</div></div>' % pct)
    p.append("</div><div class=\"legend\">")
    for key in BOARD_ORDER:
        p.append('<span><i class="dot seg %s"></i>%s · <b class="mono">%d</b></span>'
                 % (key, STATUS[key]["label"], counts.get(key, 0)))
    p.append("</div></section>")

    # ---- activity
    if series:
        peak = max((sum(c.values()) for _, c in series), default=1) or 1
        p.append("<section><h2>Commits pro Woche</h2>")
        p.append('<div class="chart">')
        for week, c in series:
            total = sum(c.values())
            h = 118.0 * total / peak
            p.append('<div class="wk" title="%s: %d Commits">' % (esc(week), total))
            p.append('<div class="wk-total">%s</div>' % (total or ""))
            p.append('<div class="stack" style="height:%.1fpx">' % h)
            for kind in KIND_ORDER:
                if c.get(kind):
                    p.append('<i class="k-%s" style="height:%.4f%%"></i>'
                             % (kind, 100.0 * c[kind] / total))
            p.append('</div><div class="wk-label">%s</div></div>' % esc(week.split("-")[1]))
        p.append("</div>")
        p.append('<div class="legend">')
        for kind, n in kinds:
            p.append('<span><i class="dot k-%s"></i>%s · <b class="mono">%d</b></span>'
                     % (kind, esc(kind), n))
        p.append("</div></section>")

    # ---- board: what is actually moving
    moving = [t for t in all_tasks if t["status"] in ("progress", "review")]
    if moving:
        p.append("<section><h2>Gerade in Arbeit</h2><div class=\"cols\">")
        for key in ("progress", "review"):
            group = [t for t in moving if t["status"] == key]
            p.append('<div class="col"><div class="col-head"><i class="dot seg %s"></i>'
                     '<b>%s</b><span class="n">%d</span></div>'
                     % (key, esc(STATUS[key]["label"]), len(group)))
            for t in group:
                p.append('<article class="card %s">' % key)
                p.append('<div class="card-id">%s</div>' % esc(t["id"]))
                p.append("<h3>%s</h3>" % esc(t["title"]))
                if t["detail"]:
                    p.append("<p>%s</p>" % esc(first_sentence(t["detail"])))
                tags = []
                for tr in [x for x in re.split(r"[/\s]+", t["track"]) if x]:
                    tags.append('<span class="tag">%s</span>' % esc(tr))
                if t["owner"]:
                    tags.append('<span class="tag owner">%s</span>' % esc(t["owner"]))
                if tags:
                    p.append('<div class="tags">%s</div>' % "".join(tags))
                p.append("</article>")
            p.append("</div>")
        p.append("</div></section>")

    # ---- full inventory
    p.append("<section><h2>Alle Aufgaben</h2><div class=\"tablewrap\"><table>")
    p.append("<thead><tr><th>ID</th><th>Aufgabe</th><th>Status</th><th>Track</th><th>Owner</th></tr></thead><tbody>")
    for m in milestones:
        p.append('<tr class="ms-row"><td colspan="5">%s — %s</td></tr>'
                 % (esc(m["name"]), esc(m["title"])))
        order = {k: i for i, k in enumerate(BOARD_ORDER)}
        for t in sorted(m["tasks"], key=lambda x: order.get(x["status"], 9)):
            st = STATUS.get(t["status"], STATUS["pending"])
            p.append('<tr><td class="id">%s</td><td>%s</td>'
                     '<td class="st"><span class="pill %s">%s</span></td>'
                     '<td class="mono">%s</td><td class="mono">%s</td></tr>'
                     % (esc(t["id"]), esc(t["title"]), t["status"], esc(st["label"]),
                        esc(t["track"] or "—"), esc(t["owner"] or "—")))
    p.append("</tbody></table></div></section>")

    p.append('<footer>Neu erzeugen mit <code>python tools/roadmap-dashboard.py</code>. '
             'Quelle: <code>docs/ROADMAP.md</code> + git.</footer>')
    p.append("</div>")

    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(p) + "\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("-o", "--out", default=os.path.join(REPO, "docs", "dashboard.html"))
    ap.add_argument("--roadmap", default=os.path.join(REPO, "docs", "ROADMAP.md"))
    ap.add_argument("--tests", type=int, default=None,
                    help="green test count to show in the header (from dotnet test)")
    ap.add_argument("--weeks", type=int, default=18)
    args = ap.parse_args()

    milestones = parse_roadmap(args.roadmap)
    if not milestones:
        print("no milestones parsed from %s" % args.roadmap, file=sys.stderr)
        return 1

    series, kinds = weekly_activity(args.weeks)
    render(milestones, series, kinds, app_version(), args.tests, args.out)

    total = sum(len(m["tasks"]) for m in milestones)
    print("%s — %d milestones, %d tasks" % (args.out, len(milestones), total))
    return 0


if __name__ == "__main__":
    sys.exit(main())
