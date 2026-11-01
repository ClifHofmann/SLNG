"""Summarise the FEAT-PERF-28 / FEAT-PERF-30 lines of one or more godot.log files, one block per log.

    python -I arrival_summary.py [--no-timeline] <godot.log> [<godot.log> ...]

Next to <name>.log, a <name>.perf.txt (the copy of slng-perf.log that tools/baseline-run.ps1 makes) is read too: it is written
straight through and carries the [Arrival*] and [TexStage*] lines as well, so a line godot.log lost can be told from one that
was never written.

Reads only; changes nothing.
"""
import os
import re
import sys

NUM = r"([0-9.]+)"

# The stages of one texture fetch, in the order a warm one takes them (SLNG.Core.TexStage).
STAGES = ["Pool", "Copy", "Disk", "DecodeWait", "DecodedRead", "Decode", "Net", "PrepWait", "Prepare", "QueueWait", "Upload"]
STAGE_LINE = re.compile(
    r"\[TexStage\.(\w+)\] t=" + NUM + r" n=(\d+) p50=(\d+)ms p95=(\d+)ms max=(\d+)ms open=(\d+)(?: faces=(\d+))?(?: oldest=(\d+)ms)?")
COUNTERS_LINE = re.compile(
    r"\[TexStage\] t=" + NUM + r" gpuLookups=\+(\d+) gpuHits=\+(\d+) fetches=\+(\d+) faces=\+(\d+) \| "
    r"warmCopy tried=\+(\d+) found=\+(\d+) \| made: copy=\+(\d+) decodedCache=\+(\d+) decode=\+(\d+) http=\+(\d+)")


def field(line, name):
    m = re.search(name + r"=([^ |]+)", line)
    return m.group(1) if m else "?"


def read_lines(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8", errors="replace") as f:
        return [raw.rstrip("\n") for raw in f]


def count_key(line):
    m = re.search(r"\[ArrivalCount\].*?(\+\d+s)", line)
    return m.group(1) if m else "?"


def summarise(path, timeline=True):
    arrivals, counts, terms, parked = [], [], [], 0
    wait_p95 = {}      # class -> max p95 ms seen
    wait_inflight = {}  # class -> max inflight seen
    http_mb = 0.0
    stages = {}        # stage -> {t: (n, p50, p95, max, open, faces, oldest)}
    counters = {}      # t -> tuple of the [TexStage] counters
    perf_path = os.path.splitext(path)[0] + ".perf.txt"
    perf_lines = read_lines(perf_path)
    perf_counts = [l for l in perf_lines if "[ArrivalCount]" in l]
    perf_arrivals = [l for l in perf_lines if "[Arrival]" in l]

    lines = read_lines(path)
    for line in lines:
        if "[Arrival]" in line:
            arrivals.append(line)
        elif "[ArrivalCount]" in line:
            counts.append(line)
        elif "[VramTerms]" in line:
            terms.append(line)
        elif "parked outfit unloaded under VRAM pressure" in line:
            parked += 1
        elif "[TexHttp]" in line:
            m = re.search(r"MB=" + NUM, line)
            if m:
                http_mb = max(http_mb, float(m.group(1)))
        elif "[TexStage" in line:
            pass  # handled below, from both files
        else:
            m = re.search(r"\[TexWait(?:\.(\w+))?\].*?inflight=(\d+).*?p95=(\d+)ms", line)
            if m:
                cls = m.group(1) or "ALL"
                wait_inflight[cls] = max(wait_inflight.get(cls, 0), int(m.group(2)))
                wait_p95[cls] = max(wait_p95.get(cls, 0), int(m.group(3)))

    for line in lines + perf_lines:
        m = STAGE_LINE.search(line)
        if m:
            stage, t = m.group(1), float(m.group(2))
            vals = tuple(int(x) if x is not None else 0 for x in m.groups()[2:])
            stages.setdefault(stage, {})[t] = vals
            continue
        m = COUNTERS_LINE.search(line)
        if m:
            counters[float(m.group(1))] = tuple(int(x) for x in m.groups()[1:])

    handshake = None
    for line in arrivals + perf_arrivals:
        m = re.search(r"handshakeAt=" + NUM + "s", line)
        if m:
            handshake = float(m.group(1))
            break

    print(f"=== {path}")
    if not arrivals and not perf_arrivals:
        print("  no [Arrival] line (client not started with -Diag, or the run ended before the first region finished)")
    for line in (arrivals or perf_arrivals):
        print("  region      :", field(line, "region") if "region=" in line else "?",
              "| ended:", line.split("ended:")[-1].strip())
        for key in ("bakes", "noFaceWithoutTexture", "texInflight0"):
            print(f"  {key:22}: {field(line, key)}")
    for line in counts or perf_counts:
        print("  ", re.sub(r"^(?:\d\d:\d\d:\d\d )?\[ArrivalCount\] t=[0-9.]+ ", "", line))

    # Where a missing line was lost: godot.log against the sidecar. Only when the sidecar exists.
    if perf_lines:
        in_log = {count_key(l) for l in counts}
        in_perf = {count_key(l) for l in perf_counts}
        for key in ("+30s", "+60s"):
            print(f"  [ArrivalCount] {key}: godot.log {'yes' if key in in_log else 'MISSING'}, "
                  f"slng-perf {'yes' if key in in_perf else 'MISSING'}")
    print(f"  texture MB over HTTP (whole run): {http_mb:.1f}")
    if wait_p95:
        print("  [TexWait] worst p95 / peak open, per class:")
        for cls in sorted(wait_p95):
            print(f"    {cls:10} p95 {wait_p95[cls]/1000:7.1f} s   open {wait_inflight[cls]}")

    if stages or counters:
        print_stages(stages, counters, handshake, timeline)

    if terms:
        budgets = [int(re.search(r"budget=(\d+)MB", t).group(1)) for t in terms]
        print(f"  [VramTerms]: {len(terms)} changes, budget first {budgets[0]} / lowest {min(budgets)} / last {budgets[-1]} MB")
    print(f"  'parked outfit unloaded under VRAM pressure': {parked}")


def print_stages(stages, counters, handshake, timeline):
    """FEAT-PERF-30: where a texture waits between the face's request and the bound texture."""
    names = [s for s in STAGES if s in stages] + sorted(s for s in stages if s not in STAGES and s != "Total")
    if "Total" in stages:
        names.append("Total")

    print("  [TexStage] per stage over the run (n = fetches that left it; open = most fetches / faces in it at one report;")
    print("             oldest = longest a fetch had been in it at a report; p95 = worst 5 s interval):")
    print(f"    {'stage':12} {'n':>8} {'worst p50':>10} {'worst p95':>10} {'worst max':>10} {'open':>7} {'faces':>7} {'oldest':>9}")
    for name in names:
        rows = stages[name].values()
        n = sum(r[0] for r in rows)
        print(f"    {name:12} {n:>8} {max(r[1] for r in rows):>8}ms {max(r[2] for r in rows):>8}ms {max(r[3] for r in rows):>8}ms "
              f"{max(r[4] for r in rows):>7} {max(r[5] for r in rows):>7} {max(r[6] for r in rows)/1000:>8.1f}s")

    if counters:
        sums = [sum(v[i] for v in counters.values()) for i in range(10)]
        lookups, hits, fetches, faces, tried, found, copy, dec_cache, decode, http = sums
        rate = f"{100.0 * hits / lookups:.1f} %" if lookups else "n/a"
        print(f"  [TexStage] GPU-cache lookups {lookups}, hits {hits} ({rate}); fetches opened {fetches}, faces that waited {faces}")
        print(f"             warm compressed copy: tried {tried}, found {found}")
        print(f"             textures made from: compressed copy {copy}, decoded cache {dec_cache}, J2K decode of the disk cache {decode}, network {http}")

    if not timeline:
        return
    times = sorted({t for s in stages.values() for t in s})
    if not times:
        return
    base = handshake if handshake is not None else 0.0
    shown = [s for s in names if s != "Total"]
    print(f"  [TexStage] per 5 s interval, seconds from {'the handshake' if handshake is not None else 'process start'}; p50/p95 ms, "
          "(faces open) -- a stage nobody left or sits in shows '.':")
    header = "    " + f"{'t':>6} " + " ".join(f"{s[:10]:>16}" for s in shown)
    print(header)
    for t in times:
        cells = []
        for s in shown:
            r = stages[s].get(t)
            if r is None:
                cells.append(f"{'.':>16}")
                continue
            n, p50, p95, _mx, opn, faces, _old = r
            if n == 0 and opn == 0:
                cells.append(f"{'.':>16}")
            else:
                cells.append(f"{f'{p50}/{p95}':>10}({faces:>4})" if opn else f"{f'{p50}/{p95}':>16}")
        print("    " + f"{t - base:>6.0f} " + " ".join(cells))


if __name__ == "__main__":
    args = sys.argv[1:]
    show_timeline = "--no-timeline" not in args
    for p in args:
        if p.startswith("--"):
            continue
        summarise(p, show_timeline)
